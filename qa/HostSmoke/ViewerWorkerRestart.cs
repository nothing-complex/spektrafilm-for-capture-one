using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
        var priorStrength = (double)Prop(strength, "Value")!;
        var priorExposure = (double)Prop(exposure, "Value")!;
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
            await WaitFor(() => Field(spatial, "renderingFinal") is true && Worker() is { HasExited: false } current && match(current.Id),
                "Did not catch the running film render before completion.");
            using var worker = Process.GetProcessById(Worker()!.Id);
            if (!string.Equals(worker.MainModule?.FileName, expectedWorker, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The running renderer does not belong to the disposable launcher.");
            worker.Kill();
            return worker.Id;
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
            File.AppendAllText(Path.Combine(folder, "host-smoke.log"),
                $"Viewer worker recovery took {Stopwatch.GetElapsedTime(lostAt).TotalMilliseconds:F1} ms after terminating the active child.{Environment.NewLine}");

            var recoveredWorker = Worker()!.Id;
            exposure.GetType().GetProperty("Value", Flags)!.SetValue(exposure, priorExposure);
            _ = await TerminateRunningChild(pid => pid == recoveredWorker);
            _ = await TerminateRunningChild(pid => pid != recoveredWorker);
            await WaitFor(() => Prop(spatial, "Health")?.ToString() == "ProcessingError" && Field(spatial, "pending") is null,
                "The Viewer did not stop after its one retry also failed.");
            await Task.Delay(600);
            CheckReady("a second transport failure stops retrying and reports a Viewer error while Export stays connected", "ProcessingError");
            Check(Field(spatial, "viewerFrame") is null && Field(spatial, "queued") is false && Worker() is null,
                "an exhausted retry publishes no obsolete frame and starts no additional worker");
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
