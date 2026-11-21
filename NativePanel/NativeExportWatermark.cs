using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ImgCore;
using P1.C1.Components;
using P1.C1.Core;
using P1.C1.Core.DestinationProcessing;
using P1.C1.Core.ModelEntities;
using P1.C1.ImageCoreWrapper;
using P1.C1.ImageCoreWrapper.ImageCore;
using P1.ModelCoreWrapper;

internal static class NativeExportWatermark
{
    private static readonly Lazy<Func<ICoreVariant, IDestinationProcessingSettings, ICaptureOneDocument, string>?> ResolveText = new(() =>
        typeof(VariantProcessSettingsUpdater).Assembly.GetType("P1.C1.Components.VariantProcessSettingsFactory")
            ?.GetMethod("ResolveWatermarkText", BindingFlags.Static | BindingFlags.NonPublic, null,
                new[] { typeof(ICoreVariant), typeof(IDestinationProcessingSettings), typeof(ICaptureOneDocument) }, null)
            ?.CreateDelegate<Func<ICoreVariant, IDestinationProcessingSettings, ICaptureOneDocument, string>>());

    internal static ExportOverlay? Capture(HostProfile.Selection selection, IDestinationProcessingSettings recipe,
        IC_ProcessSettings settings, float scale, bool ignoreCrop, string profilePath)
    {
        try { return CaptureCore(selection, recipe, settings, scale, ignoreCrop, profilePath); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or InsufficientMemoryException
            or InvalidOperationException or ArgumentException or ExternalException or TargetInvocationException)
        {
            StartupHook.Log("Could not capture the native recipe watermark.", new { ErrorKind = error.GetBaseException().GetType().Name });
            return null;
        }
    }

    private static ExportOverlay? CaptureCore(HostProfile.Selection selection, IDestinationProcessingSettings recipe,
        IC_ProcessSettings settings, float scale, bool ignoreCrop, string profilePath)
    {
        if (recipe.WatermarkType is not (WatermarkType.Text or WatermarkType.Image))
            return null;
        var text = recipe.WatermarkType == WatermarkType.Text ? ResolveText.Value?.Invoke(selection.Variant, recipe, selection.Document) : "";
        if (text is null)
            return null;
        if (ignoreCrop)
            ImgCoreDll.IC_ResetCrop(ref settings);
        var geometry = VariantGeometryTransformer.FromProcessSettings(ref settings);
        IcProcessSettingsHelper.ClearOverlays(ref settings);
        VariantProcessSettingsUpdater.UpdateIcProcessSettingsForWatermark(true, ProcessingMode.File, scale, 1,
            text, geometry, ref settings, recipe, false);
        if (IcProcessSettingsHelper.GetOverlayCount(ref settings) != 1)
            return null;
        var overlay = IcProcessSettingsHelper.GetOverlayAtIndex(ref settings, 0);
        if (overlay.type != IC_BitmapOverlayType.botWatermark || overlay.imageReference is not { IsInvalid: false, IsClosed: false } handle
            || overlay.proportionalScale != 1f || !float.IsFinite(overlay.opacity) || overlay.opacity is < 0 or > 1
            || !float.IsFinite(overlay.horizontalPosition) || !float.IsFinite(overlay.verticalPosition)
            || Math.Abs(overlay.horizontalPosition) > int.MaxValue / 2 || Math.Abs(overlay.verticalPosition) > int.MaxValue / 2)
            return null;
        var pinned = false;
        try
        {
            handle.DangerousAddRef(ref pinned);
            var format = (PixelFormat)ImgCoreDll.ICB_GetPixelFormat(handle);
            if (format is not (PixelFormat.ipfARGB8 or PixelFormat.ipfBGRA8))
                return null;
            using var bitmap = new ImageCoreBitmap(handle, false);
            var width = bitmap.Width;
            var height = bitmap.Height;
            var stride = bitmap.Stride;
            var address = bitmap.Data;
            if (width is <= 0 or > 16384 || height is <= 0 or > 16384
                || (long)width * height > RenderFrameLimits.MaxHighDepthPixels || stride < width * 4 || address == IntPtr.Zero)
                throw new InvalidDataException("Capture One returned an invalid recipe watermark bitmap.");
            using var memory = RenderMemoryBudget.SystemMemory.Reserve(RenderAllocation.ImageEncoding, (long)width * height,
                "Choose a smaller recipe watermark.");
            var bytes = GC.AllocateUninitializedArray<byte>(checked(width * height * 4));
            for (var y = 0; y < height; y++)
                Marshal.Copy(address + checked(y * stride), bytes, y * width * 4, width * 4);
            var profile = ExportColorProfile.Capture("Capture One watermark", profilePath);
            return new ExportOverlay((int)MathF.Ceiling(overlay.horizontalPosition), (int)MathF.Ceiling(overlay.verticalPosition),
                width, height, overlay.opacity, bytes, Convert.ToHexString(SHA256.HashData(bytes)), profile);
        }
        finally { if (pinned) handle.DangerousRelease(); }
    }
}
