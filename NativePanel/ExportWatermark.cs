using System;
using System.Drawing;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

internal sealed record ExportWatermark(int X, int Y, int Width, int Height, float Opacity, byte[] Bgra,
    string PixelsSha256, ExportColorProfile.Profile Profile)
{
    internal object Identity => new { X, Y, Width, Height, Opacity, PixelsSha256, Profile.Sha256 };

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
        var converted = delivery.ConvertWatermark(bitmap, Profile);
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

    internal BitmapSource Composite(BitmapSource destination, Rectangle crop, ushort[] overlay, CancellationToken stop)
    {
        if (Opacity == 0 || !crop.IntersectsWith(new Rectangle(X, Y, Width, Height)))
            return destination;
        var channels = destination.Format == PixelFormats.Rgba64 ? 4 : 3;
        var stride = checked(crop.Width * channels * sizeof(ushort));
        var samples = GC.AllocateUninitializedArray<ushort>(checked(crop.Width * crop.Height * channels));
        stop.ThrowIfCancellationRequested();
        destination.CopyPixels(samples, stride, 0);
        Composite(samples, channels, crop, overlay, stop);
        var output = BitmapSource.Create(crop.Width, crop.Height, destination.DpiX, destination.DpiY,
            destination.Format, null, samples, stride);
        output.Freeze();
        return output;
    }

    internal (PsdLayer Layer, BitmapSource Merged)? ComposePsdLayer(BitmapSource background, Rectangle crop,
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
        var bitmap = BitmapSource.Create(Width, Height, background.DpiX, background.DpiY, PixelFormats.Rgba64, null,
            overlay, checked(Width * 8));
        BitmapSource layer = local.Width == Width && local.Height == Height ? bitmap : new CroppedBitmap(bitmap, local);
        layer.Freeze();
        var merged = (this with { Opacity = 1 }).Composite(background, crop, overlay, stop);
        return (new PsdLayer("Watermark", bounds.Left - crop.Left, bounds.Top - crop.Top, layer), merged);
    }
}
