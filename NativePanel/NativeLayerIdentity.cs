using System;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using ImgCore;
using P1.C1.Core;
using P1.C1.Core.LocalAdjustments;
using P1.ModelCoreWrapper;

internal sealed record NativeLayerIdentity(string Model, string MasksSha256)
{
    private const BindingFlags NativeFields = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly FieldInfo? CollectionReader = typeof(ILocalAdjustmentsLayerCollection).Assembly
        .GetType("P1.C1.Core.LocalAdjustments.LocalAdjustmentsLayerCollection")?.GetField("m_lockReadAccessInner", NativeFields);
    private static readonly FieldInfo? DrawingLock = typeof(LocalAdjustmentsMask).GetField("m_applyingDrawOperationsLock", NativeFields);
    private static readonly FieldInfo? BufferLock = typeof(LocalAdjustmentsMask).GetField("m_bufferedDrawOperationsLock", NativeFields);
    private static readonly FieldInfo? LoadedHandle = typeof(LocalAdjustmentsMask).GetField("m_handle", NativeFields);

    // The caller owns the variant reader lock. Capture One takes the collection
    // reader lock in the same order when copying local adjustments into its model.
    internal static NativeLayerIdentity? TryRead(VariantSettings settings, ILocalAdjustmentsLayerCollection layers)
    {
        if (CollectionReader?.FieldType != typeof(object) || CollectionReader.DeclaringType?.IsInstanceOfType(layers) != true
            || CollectionReader.GetValue(layers) is not { } reader
            || !Monitor.TryEnter(reader))
            return null;
        try
        {
            using var copy = settings.GetMcVariantDeepCopy(argIncludeLocalAdjustmentsLayers: true);
            if (copy is null || copy.GetNativePointer() == IntPtr.Zero)
                return null;
            using var borrowed = new McVariant(copy.GetNativePointer());
            string model;
            try { model = borrowed.ToJSON(); }
            finally { borrowed.DisownNativePointer(); }

            using var masks = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var layer in layers.GetEnabledLayers())
            {
                if (layer.IsCloneOrHealLayer || layer.Mask is not { } mask || !TryAppendMask(masks, mask))
                    return null;
            }
            return new NativeLayerIdentity(model, Convert.ToHexString(masks.GetHashAndReset()));
        }
        finally { Monitor.Exit(reader); }
    }

    private static bool TryAppendMask(IncrementalHash masks, LocalAdjustmentsMask mask)
    {
        if (DrawingLock?.FieldType != typeof(object) || DrawingLock.GetValue(mask) is not { } drawing
            || BufferLock?.FieldType != typeof(object) || BufferLock.GetValue(mask) is not { } buffered
            || LoadedHandle?.FieldType != typeof(LocalAdjustmentsMaskHandle) || !Monitor.TryEnter(drawing))
            return false;
        try
        {
            if (!Monitor.TryEnter(buffered))
                return false;
            try
            {
                // A missing live handle would require lazy loading. Keep the strict
                // guard in that case or if Capture One still has a stroke to apply.
                if (LoadedHandle.GetValue(mask) is not LocalAdjustmentsMaskHandle { IsClosed: false, IsInvalid: false } handle
                    || mask.HasBufferedDrawingOperations() || mask.HasLumaMask)
                    return false;
                return AppendSerializedMask(masks, handle);
            }
            finally { Monitor.Exit(buffered); }
        }
        finally { Monitor.Exit(drawing); }
    }

    private static unsafe bool AppendSerializedMask(IncrementalHash masks, LocalAdjustmentsMaskHandle handle)
    {
        try
        {
            // This is Capture One's in-memory sidecar format with recompression off.
            // The owned buffer remains live until the checksum has consumed its bytes.
            var result = ImgCoreDll.IC_MaskSave64(out var size, out var data, handle, 0);
            using (data)
            {
                if (result != IC_Result.resOK || data is null || data.IsInvalid || size is 0 or > int.MaxValue)
                    return false;
                var bytes = new ReadOnlySpan<byte>(data.DangerousGetHandle().ToPointer(), checked((int)size));
                masks.AppendData(SHA256.HashData(bytes));
                return true;
            }
        }
        catch (ObjectDisposedException) { return false; }
    }
}
