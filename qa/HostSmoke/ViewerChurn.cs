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
        long HostPrivateBytes, long RendererPrivateBytes, double Scale, int PixelWidth, int PixelHeight);

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
        size.SelectedItem = size.ItemsSource.Cast<object>().Single(item => Prop(item, "Id") is "2048");
        var viewer = Field(spatial, "viewer")!;
        Call(spatial, "SetEnabled", true);
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
                if (live is not null && Field(spatial, "frameCurrent") is true && Field(spatial, "sourceSize") is "2048"
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
                        rendererBytes, (double)Call(viewer, "ProjectedLongEdge", variant)!, image.PixelWidth, image.PixelHeight));
                    sources.Add(new WeakReference(source));
                    break;
                }
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(18))
                    throw new TimeoutException($"Viewer visit {visit + 1} never painted the exact selected 16-bit source at 2048 px.");
                await Task.Delay(16);
            }
            if ((visit + 1) % 20 == 0)
                File.AppendAllText(Path.Combine(folder, "host-smoke.log"), $"Recorded {visit + 1} live Viewer visits.{Environment.NewLine}");
        }

        Call(spatial, "SetEnabled", false);
        var runningWorker = Field(spatial, "renderer") is { } owned ? Field(owned, "process") as Process : null;
        using var workerProbe = runningWorker is { HasExited: false } ? Process.GetProcessById(runningWorker.Id) : null;
        await (Task)Call(spatial, "DrainPreviewWorkersAsync", CancellationToken.None)!;
        Check(workerProbe is null || workerProbe.HasExited, "active Viewer worker exits after measured navigation and explicit drain");
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var retained = sources.Count(item => item.IsAlive);
        Check(retained <= 4, "all but the bounded current RAW frames are collectible after navigation and drain");
        var detailed = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(folder, "viewer-churn.json"), JsonSerializer.Serialize(new
        {
            Visits = visitCount, InterleavedSelections = superseded, DistinctSourceFormats = samples.Select(s => s.Format).Distinct().ToArray(),
            EditAndDurableSaveMs = Percentiles(samples.Select(s => s.SaveMs).ToArray()),
            SettledPaintMs = Percentiles(samples.Select(s => s.SettledPaintMs).ToArray()),
            PeakHostPrivateBytes = samples.Max(s => s.HostPrivateBytes), PeakRendererPrivateBytes = samples.Max(s => s.RendererPrivateBytes),
            MaxManagedBytes = samples.Max(s => s.ManagedBytes), RetainedSourceObjectsAfterDrain = retained,
            PostDrainManagedBytes = GC.GetTotalMemory(false), Samples = samples,
        }, detailed));
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} Viewer churn assertions; {visitCount} individually verified drawn frames");
    }
}
