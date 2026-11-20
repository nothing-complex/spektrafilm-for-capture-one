using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

public static partial class StartupHook
{
    private sealed record ColorProbe(string Source, string InputEncoding, int Width, int Height, string ProfileSha256,
        string NativePixels, string FilmPixels);
    private sealed record AdjustmentProbe(string Source, string CameraProfile, string CameraProfileSha256, int Width, int Height,
        double Exposure, double Temperature, double Highlight, double MinusEv, double PlusEv, double WarmTemperature, double RecoveredHighlight,
        Dictionary<string, string> Pixels);

    private static async Task RunColorMatrix(Assembly assembly, object panel, object window, object spatial)
    {
        var directory = Path.Combine(folder, "color-matrix");
        Directory.CreateDirectory(directory);
        var matrix = new List<ColorProbe>();
        var adjustments = new List<AdjustmentProbe>();
        var document = Field(panel, "document")!;
        var core = AppDomain.CurrentDomain.GetAssemblies().Single(item => item.GetName().Name == "P1.C1.Core");
        var provider = core.GetType("P1.C1.Core.Interfaces.ISelectedCollectionsProvider", true)!;
        var coreVariant = core.GetType("P1.C1.Core.ICoreVariant", true)!;
        var collection = provider.GetProperty("PrimarySelectedCollection")!.GetValue(document)!;
        var variants = ((IEnumerable)core.GetType("P1.C1.Core.ModelEntities.ISimpleCollection", true)!
            .GetMethod("GetAllFilteredAndVisibleVariants")!.Invoke(collection, null)!).Cast<object>().ToArray();
        var selection = provider.GetProperty("PrimarySelectedCollectionSelection")!.GetValue(document)!;
        var targetSelection = core.GetType("P1.C1.Core.Selection.ISelection", true)!.GetProperty("SelectedVariants")!.GetValue(selection)!;
        var select = core.GetType("P1.C1.Core.Selection.IVariantSelection", true)!.GetMethod("SelectAndSetAsPrimary")!;
        var targetMethod = assembly.GetType("HostProfile")!.GetMethod("CaptureTarget", Flags)!;
        var capture = assembly.GetType("HostImageSource")!.GetMethod("Capture", Flags)!;
        var colorEnum = assembly.GetType("PreviewInputSpace")!;
        var workerType = assembly.GetType("OpticalRenderer")!;
        var remaining = new HashSet<string>(new[] { ".CR3", ".RAF", ".NEF", ".DNG" }, StringComparer.OrdinalIgnoreCase);
        var encodings = new[] { ("Srgb", "srgb"), ("AdobeRgb", "adobe_rgb"), ("ProPhotoRgb", "prophoto"), ("LinearRec2020", "linear_rec2020") };
        Call(spatial, "SetEnabled", false);
        await (Task)Call(spatial, "DrainPreviewWorkersAsync", CancellationToken.None)!;
        var renderer = Activator.CreateInstance(workerType, Flags, null, new object[] { false, true }, null)!;
        await using var worker = (IAsyncDisposable)renderer;
        foreach (var item in variants)
        {
            select.Invoke(targetSelection, new[] { item, (object)false });
            Call(panel, "SyncSelection");
            Call(spatial, "SetEnabled", false);
            var target = targetMethod.Invoke(null, new[] { window })!;
            var source = Path.GetFileName((string)Prop(target, "SourcePath")!);
            if (!remaining.Remove(Path.GetExtension(source)))
                continue;
            (int Width, int Height)? dimensions = null;
            var look = Call(panel, "CurrentSettings")!;
            foreach (var (color, encoding) in encodings)
            {
                var space = Enum.Parse(colorEnum, color);
                var snapshot = capture.Invoke(null, new[] { window, target, space, false })!;
                var frame = (await Task.Run(() => Call(snapshot, "RenderScaledPreview", 480, CancellationToken.None)))!;
                var width = (int)Prop(frame, "Width")!;
                var height = (int)Prop(frame, "Height")!;
                var bytes = (byte[])Prop(frame, "FloatBytes")!;
                var description = Prop(frame, "Description")!;
                var digest = (string)Prop(description, "ProfileSha256")!;
                Check(Prop(frame, "BitDepth") is 16 && Math.Max(width, height) == 480
                    && bytes.LongLength == width * height * 16L && (dimensions is null || dimensions == (width, height)),
                    $"{source} develops as native 16-bit RGBA32F in {encoding} on identical geometry");
                Check(Equals(Prop(description, "InputEncoding"), space) && Equals(Prop(description, "VariantId"), Prop(target, "VariantId"))
                    && Equals(Prop(description, "AdjustmentKey"), Prop(snapshot, "AdjustmentKey")) && digest.Length == 64 && digest.All(Uri.IsHexDigit),
                    $"{source} {encoding} frame identifies its selected variant, native revision and exact ICC bytes");
                dimensions = (width, height);
                var options = (string[])Call(spatial, "RendererOptions", look, encoding, "linear_rec709", false, false)!;
                var film = await (Task<byte[]>)Call(renderer, "RenderAsync", width, height, bytes, options, CancellationToken.None, false)!;
                Check(ValidPixels(bytes, film), $"{source} {encoding} produces a finite, distinct Rec.709 film result with unchanged alpha");
                var rawFile = source + "-" + encoding + ".f32";
                var filmFile = source + "-" + encoding + "-rec709.f32";
                File.WriteAllBytes(Path.Combine(directory, rawFile), bytes);
                File.WriteAllBytes(Path.Combine(directory, filmFile), film);
                matrix.Add(new ColorProbe(source, encoding, width, height, digest, rawFile, filmFile));
            }
            adjustments.Add(await ProbeNativeAdjustments(source, item, target, dimensions!.Value, capture, window,
                Enum.Parse(colorEnum, "LinearRec2020"), coreVariant, directory));
        }
        Check(remaining.Count == 0 && matrix.Count == 16, "all four camera RAW formats exercised with all four native working profiles");
        File.WriteAllText(Path.Combine(directory, "matrix.json"), JsonSerializer.Serialize(matrix, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(directory, "native-adjustments.json"),
            JsonSerializer.Serialize(adjustments, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task<AdjustmentProbe> ProbeNativeAdjustments(string source, object variant, object target,
        (int Width, int Height) dimensions, MethodInfo capture, object window, object space, Type coreVariant, string directory)
    {
        var exposure = coreVariant.GetProperty("Exposure")!;
        var temperature = coreVariant.GetProperty("WhiteBalanceTemperature")!;
        var whiteBalance = coreVariant.GetProperty("WhiteBalance")!;
        var highlight = coreVariant.GetProperty("HighlightRecovery")!;
        var originalExposure = exposure.GetValue(variant)!;
        var originalTemperature = temperature.GetValue(variant)!;
        var originalWhiteBalance = whiteBalance.GetValue(variant)!;
        var originalHighlight = highlight.GetValue(variant)!;
        var cameraProfile = coreVariant.GetProperty("ColorProfile")!.GetValue(variant)!;
        var profileName = (string)Prop(cameraProfile, "displayName")!;
        var profilePath = (string?)Prop(cameraProfile, "FilePathInLibrary");
        var embeddedProfile = Prop(cameraProfile, "rawData") as byte[];
        var cameraProfileSha256 = embeddedProfile is { Length: > 0 }
            ? Convert.ToHexString(SHA256.HashData(embeddedProfile))
            : File.Exists(profilePath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(profilePath!))) : "";
        Check(!string.IsNullOrWhiteSpace(profileName) && cameraProfileSha256.Length == 64,
            $"{source} identifies its actual camera ICC profile '{profileName}'");
        var startExposure = Convert.ToDouble(originalExposure, CultureInfo.InvariantCulture);
        var startTemperature = Convert.ToDouble(originalTemperature, CultureInfo.InvariantCulture);
        var startHighlight = Convert.ToDouble(originalHighlight, CultureInfo.InvariantCulture);
        var warmer = Math.Min(20_000, startTemperature + 1200);
        var recover = startHighlight < 60 ? 75 : 10;
        var evidence = new Dictionary<string, string>(StringComparer.Ordinal);

        void Set(PropertyInfo property, double value) =>
            property.SetValue(variant, Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture));

        async Task<(object Revision, byte[] Bytes)> Develop(string label)
        {
            var snapshot = capture.Invoke(null, new[] { window, target, space, false })!;
            var frame = (await Task.Run(() => Call(snapshot, "RenderScaledPreview", 480, CancellationToken.None)))!;
            var bytes = (byte[])Prop(frame, "FloatBytes")!;
            Check(Prop(frame, "BitDepth") is 16 && Equals(Prop(frame, "Width"), dimensions.Width)
                && Equals(Prop(frame, "Height"), dimensions.Height), $"{source} {label} native tone probe retains its exact high-depth geometry");
            var filename = source + "-adjustment-" + label + ".f32";
            File.WriteAllBytes(Path.Combine(directory, filename), bytes);
            evidence.Add(label, filename);
            return (Prop(snapshot, "AdjustmentKey")!, bytes);
        }

        try
        {
            var original = await Develop("original");
            Set(exposure, startExposure - 1);
            var darker = await Develop("minus-ev");
            Set(exposure, startExposure + 1);
            var brighter = await Develop("plus-ev");
            Set(highlight, recover);
            var recovered = await Develop("highlight-recovery");
            highlight.SetValue(variant, originalHighlight);
            exposure.SetValue(variant, originalExposure);
            Set(temperature, warmer);
            var warm = await Develop("warmer-wb");
            var cases = new[] { original, darker, brighter, recovered, warm };
            Check(cases.Select(item => item.Revision).Distinct().Count() == cases.Length,
                $"{source} native exposure, highlight and white balance each invalidate the source identity");
            Check(ValidPixels(original.Bytes, darker.Bytes) && ValidPixels(original.Bytes, brighter.Bytes)
                && ValidPixels(brighter.Bytes, recovered.Bytes) && ValidPixels(original.Bytes, warm.Bytes),
                $"{source} exposure, white balance and recovery change finite RGB while preserving alpha");
        }
        finally
        {
            exposure.SetValue(variant, originalExposure);
            highlight.SetValue(variant, originalHighlight);
            temperature.SetValue(variant, originalTemperature);
            // Restore the native multipliers last. Kelvin is a derived value,
            // and setting it rounds the coefficients through the temperature model.
            whiteBalance.SetValue(variant, originalWhiteBalance);
        }
        var finalTemperature = Convert.ToDouble(temperature.GetValue(variant), CultureInfo.InvariantCulture);
        Check(Equals(exposure.GetValue(variant), originalExposure) && Equals(highlight.GetValue(variant), originalHighlight)
            && Equals(whiteBalance.GetValue(variant), originalWhiteBalance) && Math.Abs(finalTemperature - startTemperature) <= .02,
            $"{source} disposable tone probes restore the native values and white balance multipliers ({startTemperature:F2}→{finalTemperature:F2} K)");
        return new AdjustmentProbe(source, profileName, cameraProfileSha256, dimensions.Width, dimensions.Height, startExposure, startTemperature, startHighlight,
            startExposure - 1, startExposure + 1, warmer, recover, evidence);
    }

    private static bool ValidPixels(byte[] native, byte[] film)
    {
        if (native.Length != film.Length) return false;
        var a = MemoryMarshal.Cast<byte, float>(native.AsSpan());
        var b = MemoryMarshal.Cast<byte, float>(film.AsSpan());
        var changed = false;
        for (var index = 0; index < a.Length; index++)
        {
            if (!float.IsFinite(a[index]) || !float.IsFinite(b[index])) return false;
            if (index % 4 == 3 && a[index] != b[index]) return false;
            changed |= a[index] != b[index];
        }
        return changed;
    }
}
