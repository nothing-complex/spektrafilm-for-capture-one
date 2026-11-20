using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

public static partial class StartupHook
{
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
                    && Field(spatial, "source") is { } source && Prop(source, "Description") is { } description
                    && Prop(description, "VariantId") is Guid sourceId && sourceId == variant
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
