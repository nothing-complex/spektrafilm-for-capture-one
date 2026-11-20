using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

public static partial class StartupHook
{
    private static async Task TestNativeDeviceLossRecovery(object panel, object spatial, object store, Guid variant)
    {
        var viewer = Field(spatial, "viewer")!;
        var status = (TextBlock)Field(spatial, "status")!;
        async Task WaitFor(Func<bool> condition, string description)
        {
            var began = Stopwatch.GetTimestamp();
            while (!condition())
            {
                if (Stopwatch.GetElapsedTime(began) > TimeSpan.FromSeconds(20))
                    throw new TimeoutException(description + " Viewer: " + status.Text);
                await Task.Delay(30);
            }
        }
        Process? CurrentProcess() => Field(spatial, "renderer") is { } renderer ? Field(renderer, "process") as Process : null;
        bool Settled() => Field(spatial, "sourceSize") is "1200" && Field(spatial, "frameCurrent") is true
            && Prop(viewer, "HasPainted") is true && Field(spatial, "pending") is null && CurrentProcess() is { HasExited: false };
        Call(spatial, "SetEnabled", false);
        using (var drain = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            await (Task)Call(spatial, "DrainPreviewWorkersAsync", drain.Token)!;
        var quality = (ComboBox)Field(spatial, "previewSize")!;
        var draft = (ComboBox)Field(spatial, "dragPreviewSize")!;
        quality.SelectedItem = quality.ItemsSource.Cast<object>().Single(item => Prop(item, "Id") is "1200");
        draft.SelectedItem = draft.ItemsSource.Cast<object>().Single(item => Prop(item, "LongEdge") is 0);
        ((CheckBox)Field(panel, "enableSpektraFilm")!).IsChecked = true;
        Call(spatial, "SetEnabled", true);
        await WaitFor(Settled, "The disposable worker did not paint its first healthy frame.");
        var source = Field(spatial, "source")!;
        var bitmap = Field(spatial, "viewerFrame")!;
        using var previous = Process.GetProcessById(CurrentProcess()!.Id);
        var exposure = Field(panel, "exposure")!;
        var start = (double)Prop(exposure, "Value")!;
        var edited = Math.Round(start + (start > 7 ? -.25 : .25), 2);
        exposure.GetType().GetProperty("Value", Flags)!.SetValue(exposure, edited);
        var generation = Field(spatial, "inputRevision");
        var saved = (string)Call(store, "GetPath", variant)!;
        var snapshot = File.ReadAllBytes(saved);
        await WaitFor(() => status.Text.Contains("Restarting the Spektrafilm Viewer renderer", StringComparison.Ordinal)
            && Field(spatial, "viewerFrame") is null, "The typed Vulkan device-loss reply was not recognized as a retryable Viewer failure.");
        var lostAt = Stopwatch.GetTimestamp();
        var log = File.ReadAllText(Path.Combine(folder, "panel.log"));
        Check(log.Contains("RendererDeviceLostException", StringComparison.Ordinal)
            && log.Contains("VK_ERROR_DEVICE_LOST", StringComparison.Ordinal)
            && Prop(panel, "ExportBridgeStatus")?.ToString() == "Connected",
            "real worker fence-loss reply is distinguishable from process death and leaves native Export connected");
        await WaitFor(() => Settled() && CurrentProcess()!.Id != previous.Id && previous.HasExited,
            "The exact same Viewer generation did not restart on a fresh Vulkan worker.");
        var arguments = (string[])Field(spatial, "renderedArguments")!;
        var index = Array.IndexOf(arguments, "--film-exposure");
        Check(index >= 0 && double.Parse(arguments[index + 1], CultureInfo.InvariantCulture) == edited
            && Equals(generation, Field(spatial, "inputRevision")) && snapshot.AsSpan().SequenceEqual(File.ReadAllBytes(saved))
            && ReferenceEquals(source, Field(spatial, "source")) && !ReferenceEquals(bitmap, Field(spatial, "viewerFrame")),
            "device-loss recovery reapplies the exact durable look from the original high-depth source");
        File.WriteAllText(Path.Combine(folder, "native-vulkan-device-loss.json"), JsonSerializer.Serialize(new
        {
            repaintMs = Stopwatch.GetElapsedTime(lostAt).TotalMilliseconds,
            originalWorkerId = previous.Id,
            replacementWorkerId = CurrentProcess()!.Id,
        }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} native Vulkan recovery assertions");
    }
}
