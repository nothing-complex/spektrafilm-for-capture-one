using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

internal static class StartupHook { internal static void Log(string message) { } }

internal static class Program
{
    private const string Contract = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static int checks;

    private static async Task Main(string[] args)
    {
        if (args.Length == 1 && args[0].EndsWith("stream")) { Worker(); return; }
        if (args.Length == 2 && args[0] == "--gpu") { await CheckGpu(args[1]); return; }
        var scratch = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var previous = Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT");
        try
        {
            foreach (var malformed in new[] { "status", "length", "truncated", "error-size", "bad-json", "old-version", "bad-json-type", "bad-contract", "bad-precision", "bad-endian", "bad-limits" })
            {
                var root = Setup(scratch, malformed);
                using var renderer = new OpticalRenderer(nativePreview: true);
                await Throws<Exception>(() => renderer.RenderAsync(1, 1, Pixels(1), [], default));
                File.WriteAllText(Path.Combine(root, "renderer", "scenario.txt"), "valid");
                var frame = await renderer.RenderAsync(1, 1, Pixels(1), [], default);
                Check(frame.Length == 16, "fresh frame after " + malformed);
                var trace = File.ReadAllLines(Path.Combine(root, "renderer", "trace.txt"));
                Check(trace.Count(l => l.Contains("HELLO")) == 2, "renegotiated " + malformed);
                Check(trace.Select(l => l.Split(':')[0]).Distinct().Count() == 2, "worker replaced " + malformed);
            }
            {
                var root = Setup(scratch, "valid");
                using var renderer = new OpticalRenderer(nativePreview: true);
                var pixels = Pixels(2);
                await renderer.RenderAsync(2, 1, pixels, [], default);
                await renderer.RenderAsync(2, 1, pixels, [], default);
                await renderer.RenderAsync(1, 2, pixels, [], default);
                var trace = File.ReadAllLines(Path.Combine(root, "renderer", "trace.txt"));
                Check(trace.Count(l => l.Contains("HELLO")) == 1, "one negotiation per worker");
                Check(trace.Count(l => l.Contains("FRAME ")) == 2, "layout change replaces input");
                Check(trace.Count(l => l.EndsWith(":REUSE")) == 1, "immutable frame reused");
                var diagnostics = await renderer.ReadDiagnosticsAsync(default);
                Check(diagnostics?.GetProperty("schema").GetInt32() == 1, "versioned diagnostics");
                Check(renderer.ParameterContract == Contract, "negotiated contract");
            }
            {
                var root = Setup(scratch, "limited-dimensions");
                await using var renderer = new OpticalRenderer(export: true);
                await Throws<InvalidOperationException>(() => renderer.RenderAsync(2, 1, Pixels(2), [], default));
                var log = Path.Combine(root, "renderer", "trace.txt");
                Check(File.ReadAllLines(log).All(line => !line.Contains("FRAME")), "oversized input refused before sending its bytes");
                await renderer.RenderAsync(1, 1, Pixels(1), [], default);
                Check(File.ReadAllLines(log).Count(line => line.Contains("HELLO")) == 1, "negotiated size refusal preserves aligned stream");
            }
            {
                var root = Setup(scratch, "no-packed");
                await using var renderer = new OpticalRenderer(nativePreview: true);
                await Throws<InvalidOperationException>(() => renderer.RenderAsync(1, 1, Pixels(1), [], default, displayPixels: true));
                await renderer.RenderAsync(1, 1, Pixels(1), [], default);
                Check(!File.ReadAllText(Path.Combine(root, "renderer", "trace.txt")).Contains("FRAME8"),
                    "worker without packed mode still supports the float path");
            }
            {
                var root = Setup(scratch, "valid-error");
                using var renderer = new OpticalRenderer(nativePreview: true);
                await Throws<InvalidOperationException>(() => renderer.RenderAsync(1, 1, Pixels(1), [], default));
                File.WriteAllText(Path.Combine(root, "renderer", "scenario.txt"), "valid");
                await renderer.RenderAsync(1, 1, Pixels(1), [], default);
                var trace = File.ReadAllLines(Path.Combine(root, "renderer", "trace.txt"));
                Check(trace.Count(l => l.Contains("HELLO")) == 1, "valid error keeps synchronized worker");
                Check(trace.Count(l => l.Contains("FRAME ")) == 2, "valid error invalidates remembered source");
            }
            {
                var root = Setup(scratch, "hang");
                using var renderer = new OpticalRenderer(nativePreview: true);
                using var cancel = new CancellationTokenSource();
                var render = renderer.RenderAsync(1, 1, Pixels(1), [], cancel.Token);
                await WaitForRequest(root, render);
                cancel.Cancel();
                await Throws<OperationCanceledException>(() => render.WaitAsync(TimeSpan.FromSeconds(10)));
                File.WriteAllText(Path.Combine(root, "renderer", "scenario.txt"), "valid");
                await renderer.RenderAsync(1, 1, Pixels(1), [], default);
                Check(File.ReadAllLines(Path.Combine(root, "renderer", "trace.txt")).Count(l => l.Contains("HELLO")) == 2, "cancel restarts and renegotiates");
            }
            {
                var root = Setup(scratch, "valid");
                string PathInRenderer(string name) => Path.Combine(root, "renderer", name);
                int LastWorker() => int.Parse(File.ReadLines(PathInRenderer("trace.txt")).Last().Split(':')[0]);
                await using var idle = new OpticalRenderer(nativePreview: true);
                await idle.RenderAsync(1, 1, Pixels(1), [], default);
                using var idleProcess = Process.GetProcessById(LastWorker());
                File.Delete(PathInRenderer("request-received"));
                File.WriteAllText(PathInRenderer("scenario.txt"), "hang");

                await using var busy = new OpticalRenderer(nativePreview: true);
                var rendering = busy.RenderAsync(1, 1, Pixels(1), [], default);
                await WaitForRequest(root, rendering);
                using var busyProcess = Process.GetProcessById(LastWorker());
                Check(!busyProcess.HasExited && !idleProcess.HasExited, "idle and busy worker fixture reached the resource barrier");
                var idleExit = idle.DisposeAsync().AsTask();
                var busyExit = busy.DisposeAsync().AsTask();
                var secondExit = busy.DisposeAsync().AsTask();
                await Task.WhenAll(idleExit, busyExit, secondExit).WaitAsync(TimeSpan.FromSeconds(10));
                await Throws<OperationCanceledException>(() => rendering.WaitAsync(TimeSpan.FromSeconds(10)));
                Check(idleProcess.HasExited && busyProcess.HasExited, "awaited shutdown confirms both renderer processes exited");
                File.WriteAllText(PathInRenderer("scenario.txt"), "valid");
                await using var export = new OpticalRenderer(export: true);
                Check((await export.RenderAsync(1, 1, Pixels(1), [], default)).Length == 16,
                    "a replacement worker can start after preview resource release");
            }
            {
                var root = Setup(scratch, "gated");
                using var renderer = new OpticalRenderer(nativePreview: true);
                var pixels = Pixels(1);
                var first = renderer.RenderAsync(1, 1, pixels, [], default);
                await WaitForRequest(root, first);
                using var cancel = new CancellationTokenSource();
                var queued = renderer.RenderAsync(1, 1, pixels, [], cancel.Token);
                cancel.Cancel();
                await Throws<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(10)));
                Check(!first.IsCompleted, "queued cancellation did not disturb the in-flight request");
                File.WriteAllText(Path.Combine(root, "renderer", "release-response"), "");
                await first.WaitAsync(TimeSpan.FromSeconds(10));
                File.WriteAllText(Path.Combine(root, "renderer", "scenario.txt"), "valid");
                await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => renderer.RenderAsync(1, 1, pixels, [], default)));
                Check(File.ReadAllLines(Path.Combine(root, "renderer", "trace.txt")).Count(l => l.Contains("HELLO")) == 1, "queued cancellation/concurrent clients preserve stream");
            }
            {
                var root = Setup(scratch, "valid");
                using var renderer = new OpticalRenderer();
                await Throws<ArgumentException>(() => renderer.RenderAsync(1, 1, [], [], default));
                await Throws<ArgumentException>(() => renderer.RenderAsync(-1, 1, Pixels(1), [], default));
                await Throws<ArgumentException>(() => renderer.RenderAsync(1, 1, Pixels(1), ["--stock", "bad\nQUIT"], default));
                await Throws<ArgumentException>(() => renderer.RenderAsync(1, 1, Pixels(1), ["--stock"], default));
                Check(!File.Exists(Path.Combine(root, "renderer", "trace.txt")), "reject invalid request before launching child");
            }
            {
                var root = Setup(scratch, "valid");
                var names = new[] { "SPEKTRAFILM_VULKAN_COPY_PASS", "SPEKTRAFILM_VULKAN_GRAIN_PASS", "SPEKTRAFILM_RESOURCE_DIR", "DOTNET_STARTUP_HOOKS" };
                var old = names.Select(Environment.GetEnvironmentVariable).ToArray();
                try
                {
                    foreach (var name in names) Environment.SetEnvironmentVariable(name, "invalid-test-value");
                    using var renderer = new OpticalRenderer();
                    await renderer.RenderAsync(1, 1, Pixels(1), [], default);
                    Check(!File.ReadAllText(Path.Combine(root, "renderer", "trace.txt")).Contains("ENV_BAD"), "production child environment isolated");
                    Check(names.All(n => Environment.GetEnvironmentVariable(n) == "invalid-test-value"), "parent environment remains unchanged");
                }
                finally { for (var i = 0; i < names.Length; i++) Environment.SetEnvironmentVariable(names[i], old[i]); }
            }
            foreach (var manifest in new[] { "[]", "{\"rendererProtocol\":\"1\"}", "{\"rendererProtocol\":1,\"parameterContract\":\"wrong\"}" })
            {
                var root = Setup(scratch, "valid");
                File.WriteAllText(Path.Combine(root, "build-manifest.json"), manifest);
                using var renderer = new OpticalRenderer();
                await Throws<InvalidDataException>(() => renderer.RenderAsync(1, 1, Pixels(1), [], default));
                File.WriteAllText(Path.Combine(root, "build-manifest.json"), JsonSerializer.Serialize(new { rendererProtocol = 1, parameterContract = Contract }));
                await renderer.RenderAsync(1, 1, Pixels(1), [], default);
                Check(true, "manifest mismatch recoverable after corrected runtime set");
            }
            {
                Setup(scratch, "valid");
                var argsForLut = new[] { "--film-exposure", "1.2", "--input-color", "prophoto", "--output-color", "linear_rec709",
                    "--auto-exposure", "true", "--grain-model", "synthesis", "--halation-amount", "2", "--camera-diffusion-family", "pro_mist",
                    "--scanner-enabled", "true", "--scanner-glare", "0.1", "--enlarger-scale", "2" };
                var plan = NativeLutExporter.Prepare(argsForLut);
                var options = Enumerable.Range(0, plan.Arguments.Length / 2).ToDictionary(i => plan.Arguments[2 * i], i => plan.Arguments[2 * i + 1]);
                Check(options["--film-exposure"] == "1.2", "LUT retains pointwise creative settings");
                Check(options["--input-color"] == "srgb" && options["--output-color"] == "srgb", "LUT explicitly uses sRGB domain");
                Check(options["--grain-model"] == "off" && options["--dir-tail-weight"] == "0" && options["--scanner-glare"] == "0", "LUT disables nonpointwise effects");
                Check(plan.ExcludedEffects.Contains("automatic exposure") && plan.ExcludedEffects.Contains("scanner glare")
                    && plan.ExcludedEffects.Contains("film-plane framing"), "LUT reports active exclusions");
                Check(argsForLut[3] == "prophoto", "LUT does not modify source options");
                var cube = await NativeLutExporter.ExportAsync(plan, 33, 0, default);
                Check(cube.Contains("LUT_3D_SIZE 33") && cube.Contains("CC BY-SA 4.0") && cube.Contains("Andrea Volpato") && cube.Contains("Aedan Diez"), "LUT size and credits");
                var rows = cube.Split('\n').Where(l => l.Length > 0 && (char.IsDigit(l[0]) || l[0] == '-')).ToArray();
                Check(rows.Length == 33 * 33 * 33 && rows[0] == "0 0 0" && rows[^1] == "1 1 1", "LUT identity ordering/endpoints");
                Check(rows[1] == "0.03125 0 0" && rows[33] == "0 0.03125 0", "red varies fastest");
                var nativeCube = await NativeLutExporter.ExportAsync(plan, 33, 1, default);
                Check(nativeCube.Contains(Contract), "LUT uses negotiated native engine");
                var delivery = new SrgbDelivery(PreviewInputSpace.Srgb, "srgb", .5);
                Check(Math.Abs(delivery.GetSrgb(new[] { .2f, .2f, .2f }, new[] { .8f, .8f, .8f }, 0).Red - .5999308) < .00001,
                    "LUT and image delivery blend in linear light");
                foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                {
                    var samples = NativeLutExporter.BuildGrid(2, default);
                    BitConverter.GetBytes(invalid).CopyTo(samples, 0);
                    await Throws<InvalidDataException>(() => Task.Run(() => NativeLutExporter.FormatCube(plan, 2, 1,
                        NativeLutExporter.BuildGrid(2, default), samples, Contract, default)));
                }
                await Throws<ArgumentOutOfRangeException>(() => NativeLutExporter.ExportAsync(plan, 129, 1, default));
                await Throws<ArgumentOutOfRangeException>(() => NativeLutExporter.ExportAsync(plan, 33, double.NaN, default));
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                await Throws<OperationCanceledException>(() => NativeLutExporter.ExportAsync(plan, 65, 1, canceled.Token));
            }
            Console.WriteLine($"PASS: {checks} renderer client checks; no GPU or Capture One required.");
        }
        finally { Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", previous); }
    }

    private static async Task WaitForRequest(string root, Task render)
    {
        var marker = Path.Combine(root, "renderer", "request-received");
        var started = Stopwatch.GetTimestamp();
        while (!File.Exists(marker))
        {
            if (render.IsCompleted)
            {
                await render;
                throw new InvalidOperationException("Fake renderer completed before the request checkpoint.");
            }
            if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(10))
                throw new TimeoutException("Fake renderer did not receive its request.");
            await Task.Delay(10);
        }
    }

    private static async Task CheckGpu(string root)
    {
        var previousRoot = Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT");
        var previousDebug = Environment.GetEnvironmentVariable("SPEKTRAFILM_VULKAN_COPY_PASS");
        try
        {
            Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", Path.GetFullPath(root));
            var source = Pixels(64);
            var options = new[] { "--film-exposure", "2", "--stock", "kodak_portra_400", "--paper", "kodak_portra_endura" };
            byte[] clean;
            using (var renderer = new OpticalRenderer(nativePreview: true))
            {
                clean = await renderer.RenderAsync(8, 8, source, options, default);
                var metrics = await renderer.ReadDiagnosticsAsync(default);
                Check(metrics?.GetProperty("schema").GetInt32() == 1, "GPU diagnostics protocol");
                Check(metrics?.GetProperty("renderSequence").GetInt32() == 1, "GPU render counted");
                await renderer.RenderAsync(8, 8, source, options, default);
                metrics = await renderer.ReadDiagnosticsAsync(default);
                Check(metrics?.GetProperty("renderSequence").GetInt32() == 1, "native unchanged request cached");
                var packed = await renderer.RenderAsync(8, 8, source, options, default, displayPixels: true);
                var delivery = new SrgbDelivery(PreviewInputSpace.Srgb, "srgb", 1);
                var input = new float[source.Length / sizeof(float)];
                var rendered = new float[clean.Length / sizeof(float)];
                Buffer.BlockCopy(source, 0, input, 0, source.Length);
                Buffer.BlockCopy(clean, 0, rendered, 0, clean.Length);
                var expectedPacked = new byte[packed.Length];
                for (var index = 0; index < input.Length; index += 4)
                {
                    var rgb = delivery.GetSrgb(input, rendered, index);
                    expectedPacked[index] = (byte)Math.Round(rgb.Blue * 255);
                    expectedPacked[index + 1] = (byte)Math.Round(rgb.Green * 255);
                    expectedPacked[index + 2] = (byte)Math.Round(rgb.Red * 255);
                    expectedPacked[index + 3] = (byte)MathF.Round(Math.Clamp(input[index + 3], 0, 1) * 255);
                }
                Check(packed.SequenceEqual(expectedPacked), "worker packed BGRA and managed delivery round identically at full sRGB strength");
            }
            Environment.SetEnvironmentVariable("SPEKTRAFILM_VULKAN_COPY_PASS", "1");
            using (var renderer = new OpticalRenderer(nativePreview: true))
            {
                var isolated = await renderer.RenderAsync(8, 8, source, options, default);
                Check(clean.SequenceEqual(isolated), "inherited bypass flag cannot alter production render");
                Check(!source.SequenceEqual(isolated), "film still changes source");
            }
            var plan = NativeLutExporter.Prepare(options);
            var maximum = 0.0;
            foreach (var size in new[] { 33, 65 })
            {
                var cube = await NativeLutExporter.ExportAsync(plan, size, 1, default);
                var grid = NativeLutExporter.BuildGrid(size, default);
                byte[] expected;
                using (var renderer = new OpticalRenderer(export: true))
                    expected = await renderer.RenderAsync(size * size, size, grid, plan.Arguments, default);
                var rows = cube.Split('\n').Where(l => l.Length > 0 && (char.IsDigit(l[0]) || l[0] == '-')).ToArray();
                Check(rows.Length == size * size * size, "native LUT sample count " + size);
                for (var i = 0; i < rows.Length; i++)
                {
                    var rgb = rows[i].Split(' ').Select(v => double.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    for (var c = 0; c < 3; c++) maximum = Math.Max(maximum, Math.Abs(rgb[c] - Math.Clamp(BitConverter.ToSingle(expected, (i * 4 + c) * 4), 0, 1)));
                }
                Check(maximum <= 6e-9, "native LUT exact sampled-image parity " + size);
                if (size == 33)
                {
                    var mixed = NativeLutExporter.FormatCube(plan, size, .5, grid, expected, null, default);
                    var mixedRows = mixed.Split('\n').Where(line => line.Length > 0 && char.IsDigit(line[0])).ToArray();
                    var maximumMixed = 0d;
                    for (var index = 0; index < mixedRows.Length; index++)
                    {
                        var rgb = mixedRows[index].Split(' ').Select(value => double.Parse(value, CultureInfo.InvariantCulture)).ToArray();
                        for (var channel = 0; channel < 3; channel++)
                        {
                            var offset = (index * 4 + channel) * 4;
                            var sourceLinear = Decode(BitConverter.ToSingle(grid, offset));
                            var filmLinear = Decode(BitConverter.ToSingle(expected, offset));
                            var target = Encode((sourceLinear + filmLinear) / 2);
                            maximumMixed = Math.Max(maximumMixed, Math.Abs(rgb[channel] - target));
                        }
                    }
                    Console.WriteLine($"Half-strength LUT maximum error against the analytic transfer {maximumMixed:R}.");
                    Check(mixedRows.Length == rows.Length && maximumMixed < 0.5 / ushort.MaxValue,
                        "half-strength LUT matches an independent SDR linear mix within half a 16-bit code");
                }
            }
            Console.WriteLine($"Exact grid maximum code error {maximum:R}.");
            await TestHeldOutLut(options);
            Console.WriteLine($"PASS: {checks} GPU integration checks.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", previousRoot);
            Environment.SetEnvironmentVariable("SPEKTRAFILM_VULKAN_COPY_PASS", previousDebug);
        }

        static double Decode(double value)
        {
            var clipped = Math.Clamp(value, 0, 1);
            return clipped <= .04045 ? clipped / 12.92 : Math.Pow((clipped + .055) / 1.055, 2.4);
        }

        static double Encode(double value) => value <= .0031308 ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - .055;
    }

    private static async Task TestHeldOutLut(string[] options)
    {
        var colors = new float[1024 * 4];
        var random = new Random(707);
        for (var index = 0; index < 1024; index++)
        {
            for (var channel = 0; channel < 3; channel++)
                colors[index * 4 + channel] = (float)random.NextDouble();
            colors[index * 4 + 3] = 1;
        }
        var pixels = new byte[colors.Length * sizeof(float)];
        Buffer.BlockCopy(colors, 0, pixels, 0, pixels.Length);

        foreach (var method in new[] { "hanatos2025", "mallett2019" })
        {
            var plan = NativeLutExporter.Prepare(options.Concat(new[] { "--spectral-method", method }).ToArray());
            using var renderer = new OpticalRenderer(export: true);
            var direct = await renderer.RenderAsync(32, 32, pixels, plan.Arguments, default);
            var previousMean = double.PositiveInfinity;
            foreach (var size in new[] { 33, 65 })
            {
                var cube = await NativeLutExporter.ExportAsync(plan, size, 1, default);
                var samples = cube.Split('\n').Where(line => line.Length > 0 && (char.IsDigit(line[0]) || line[0] == '-'))
                    .SelectMany(line => line.Split(' ')).Select(text => float.Parse(text, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                var errors = new double[3072];
                for (var index = 0; index < 1024; index++)
                for (var channel = 0; channel < 3; channel++)
                {
                    var interpolated = Interpolate(samples, size, colors, index * 4, channel);
                    var expected = Math.Clamp(BitConverter.ToSingle(direct, (index * 4 + channel) * 4), 0, 1);
                    errors[index * 3 + channel] = Math.Abs(interpolated - expected);
                }
                Array.Sort(errors);
                var mean = errors.Average();
                var p95 = errors[(int)(errors.Length * .95) - 1];
                Console.WriteLine($"{method} {size}-point LUT held-out sRGB code error: mean {mean:G6}, p95 {p95:G6}, max {errors[^1]:G6}.");
                Check(p95 < .012 && errors[^1] < .09, "held-out interpolation error within bound: " + method + "/" + size);
                Check(mean < previousMean, "denser grid improves mean interpolation error: " + method + "/" + size);
                previousMean = mean;
            }
        }

        static double Interpolate(float[] samples, int size, float[] colors, int offset, int channel)
        {
            var (r, dr) = Cell(colors[offset], size);
            var (g, dg) = Cell(colors[offset + 1], size);
            var (b, db) = Cell(colors[offset + 2], size);
            double result = 0;
            for (var bz = 0; bz <= 1; bz++)
            for (var gy = 0; gy <= 1; gy++)
            for (var rx = 0; rx <= 1; rx++)
            {
                var weight = (bz == 0 ? 1 - db : db) * (gy == 0 ? 1 - dg : dg) * (rx == 0 ? 1 - dr : dr);
                result += weight * samples[3 * (((b + bz) * size + g + gy) * size + r + rx) + channel];
            }
            return result;
        }

        static (int Base, double Fraction) Cell(float color, int size)
        {
            var coordinate = (double)color * (size - 1);
            var lower = Math.Min(size - 2, (int)coordinate);
            return (lower, coordinate - lower);
        }
    }

    private static string Setup(string scratch, string scenario)
    {
        var root = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
        var worker = Path.Combine(root, "renderer");
        Directory.CreateDirectory(worker);
        var exe = Environment.ProcessPath!;
        File.Copy(exe, Path.Combine(worker, "SpektraStillRenderer.exe"));
        foreach (var path in Directory.EnumerateFiles(AppContext.BaseDirectory, "RendererContracts.*"))
            if (Path.GetExtension(path) != ".exe") File.Copy(path, Path.Combine(worker, Path.GetFileName(path)));
        File.WriteAllText(Path.Combine(worker, "scenario.txt"), scenario);
        Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", root);
        return root;
    }

    private static byte[] Pixels(int count)
    {
        var bytes = new byte[count * 16];
        for (var i = 0; i < count * 4; i++) BitConverter.GetBytes(i % 4 == 3 ? 1f : 0.2f).CopyTo(bytes, i * 4);
        return bytes;
    }
    private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { checks++; return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Worker()
    {
        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        byte[] pixels = [];
        while (ReadLine(input) is { } line)
        {
            var scenario = File.ReadAllText("scenario.txt");
            File.AppendAllText("trace.txt", Environment.ProcessId + ":" + line.TrimEnd() + "\n");
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens[0] == "HELLO")
            {
                if (Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(n => n.StartsWith("SPEKTRAFILM_", StringComparison.OrdinalIgnoreCase) || n == "DOTNET_STARTUP_HOOKS"))
                    File.AppendAllText("trace.txt", "ENV_BAD\n");
                var json = scenario switch
                {
                    "bad-json" => "broken-json",
                    "bad-json-type" => "{\"protocol\":\"1\"}",
                    _ => JsonSerializer.Serialize(new { protocol = scenario == "old-version" ? 0 : 1, floatChannels = 4,
                        floatBits = scenario == "bad-precision" ? 16 : 32, littleEndian = scenario != "bad-endian",
                        maxSide = scenario == "limited-dimensions" ? 1 : 16384,
                        maxPixels = scenario switch { "bad-limits" => 0, "limited-dimensions" => 1, _ => 100_000_000 },
                        packedDisplay = scenario != "no-packed",
                        parameterContract = scenario == "bad-contract" ? new string('z', 64) : Contract }),
                };
                Reply(output, 0, Encoding.UTF8.GetBytes(json)); continue;
            }
            if (tokens[0] == "METRICS") { Reply(output, 0, "{\"schema\":1}"u8.ToArray()); continue; }
            if (tokens[0].StartsWith("FRAME"))
            {
                pixels = new byte[int.Parse(tokens[1]) * int.Parse(tokens[2]) * 16];
                input.ReadExactly(pixels);
            }
            if (scenario is "hang" or "gated") File.WriteAllText("request-received", "");
            if (scenario == "hang") { Thread.Sleep(10000); continue; }
            if (scenario == "gated")
            {
                var started = Stopwatch.GetTimestamp();
                while (!File.Exists("release-response") && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
                    Thread.Sleep(10);
                if (!File.Exists("release-response")) return;
            }
            if (scenario == "status") Reply(output, 99, pixels);
            else if (scenario == "length") Reply(output, 0, new byte[pixels.Length + 1]);
            else if (scenario == "error-size") Reply(output, 1, new byte[65536]);
            else if (scenario == "truncated") { Reply(output, 0, [], pixels.Length); return; }
            else if (scenario == "valid-error") Reply(output, 1, "Invalid stock"u8.ToArray());
            else Reply(output, 0, pixels);
        }
    }
    private static string? ReadLine(Stream input)
    {
        var bytes = new List<byte>();
        for (int n; (n = input.ReadByte()) >= 0;)
        {
            if (n == 10) return Encoding.ASCII.GetString(bytes.ToArray());
            bytes.Add((byte)n);
        }
        return null;
    }
    private static void Reply(Stream output, int status, byte[] bytes, int? length = null)
    {
        var header = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(header, status);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), length ?? bytes.Length);
        output.Write(header); output.Write(bytes); output.Flush();
    }
}
