using System;

internal readonly record struct NativeImageRevision(long HighRes, long LowRes, long Geometry);

internal enum SourceStage { CaptureOneDevelopedRgb }
internal enum SourcePrimaries { SrgbD65, AdobeRgbD65, ProPhotoRgbD50, Rec2020D65 }
internal enum SourceTransfer { Srgb, AdobeRgb1998, ProPhotoRgb, Linear }
internal enum NativeSourceFormat { Rgb16, Rgba16, Rgb32Float }
internal enum SourceSampleRange { NormalizedUnsigned, FiniteExtended }
internal enum SourceAlpha { Opaque, PreservedNativeChannel }
internal enum SourceSampling { CaptureOne, LinearLightArea }

internal sealed record FrameSourceDescription(Guid VariantId, string SourceFormat, PreviewInputSpace InputEncoding,
    string ProfileSha256, NativeImageRevision AdjustmentKey, string HostVersion, string HostAssemblySha256)
{
    internal SourceStage Stage => SourceStage.CaptureOneDevelopedRgb;

    internal SourcePrimaries Primaries => InputEncoding switch
    {
        PreviewInputSpace.Srgb => SourcePrimaries.SrgbD65,
        PreviewInputSpace.AdobeRgb => SourcePrimaries.AdobeRgbD65,
        PreviewInputSpace.ProPhotoRgb => SourcePrimaries.ProPhotoRgbD50,
        PreviewInputSpace.LinearRec2020 => SourcePrimaries.Rec2020D65,
        _ => throw new ArgumentOutOfRangeException(nameof(InputEncoding)),
    };

    internal SourceTransfer Transfer => InputEncoding switch
    {
        PreviewInputSpace.Srgb => SourceTransfer.Srgb,
        PreviewInputSpace.AdobeRgb => SourceTransfer.AdobeRgb1998,
        PreviewInputSpace.ProPhotoRgb => SourceTransfer.ProPhotoRgb,
        PreviewInputSpace.LinearRec2020 => SourceTransfer.Linear,
        _ => throw new ArgumentOutOfRangeException(nameof(InputEncoding)),
    };
}

internal sealed record FrameDescription(FrameSourceDescription Source, int Width, int Height,
    NativeSourceFormat NativeFormat, SourceSampling Sampling = SourceSampling.CaptureOne)
{
    internal const int TransportBits = 32;
    internal const int TransportChannels = 4;

    internal int NativeBitDepth => NativeFormat switch
    {
        NativeSourceFormat.Rgb16 or NativeSourceFormat.Rgba16 => 16,
        NativeSourceFormat.Rgb32Float => 32,
        _ => throw new ArgumentOutOfRangeException(nameof(NativeFormat)),
    };

    internal SourceSampleRange Range => NativeFormat == NativeSourceFormat.Rgb32Float
        ? SourceSampleRange.FiniteExtended : SourceSampleRange.NormalizedUnsigned;

    internal SourceAlpha Alpha => NativeFormat == NativeSourceFormat.Rgba16
        ? SourceAlpha.PreservedNativeChannel : SourceAlpha.Opaque;

    internal bool Describes(int width, int height, int nativeBitDepth, int transportBytes) =>
        width == Width && height == Height && width > 0 && height > 0 && NativeBitDepth == nativeBitDepth
        && transportBytes >= 0 && transportBytes % (TransportChannels * sizeof(float)) == 0
        && (long)width * height == transportBytes / (TransportChannels * sizeof(float));
}
