using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

public static partial class StartupHook
{
    private static IEnumerable<(Guid Id, Visual Painter)> NativeViewerNodes(DependencyObject node, FixtureSelection selection)
    {
        if (node is UIElement { IsVisible: false }) yield break;
        if (node.GetType().Name == "CompareCapableVariantGridItem")
        {
            var view = Field(node, "Viewer");
            if (view is UIElement { IsVisible: true } && Prop(view, "Variant") is { } variant &&
                Prop(view, "WpfViewer") is Visual painter)
                yield return (selection.Identity(variant), painter);
            yield break;
        }
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
            foreach (var view in NativeViewerNodes(VisualTreeHelper.GetChild(node, index), selection)) yield return view;
    }

    private sealed record NativeVisit(int Index, string Format, double NativeLayerMs, long ManagedBytes, long HostPrivateBytes,
        double LayerWidth, double LayerHeight);

    private static async Task<bool> TryRunNativeViewerBaseline(int visits)
    {
        var host = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly => assembly.GetName().Name == "CaptureOne");
        var program = host?.GetType("P1.C1.Program", true);
        var manager = program?.GetProperty("DocumentManager", Flags)?.GetValue(null);
        if (manager is not null && Prop(manager, "ActiveDocument") is { } document
            && ((string?)Prop(document, "Path")) == Environment.GetEnvironmentVariable("SPEKTRAFILM_CATALOG"))
        {
            var type = document.GetType().GetInterface("P1.C1.Core.ModelEntities.ICaptureOneDocument")!;
            if (type.GetProperty("CollectionsLoaded")!.GetValue(document) is not true) return false;
            var mainForm = program!.GetMethod("GetMainForm", Flags, null, new[] { type }, null)!;
            if (mainForm.Invoke(null, new[] { document }) is { } form && Prop(form, "Viewer") is { } viewer
                && Prop(viewer, "VariantGridView") is FrameworkElement { IsVisible: true } grid)
            {
                timer!.Stop();
                await MeasureNativeViewerNavigation(document, grid, visits);
                return true;
            }
        }
        return false;
    }

    private static async Task MeasureNativeViewerNavigation(object document, FrameworkElement grid, int visitCount)
    {
        using var host = Process.GetCurrentProcess();
        var selection = new FixtureSelection(document, panel: null);
        Check(selection.Variants.Length == 4, "native-only Viewer baseline uses the same four RAWs as the live Viewer probe");
        var samples = new List<NativeVisit>(visitCount);
        var interleaved = 0;
        for (var visit = 0; visit < visitCount; visit++)
        {
            if (visit % 10 == 7)
            {
                foreach (var offset in new[] { 2, 3, 1 })
                {
                    var transient = selection.Variants[(visit + offset) % selection.Variants.Length];
                    selection.Select(transient, transient);
                    await Task.Delay(25);
                    interleaved++;
                }
            }
            var variant = selection.Variants[visit % selection.Variants.Length];
            var started = Stopwatch.GetTimestamp();
            selection.Select(variant, variant);
            while (true)
            {
                var slot = NativeViewerNodes(grid, selection).SingleOrDefault(item => item.Id == variant);
                var image = slot.Painter is null ? null : ImageLayers(slot.Painter)
                    .FirstOrDefault(item => item.Width > 64 && item.Height > 64);
                if (image is not null)
                {
                    var capturedAt = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    host.Refresh();
                    samples.Add(new(visit + 1, selection.Format(variant), capturedAt, GC.GetTotalMemory(false),
                        host.PrivateMemorySize64, image.Width, image.Height));
                    break;
                }
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(18))
                    throw new TimeoutException($"Native-only visit {visit + 1} did not show an image layer for the selected variant.");
                await Task.Delay(16);
            }
        }

        host.Refresh();
        var beforeCollection = host.PrivateMemorySize64;
        Check(!AppDomain.CurrentDomain.GetAssemblies().Any(assembly => assembly.GetName().Name == "NativePanel"),
            "Capture One ran the complete navigation workload without loading the Spektrafilm plugin");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        host.Refresh();
        File.WriteAllText(Path.Combine(folder, "viewer-native-baseline.json"), JsonSerializer.Serialize(new
        {
            Visits = visitCount, InterleavedSelections = interleaved, DistinctSourceFormats = samples.Select(s => s.Format).Distinct().ToArray(),
            NativeSelectionToVisibleLayerMs = Percentiles(samples.Select(s => s.NativeLayerMs).ToArray()),
            PeakHostPrivateBytes = samples.Max(s => s.HostPrivateBytes), MaxManagedBytes = samples.Max(s => s.ManagedBytes),
            BeforeCollectionHostBytes = beforeCollection, AfterCollectionHostBytes = host.PrivateMemorySize64,
            PostCollectionManagedBytes = GC.GetTotalMemory(false), NoFilmDevelopmentOrRender = true, Samples = samples,
        }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} native baseline assertions; {visitCount} verified native image layers");
    }

    private sealed record ViewerVisit(int Index, string Format, int StrengthPercent, double SaveMs, double SettledPaintMs, long ManagedBytes,
        long HostPrivateBytes, long RendererPrivateBytes, double Scale, int PixelWidth, int PixelHeight, int SettingsGeneration);

    private sealed record ViewerMemorySample(int Visit, double ElapsedMs, long ManagedBytes, long HostPrivateBytes,
        long WorkerPrivateBytes, int LiveWorkers);

    private sealed class ViewerMemoryTrace(object spatial) : IAsyncDisposable
    {
        private readonly CancellationTokenSource lifetime = new();
        private int visit;
        private Task<List<ViewerMemorySample>>? work;

        internal void Start() => work = Task.Run(SampleAsync);
        internal void MarkVisit(int value) => Volatile.Write(ref visit, value);

        private async Task<List<ViewerMemorySample>> SampleAsync()
        {
            const int sampleIntervalMs = 50;
            using var host = Process.GetCurrentProcess();
            var workers = new Dictionary<int, Process>();
            var samples = new List<ViewerMemorySample>();
            var started = Stopwatch.GetTimestamp();
            var token = lifetime.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    ObserveWorker("renderer");
                    ObserveWorker("draftRenderer");
                    long privateBytes = 0;
                    foreach (var (id, worker) in workers.ToArray())
                    {
                        if (worker.HasExited)
                        {
                            worker.Dispose();
                            workers.Remove(id);
                            continue;
                        }
                        worker.Refresh();
                        try { privateBytes += worker.PrivateMemorySize64; }
                        catch (InvalidOperationException) when (worker.HasExited) { }
                    }
                    host.Refresh();
                    samples.Add(new(Volatile.Read(ref visit), Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                        GC.GetTotalMemory(false), host.PrivateMemorySize64, privateBytes, workers.Count));
                    await Task.Delay(sampleIntervalMs, token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally
            {
                foreach (var worker in workers.Values)
                    worker.Dispose();
            }
            return samples;

            void ObserveWorker(string field)
            {
                if (Field(spatial, field) is not { } renderer || Field(renderer, "process") is not Process owned)
                    return;
                try
                {
                    var id = owned.Id;
                    if (!workers.ContainsKey(id))
                        workers.Add(id, Process.GetProcessById(id));
                }
                catch (InvalidOperationException) { /* The owned worker was disposed between observations. */ }
                catch (ArgumentException) { /* The worker exited before it could be observed. */ }
            }
        }

        internal async Task<List<ViewerMemorySample>> FinishAsync()
        {
            lifetime.Cancel();
            return work is null ? new() : await work;
        }

        public async ValueTask DisposeAsync()
        {
            try { await FinishAsync(); }
            finally { lifetime.Dispose(); }
        }
    }

    private static async Task MeasureViewerChurn(object panel, object spatial, object store, int visitCount)
    {
        using var host = Process.GetCurrentProcess();
        var selection = new FixtureSelection(panel);
        Check(selection.Variants.Length == 4, "Viewer memory and latency probe uses four native RAW formats");
        var enabled = (CheckBox)Field(panel, "enableSpektraFilm")!;
        var exposure = Field(panel, "exposure")!;
        var strength = Field(panel, "strength")!;
        var exposureValue = exposure.GetType().GetProperty("Value", Flags)!;
        var strengthValue = strength.GetType().GetProperty("Value", Flags)!;
        var size = (ComboBox)Field(spatial, "previewSize")!;
        var quality = Environment.GetEnvironmentVariable("SPEKTRAFILM_HOST_VIEWER_CHURN_RESOLUTION") ?? "2048";
        size.SelectedItem = size.ItemsSource.Cast<object>().Single(item => Equals(Prop(item, "Id"), quality));
        var viewer = Field(spatial, "viewer")!;
        Call(spatial, "SetEnabled", false);
        await (Task)Call(spatial, "DrainPreviewWorkersAsync", CancellationToken.None)!;
        Call(spatial, "SetEnabled", true);
        await using var memoryTrace = quality == "native" ? new ViewerMemoryTrace(spatial) : null;
        memoryTrace?.Start();
        var samples = new List<ViewerVisit>(visitCount);
        var sources = new List<WeakReference>();
        var superseded = 0;

        object? ValidateDrawn(Guid selected)
        {
            if (Prop(viewer, "HasPainted") is not true || Field(viewer, "attachment") is not { } live
                || Prop(live, "VariantId") is not Guid painted) return null;
            if (painted != selected)
                throw new InvalidOperationException("An obsolete variant claimed to have painted after a selection changed.");
            return live;
        }

        for (var visit = 0; visit < visitCount; visit++)
        {
            memoryTrace?.MarkVisit(visit + 1);
            if (visit % 10 == 7)
            {
                foreach (var offset in new[] { 2, 3, 1 })
                {
                    var transient = selection.Variants[(visit + offset) % selection.Variants.Length];
                    selection.Select(transient, transient);
                    await Task.Delay(25);
                    ValidateDrawn(transient);
                    superseded++;
                }
            }
            var variant = selection.Variants[visit % selection.Variants.Length];
            var started = Stopwatch.GetTimestamp();
            selection.Select(variant, variant);
            enabled.IsChecked = true;
            var strengthPercent = visit % 3 * 50;
            strengthValue.SetValue(strength, (double)strengthPercent);
            var ev = (visit * 17 % 51 - 25) / 10d;
            if (Equals(exposureValue.GetValue(exposure), ev)) ev += .1;
            var saveAt = Stopwatch.GetTimestamp();
            exposureValue.SetValue(exposure, ev);
            var saveTime = Stopwatch.GetElapsedTime(saveAt).TotalMilliseconds;
            var saved = Call(store, "Read", variant)!;
            if (!Equals(Prop(Prop(saved, "Look")!, "Exposure"), ev) || Prop(store, "HasPending") is not false)
                throw new InvalidOperationException($"Viewer visit {visit + 1} did not persist its slider edit.");

            while (true)
            {
                var live = ValidateDrawn(variant);
                if (live is not null && Field(spatial, "frameCurrent") is true && Equals(Field(spatial, "sourceSize"), quality)
                    && Field(spatial, "source") is { } source && Prop(source, "Description") is { } frameDescription
                    && Prop(frameDescription, "Source") is { } description && Prop(description, "VariantId") is Guid sourceId && sourceId == variant
                    && Prop(source, "BitDepth") is 16 && Field(spatial, "viewerFrame") is BitmapSource image
                    && ReferenceEquals(Prop(live, "PaintedBitmap"), image))
                {
                    var worker = Field(spatial, "renderer") is { } renderer ? Field(renderer, "process") as Process : null;
                    long rendererBytes = 0;
                    if (worker is { HasExited: false }) { worker.Refresh(); rendererBytes = worker.PrivateMemorySize64; }
                    host.Refresh();
                    samples.Add(new(visit + 1, (string)Prop(description, "SourceFormat")!, strengthPercent, saveTime,
                        Stopwatch.GetElapsedTime(started).TotalMilliseconds, GC.GetTotalMemory(false), host.PrivateMemorySize64,
                        rendererBytes, (double)Call(viewer, "ProjectedLongEdge", variant)!, image.PixelWidth, image.PixelHeight,
                        (int)Field(spatial, "inputRevision")!));
                    sources.Add(new WeakReference(source));
                    break;
                }
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(18))
                    throw new TimeoutException($"Viewer visit {visit + 1} never painted the exact selected 16-bit source at {quality} quality.");
                await Task.Delay(16);
            }
            if ((visit + 1) % 20 == 0)
                File.AppendAllText(Path.Combine(folder, "host-smoke.log"), $"Recorded {visit + 1} live Viewer visits.{Environment.NewLine}");
        }

        var lastSource = Field(spatial, "source")!;
        memoryTrace?.MarkVisit(visitCount + 1);
        var currentVariant = selection.Variants[(visitCount - 1) % selection.Variants.Length];
        await ChangeLookAndPaint(100d, .71);
        var packedReuse = await ChangeLookAndPaint(100d, .76);
        var floatReuse = await ChangeLookAndPaint(50d, .76);
        var blendReusedFloat = Field(spatial, "renderedPixels") is byte[];
        if (!blendReusedFloat)
        {
            var budgetType = spatial.GetType().Assembly.GetType("ViewerCacheBudget")!;
            var budget = budgetType.GetProperty("Shared", Flags)!.GetValue(null)!;
            var limit = (long)budgetType.GetField("SharedByteLimit", Flags)!.GetRawConstantValue()!;
            var required = (long)(int)Prop(lastSource, "Width")! * (int)Prop(lastSource, "Height")! * 16;
            Check(required > limit - (long)Prop(budget, "RetainedBytes")!,
                "the unused float reply exceeds the remaining Viewer retention allowance");
        }
        var blendReuse = await ChangeLookAndPaint(25d, .76);
        var warm = await ReadViewerTimingRecords(new HashSet<int> { packedReuse, floatReuse, blendReuse });
        Check(warm.Count == 3 && warm.Values.All(timing => timing.GetProperty("NativeDevelopmentMs").ValueKind == JsonValueKind.Null),
            "exposure and strength edits retain the developed high-depth Viewer source and record no repeated native work");
        foreach (var (generation, packed) in new[] { (packedReuse, true), (floatReuse, false) })
        {
            var timing = warm[generation];
            var worker = timing.GetProperty("Worker");
            Check(timing.GetProperty("FilmRenderAndTransportMs").GetDouble() > 0 && !worker.GetProperty("SourceUploaded").GetBoolean()
                && worker.GetProperty("PackedDisplay").GetBoolean() == packed,
                "both packed and float warm edits reuse the worker source and report its exact transfer mode");
        }
        var blendTiming = warm[blendReuse];
        var blendWorker = blendTiming.GetProperty("Worker");
        var blendFilmMs = blendTiming.GetProperty("FilmRenderAndTransportMs");
        Check(blendTiming.GetProperty("CreateDisplayBitmapMs").GetDouble() > 0 && (blendReusedFloat
            ? blendWorker.ValueKind == JsonValueKind.Null && blendFilmMs.ValueKind == JsonValueKind.Null
            : blendFilmMs.GetDouble() > 0 && !blendWorker.GetProperty("SourceUploaded").GetBoolean()
                && !blendWorker.GetProperty("PackedDisplay").GetBoolean()),
            blendReusedFloat ? "strength-only blending from retained float pixels leaves native development and Vulkan idle"
                : "the uncached blend reuses the native and GPU sources and rebuilds the float reply");
        File.WriteAllText(Path.Combine(folder, "viewer-warm-stage-timings.json"),
            JsonSerializer.Serialize(new { PackedReuse = warm[packedReuse], FloatReuse = warm[floatReuse], BlendReuse = blendTiming, BlendReusedFloat = blendReusedFloat },
                new JsonSerializerOptions { WriteIndented = true }));

        lastSource = null;
        var runningWorker = Field(spatial, "renderer") is { } owned ? Field(owned, "process") as Process : null;
        using var workerProbe = runningWorker is { HasExited: false } ? Process.GetProcessById(runningWorker.Id) : null;
        enabled.IsChecked = false;
        await (Task)Call(spatial, "DrainPreviewWorkersAsync", CancellationToken.None)!;
        Check(workerProbe is null || workerProbe.HasExited, "active Viewer worker exits after measured navigation and explicit drain");
        var draining = Stopwatch.GetTimestamp();
        while (Field(spatial, "pending") is not null || Field(spatial, "pendingDraft") is not null)
        {
            if (Stopwatch.GetElapsedTime(draining) > TimeSpan.FromSeconds(10))
                throw new TimeoutException("Viewer work survived the drain after disabling Spektrafilm.");
            await Task.Delay(25);
        }
        Check(Field(spatial, "source") is null && Field(spatial, "renderedPixels") is null
            && Field(spatial, "viewerFrame") is null && Prop(Field(spatial, "draftPyramid")!, "RetainedBytes") is 0L,
            "disabling the master releases every active Viewer source, rendered buffer, display and draft cache");
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var retained = sources.Count(item => item.IsAlive);
        if (memoryTrace is not null)
        {
            var timeline = await memoryTrace.FinishAsync();
            Check(timeline.Any(sample => sample.LiveWorkers > 0) && timeline.Any(sample => sample.LiveWorkers == 0)
                && samples.All(visit => timeline.Any(sample => sample.Visit == visit.Index)),
                "native-resolution memory sampling spans every visit and observes live and idle workers");
            File.WriteAllText(Path.Combine(folder, "viewer-memory-timeline.json"), JsonSerializer.Serialize(new
            {
                Scope = "Actual parent and observed Preview worker processes only. Parent includes Capture One caches.",
                PeakHostPrivateBytes = timeline.Max(sample => sample.HostPrivateBytes),
                PeakWorkerPrivateBytes = timeline.Max(sample => sample.WorkerPrivateBytes),
                PeakCombinedPrivateBytes = timeline.Max(sample => sample.HostPrivateBytes + sample.WorkerPrivateBytes),
                PeakManagedBytes = timeline.Max(sample => sample.ManagedBytes),
                Samples = timeline,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        await VerifyViewerStageTimings(samples, quality);
        var detailed = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(folder, "viewer-churn.json"), JsonSerializer.Serialize(new
        {
            Visits = visitCount, Quality = quality, InterleavedSelections = superseded, DistinctSourceFormats = samples.Select(s => s.Format).Distinct().ToArray(),
            EditAndDurableSaveMs = Percentiles(samples.Select(s => s.SaveMs).ToArray()),
            SettledPaintMs = Percentiles(samples.Select(s => s.SettledPaintMs).ToArray()),
            PeakHostPrivateBytes = samples.Max(s => s.HostPrivateBytes), PeakRendererPrivateBytes = samples.Max(s => s.RendererPrivateBytes),
            MaxManagedBytes = samples.Max(s => s.ManagedBytes), RetainedSourceObjectsAfterDrain = retained,
            PostDrainManagedBytes = GC.GetTotalMemory(false), Samples = samples,
        }, detailed));
        Check(retained == 0, "all sampled native RAW buffers are collectible after the Viewer and its workers are drained");
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} Viewer churn assertions; {visitCount} individually verified drawn frames");

        async Task<int> ChangeLookAndPaint(double filmStrength, double filmExposure)
        {
            var previous = Field(spatial, "viewerFrame");
            strengthValue.SetValue(strength, filmStrength);
            exposureValue.SetValue(exposure, filmExposure);
            var started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(18))
            {
                if (ValidateDrawn(currentVariant) is { } live && Field(spatial, "frameCurrent") is true
                    && Field(spatial, "viewerFrame") is BitmapSource image && !ReferenceEquals(previous, image)
                    && ReferenceEquals(image, Prop(live, "PaintedBitmap")))
                {
                    Check(ReferenceEquals(lastSource, Field(spatial, "source")), "a same-variant edit retains the exact developed source");
                    return (int)Field(spatial, "inputRevision")!;
                }
                await Task.Delay(16);
            }
            throw new TimeoutException("The warm Viewer edit did not paint a settled frame.");
        }
    }

    private static async Task<Dictionary<int, JsonElement>> ReadViewerTimingRecords(HashSet<int> generations)
    {
        var seen = new Dictionary<int, JsonElement>();
        var log = Path.Combine(folder, "panel.log");
        var start = Stopwatch.GetTimestamp();
        while (!generations.All(seen.ContainsKey) && Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(4))
        {
            foreach (var line in File.ReadLines(log).Where(line => line.Contains("viewer.performance.v1", StringComparison.Ordinal)))
            {
                try
                {
                    using var entry = JsonDocument.Parse(line);
                    var detail = entry.RootElement.GetProperty("Details");
                    if (detail.TryGetProperty("Stage", out var stage) && stage.GetString() != "Settled") continue;
                    var generation = detail.GetProperty("SettingsGeneration").GetInt32();
                    if (generations.Contains(generation)) seen[generation] = detail.Clone();
                }
                catch (JsonException) { /* The log can end with a partial entry while it flushes. */ }
            }
            if (!generations.All(seen.ContainsKey)) await Task.Delay(80);
        }
        return seen;
    }

    private static async Task VerifyViewerStageTimings(IReadOnlyList<ViewerVisit> visits, string quality)
    {
        var generations = visits.Select(visit => visit.SettingsGeneration).ToHashSet();
        var seen = await ReadViewerTimingRecords(generations);
        Check(generations.Count == visits.Count && generations.All(seen.ContainsKey),
            "each exact settled native frame has a structured stage record for its settings generation");
        foreach (var visit in visits)
        {
            var entry = seen[visit.SettingsGeneration];
            var film = visit.StrengthPercent != 0;
            var timings = new[] { "NativeDevelopmentMs", "FilmRenderAndTransportMs", "CreateDisplayBitmapMs", "ReadyToPaintMs" }
                .Select(key => entry.GetProperty(key)).ToArray();
            var total = entry.GetProperty("RequestToPaintMs").GetDouble();
            var measured = timings.Where(value => value.ValueKind == JsonValueKind.Number).Select(value => value.GetDouble()).ToArray();
            var worker = entry.GetProperty("Worker");
            Check(entry.GetProperty("SourceFormat").GetString() == visit.Format && entry.GetProperty("Quality").GetString() == quality
                && entry.GetProperty("Width").GetInt32() == visit.PixelWidth && entry.GetProperty("Height").GetInt32() == visit.PixelHeight
                && entry.GetProperty("SourceBitDepth").GetInt32() == 16 && entry.GetProperty("FilmApplied").GetBoolean() == film
                && entry.GetProperty("HostAssemblySha256").GetString() is { Length: 64 }
                && !entry.TryGetProperty("VariantId", out _) && !entry.TryGetProperty("SourcePath", out _)
                && double.IsFinite(total) && total >= measured.Sum() && measured.All(value => double.IsFinite(value) && value >= 0)
                && timings[0].ValueKind == JsonValueKind.Number && timings[0].GetDouble() > 0
                && timings[2].ValueKind == JsonValueKind.Number && timings[2].GetDouble() > 0
                && (film ? timings[1].ValueKind == JsonValueKind.Number && timings[1].GetDouble() > 0 && worker.GetProperty("Request").GetUInt64() > 0
                    && worker.GetProperty("ProcessId").GetInt32() > 0 && worker.GetProperty("SourceUploaded").GetBoolean()
                    && worker.GetProperty("PackedDisplay").GetBoolean() == (visit.StrengthPercent == 100)
                    && worker.GetProperty("ParameterContract").GetString() is { Length: 64 }
                    : timings[1].ValueKind == JsonValueKind.Null && worker.ValueKind == JsonValueKind.Null),
                "the actual settled paint timing matches the fixture's native source, settings and worker request");
        }
        Check(seen.Values.Select(entry => entry.GetProperty("FrameId").GetGuid()).Distinct().Count() == visits.Count,
            "the timed native paints have independent ephemeral identifiers");
        File.WriteAllText(Path.Combine(folder, "viewer-stage-timings.json"),
            JsonSerializer.Serialize(visits.Select(visit => seen[visit.SettingsGeneration]), new JsonSerializerOptions { WriteIndented = true }));
    }
}
