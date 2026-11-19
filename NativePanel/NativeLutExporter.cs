using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>Samples the same Vulkan colour engine as image rendering, in an explicit sRGB LUT domain.</summary>
internal static class NativeLutExporter
{
    internal sealed record Plan(string[] Arguments, string[] ExcludedEffects);

    internal static Plan Prepare(string[] rendererArguments)
    {
        ArgumentNullException.ThrowIfNull(rendererArguments);
        if (rendererArguments.Length % 2 != 0) throw new ArgumentException("Each renderer option requires a value.");
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < rendererArguments.Length; i += 2) values[rendererArguments[i]] = rendererArguments[i + 1];
        double Number(string key, double fallback = 0) => values.TryGetValue("--" + key, out var value)
            && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : fallback;
        bool On(string key) => values.GetValueOrDefault("--" + key) == "true";
        bool ChoiceOn(string key) => values.TryGetValue("--" + key, out var value) && value != "off";
        var excluded = new List<string>();
        if (On("auto-exposure")) excluded.Add("automatic exposure");
        if (ChoiceOn("grain-model")) excluded.Add("optical grain");
        if (Number("halation-amount") > 0 || Number("scatter-amount") > 0) excluded.Add("halation and scattering");
        if (ChoiceOn("camera-diffusion-family") && Number("camera-diffusion-strength", 1) > 0) excluded.Add("camera diffusion");
        if (ChoiceOn("print-diffusion-family") && Number("print-diffusion-strength", 1) > 0) excluded.Add("print diffusion");
        if (Number("dir-amount", 1) > 0 && (Number("dir-diffusion", 20) > 0
            || Number("dir-tail-size", 200) * Number("dir-tail-weight", 0.06) > 0)) excluded.Add("spatial DIR diffusion");
        if (Math.Abs(Number("enlarger-scale", 1) - 1) > 1e-6 || Number("enlarger-x") != 0 || Number("enlarger-y") != 0)
            excluded.Add("film-plane framing");
        if (On("scanner-enabled"))
        {
            if (Number("scanner-mtf", 60) > 0) excluded.Add("scanner blur");
            if (Number("scanner-unsharp-radius") > 0 && Number("scanner-unsharp-amount") > 0) excluded.Add("scanner sharpening");
            if (Number("scanner-glare", 0.03) > 0) excluded.Add("scanner glare");
        }
        // A cube cannot represent image-dependent metering, neighbourhoods,
        // geometric changes or coordinate-dependent noise. Keep pointwise colour
        // controls, including negative/print development and scanner calibration.
        foreach (var (key, value) in new (string, string)[]
        {
            ("input-color", "srgb"), ("output-color", "srgb"), ("auto-exposure", "false"), ("grain-model", "off"),
            ("halation-amount", "0"), ("scatter-amount", "0"), ("camera-diffusion-family", "off"), ("print-diffusion-family", "off"),
            ("dir-diffusion", "0"), ("dir-tail-size", "0"), ("dir-tail-weight", "0"),
            ("enlarger-scale", "1"), ("enlarger-x", "0"), ("enlarger-y", "0"),
            ("scanner-mtf", "0"), ("scanner-unsharp-radius", "0"), ("scanner-unsharp-amount", "0"), ("scanner-glare", "0"),
        }) values["--" + key] = value;
        return new Plan(values.SelectMany(pair => new[] { pair.Key, pair.Value }).ToArray(), excluded.ToArray());
    }

    internal static async Task<string> ExportAsync(Plan plan, int size, double strength, CancellationToken stop)
    {
        if (size is not (33 or 65)) throw new ArgumentOutOfRangeException(nameof(size), "Choose a 33- or 65-point LUT.");
        if (!double.IsFinite(strength) || strength < 0 || strength > 1) throw new ArgumentOutOfRangeException(nameof(strength));
        var source = BuildGrid(size, stop);
        byte[] result = source;
        string? contract = null;
        if (strength > 0)
        {
            await using var renderer = new OpticalRenderer(export: true);
            result = await renderer.RenderAsync(size * size, size, source, plan.Arguments, stop).ConfigureAwait(false);
            contract = renderer.ParameterContract;
        }
        return await Task.Run(() => FormatCube(plan, size, strength, source, result, contract, stop), stop).ConfigureAwait(false);
    }

    internal static byte[] BuildGrid(int size, CancellationToken stop)
    {
        if (size is < 2 or > 65) throw new ArgumentOutOfRangeException(nameof(size));
        var bytes = new byte[checked(size * size * size * 16)];
        var samples = MemoryMarshal.Cast<byte, float>(bytes);
        var index = 0;
        for (var blue = 0; blue < size; blue++)
        {
            stop.ThrowIfCancellationRequested();
            for (var green = 0; green < size; green++)
                for (var red = 0; red < size; red++)
                {
                    samples[index++] = red / (float)(size - 1);
                    samples[index++] = green / (float)(size - 1);
                    samples[index++] = blue / (float)(size - 1);
                    samples[index++] = 1;
                }
        }
        return bytes;
    }

    internal static string FormatCube(Plan plan, int size, double strength, byte[] source, byte[] rendered,
        string? contract, CancellationToken stop)
    {
        if (source.Length != checked(size * size * size * 16) || rendered.Length != source.Length)
            throw new ArgumentException("The LUT grid and renderer reply dimensions do not agree.");
        var original = MemoryMarshal.Cast<byte, float>(source);
        var processed = MemoryMarshal.Cast<byte, float>(rendered);
        var delivery = new SrgbDelivery(PreviewInputSpace.Srgb, "srgb", strength);
        var text = new StringBuilder(size * size * size * 36);
        text.AppendLine($"TITLE \"Spektrafilm for Capture One sRGB {size}\"");
        text.AppendLine("# Same source-built Vulkan colour engine as Spektrafilm image rendering.");
        text.AppendLine("# Original simulation and film profiles: Andrea Volpato https://github.com/andreavolpato/spektrafilm");
        text.AppendLine("# Vulkan/OFX engine: Aedan Diez https://github.com/chaert-s/spektrafilm-ofx");
        text.AppendLine("# Capture One integration: Luka Kauzlaric https://www.lukakauzlaric.com");
        text.AppendLine("# Film-profile derivatives: CC BY-SA 4.0 https://creativecommons.org/licenses/by-sa/4.0/");
        text.AppendLine("# Modified derivative: colour-only sampled LUT; retain attribution and this notice when sharing.");
        text.AppendLine("# Domain: sRGB input to sRGB output. Capture One RAW development/native adjustments are not included.");
        text.AppendLine("# Excluded active effects: " + (plan.ExcludedEffects.Length == 0 ? "none" : string.Join(", ", plan.ExcludedEffects)));
        text.AppendLine("# Engine parameter contract: " + (contract ?? "identity; renderer not required"));
        text.AppendLine("# Look strength: " + strength.ToString("R", CultureInfo.InvariantCulture));
        text.AppendLine($"# Look strength policy v{SrgbDelivery.LookStrengthPolicyVersion} uses SDR clipping before a linear-light blend.");
        text.AppendLine("# Colour parameters: " + string.Join(' ', plan.Arguments));
        text.AppendLine($"LUT_3D_SIZE {size}\nDOMAIN_MIN 0 0 0\nDOMAIN_MAX 1 1 1");
        for (var i = 0; i < original.Length; i += 4)
        {
            if (i % 4096 == 0) stop.ThrowIfCancellationRequested();
            var rgb = delivery.GetSrgb(original, processed, i);
            text.Append(rgb.Red.ToString("G9", CultureInfo.InvariantCulture)).Append(' ');
            text.Append(rgb.Green.ToString("G9", CultureInfo.InvariantCulture)).Append(' ');
            text.Append(rgb.Blue.ToString("G9", CultureInfo.InvariantCulture)).Append('\n');
        }
        return text.ToString();
    }
}
