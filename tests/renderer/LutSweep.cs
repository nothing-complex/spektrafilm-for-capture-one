using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

internal static partial class Program
{
    private static async Task CheckLutSweep(string runtime, string report)
    {
        const int size = 33;
        var root = Path.GetFullPath(runtime);
        var output = Path.GetFullPath(report);
        if (File.Exists(output)) throw new IOException("Choose a new LUT sweep receipt.");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        const string primary = "--stock kodak_portra_400 --paper kodak_portra_endura --mode print --spectral-method hanatos2025";
        var cases = new (string Key, string Value, string Enables)[]
        {
            ("stock", "kodak_vision3_250d", ""), ("paper", "kodak_endura_premier", ""),
            ("mode", "scan", ""), ("mode", "scan_inverted", "--mode scan"),
            ("spectral-method", "mallett2019", ""), ("spectral-method", "hanatos2026", ""),
            ("film-exposure", "0.65", ""), ("print-exposure", "0.45", ""),
            ("film-gamma", "0.72", ""), ("print-gamma", "0.7", ""),
            ("film-push-pull", "0.9", ""), ("print-push-pull", "-1.2", ""),
            ("film-push-pull-mode", "experimental", "--film-push-pull 1.25"),
            ("negative-bleach-bypass", "0.55", ""), ("print-bleach-bypass", "0.6", ""),
            ("negative-leuco-cyan", "0.4", "--negative-bleach-bypass 0.8"),
            ("print-shadow-shape", "-0.73", ""), ("print-highlight-shape", "0.62", ""),
            ("cyan-filter", "43", ""), ("magenta-filter", "-30", ""), ("yellow-filter", "24", ""),
            ("preflash", "0.6", ""), ("preflash-magenta", "40", "--preflash 0.6"),
            ("preflash-yellow", "-36", "--preflash 0.6"),
            ("uv-enabled", "true", "--uv-cutoff 445"), ("uv-cutoff", "445", "--uv-enabled true"),
            ("ir-enabled", "true", "--ir-cutoff 650"), ("ir-cutoff", "650", "--ir-enabled true"),
            ("dir-amount", "1.2", ""), ("dir-same-layer", "0.4", "--dir-amount 1.2"),
            ("dir-interlayer", "0.25", "--dir-amount 1.2"),
            ("dir-gamma-r", "0.05", "--dir-amount 1.2"), ("dir-gamma-g", "0.05", "--dir-amount 1.2"),
            ("dir-gamma-b", "0.05", "--dir-amount 1.2"), ("dir-r-to-g", "0.05", "--dir-amount 1.2"),
            ("dir-r-to-b", "0.05", "--dir-amount 1.2"), ("dir-g-to-r", "0.05", "--dir-amount 1.2"),
            ("dir-g-to-b", "0.05", "--dir-amount 1.2"), ("dir-b-to-r", "0.05", "--dir-amount 1.2"),
            ("dir-b-to-g", "0.05", "--dir-amount 1.2"),
            ("scanner-enabled", "true", "--scanner-white true --scanner-white-level 0.8"),
            ("scanner-white", "true", "--scanner-enabled true --scanner-white-level 0.8"),
            ("scanner-white-level", "0.8", "--scanner-enabled true --scanner-white true"),
            ("scanner-black", "true", "--scanner-enabled true --scanner-black-level 0.08"),
            ("scanner-black-level", "0.08", "--scanner-enabled true --scanner-black true"),
            ("color-adaptation", "true", ""),
            ("color-input-compression", "false", "--color-adaptation true"),
            ("color-curve-smoothing", "false", "--color-adaptation true"),
            ("color-output-lightness-compression", "false", "--color-adaptation true"),
            ("color-output-chroma-compression", "false", "--color-adaptation true"),
        };
        var previous = Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT");
        Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", root);
        try
        {
            var grid = NativeLutExporter.BuildGrid(size, default);
            var baselines = new Dictionary<string, byte[]>();
            var facts = new List<object>();
            await using var directRenderer = new OpticalRenderer(export: true);
            foreach (var (option, value, enables) in cases)
            {
                var original = string.Join(' ', primary, enables).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (!baselines.TryGetValue(enables, out var baseline))
                {
                    baseline = await Direct(NativeLutExporter.Prepare(original));
                    baselines.Add(enables, baseline);
                }
                var plan = NativeLutExporter.Prepare([.. original, "--" + option, value]);
                var pixels = await Direct(plan);
                var cube = await NativeLutExporter.ExportAsync(plan, size, 1, default);
                var samples = cube.Split('\n').Where(line => line.Length != 0 && (char.IsDigit(line[0]) || line[0] == '-'))
                    .SelectMany(line => line.Split(' ')).Select(part => double.Parse(part, CultureInfo.InvariantCulture)).ToArray();
                Check(samples.Length == size * size * size * 3, "full RGB sample count for " + option);
                double maximum = 0, change = 0;
                for (var index = 0; index < samples.Length; index++)
                {
                    var offset = (index / 3 * 4 + index % 3) * sizeof(float);
                    var direct = Math.Clamp(BitConverter.ToSingle(pixels, offset), 0, 1);
                    maximum = Math.Max(maximum, Math.Abs(samples[index] - direct));
                    change = Math.Max(change, Math.Abs(direct - Math.Clamp(BitConverter.ToSingle(baseline, offset), 0, 1)));
                }
                Check(double.IsFinite(maximum) && maximum <= 6e-9, $"exact pointwise LUT parity for {option}={value}");
                Check(change > 1d / ushort.MaxValue, $"the targeted pointwise control changes {option}={value}");
                facts.Add(new { option, value, enables, maximumGridError = maximum, maximumChangeFromBase = change });
                Console.WriteLine(FormattableString.Invariant($"{option}={value}: error {maximum:G4}, response {change:G4}"));
            }
            using var executable = File.OpenRead(Path.Combine(root, "renderer", "SpektraStillRenderer.exe"));
            using var file = new FileStream(output, FileMode.CreateNew);
            JsonSerializer.Serialize(file, new
            {
                size, cases = facts, exactGridChecks = checks,
                workerSha256 = Convert.ToHexString(SHA256.HashData(executable)),
                parameterContract = directRenderer.ParameterContract, assetContract = directRenderer.AssetContract,
            }, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine($"PASS: {checks} exact pointwise LUT sweep checks.");

            Task<byte[]> Direct(NativeLutExporter.Plan plan) => directRenderer.RenderAsync(size * size, size, grid, plan.Arguments, default);
        }
        finally { Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", previous); }
    }
}
