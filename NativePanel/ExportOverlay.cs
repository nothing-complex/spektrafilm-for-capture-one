using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal sealed record ExportOverlay(int X, int Y, int Width, int Height, float Opacity, byte[] Bgra,
    string PixelsSha256, ExportColorProfile.Profile Profile)
{
    internal object Identity => new { X, Y, Width, Height, Opacity, PixelsSha256, Profile.Sha256 };

    internal static ExportOverlay? Capture(BitmapSource bitmap, int x, int y, float opacity, string label, string profilePath)
    {
        if (bitmap.Format != PixelFormats.Bgra32)
            throw new InvalidDataException("Capture One returned an unsupported overlay image.");
        if (VisibleBounds(bitmap) is not { } bounds)
            return null;
        var bytes = GC.AllocateUninitializedArray<byte>(checked(bounds.Width * bounds.Height * 4));
        bitmap.CopyPixels(bounds, bytes, checked(bounds.Width * 4), 0);
        var profile = ExportColorProfile.Capture(label, profilePath);
        return new(checked(x + bounds.X), checked(y + bounds.Y), bounds.Width, bounds.Height, opacity, bytes,
            Convert.ToHexString(SHA256.HashData(bytes)), profile);
    }

    private static Int32Rect? VisibleBounds(BitmapSource bitmap)
    {
        var width = bitmap.PixelWidth;
        var height = bitmap.PixelHeight;
        var stride = checked(width * 4);
        var step = Math.Min(height, Math.Max(1, 256 * 1024 / stride));
        var block = new byte[stride * step];
        var left = width;
        var right = -1;
        var top = height;
        var bottom = -1;
        for (var y = 0; y < height; y += step)
        {
            var rows = Math.Min(height - y, step);
            bitmap.CopyPixels(new Int32Rect(0, y, width, rows), block, stride, 0);
            for (var row = 0; row < rows; row++)
            {
                var alpha = row * stride + 3;
                for (var x = 0; x < width; x++, alpha += 4)
                {
                    if (block[alpha] == 0) continue;
                    left = Math.Min(left, x);
                    right = Math.Max(right, x);
                    top = Math.Min(top, y + row);
                    bottom = y + row;
                }
            }
        }
        return right < 0 ? null : new Int32Rect(left, top, right - left + 1, bottom - top + 1);
    }

    internal ushort[] Prepare(ExportColorProfile delivery, CancellationToken stop)
    {
        var rgba = GC.AllocateUninitializedArray<ushort>(Bgra.Length);
        for (var y = 0; y < Height; y++)
        {
            stop.ThrowIfCancellationRequested();
            var end = (y + 1) * Width * 4;
            for (var i = y * Width * 4; i < end; i += 4)
            {
                rgba[i] = (ushort)(257 * Bgra[i + 2]);
                rgba[i + 1] = (ushort)(257 * Bgra[i + 1]);
                rgba[i + 2] = (ushort)(257 * Bgra[i]);
                rgba[i + 3] = (ushort)(257 * Bgra[i + 3]);
            }
        }
        if (Profile.Sha256 == delivery.Sha256)
            return rgba;
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Rgba64, null, rgba, Width * 8);
        var converted = delivery.ConvertOverlay(bitmap, Profile);
        converted.CopyPixels(rgba, Width * 8, 0);
        return rgba;
    }

    internal void Composite(ushort[] destination, int channels, Rectangle crop, ushort[] overlay, CancellationToken stop)
    {
        // Capture One places the watermark on the scaled full canvas. The crop
        // identifies how much of that canvas is present in the delivered pixels.
        var bounds = Rectangle.Intersect(crop, new Rectangle(X, Y, Width, Height));
        if (bounds.IsEmpty || Opacity == 0)
            return;
        for (var y = bounds.Top; y < bounds.Bottom; y++)
        {
            stop.ThrowIfCancellationRequested();
            var src = ((y - Y) * Width + bounds.Left - X) * 4;
            var dst = ((y - crop.Top) * crop.Width + bounds.Left - crop.Left) * channels;
            for (var x = bounds.Left; x < bounds.Right; x++, src += 4, dst += channels)
            {
                var alpha = overlay[src + 3] * (double)Opacity / ushort.MaxValue;
                if (alpha == 0)
                    continue;
                var baseAlpha = channels == 4 ? destination[dst + 3] / (double)ushort.MaxValue : 1;
                var resultAlpha = alpha + baseAlpha * (1 - alpha);
                var foreground = alpha / resultAlpha;
                for (var channel = 0; channel < 3; channel++)
                    destination[dst + channel] = (ushort)Math.Round(destination[dst + channel] * (1 - foreground)
                        + overlay[src + channel] * foreground);
                if (channels == 4)
                    destination[dst + 3] = (ushort)Math.Round(resultAlpha * ushort.MaxValue);
            }
        }
    }

    internal readonly record struct Prepared(string Name, ExportOverlay Source, ushort[] Pixels);

    internal static BitmapSource Composite(BitmapSource destination, Rectangle crop, IReadOnlyList<Prepared> overlays,
        CancellationToken stop, List<PsdLayer>? layers)
    {
        var channels = destination.Format == PixelFormats.Rgba64 ? 4 : 3;
        var stride = checked(crop.Width * channels * sizeof(ushort));
        ushort[]? samples = null;
        foreach (var overlay in overlays)
        {
            var source = overlay.Source;
            if (source.Opacity == 0 || !crop.IntersectsWith(new Rectangle(source.X, source.Y, source.Width, source.Height)))
                continue;
            if (layers is not null)
            {
                if (source.PreparePsdLayer(overlay.Name, crop, overlay.Pixels, stop) is not { } layer)
                    continue;
                layers.Add(layer);
                source = source with { Opacity = 1 };
            }
            if (samples is null)
            {
                stop.ThrowIfCancellationRequested();
                samples = GC.AllocateUninitializedArray<ushort>(checked(crop.Width * crop.Height * channels));
                destination.CopyPixels(samples, stride, 0);
            }
            source.Composite(samples, channels, crop, overlay.Pixels, stop);
        }
        if (samples is null) return destination;
        var output = BitmapSource.Create(crop.Width, crop.Height, destination.DpiX, destination.DpiY,
            destination.Format, null, samples, stride);
        output.Freeze();
        return output;
    }

    private PsdLayer? PreparePsdLayer(string name, Rectangle crop,
        ushort[] overlay, CancellationToken stop)
    {
        var bounds = Rectangle.Intersect(crop, new Rectangle(X, Y, Width, Height));
        if (bounds.Width <= 0 || bounds.Height <= 0 || Opacity == 0)
            return null;
        var visible = false;
        var local = new Int32Rect(bounds.Left - X, bounds.Top - Y, bounds.Width, bounds.Height);
        for (var y = local.Y; y < local.Y + local.Height; y++)
        {
            stop.ThrowIfCancellationRequested();
            var alpha = (y * Width + local.X) * 4 + 3;
            for (var x = 0; x < local.Width; x++, alpha += 4)
            {
                overlay[alpha] = (ushort)Math.Round(overlay[alpha] * (double)Opacity);
                visible |= overlay[alpha] != 0;
            }
        }
        if (!visible) return null;

        // Bake the recipe opacity into 16-bit alpha so the layer and merged
        // preview agree even when the recipe opacity falls between PSD steps.
        var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Rgba64, null,
            overlay, checked(Width * 8));
        BitmapSource layer = local.Width == Width && local.Height == Height ? bitmap : new CroppedBitmap(bitmap, local);
        layer.Freeze();
        return new PsdLayer(name, bounds.Left - crop.Left, bounds.Top - crop.Top, layer);
    }
}
