using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ImgCore;
using P1.C1.Core;
using P1.C1.Core.Annotation;
using P1.C1.Core.DestinationProcessing;
using P1.C1.ImageCoreWrapper;
using P1.ModelCoreWrapper;

internal static class NativeExportAnnotations
{
    internal static bool TryCapture(ICoreVariant variant, VariantGeometryTransformer geometry, IC_ProcessSettings settings,
        float recipeScale, bool ignoreCrop, string profilePath, out ExportOverlay? overlay)
    {
        overlay = null;
        try
        {
            var annotations = variant.Annotations;
            if (annotations is null)
                return false;
            // Match the ordinary File-mode annotation renderer. For Ignore
            // Crop and Crop to Path it uses the native reset crop's geometry.
            var crop = geometry.CropCanvasConstrained;
            var origin = new PointF(crop.Left, crop.Top);
            var size = new SizeF(crop.Width, crop.Height);
            if (ignoreCrop)
            {
                ImgCoreDll.IC_ResetCrop(ref settings);
                var center = geometry.Raw2Canvas([new PointF(settings.cropCenterX, settings.cropCenterY)])[0];
                size = new SizeF(settings.cropWidth, settings.cropHeight);
                origin = new PointF(center.X - size.Width / 2, center.Y - size.Height / 2);
            }
            var scale = VariantProcessGeometry.GetAnnotationsScaleFactor(ProcessingMode.File, recipeScale, 1);
            overlay = CaptureRaster(annotations, geometry.Raw2Canvas, origin, size, scale, profilePath);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            InvalidOperationException or ArgumentException or ExternalException or InsufficientMemoryException or OverflowException)
        {
            StartupHook.Log("Could not capture the native recipe annotations.", new { ErrorKind = error.GetBaseException().GetType().Name });
            return false;
        }
    }

    internal static ExportOverlay? CaptureRaster(Annotations annotations, Func<System.Windows.Point[], System.Windows.Point[]> transform,
        PointF origin, SizeF size, float scale, string profilePath)
    {
        var (width, height) = RenderFrameLimits.ValidateExportCanvas(size.Width, size.Height, scale);
        using var reserve = RenderMemoryBudget.SystemMemory.Reserve(RenderAllocation.ImageEncoding, (long)width * height,
            "Choose a smaller export size for the recipe annotations.");
        var raster = AnnotationsRenderer.RenderToBitmap(annotations, transform, origin, size.Width, size.Height, scale);
        if (raster is null || raster.Format != PixelFormats.Bgra32)
            throw new InvalidDataException("Capture One returned an unsupported annotation image.");
        var position = LayoutHelper.AlignToScreenPixels(new PointF(origin.X * scale, origin.Y * scale));
        if (VisibleBounds(raster) is not { } bounds)
            return null;
        var bytes = GC.AllocateUninitializedArray<byte>(checked(bounds.Width * bounds.Height * 4));
        raster.CopyPixels(bounds, bytes, checked(bounds.Width * 4), 0);
        var profile = ExportColorProfile.Capture("Capture One annotations", profilePath);
        // Capture One positions overlays at pixel centres. Use the same
        // whole-pixel conversion as the native recipe watermark.
        return new ExportOverlay(checked((int)MathF.Ceiling(position.X) + bounds.X), checked((int)MathF.Ceiling(position.Y) + bounds.Y),
            bounds.Width, bounds.Height, 1, bytes, Convert.ToHexString(SHA256.HashData(bytes)), profile);
    }

    private static Int32Rect? VisibleBounds(BitmapSource raster)
    {
        var width = raster.PixelWidth;
        var height = raster.PixelHeight;
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
            raster.CopyPixels(new Int32Rect(0, y, width, rows), block, stride, 0);
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
}
