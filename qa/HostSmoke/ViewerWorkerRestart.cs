using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

public static partial class StartupHook
{
    private static async Task TestViewerWorkerRestart(object hosted, object panel, object spatial, object store, Guid variant)
    {
        var quality = (ComboBox)Field(spatial, "previewSize")!;
        var priorQuality = quality.SelectedItem;
        var priorEnabled = Field(spatial, "enabled") is true;
        var strength = Field(panel, "strength")!;
        var exposure = Field(panel, "exposure")!;
        var enlarger = ((IDictionary)Field(spatial, "values")!)["enlarger-scale"]!;
        var enlargerValue = enlarger.GetType().GetProperty("Value", Flags)!;
        var priorStrength = (double)Prop(strength, "Value")!;
        var priorExposure = (double)Prop(exposure, "Value")!;
        var priorEnlarger = (double)enlargerValue.GetValue(enlarger)!;
        var viewer = Field(spatial, "viewer")!;
        var status = (TextBlock)Field(spatial, "status")!;
        var runtime = Path.GetFullPath(Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")!);
        var expectedWorker = Path.Combine(runtime, "renderer", "SpektraStillRenderer.exe");
        Check(Path.GetDirectoryName(runtime) == Path.GetFullPath(folder), "failure injection targets only the disposable viewer runtime");
        Process? Worker() => Field(spatial, "renderer") is { } renderer ? Field(renderer, "process") as Process : null;
        async Task WaitFor(Func<bool> ready, string message)
        {
            var started = Stopwatch.GetTimestamp();
            while (!ready())
            {
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(20))
                    throw new TimeoutException(message + " Viewer: " + status.Text);
                await Task.Delay(8);
            }
        }
        async Task<int> TerminateRunningChild(Func<int, bool> match)
        {
            var terminated = 0;
            await WaitFor(() =>
            {
                if (Field(spatial, "renderingFinal") is not true || Worker() is not { HasExited: false } child || !match(child.Id))
                    return false;
                // A just-started Windows worker may not expose its main module yet.
                // Confirm the exact image on the same Process instance before terminating it.
                var image = child.MainModule?.FileName;
                if (string.IsNullOrEmpty(image)) return false;
                if (!string.Equals(image, expectedWorker, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The running renderer is outside the disposable launcher: " + image);
                var pid = child.Id;
                child.Kill();
                terminated = pid;
                return true;
            }, "Did not catch the running film render with a verified executable path before completion.");
            return terminated;
        }
        void CheckReady(string description, params string[] viewerStates)
        {
            Call(hosted, "ReportLaunchHealth");
            var report = ReadLaunchHealth();
            Check(report.GetProperty("Phase").GetString() == "Ready" && report.GetProperty("Export").GetString() == "Connected"
                && viewerStates.Contains(report.GetProperty("Viewer").GetString()), description);
        }
        try
        {
            strength.GetType().GetProperty("Value", Flags)!.SetValue(strength, 100d);
            quality.SelectedItem = quality.ItemsSource.Cast<object>().Single(item => Prop(item, "Id") is "4096");
            Call(spatial, "SetEnabled", true);
            bool Settled() => Field(spatial, "frameCurrent") is true && Field(spatial, "sourceSize") is "4096"
                && Field(spatial, "pending") is null && Prop(viewer, "HasPainted") is true && Worker() is { HasExited: false };
            await WaitFor(Settled, "The Viewer did not prime its 4096-pixel worker.");
            var before = Field(spatial, "source")!;
            var drag = (ComboBox)Field(spatial, "dragPreviewSize")!;
            var previousDragSize = drag.SelectedItem;
            var exposureValue = exposure.GetType().GetProperty("Value", Flags)!;
            var testEdit = priorExposure < 7.5 ? priorExposure + .23 : priorExposure - .23;
            var burstStarted = Stopwatch.GetTimestamp();
            var warmWorker = Field(spatial, "renderer")!;
            var warmPid = Worker()!.Id;
            async Task<JsonElement> Metrics(object renderer)
            {
                var metrics = await (Task<JsonElement?>)Call(renderer, "ReadDiagnosticsAsync", CancellationToken.None)!;
                return metrics ?? throw new InvalidOperationException("The active worker did not publish its render metrics.");
            }
            async Task<int> Sequence(object renderer) => (await Metrics(renderer)).GetProperty("renderSequence").GetInt32();
            var initialSequence = await Sequence(warmWorker);
            var latest = testEdit;
            try
            {
                drag.SelectedItem = drag.ItemsSource.Cast<object>().Single(item => Prop(item, "LongEdge") is 720);
                exposureValue.SetValue(exposure, testEdit);
                await WaitFor(() => Field(spatial, "renderingFinal") is true && Worker() is { HasExited: false },
                    "The live worker did not begin the render before the drag burst.");
                Call(spatial, "BeginSliderDrag");
                try
                {
                    for (var edit = 1; edit <= 100; edit++)
                    {
                        latest = Math.Round(-2 + edit * .037, 3);
                        exposureValue.SetValue(exposure, latest);
                        if (edit % 4 == 0) await Task.Delay(1);
                    }
                    Check(Field(spatial, "dragging") is true && Field(spatial, "queued") is true
                        && Field(spatial, "frameCurrent") is false,
                        "100 rapid slider changes coalesce into one queued final request without claiming an obsolete frame is current");
                }
                finally { Call(spatial, "EndSliderDrag"); }
                await WaitFor(Settled, "The Viewer did not publish the latest high-depth frame after 100 slider changes.");
                var arguments = (string[])Field(spatial, "renderedArguments")!;
                var exposureIndex = Array.IndexOf(arguments, "--film-exposure");
                var saved = Prop(Prop(Call(store, "Read", variant)!, "Look")!, "Exposure");
                Check(exposureIndex >= 0 && double.Parse(arguments[exposureIndex + 1], CultureInfo.InvariantCulture) == latest
                    && Equals(saved, latest) && ReferenceEquals(Field(spatial, "source"), before),
                    "the burst settles the final durable slider value on the same immutable 16-bit source");
                await WaitFor(() => Field(spatial, "pending") is null && Field(spatial, "pendingDraft") is null
                    && Field(spatial, "queued") is false, "The final frame painted but obsolete work did not drain.");
                var afterWorker = Field(spatial, "renderer")!;
                var afterPid = Worker()!.Id;
                var afterSequence = await Sequence(afterWorker);
                var renders = afterPid == warmPid ? afterSequence - initialSequence : afterSequence;
                await Task.Delay(300);
                var quietSequence = await Sequence(afterWorker);
                File.AppendAllText(Path.Combine(folder, "host-smoke.log"),
                    $"100 native slider edits settled in {Stopwatch.GetElapsedTime(burstStarted).TotalMilliseconds:F1} ms, " +
                    $"with {renders} completed final render(s). Worker sequence {warmPid}:{initialSequence} to {afterPid}:{afterSequence}, " +
                    $"then {quietSequence} after the quiet check.{Environment.NewLine}");
                Check(renders is >= 1 and <= 3 && quietSequence == afterSequence && Worker()?.Id == afterPid
                    && Field(spatial, "pending") is null && Field(spatial, "pendingDraft") is null && Field(spatial, "queued") is false,
                    "the slider burst completes at most three final renders and leaves no obsolete work behind");
            }
            finally { drag.SelectedItem = previousDragSize; }
            await WaitFor(Settled, "The Viewer did not settle after restoring the drag preference.");
            enlargerValue.SetValue(enlarger, 1.25d);
            await WaitFor(Settled, "The actual 4096-pixel enlarger did not paint.");
            var scaled = await Metrics(Field(spatial, "renderer")!);
            var retained = scaled.GetProperty("enlargerCachedBytes").GetInt64();
            var transformWorker = Worker()!.Id;
            Check(retained is > 0 and <= 192L * 1024 * 1024 && ReferenceEquals(before, Field(spatial, "source")),
                "the native enlarger produces a separate cached transformation of the immutable high-depth Viewer source");
            enlargerValue.SetValue(enlarger, 1d);
            await WaitFor(Settled, "The native Viewer did not return to identity geometry.");
            var identity = await Metrics(Field(spatial, "renderer")!);
            Check(identity.GetProperty("enlargerCachedBytes").GetInt64() == retained && Worker()!.Id == transformWorker,
                "identity geometry retains the bounded private transformed pixels for this source");
            enlargerValue.SetValue(enlarger, 1.25d);
            await WaitFor(Settled, "The native Viewer did not restore the same enlarger settings.");
            var restored = await Metrics(Field(spatial, "renderer")!);
            Check(restored.GetProperty("enlargerCacheHit").GetBoolean() && restored.GetProperty("enlargerMs").GetDouble() == 0
                && Worker()!.Id == transformWorker && ReferenceEquals(before, Field(spatial, "source")),
                "returning to the same enlarger geometry skips CPU resampling in the active native Viewer");
            if (restored.GetProperty("dynamicGpuBudget").GetBoolean()
                && restored.GetProperty("gpuMemoryBudgetBytes").GetInt64() >= 2L * 1024 * 1024 * 1024)
                Check(restored.GetProperty("alternateSourceCachedBytes").GetInt64() >= retained
                    && restored.GetProperty("uploadBytes").GetInt64() < retained,
                    "the licensed Viewer switches back to the resident transformed GPU source under adequate headroom");
            var priorFrame = Field(spatial, "viewerFrame")!;
            var firstWorker = Worker()!.Id;

            var edited = priorExposure < 7.5 ? priorExposure + .15 : priorExposure - .15;
            exposure.GetType().GetProperty("Value", Flags)!.SetValue(exposure, edited);
            var editPath = (string)Call(store, "GetPath", variant)!;
            var committed = File.ReadAllBytes(editPath);
            Check(Math.Abs((double)Prop(Prop(Call(store, "Read", variant)!, "Look")!, "Exposure")! - edited) < 1e-12,
                "the edit has reached durable storage before the render failure");
            var generation = Field(spatial, "inputRevision");
            _ = await TerminateRunningChild(pid => pid == firstWorker);
            var lostAt = Stopwatch.GetTimestamp();
            await WaitFor(() => status.Text.Contains("Restarting", StringComparison.Ordinal) && Field(spatial, "viewerFrame") is null,
                "The Viewer did not report a suspended transport retry.");
            CheckReady("a disconnected renderer suspends preview without losing native Export or panel readiness",
                "Rendering", "WaitingForImage");
            await WaitFor(() => Settled() && Worker()!.Id != firstWorker && !ReferenceEquals(priorFrame, Field(spatial, "viewerFrame")),
                "The renderer did not restart and display the outstanding look.");
            Check(Equals(generation, Field(spatial, "inputRevision")) && File.ReadAllBytes(editPath).SequenceEqual(committed),
                "automatic retry paints the exact durable generation without another edit or file write");
            Check(ReferenceEquals(before, Field(spatial, "source")),
                "automatic retry reuses the exact high-depth source");
            var rebuilt = await Metrics(Field(spatial, "renderer")!);
            Check(rebuilt.GetProperty("enlargerCachedBytes").GetInt64() == retained
                && !rebuilt.GetProperty("enlargerCacheHit").GetBoolean() && Equals(enlargerValue.GetValue(enlarger), 1.25d),
                "the replacement worker rebuilds the exact transformed source and keeps the committed enlarger geometry");
            File.AppendAllText(Path.Combine(folder, "host-smoke.log"),
                $"Viewer worker recovery took {Stopwatch.GetElapsedTime(lostAt).TotalMilliseconds:F1} ms after terminating the active child.{Environment.NewLine}");

            var recoveredWorker = Worker()!.Id;
            exposure.GetType().GetProperty("Value", Flags)!.SetValue(exposure, priorExposure);
            enlargerValue.SetValue(enlarger, priorEnlarger);
            _ = await TerminateRunningChild(pid => pid == recoveredWorker);
            _ = await TerminateRunningChild(pid => pid != recoveredWorker);
            await WaitFor(() => Prop(spatial, "Health")?.ToString() == "ProcessingError" && Field(spatial, "pending") is null,
                "The Viewer did not stop after its one retry also failed.");
            await Task.Delay(600);
            CheckReady("a second transport failure stops retrying and reports a Viewer error while Export stays connected", "ProcessingError");
            Check(Field(spatial, "viewerFrame") is null && Field(spatial, "queued") is false && Worker() is null,
                "an exhausted retry publishes no obsolete frame and starts no additional worker");
            var retry = (Button)Field(spatial, "retryViewer")!;
            Check(retry.Visibility == Visibility.Visible && retry.IsEnabled,
                "the native panel exposes a retry action after automatic Viewer recovery is exhausted");
            var latestSaved = File.ReadAllBytes(editPath);
            retry.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitFor(Settled, "The native Viewer retry action did not recover the same image.");
            Check(retry.Visibility == Visibility.Collapsed && ReferenceEquals(before, Field(spatial, "source"))
                && File.ReadAllBytes(editPath).SequenceEqual(latestSaved),
                "the retry action restores the current picture from the same high-depth source and durable look");
            CheckReady("manual Viewer retry restores Ready with native Export connected", "Visible");

            var beforeDisable = Worker()!.Id;
            using var activeWorker = Process.GetProcessById(beforeDisable);
            exposureValue.SetValue(exposure, testEdit);
            await WaitFor(() => Field(spatial, "renderingFinal") is true && Worker()?.Id == beforeDisable,
                "The actual worker did not enter a final request before disable.");
            var duringDisable = File.ReadAllBytes(editPath);
            Call(spatial, "SetEnabled", false);
            await WaitFor(() => Field(spatial, "pending") is null && activeWorker.HasExited,
                "Disabling did not drain the in-flight Viewer request.");
            Check(Field(spatial, "viewerFrame") is null && Prop(viewer, "HasPainted") is false
                && retry.Visibility == Visibility.Collapsed && Worker() is null,
                "disabling during an active final render discards the result and exits its worker");
            CheckReady("disabled Viewer keeps the native Export bridge connected", "Disabled");
            Call(spatial, "SetEnabled", true);
            await WaitFor(Settled, "Enabling the Viewer did not recover the saved look.");
            Check(Worker()!.Id != beforeDisable && File.ReadAllBytes(editPath).SequenceEqual(duringDisable),
                "enabling after cancellation renders the same durable edit in a fresh worker");
        }
        finally
        {
            Call(spatial, "SetEnabled", false);
            strength.GetType().GetProperty("Value", Flags)!.SetValue(strength, priorStrength);
            exposure.GetType().GetProperty("Value", Flags)!.SetValue(exposure, priorExposure);
            quality.SelectedItem = priorQuality;
            Call(spatial, "SetEnabled", priorEnabled);
        }
    }
}
