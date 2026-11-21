using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class StartupHook { internal static void Log(string message) { } }

internal static partial class Program
{
    private const string Contract = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static int checks;

    private static async Task Main(string[] args)
    {
        if (args.Length == 1 && args[0].EndsWith("stream")) { Worker(); return; }
        if (args.Length == 2 && args[0] == "--gpu") { await CheckGpu(args[1]); return; }
        if (args is ["--gpu-idle-reclamation", var idleRuntime, var adapter, var heap, var idleReport])
        {
            await CheckPhysicalIdleWorker(idleRuntime, Guid.ParseExact(adapter, "N"),
                int.Parse(heap, CultureInfo.InvariantCulture), idleReport);
            return;
        }
        if (args is ["--gpu-admission", var admissionRuntime, var admissionReport, var admissionScenario])
        {
            await CheckRealGpuAdmission(admissionRuntime, admissionReport, admissionScenario switch
            {
                "room" => false, "pressure" => true,
                _ => throw new ArgumentException("Use room or pressure for the GPU admission scenario."),
            });
            return;
        }
        if (args is ["--lut-sweep", var runtime, var report]) { await CheckLutSweep(runtime, report); return; }
        var scratch = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var previous = Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT");
        try
        {
            await CheckGpuAdmission();
            await CheckWorkerAdmission(scratch);
            await CheckIdleWorkerReclamation(scratch);
            foreach (var malformed in new[] { "status", "fatal", "wrong-request", "length", "truncated", "truncated-header", "error-size",
                "bad-error-text", "bad-device-lost", "bad-json", "old-version", "bad-json-type", "bad-contract", "bad-precision", "bad-endian", "bad-limits",
                "bad-reply-layout", "bad-float-layout", "bad-packed-layout" })
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
                await using var renderer = new OpticalRenderer(nativePreview: true);
                var pixels = Pixels(1);
                await renderer.RenderAsync(1, 1, pixels, [], default);
                var scenario = Path.Combine(root, "renderer", "scenario.txt");
                File.WriteAllText(scenario, "device-lost");
                await Throws<RendererDeviceLostException>(() => renderer.RenderAsync(1, 1, pixels, [], default));
                File.WriteAllText(scenario, "valid");
                var recovered = await renderer.RenderAsync(1, 1, pixels, [], default);
                Check(recovered.AsSpan().SequenceEqual(pixels), "the same complete source succeeds after a Vulkan device loss");
                var trace = File.ReadAllLines(Path.Combine(root, "renderer", "trace.txt"));
                Check(trace.Count(line => line.Contains(" FRAME ")) == 2 && trace.Count(line => line.Contains(" REUSE ")) == 1,
                    "a Vulkan device loss invalidates the old worker source identity");
            }
            {
                var root = Setup(scratch, "hang");
                await using var renderer = new OpticalRenderer();
                var pixels = Pixels(1);
                await Throws<RendererDeadlineException>(() =>
                    renderer.RenderAsync(1, 1, pixels, [], default).WaitAsync(TimeSpan.FromSeconds(27)));
                File.WriteAllText(Path.Combine(root, "renderer", "scenario.txt"), "valid");
                Check((await renderer.RenderAsync(1, 1, pixels, [], default)).Length == 16,
                    "a renderer that exceeds its real deadline is replaced before the same source can succeed");
                var trace = File.ReadAllLines(Path.Combine(root, "renderer", "trace.txt"));
                Check(trace.Count(line => line.Contains(":HELLO 2")) == 2 && trace.Count(line => line.Contains(" FRAME ")) == 2,
                    "deadline recovery negotiates a fresh worker and sends the complete source");
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
                Check(trace.Count(l => l.Contains(" REUSE 2 1")) == 1, "immutable frame reused with exact dimensions");
                var diagnostics = await renderer.ReadDiagnosticsAsync(default);
                Check(diagnostics?.GetProperty("schema").GetInt32() == 1, "versioned diagnostics");
                Check(renderer.ParameterContract == Contract, "negotiated contract");
                var requestIds = File.ReadLines(Path.Combine(root, "renderer", "trace.txt"))
                    .Where(l => l.Contains(":REQUEST ")).Select(l => ulong.Parse(l.Split(' ')[1])).ToArray();
                Check(requestIds.SequenceEqual(new ulong[] { 1, 2, 3, 4 }), "render, reuse and metrics share a monotonic sequence");
            }
            {
                var root = Setup(scratch, "valid");
                await using var renderer = new OpticalRenderer(nativePreview: true);
                var pixels = Pixels(1);
                string PathInRenderer(string name) => Path.Combine(root, "renderer", name);
                await renderer.RenderAsync(1, 1, pixels, [], default);
                File.WriteAllText(PathInRenderer("scenario.txt"), "wrong-request");
                await Throws<InvalidDataException>(() => renderer.RenderAsync(1, 1, pixels, [], default));
                File.WriteAllText(PathInRenderer("scenario.txt"), "valid");
                await renderer.RenderAsync(1, 1, pixels, [], default);
                File.WriteAllText(PathInRenderer("scenario.txt"), "wrong-metrics-request");
                await Throws<InvalidDataException>(() => renderer.ReadDiagnosticsAsync(default));
                File.WriteAllText(PathInRenderer("scenario.txt"), "valid");
                await renderer.RenderAsync(1, 1, pixels, [], default);
                var trace = File.ReadAllLines(PathInRenderer("trace.txt"));
                Check(trace.Count(l => l.Contains(" FRAME ")) == 3 && trace.Count(l => l.Contains(" REUSE ")) == 1,
                    "corrupt frame and diagnostic envelopes discard all remembered source data");
                Check(trace.Count(l => l.Contains(":HELLO 2")) == 3, "corrupt replies restart and negotiate a new worker");
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
            foreach (var scenario in new[] { "hang", "hang-upload", "hang-reply" })
            {
                var root = Setup(scratch, scenario);
                using var renderer = new OpticalRenderer(nativePreview: true);
                using var cancel = new CancellationTokenSource();
                var edge = scenario == "hang-upload" ? 512 : 1;
                var frame = new byte[edge * edge * 16];
                var render = renderer.RenderAsync(edge, edge, frame, [], cancel.Token);
                await WaitForRequest(root, render);
                cancel.Cancel();
                await Throws<OperationCanceledException>(() => render.WaitAsync(TimeSpan.FromSeconds(10)));
                File.WriteAllText(Path.Combine(root, "renderer", "scenario.txt"), "valid");
                await renderer.RenderAsync(1, 1, Pixels(1), [], default);
                Check(File.ReadAllLines(Path.Combine(root, "renderer", "trace.txt")).Count(l => l.Contains("HELLO")) == 2, $"cancel restarts and renegotiates after {scenario}");
            }
            foreach (var scenario in new[] { "cooperative", "cooperative-late-reply", "cooperative-stuck" })
            {
                var root = Setup(scratch, scenario);
                await using var renderer = new OpticalRenderer(nativePreview: true);
                using var cancel = new CancellationTokenSource();
                var pixels = Pixels(2);
                var render = renderer.RenderAsync(2, 1, pixels, [], cancel.Token);
                await WaitForRequest(root, render);
                var started = Stopwatch.GetTimestamp();
                cancel.Cancel();
                await Throws<OperationCanceledException>(() => render.WaitAsync(TimeSpan.FromSeconds(5)));
                Check(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(4), "bounded cancellation latency in " + scenario);
                File.WriteAllText(Path.Combine(root, "renderer", "scenario.txt"), "valid");
                var verified = await renderer.RenderAsync(2, 1, pixels, [], default);
                var trace = File.ReadAllLines(Path.Combine(root, "renderer", "trace.txt"));
                Check(verified.SequenceEqual(pixels) && trace.Count(l => l.Contains(" FRAME ")) == 2,
                    "canceled frame cannot become the reused source in " + scenario);
                Check(trace.Count(l => l.Contains(":HELLO")) == (scenario == "cooperative-stuck" ? 2 : 1),
                    "worker survives aligned reply and restarts on a stuck request in " + scenario);
            }
            foreach (var scenario in new[] { "cooperative-progress", "cooperative-progress-backwards" })
            {
                var root = Setup(scratch, scenario);
                var stages = new List<RendererStage>();
                await using var renderer = new OpticalRenderer { ReportProgress = stages.Add };
                if (scenario == "cooperative-progress")
                {
                    await renderer.RenderAsync(1, 1, Pixels(1), [], default);
                    Check(stages.SequenceEqual(new[] { RendererStage.SourceAccepted, RendererStage.Vulkan, RendererStage.Sending }),
                        "numbered, optional renderer stages do not alter final response bytes");
                }
                else
                {
                    await Throws<InvalidDataException>(() => renderer.RenderAsync(1, 1, Pixels(1), [], default));
                    File.WriteAllText(Path.Combine(root, "renderer", "scenario.txt"), "valid");
                    await renderer.RenderAsync(1, 1, Pixels(1), [], default);
                    Check(File.ReadAllLines(Path.Combine(root, "renderer", "trace.txt")).Count(l => l.Contains(":HELLO")) == 2,
                        "backwards renderer progress rejects the request and restarts the stream");
                }
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
            foreach (var manifest in new[] { "[]", "{\"rendererProtocol\":\"2\"}", "{\"rendererProtocol\":2,\"parameterContract\":\"wrong\"}",
                JsonSerializer.Serialize(new { rendererProtocol = 1, parameterContract = Contract }) })
            {
                var root = Setup(scratch, "valid");
                File.WriteAllText(Path.Combine(root, "build-manifest.json"), manifest);
                using var renderer = new OpticalRenderer();
                await Throws<InvalidDataException>(() => renderer.RenderAsync(1, 1, Pixels(1), [], default));
                WriteRuntimeManifest(root);
                await renderer.RenderAsync(1, 1, Pixels(1), [], default);
                Check(true, "manifest mismatch recoverable after corrected runtime set");
            }
            {
                var root = Setup(scratch, "valid");
                WriteRuntimeManifest(root);
                var asset = Path.Combine(root, "renderer", "shaders", "SpektraFilmExposure.comp.spv");
                var original = File.ReadAllBytes(asset);
                var altered = original.ToArray();
                altered[0] ^= 1;
                File.WriteAllBytes(asset, altered);
                await using var renderer = new OpticalRenderer();
                await Throws<InvalidDataException>(() => renderer.RenderAsync(1, 1, Pixels(1), [], default));
                var trace = Path.Combine(root, "renderer", "trace.txt");
                Check(File.ReadAllLines(trace).All(line => !line.Contains(":REQUEST ")), "modified shader rejected before an image is sent");
                File.WriteAllBytes(asset, original);
                await renderer.RenderAsync(1, 1, Pixels(1), [], default);
                Check(renderer.AssetContract is { Length: 64 }, "valid runtime produces a pinned asset fingerprint");
                var provenance = RuntimeAssets.ReadPresetIdentity(root);
                Check(provenance is { IsValid: true } && provenance.AssetSha256 == renderer.AssetContract
                    && provenance.ParameterSha256 == Contract, "preset identity uses the same verified files and parameter contract");
                var plan = NativeLutExporter.Prepare([]);
                var cube = await NativeLutExporter.ExportAsync(plan, 33, 1, default);
                Check(cube.Contains(renderer.AssetContract!, StringComparison.Ordinal), "native LUT records the exact verified runtime assets");
            }
            {
                var root = Setup(scratch, "valid");
                File.WriteAllText(Path.Combine(root, "runtime-manifest.required"), "");
                await using var renderer = new OpticalRenderer();
                await Throws<InvalidDataException>(() => renderer.RenderAsync(1, 1, Pixels(1), [], default));
                WriteRuntimeManifest(root, "profiles/preview/ProPhoto RGB.icc");
                await Throws<InvalidDataException>(() => renderer.RenderAsync(1, 1, Pixels(1), [], default));
                WriteRuntimeManifest(root);
                await renderer.RenderAsync(1, 1, Pixels(1), [], default);
                Check(renderer.AssetContract is not null, "complete runtime manifest can replace an incomplete one");
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

    private static async Task CheckGpuCancellation(string[] options)
    {
        var stages = new List<RendererStage>();
        TaskCompletionSource? rendering = null;
        await using var renderer = new OpticalRenderer(nativePreview: true)
        {
            ReportProgress = stage =>
            {
                stages.Add(stage);
                if (stage == RendererStage.Vulkan) rendering?.TrySetResult();
            },
        };
        var pixels = Pixels(64);
        var pristine = await renderer.RenderAsync(8, 8, pixels, options, default, displayPixels: true);
        Check(stages.SequenceEqual(Enum.GetValues<RendererStage>()), "actual Vulkan worker reports five truthful bounded stages");
        var child = typeof(OpticalRenderer).GetField("process", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        int Pid() => ((Process)child.GetValue(renderer)!).Id;
        var process = Pid();
        stages.Clear();
        rendering = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        var width = 1920;
        var height = 1280;
        var frame = Pixels(width * height);
        var active = renderer.RenderAsync(width, height, frame, [.. options, "--halation-amount", "1", "--halation-boost", "2"],
            stop.Token, displayPixels: true);
        await rendering.Task.WaitAsync(TimeSpan.FromSeconds(12));
        var started = Stopwatch.GetTimestamp();
        stop.Cancel();
        await Throws<OperationCanceledException>(() => active.WaitAsync(TimeSpan.FromSeconds(10)));
        var cancelMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        Check(child.GetValue(renderer) is Process survived && survived.Id == process && !survived.HasExited,
            "actual canceled Vulkan request retains its warm worker after bounded acknowledgement");
        rendering = null;
        var recovered = await renderer.RenderAsync(8, 8, pixels, options, default, displayPixels: true);
        Check(recovered.SequenceEqual(pristine) && Pid() == process, "the next actual Vulkan request matches the uncanceled pixels in the same process");
        Console.WriteLine(FormattableString.Invariant($"Physical Vulkan cancellation acknowledged after {cancelMs:F2} ms, warm worker retained."));
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

    private static async Task CheckGpuAdmission()
    {
        var coordinator = new GpuRenderAdmission();
        var adapter = Guid.NewGuid();
        var heap = new GpuHeapIdentity(adapter, 1);
        GpuRenderPlan Plan(long amount) => new(heap, 100, amount);
        using var first = await coordinator.AcquireAsync(Plan(60), GpuWorkPriority.Interactive, default);
        using var second = await coordinator.AcquireAsync(Plan(20), GpuWorkPriority.Interactive, default);
        using var cancellation = new CancellationTokenSource();
        var warm = coordinator.AcquireAsync(Plan(70), GpuWorkPriority.Warmup, default);
        var interactive = coordinator.AcquireAsync(Plan(60), GpuWorkPriority.Interactive, default);
        var output = coordinator.AcquireAsync(Plan(100), GpuWorkPriority.Output, default);
        var cancelled = coordinator.AcquireAsync(Plan(100), GpuWorkPriority.Output, cancellation.Token);
        Check(!output.IsCompleted && !interactive.IsCompleted && !warm.IsCompleted, "same-heap estimates only overlap inside their planning budget");
        using var otherHeap = await coordinator.AcquireAsync(new(new(adapter, 0), 100, 100), GpuWorkPriority.Output, default);
        using var otherAdapter = await coordinator.AcquireAsync(new(new(Guid.NewGuid(), 1), 100, 100), GpuWorkPriority.Interactive, default);
        Check(!output.IsCompleted, "independent adapter and heap do not wait on the original queue");
        cancellation.Cancel();
        await Throws<OperationCanceledException>(() => cancelled);
        first.Dispose();
        Check(!interactive.IsCompleted && !warm.IsCompleted && !output.IsCompleted, "smaller waiting work cannot overtake an accepted output");
        second.Dispose();
        using var outputLease = await output.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!interactive.IsCompleted && !warm.IsCompleted, "accepted output has first priority");
        outputLease.Dispose();
        using var interactiveLease = await interactive.WaitAsync(TimeSpan.FromSeconds(2));
        Check(!warm.IsCompleted, "interactive work goes ahead of an earlier optional warmup");
        interactiveLease.Dispose();
        using var warmLease = await warm.WaitAsync(TimeSpan.FromSeconds(2));
        var oversized = coordinator.AcquireAsync(Plan(160), GpuWorkPriority.Output, default);
        Check(!oversized.IsCompleted, "a frame larger than the planning estimate budget waits for an empty heap queue");
        warmLease.Dispose();
        using var oversizedLease = await oversized.WaitAsync(TimeSpan.FromSeconds(2));
        var next = coordinator.AcquireAsync(Plan(10), GpuWorkPriority.Interactive, default);
        Check(!next.IsCompleted, "the admitted solitary frame uses the worker's normal tile policy");
        oversizedLease.Dispose();
        using var last = await next.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task CheckWorkerAdmission(string scratch)
    {
        var firstRoot = Setup(scratch, "gpu-plan-gated");
        await using var active = new OpticalRenderer(export: true);
        var first = active.RenderAsync(1, 1, Pixels(1), [], default);
        await WaitForRequest(firstRoot, first);
        var trace = File.ReadLines(Path.Combine(firstRoot, "renderer", "trace.txt")).First();
        using var activeProcess = Process.GetProcessById(int.Parse(trace.Split(':')[0], CultureInfo.InvariantCulture));

        var waitingRoot = Setup(scratch, "gpu-plan");
        var waitingFolder = Path.Combine(waitingRoot, "renderer");
        await using var waiting = new OpticalRenderer(nativePreview: true);
        using var stop = new CancellationTokenSource();
        var next = waiting.RenderAsync(1, 1, Pixels(1), [], stop.Token);
        var started = Stopwatch.GetTimestamp();
        while (!File.Exists(Path.Combine(waitingFolder, "plan-received")) && !next.IsCompleted
               && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
            await Task.Delay(10);
        Check(!next.IsCompleted && File.Exists(Path.Combine(waitingFolder, "plan-received"))
            && !File.Exists(Path.Combine(waitingFolder, "request-received")),
            "a separate worker negotiates its GPU plan before the active lease allows it to upload pixels");
        stop.Cancel();
        await Throws<OperationCanceledException>(() => next.WaitAsync(TimeSpan.FromSeconds(5)));
        Check(!first.IsCompleted, "cancelling a queued GPU request leaves the other worker's render active");
        var replacement = waiting.RenderAsync(1, 1, Pixels(1), [], default);
        await active.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        await Throws<OperationCanceledException>(() => first.WaitAsync(TimeSpan.FromSeconds(5)));
        Check(activeProcess.HasExited && (await replacement.WaitAsync(TimeSpan.FromSeconds(5))).Length == 16,
            "a queued worker can render after the retired worker actually exits");
    }

    private static async Task CheckIdleWorkerReclamation(string scratch)
    {
        var root = Setup(scratch, "gpu-plan-resident");
        var file = Path.Combine(root, "renderer", "trace.txt");
        var rgba = Pixels(1);
        await using var viewer = new OpticalRenderer(nativePreview: true);
        var baseline = await viewer.RenderAsync(1, 1, rgba, [], default);
        var initial = viewer.LastRender!.Value;
        using var process = Process.GetProcessById(initial.ProcessId);

        async Task Output(string scenario)
        {
            Setup(scratch, scenario);
            await using var worker = new OpticalRenderer(export: true);
            Check((await worker.RenderAsync(1, 1, rgba, [], default)).SequenceEqual(baseline),
                "idle residency checks preserve output bytes under " + scenario);
        }

        await Output("gpu-plan-headroom");
        await Output("gpu-plan-other-heap");
        Setup(scratch, "gpu-plan");
        await using (var warmup = new OpticalRenderer(nativePreview: true))
            Check((await warmup.WarmPreviewAsync(1, 1, rgba, [], default, displayPixels: false)).SequenceEqual(baseline),
                "speculative warmup renders without evicting another Viewer");
        Check(!process.HasExited && File.ReadLines(file).Count(line => line.Contains(" PLAN ")) == 1,
            "headroom, a different heap and optional warmup preserve the idle worker without probing its pipe");

        Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", root);
        var unchanged = await viewer.RenderAsync(1, 1, rgba, [], default);
        Check(unchanged.SequenceEqual(baseline) && viewer.LastRender is { SourceUploaded: false } warm
            && warm.ProcessId == initial.ProcessId, "a warm Viewer still reuses the immutable source");

        var viewerFolder = Path.Combine(root, "renderer");
        var viewerScenario = Path.Combine(viewerFolder, "scenario.txt");
        File.WriteAllText(viewerScenario, "gpu-plan-resident-gated");
        var activeViewer = viewer.RenderAsync(1, 1, rgba, [], default);
        await WaitForRequest(root, activeViewer);
        var pressureRoot = Setup(scratch, "gpu-plan");
        try
        {
            await using var waiting = new OpticalRenderer(export: true);
            using var cancel = new CancellationTokenSource();
            var pressure = waiting.RenderAsync(1, 1, rgba, [], cancel.Token);
            var planMarker = Path.Combine(pressureRoot, "renderer", "plan-received");
            var started = Stopwatch.GetTimestamp();
            while (!File.Exists(planMarker) && !pressure.IsCompleted && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
                await Task.Delay(10);
            Check(File.Exists(planMarker) && !pressure.IsCompleted && !activeViewer.IsCompleted && !process.HasExited,
                "a worker with a completed frame becomes ineligible for retirement while it is rendering another");
            cancel.Cancel();
            await Throws<OperationCanceledException>(() => pressure.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { File.WriteAllText(Path.Combine(viewerFolder, "release-response"), ""); }
        Check((await activeViewer.WaitAsync(TimeSpan.FromSeconds(5))).SequenceEqual(baseline) && !process.HasExited,
            "canceling the competing request keeps the active Viewer alive and intact");
        File.WriteAllText(viewerScenario, "gpu-plan-resident");
        var beforeProbe = File.ReadLines(file).Count(line => line.Contains(" PLAN "));
        await Output("gpu-plan");
        Check(process.HasExited && File.ReadLines(file).Count(line => line.Contains(" PLAN ")) == beforeProbe + 1,
            "a tight same-heap request verifies reclaimable bytes and waits for the idle Viewer process to exit");
        Check(viewer.LastRender?.ProcessId == initial.ProcessId, "idle reclamation retains the last completed frame receipt");
        Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", root);
        var regenerated = await viewer.RenderAsync(1, 1, rgba, [], default);
        Check(regenerated.SequenceEqual(baseline) && viewer.LastRender is { SourceUploaded: true } replacement
            && replacement.ProcessId != initial.ProcessId, "the next Viewer change starts a clean worker and renders the same pixels");
    }

    private static async Task CheckPhysicalIdleWorker(string runtime, Guid adapter, int heap, string report)
    {
        const int width = 3000, height = 2000;
        var previous = Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT");
        var root = Path.GetFullPath(runtime);
        Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", root);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            await using var viewer = new OpticalRenderer(nativePreview: true);
            var source = Pixels(width * height);
            var options = new[] { "--stock", "kodak_portra_400", "--paper", "kodak_portra_endura",
                "--camera-diffusion-family", "pro_mist", "--camera-diffusion-strength", "1.5", "--halation-amount", "1" };
            Task<byte[]> Render() => viewer.RenderAsync(width, height, source, options, deadline.Token, displayPixels: true);
            var expected = Convert.ToHexString(SHA256.HashData(await Render()));
            using var child = Process.GetProcessById(viewer.LastRender!.Value.ProcessId);
            var start = Stopwatch.GetTimestamp();
            var warm = await Render();
            var warmMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Check(viewer.LastRender is { SourceUploaded: false } warmReceipt && warmReceipt.ProcessId == child.Id
                && Convert.ToHexString(SHA256.HashData(warm)) == expected, "the physical Viewer reuses its source before pressure");

            var fake = Setup(Path.GetDirectoryName(Path.GetFullPath(report))!, "gpu-plan");
            File.WriteAllText(Path.Combine(fake, "renderer", "gpu-target.txt"), $"{adapter:N}\n{heap}");
            await using (var competing = new OpticalRenderer(export: true))
                await competing.RenderAsync(1, 1, Pixels(1), [], deadline.Token);
            Check(child.HasExited, "the GPU-backed Viewer worker exits before the synthetic same-heap pressure request completes");
            var requestTrace = File.ReadAllLines(Path.Combine(fake, "renderer", "trace.txt"));
            Check(requestTrace.Count(line => line.Contains(" PLAN ")) == 2, "the requester refreshes its plan after the physical worker exits");

            Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", root);
            start = Stopwatch.GetTimestamp();
            var after = await Render();
            var restoredMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Check(viewer.LastRender is { SourceUploaded: true } replacement && replacement.ProcessId != child.Id
                && Convert.ToHexString(SHA256.HashData(after)) == expected, "a fresh GPU worker delivers an identical packed reply after reclamation");
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                adapter, heap, width, height, expected, warmMs, restoredMs,
                physicalViewerRetired = child.HasExited, syntheticPressure = true,
            }));
            Console.WriteLine($"PASS: {checks} physical idle worker reclamation assertions.");
        }
        finally { Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", previous); }
    }

    private static async Task CheckRealGpuAdmission(string root, string report, bool pressure)
    {
        const int viewerWidth = 3000, viewerHeight = 2000;
        var (exportWidth, exportHeight) = pressure ? (9504, 6336) : (6000, 4000);
        var previousRoot = Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT");
        Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", Path.GetFullPath(root));
        try
        {
            var look = new[] { "--stock", "kodak_portra_400", "--paper", "kodak_portra_endura", "--film-format", "super8",
                "--halation-amount", "1", "--halation-boost", "1", "--scatter-amount", "0.5",
                "--camera-diffusion-family", "pro_mist", "--camera-diffusion-strength", "1.5",
                "--print-diffusion-family", "black_pro_mist", "--print-diffusion-strength", "1.5",
                "--dir-amount", "1.5", "--scanner-enabled", "true", "--grain-model", "production" };
            var viewerSource = Pixels(viewerWidth * viewerHeight);
            var exportSource = Pixels(exportWidth * exportHeight);
            using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            async Task<string> View(OpticalRenderer viewer, string[]? options = null) => Convert.ToHexString(SHA256.HashData(
                await viewer.RenderAsync(viewerWidth, viewerHeight, viewerSource, options ?? look, stop.Token, displayPixels: true)));
            async Task<string> Export(OpticalRenderer exporter) => Convert.ToHexString(SHA256.HashData(
                await exporter.RenderAsync(exportWidth, exportHeight, exportSource, look, stop.Token)));
            string expectedViewer, expectedExport;
            await using (var serial = new OpticalRenderer(nativePreview: true)) expectedViewer = await View(serial);
            await using (var serial = new OpticalRenderer(export: true)) expectedExport = await Export(serial);

            var exportOnGpu = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var releaseDelivery = new ManualResetEventSlim();
            await using var activeExport = new OpticalRenderer(export: true)
            {
                ReportProgress = stage =>
                {
                    if (stage == RendererStage.Vulkan)
                    {
                        exportOnGpu.TrySetResult();
                        if (!releaseDelivery.Wait(TimeSpan.FromSeconds(10), stop.Token))
                            throw new TimeoutException("The GPU admission fixture did not release export delivery.");
                    }
                }
            };
            await using var waitingViewer = new OpticalRenderer(nativePreview: true);
            await View(waitingViewer, [.. look, "--film-exposure", "0.1"]);
            var exportJob = Export(activeExport);
            Task<string> viewerJob;
            try
            {
                await exportOnGpu.Task.WaitAsync(stop.Token);
                viewerJob = View(waitingViewer);
                if (pressure)
                {
                    // Keep the export's unread response in flight while the
                    // warm Viewer obtains a plan without starting a device.
                    await Task.Delay(250, stop.Token);
                    Check(!viewerJob.IsCompleted, "a real managed Viewer cannot bypass a held same-heap export lease under pressure");
                }
                else
                {
                    await viewerJob.WaitAsync(TimeSpan.FromSeconds(8), stop.Token);
                    Check(!exportJob.IsCompleted, "an independent same-heap Viewer finishes while export is still active when both estimates fit");
                }
            }
            finally { releaseDelivery.Set(); }
            var actual = await Task.WhenAll(exportJob, viewerJob);
            Check(actual.SequenceEqual(new[] { expectedExport, expectedViewer }), "shared admission preserves every GPU reply byte");
            var wait = waitingViewer.LastRender?.GpuWaitMs;
            Check(pressure ? wait is > 20 : wait is >= 0 and < 20, "the real Viewer wait agrees with its shared heap admission");
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                viewerWidth, viewerHeight, exportWidth, exportHeight, viewerWaitMs = wait,
                viewerReplySha256 = actual[1], exportReplySha256 = actual[0], packedAndFloatSerialParity = true,
                scenario = pressure ? "pressure" : "room",
                minimumExportResponseHoldMs = pressure ? 250 : 0,
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS: {checks} physical GPU admission assertions.");
        }
        finally { Environment.SetEnvironmentVariable("SPEKTRAFILM_ROOT", previousRoot); }
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
                var packed = await renderer.RenderAsync(8, 8, source, options, default, displayPixels: true);
                if (File.Exists(Path.Combine(root, "build-manifest.json")))
                {
                    Check(renderer.AssetContract is { Length: 64 }, "the actual worker passed verified runtime asset admission");
                    Console.WriteLine("Verified runtime asset SHA-256 " + renderer.AssetContract);
                }
                var metrics = await renderer.ReadDiagnosticsAsync(default);
                Check(metrics?.GetProperty("schema").GetInt32() == 1, "GPU diagnostics protocol");
                Check(metrics?.GetProperty("renderSequence").GetInt32() == 1
                    && metrics?.GetProperty("readbackBytes").GetInt64() == 8 * 8 * 4 + sizeof(uint),
                    "fully packed Vulkan output readback is one word per pixel plus a validation word");
                var packedAgain = await renderer.RenderAsync(8, 8, source, options, default, displayPixels: true);
                metrics = await renderer.ReadDiagnosticsAsync(default);
                Check(packedAgain.SequenceEqual(packed) && metrics?.GetProperty("renderSequence").GetInt32() == 1,
                    "native unchanged packed request cached");
                clean = await renderer.RenderAsync(8, 8, source, options, default);
                metrics = await renderer.ReadDiagnosticsAsync(default);
                Check(metrics?.GetProperty("renderSequence").GetInt32() == 2
                    && metrics?.GetProperty("readbackBytes").GetInt64() == source.Length,
                    "switching to float output preserves full precision and reuses the accepted source");
                await renderer.RenderAsync(8, 8, source, options, default);
                metrics = await renderer.ReadDiagnosticsAsync(default);
                Check(metrics?.GetProperty("renderSequence").GetInt32() == 2, "native unchanged float request cached");
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
                using var cancellation = new CancellationTokenSource();
                var largerSource = Pixels(1600 * 1200);
                var pending = renderer.RenderAsync(1600, 1200, largerSource, options, cancellation.Token, displayPixels: true);
                cancellation.CancelAfter(10);
                await Throws<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10)));
                var recovered = await renderer.RenderAsync(8, 8, source, options, default, displayPixels: true);
                metrics = await renderer.ReadDiagnosticsAsync(default);
                Check(recovered.SequenceEqual(packed) && metrics?.GetProperty("renderSequence").GetInt32() is >= 1,
                    "a canceled packed Vulkan request admits an exact fresh frame");
            }
            await CheckGpuCancellation(options);
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

        foreach (var method in new[] { "hanatos2025", "mallett2019", "hanatos2026" })
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

    private static void WriteRuntimeManifest(string root, string? skip = null)
    {
        var records = RuntimeAssets.RequiredFiles.Where(path => path != skip).Select(path =>
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (!File.Exists(full)) File.WriteAllText(full, "inert fixture " + path);
            using var stream = File.OpenRead(full);
            return new { path, bytes = stream.Length, sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant() };
        }).ToArray();
        File.WriteAllText(Path.Combine(root, "build-manifest.json"),
            JsonSerializer.Serialize(new { rendererProtocol = RendererProtocol.Version, parameterContract = Contract, files = records,
                source = new { commit = new string('b', 40), treeDigest = new string('c', 64) } }));
    }

    private static byte[] Pixels(int count)
    {
        var bytes = new byte[count * 16];
        var samples = MemoryMarshal.Cast<byte, float>(bytes.AsSpan());
        for (var i = 0; i < samples.Length; i++) samples[i] = i % 4 == 3 ? 1f : .2f;
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
                const string resourceKey = "SPEKTRAFILM_RESOURCE_DIR";
                if (!string.Equals(Environment.GetEnvironmentVariable(resourceKey), Environment.CurrentDirectory, StringComparison.OrdinalIgnoreCase)
                    || Environment.GetEnvironmentVariables().Keys.Cast<string>().Any(n => n.StartsWith("SPEKTRAFILM_", StringComparison.OrdinalIgnoreCase)
                        && !n.Equals(resourceKey, StringComparison.OrdinalIgnoreCase)
                        && n is not ("SPEKTRAFILM_REQUEST_CANCEL_EVENT" or "SPEKTRAFILM_REQUEST_PROGRESS") || n == "DOTNET_STARTUP_HOOKS")
                    || (Environment.GetEnvironmentVariable("SPEKTRAFILM_REQUEST_CANCEL_EVENT") is { } eventName
                        && !eventName.StartsWith(@"Local\Spektrafilm.Request.", StringComparison.Ordinal)))
                    File.AppendAllText("trace.txt", "ENV_BAD\n");
                var json = scenario switch
                {
                    "bad-json" => "broken-json",
                    "bad-json-type" => "{\"protocol\":\"2\"}",
                    _ => JsonSerializer.Serialize(new { protocol = scenario == "old-version" ? 1 : 2, floatChannels = 4,
                        floatBits = scenario == "bad-precision" ? 16 : 32, littleEndian = scenario != "bad-endian",
                        requestHeaderBytes = scenario == "bad-reply-layout" ? 8 : 16,
                        floatLayout = scenario == "bad-float-layout" ? "bgra32f" : "rgba32f",
                        packedLayout = scenario == "bad-packed-layout" ? "rgba8" : "bgra8",
                        maxSide = scenario == "limited-dimensions" ? 1 : 16384,
                        maxPixels = scenario switch { "bad-limits" => 0, "limited-dimensions" => 1, _ => 100_000_000 },
                        packedDisplay = scenario != "no-packed",
                        requestCancellation = scenario.StartsWith("cooperative", StringComparison.Ordinal),
                        gpuPlanSchema = scenario.StartsWith("gpu-plan", StringComparison.Ordinal) ? 1 : 0,
                        frameProgress = scenario.StartsWith("cooperative-progress", StringComparison.Ordinal)
                            && Environment.GetEnvironmentVariable("SPEKTRAFILM_REQUEST_PROGRESS") == "1",
                        parameterContract = scenario == "bad-contract" ? new string('z', 64) : Contract }),
                };
                Reply(output, 0, Encoding.UTF8.GetBytes(json)); continue;
            }
            if (tokens[0] != "REQUEST") return;
            var request = ulong.Parse(tokens[1], CultureInfo.InvariantCulture);
            var verb = tokens[2];
            if (verb == "PLAN")
            {
                var target = File.Exists("gpu-target.txt") ? File.ReadAllLines("gpu-target.txt") : null;
                var estimate = scenario switch { "gpu-plan-resident" or "gpu-plan-resident-gated" => 35, "gpu-plan-headroom" => 45, _ => 80 };
                var gpuPlan = new
                {
                    schema = 1, adapter = target?[0] ?? "3980bc7b9d534ba3ba9f62b5f7cb97c4",
                    heap = target is not null ? int.Parse(target[1], CultureInfo.InvariantCulture) : scenario == "gpu-plan-other-heap" ? 1 : 0,
                    sharedPlanningBytes = 100, estimatedFullFrameBytes = estimate,
                    reclaimableBytes = scenario == "gpu-plan-resident" && pixels.Length != 0 ? 50 : 0,
                };
                Reply(output, 0, JsonSerializer.SerializeToUtf8Bytes(gpuPlan), request: request);
                File.WriteAllText("plan-received", "");
                continue;
            }
            if (verb == "METRICS")
            {
                Reply(output, 0, "{\"schema\":1}"u8.ToArray(), request: scenario == "wrong-metrics-request" ? request + 1 : request);
                continue;
            }
            if (scenario == "hang-upload")
            {
                _ = input.ReadByte();
                File.WriteAllText("request-received", "");
                Thread.Sleep(10000);
                return;
            }
            if (verb.StartsWith("FRAME"))
            {
                pixels = new byte[int.Parse(tokens[3]) * int.Parse(tokens[4]) * 16];
                input.ReadExactly(pixels);
            }
            if (scenario.StartsWith("cooperative-progress", StringComparison.Ordinal))
            {
                var stages = scenario.EndsWith("backwards", StringComparison.Ordinal) ? new byte[] { 3, 2 } : new byte[] { 1, 3, 5 };
                foreach (var stage in stages) Reply(output, RendererProtocol.Progress, [stage], request: request);
                Reply(output, 0, pixels, request: request);
                continue;
            }
            if (scenario.StartsWith("cooperative", StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows()) return;
                using var eventHandle = EventWaitHandle.OpenExisting(Environment.GetEnvironmentVariable("SPEKTRAFILM_REQUEST_CANCEL_EVENT")!);
                File.WriteAllText("request-received", "");
                if (!eventHandle.WaitOne(TimeSpan.FromSeconds(10))) return;
                if (scenario == "cooperative-stuck") { Thread.Sleep(10000); return; }
                if (scenario == "cooperative") Reply(output, RendererProtocol.Canceled, [], request: request);
                else Reply(output, 0, pixels, request: request);
                continue;
            }
            if (scenario == "hang-reply")
            {
                Reply(output, 0, pixels[..4], pixels.Length, request);
                File.WriteAllText("request-received", "");
                Thread.Sleep(10000);
                return;
            }
            if (scenario is "hang" or "gated" or "gpu-plan" or "gpu-plan-gated" or "gpu-plan-resident-gated")
                File.WriteAllText("request-received", "");
            if (scenario == "hang") { Thread.Sleep(10000); continue; }
            if (scenario is "gated" or "gpu-plan-gated" or "gpu-plan-resident-gated")
            {
                var started = Stopwatch.GetTimestamp();
                while (!File.Exists("release-response") && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
                    Thread.Sleep(10);
                if (!File.Exists("release-response")) return;
            }
            switch (scenario)
            {
                case "status": Reply(output, 99, pixels, request: request); break;
                case "fatal": Reply(output, 2, "Invalid envelope"u8.ToArray(), request: request); break;
                case "device-lost": Reply(output, RendererProtocol.DeviceLost, "VK_ERROR_DEVICE_LOST"u8.ToArray(), request: request); break;
                case "bad-device-lost": Reply(output, RendererProtocol.DeviceLost, [], request: request); break;
                case "wrong-request": Reply(output, 0, pixels, request: request + 1); break;
                case "length": Reply(output, 0, new byte[pixels.Length + 1], request: request); break;
                case "error-size": Reply(output, 1, new byte[65536], request: request); break;
                case "bad-error-text": Reply(output, 1, [0xff], request: request); break;
                case "truncated": Reply(output, 0, [], pixels.Length, request); return;
                case "truncated-header": Reply(output, 0, [], pixels.Length); return;
                case "valid-error": Reply(output, 1, "Invalid stock"u8.ToArray(), request: request); break;
                default: Reply(output, 0, pixels, request: request); break;
            }
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
    private static void Reply(Stream output, int status, byte[] bytes, int? length = null, ulong? request = null)
    {
        var header = new byte[request.HasValue ? 16 : 8];
        BinaryPrimitives.WriteInt32LittleEndian(header, status);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), length ?? bytes.Length);
        if (request is { } number) BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), number);
        output.Write(header); output.Write(bytes); output.Flush();
    }
}
