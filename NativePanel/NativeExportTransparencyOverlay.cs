using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using P1.C1.Components;
using P1.C1.Core;
using P1.C1.Core.Overlay;
using P1.C1.ImageCoreWrapper;

internal static class NativeExportTransparencyOverlay
{
    private static readonly PropertyInfo? ProviderProperty = typeof(VariantProcessSettingsUpdater).Assembly
        .GetType("P1.C1.Program")?.GetProperty("OverlayController", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    internal static bool TryCapture(IGeometryTransformer geometry, float recipeScale, string profilePath, out ExportOverlay? overlay)
    {
        overlay = null;
        try
        {
            if (ProviderProperty?.GetValue(null) is not IOverlayProvider provider || provider.Bitmap is not { } source)
                return false;
            // The file updater uses the recipe selection and the native overlay
            // placement. The separate Show switch belongs to the Viewer.
            var opacity = provider.Opacity / 100f;
            if (opacity == 0)
                return true;
            if (provider.GetOverlayRect(geometry) is not { } bounds)
                return false;
            overlay = CaptureRaster(source, new PointF((float)bounds.Left, (float)bounds.Top),
                provider.GetScale(geometry, false), recipeScale, opacity, profilePath);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException
            or ArgumentException or ExternalException or InsufficientMemoryException or OverflowException or TargetInvocationException)
        {
            StartupHook.Log("Could not capture the native recipe overlay.", new { ErrorKind = error.GetBaseException().GetType().Name });
            return false;
        }
    }

    internal static ExportOverlay? CaptureRaster(BitmapSource source, PointF origin, float scale,
        float recipeScale, float opacity, string profilePath)
    {
        if (!float.IsFinite(opacity) || opacity is < 0 or > 1)
            throw new InvalidDataException("Capture One returned an invalid overlay opacity.");
        if (opacity == 0)
            return null;
        var deliveredScale = scale * recipeScale;
        var (width, height) = RenderFrameLimits.ValidateExportCanvas(source.PixelWidth, source.PixelHeight, deliveredScale);
        using var memory = RenderMemoryBudget.SystemMemory.Reserve(RenderAllocation.ImageEncoding, (long)width * height,
            "Choose a smaller export size or transparency overlay.");
        BitmapSource bitmap = deliveredScale == 1 ? source : new TransformedBitmap(source, new ScaleTransform(deliveredScale, deliveredScale));
        var position = LayoutHelper.AlignToScreenPixels(new PointF(origin.X * recipeScale, origin.Y * recipeScale));
        return ExportOverlay.Capture(bitmap, checked((int)MathF.Ceiling(position.X)), checked((int)MathF.Ceiling(position.Y)),
            opacity, "Capture One transparency overlay", profilePath);
    }
}
