using System;
using System.IO;

// The delivery transform preserves the existing SDR look. Clamp the film result
// before mixing, mix in linear sRGB, then clip the final display range.
internal readonly struct SrgbDelivery
{
    internal const int LookStrengthPolicyVersion = 1;
    private const int TransferSteps = 8192;
    private static readonly double[] SrgbDecode = BuildTransfer(DecodeSrgb);
    private static readonly double[] SrgbEncode = BuildTransfer(EncodeSrgb);
    private static readonly double[] Rec709Gamma24Decode = BuildTransfer(value => Math.Pow(value, 2.4));
    private static readonly double[] AdobeDecode = BuildTransfer(value => Math.Pow(value, 563.0 / 256));
    private static readonly double[] ProPhotoDecode = BuildTransfer(value => value <= 1.0 / 32 ? value / 16 : Math.Pow(value, 1.8));

    private readonly PreviewInputSpace space;
    private readonly double strength;
    private readonly bool useWideGamut;
    private readonly double[]? inputTransfer;
    private readonly double[]? outputTransfer;

    internal SrgbDelivery(PreviewInputSpace space, string outputEncoding, double strength)
    {
        if ((uint)space > (uint)PreviewInputSpace.LinearRec2020)
            throw new ArgumentOutOfRangeException(nameof(space));
        if (!double.IsFinite(strength) || strength < 0 || strength > 1)
            throw new ArgumentOutOfRangeException(nameof(strength));
        this.space = space;
        this.strength = strength;
        useWideGamut = space != PreviewInputSpace.Srgb && strength < 1;
        inputTransfer = space switch
        {
            PreviewInputSpace.AdobeRgb => AdobeDecode,
            PreviewInputSpace.ProPhotoRgb => ProPhotoDecode,
            _ => null,
        };
        outputTransfer = outputEncoding switch
        {
            "srgb" => SrgbDecode,
            "linear_rec709" => null,
            "rec709_gamma24" => Rec709Gamma24Decode,
            _ => throw new ArgumentOutOfRangeException(nameof(outputEncoding)),
        };
    }

    internal LinearBgr GetSrgb(ReadOnlySpan<float> source, ReadOnlySpan<float> rendered, int i)
    {
        var baseline = default(LinearBgr);
        if (strength < 1)
        {
            var red = Finite(source[i], "Capture One");
            var green = Finite(source[i + 1], "Capture One");
            var blue = Finite(source[i + 2], "Capture One");
            if (strength == 0 && space == PreviewInputSpace.Srgb)
                return new(Math.Clamp(blue, 0, 1), Math.Clamp(green, 0, 1), Math.Clamp(red, 0, 1));

            baseline = useWideGamut
                ? ToLinearSrgb(space, DecodeInput(red), DecodeInput(green), DecodeInput(blue))
                : new LinearBgr(Sample(SrgbDecode, blue), Sample(SrgbDecode, green), Sample(SrgbDecode, red));
        }
        return new(
            Blend(baseline.Blue, strength > 0 ? rendered[i + 2] : 0),
            Blend(baseline.Green, strength > 0 ? rendered[i + 1] : 0),
            Blend(baseline.Red, strength > 0 ? rendered[i] : 0));
    }

    private double DecodeInput(double value) => inputTransfer is null ? value : Sample(inputTransfer, value);

    private double Blend(double baseline, float processed)
    {
        var display = Math.Clamp(Finite(processed, "Spektrafilm"), 0, 1);
        if (strength == 1 && ReferenceEquals(outputTransfer, SrgbDecode))
            return display;
        var film = outputTransfer is null ? display : Sample(outputTransfer, display);
        return Sample(SrgbEncode, baseline + strength * (film - baseline));
    }

    private static double Finite(float value, string origin)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException($"{origin} returned an invalid pixel.");
        return value;
    }

    // ProPhoto includes Bradford adaptation from D50 to the D65 sRGB white point.
    // Keep wide-gamut components out of range until final display encoding.
    private static LinearBgr ToLinearSrgb(PreviewInputSpace space, double red, double green, double blue) => space switch
    {
        PreviewInputSpace.AdobeRgb => new(-0.04294 * green + 1.04294 * blue, green, 1.39828 * red - 0.39828 * green),
        PreviewInputSpace.ProPhotoRgb => new(
            -0.008555533 * red - 0.153286575 * green + 1.161842108 * blue,
            -0.228798056 * red + 1.231730282 * green - 0.002932226 * blue,
            2.034218153 * red - 0.727536473 * green - 0.306681680 * blue),
        _ => new(
            -0.018150763 * red - 0.100578898 * green + 1.118729661 * blue,
            -0.124550475 * red + 1.132899897 * green - 0.008349423 * blue,
            1.660491002 * red - 0.587641139 * green - 0.072849863 * blue),
    };

    private static double[] BuildTransfer(Func<double, double> transfer)
    {
        var lookup = new double[TransferSteps + 1];
        for (var i = 0; i <= TransferSteps; i++)
            lookup[i] = transfer(i / (double)TransferSteps);
        return lookup;
    }

    private static double Sample(double[] lookup, double value)
    {
        var coordinate = Math.Clamp(value, 0, 1) * TransferSteps;
        var lower = Math.Min((int)coordinate, TransferSteps - 1);
        return lookup[lower] + (coordinate - lower) * (lookup[lower + 1] - lookup[lower]);
    }

    private static double DecodeSrgb(double value) => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

    private static double EncodeSrgb(double value) => value <= 0.0031308 ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055;

    internal readonly record struct LinearBgr(double Blue, double Green, double Red);
}
