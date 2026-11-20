using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

public static partial class StartupHook
{
    private static async Task MeasureViewerTelemetry(Type hook, object panel, object spatial, int duration)
    {
        var selection = new FixtureSelection(panel);
        Check(selection.Variants.Length == 4, "the live diagnostics load uses four private native RAW variants");
        var resolution = (ComboBox)Field(spatial, "previewSize")!;
        var draft = (ComboBox)Field(spatial, "dragPreviewSize")!;
        var strength = Field(panel, "strength")!;
        var exposure = Field(panel, "exposure")!;
        var exposureValue = exposure.GetType().GetProperty("Value", Flags)!;
        var viewer = Field(spatial, "viewer")!;
        var dropped = hook.GetProperty("DroppedLogEntries", Flags)!;
        var fullQuality = resolution.ItemsSource.Cast<object>().Single(item => Prop(item, "Id") is "4096");
        resolution.SelectedItem = fullQuality;
        draft.SelectedItem = draft.ItemsSource.Cast<object>().Single(item => Prop(item, "LongEdge") is 1200);
        ((CheckBox)Field(panel, "enableSpektraFilm")!).IsChecked = true;
        strength.GetType().GetProperty("Value", Flags)!.SetValue(strength, 100d);
        var log = Path.Combine(folder, "panel.log");
        var frames = new List<object>();
        var firstVisible = new List<double>();
        var settledTimes = new List<double>();
        var editTimes = new List<double>();
        var blockedEditTimes = new List<double>();
        var forcedCancellations = 0;
        using var process = Process.GetCurrentProcess();
        long peakPrivate = 0;
        var start = Stopwatch.GetTimestamp();
        var index = 0;
        while (Stopwatch.GetElapsedTime(start) < TimeSpan.FromSeconds(duration))
        {
            var variant = selection.Variants[index % selection.Variants.Length];
            var oldFrame = Field(spatial, "viewerFrame");
            var selectionStarted = Stopwatch.GetTimestamp();
            selection.Select(variant, variant);
            var first = await WaitForPaint(variant, oldFrame, settled: false);
            var firstMs = Stopwatch.GetElapsedTime(selectionStarted).TotalMilliseconds;
            var complete = await WaitForPaint(variant, oldFrame, settled: true);
            var finalMs = Stopwatch.GetElapsedTime(selectionStarted).TotalMilliseconds;
            var settings = Call(panel, "CaptureLook")!;
            var settingsHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(settings, settings.GetType())));
            firstVisible.Add(firstMs);
            settledTimes.Add(finalMs);
            frames.Add(new { index, format = selection.Format(variant), firstMs, finalMs, firstEdge = Math.Max(first.PixelWidth, first.PixelHeight),
                complete.PixelWidth, complete.PixelHeight, settingsHash, selectionGeneration = Field(spatial, "inputRevision") });
            process.Refresh();
            peakPrivate = Math.Max(peakPrivate, process.PrivateMemorySize64);

            int beforeDrop = (int)dropped.GetValue(null)!;
            using (var blocked = index == 3 ? await LockLog() : null)
            {
                Call(spatial, "BeginSliderDrag");
                try
                {
                    for (var edit = 0; edit < 8; edit++)
                    {
                        var at = Stopwatch.GetTimestamp();
                        exposureValue.SetValue(exposure, ((index + edit * 3) % 37 - 18) / 10d);
                        var milliseconds = Stopwatch.GetElapsedTime(at).TotalMilliseconds;
                        editTimes.Add(milliseconds);
                        if (blocked is not null) blockedEditTimes.Add(milliseconds);
                        await Task.Delay(34);
                    }
                    await Task.Delay(80);
                }
                finally { Call(spatial, "EndSliderDrag"); }
                if (blocked is not null) await Task.Delay(800);
            }
            if (index == 3)
                Check((int)dropped.GetValue(null)! > beforeDrop && blockedEditTimes.Count == 8,
                    "the real Viewer continues to process edits when its private diagnostic file is locked");
            await WaitForPaint(variant, complete, settled: true);
            if (index % 8 == 2)
            {
                exposureValue.SetValue(exposure, 2.17);
                var until = Stopwatch.GetTimestamp();
                while (Field(spatial, "renderingFinal") is not true && Stopwatch.GetElapsedTime(until) < TimeSpan.FromMilliseconds(900))
                    await Task.Delay(2);
                if (Field(spatial, "renderingFinal") is true)
                {
                    exposureValue.SetValue(exposure, 2.31);
                    forcedCancellations++;
                    await WaitForPaint(variant, complete, settled: true);
                }
            }
            if (++index % 20 == 0)
                File.AppendAllText(Path.Combine(folder, "host-smoke.log"), $"Recorded {index} live diagnostic navigation and slider bursts.{Environment.NewLine}");
        }

        var measuredSeconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
        Call(spatial, "SetEnabled", false);
        await (Task)Call(spatial, "DrainPreviewWorkersAsync", CancellationToken.None)!;
        await Task.Delay(250);
        var retainedEvents = new Dictionary<string, int>(StringComparer.Ordinal);
        var stagePaints = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var uploads = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var logs = new[] { log + ".previous", log }.Where(File.Exists).ToArray();
        foreach (var line in logs.SelectMany(File.ReadLines))
        {
            using var entry = JsonDocument.Parse(line);
            var message = entry.RootElement.GetProperty("Message").GetString();
            if (message is not ("viewer.performance.v1" or "viewer.unpublished.v1")) continue;
            var detail = entry.RootElement.GetProperty("Details");
            var stage = detail.GetProperty("Stage").GetString()!;
            var key = message == "viewer.performance.v1" ? stage : stage + "/" + detail.GetProperty("Outcome").GetString();
            retainedEvents[key] = retainedEvents.GetValueOrDefault(key) + 1;
            if (message == "viewer.performance.v1")
            {
                AddTiming(stagePaints, stage, detail.GetProperty("RequestToPaintMs").GetDouble());
                if (detail.GetProperty("Worker") is { ValueKind: JsonValueKind.Object } worker
                    && detail.GetProperty("FilmRenderAndTransportMs") is { ValueKind: JsonValueKind.Number } film)
                    AddTiming(uploads, stage + (worker.GetProperty("SourceUploaded").GetBoolean() ? "/Upload" : "/Resident"), film.GetDouble());
            }
        }
        Check(measuredSeconds >= duration && index >= 6 && forcedCancellations > 0,
            "the licensed telemetry workload includes navigation, sustained edits and active generation cancellation");
        Check(retainedEvents.GetValueOrDefault("Settled") > 0 && retainedEvents.GetValueOrDefault("Drag") > 0
            && retainedEvents.Any(entry => entry.Key.EndsWith("/Canceled", StringComparison.Ordinal)),
            "real settled and brief drawings and canceled work remain distinct in the bounded native log");
        Check(logs.All(path => new FileInfo(path).Length <= 1024 * 1024), "diagnostic files remain within their one-mebibyte generation bound");
        using var panelBytes = File.OpenRead(panel.GetType().Assembly.Location);
        var receipt = new
        {
            measuredSeconds, navigationBursts = index, edits = editTimes.Count, forcedCancellations, retainedEvents, peakPrivate,
            FileBytes = logs.Select(path => new FileInfo(path).Length).ToArray(), DroppedLogEntries = dropped.GetValue(null),
            PanelSha256 = Convert.ToHexString(SHA256.HashData(panelBytes)),
            FirstVisibleMs = Percentiles(firstVisible.ToArray()), SettledFromSelectionMs = Percentiles(settledTimes.ToArray()),
            EditAndSaveMs = Percentiles(editTimes.ToArray()), LockedEditAndSaveMs = Percentiles(blockedEditTimes.ToArray()),
            RetainedRequestToPaintMs = stagePaints.ToDictionary(pair => pair.Key,
                pair => new { Samples = pair.Value.Count, Timing = Percentiles(pair.Value.ToArray()) }),
            RetainedFilmWithTransportMs = uploads.ToDictionary(pair => pair.Key,
                pair => new { Samples = pair.Value.Count, Timing = Percentiles(pair.Value.ToArray()) }),
            frames,
        };
        File.WriteAllText(Path.Combine(folder, "viewer-telemetry.json"),
            JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} licensed telemetry checks over {measuredSeconds:F1}s");

        static void AddTiming(Dictionary<string, List<double>> timings, string key, double value)
        {
            if (!timings.TryGetValue(key, out var samples))
                timings.Add(key, samples = new());
            samples.Add(value);
        }

        async Task<BitmapSource> WaitForPaint(Guid variant, object? previous, bool settled)
        {
            var at = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(at) < TimeSpan.FromSeconds(18))
            {
                if (Prop(viewer, "HasPainted") is true && Field(viewer, "attachment") is { } attachment
                    && Equals(Prop(attachment, "VariantId"), variant) && Field(spatial, "viewerFrame") is BitmapSource bitmap
                    && !ReferenceEquals(previous, bitmap) && ReferenceEquals(Prop(attachment, "PaintedBitmap"), bitmap)
                    && (!settled || Field(spatial, "frameCurrent") is true))
                    return bitmap;
                await Task.Delay(15);
            }
            throw new TimeoutException("The exact current Viewer frame did not paint during diagnostic load.");
        }

        async Task<FileStream> LockLog()
        {
            var at = Stopwatch.GetTimestamp();
            while (true)
            {
                try { return new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.None); }
                catch (IOException) when (Stopwatch.GetElapsedTime(at) < TimeSpan.FromSeconds(2)) { await Task.Delay(20); }
            }
        }
    }
}
