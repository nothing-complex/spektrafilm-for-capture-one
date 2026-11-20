using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

public static partial class StartupHook
{
    private static async Task TestNativeDevelopmentDeadline(object panel, object spatial)
    {
        var viewer = Field(spatial, "viewer")!;
        var picker = (ComboBox)Field(spatial, "previewSize")!;
        var status = (TextBlock)Field(spatial, "status")!;
        void Quality(string id) => picker.SelectedItem = picker.ItemsSource.Cast<object>().Single(item => Equals(Prop(item, "Id"), id));
        bool Painted(string id) => Field(spatial, "sourceSize") is string size && size == id
            && Field(spatial, "frameCurrent") is true && Prop(viewer, "HasPainted") is true && Field(spatial, "pending") is null;
        string Look()
        {
            var look = Call(panel, "CaptureLook")!;
            return JsonSerializer.Serialize(look, look.GetType());
        }
        async Task WaitFor(Func<bool> condition, TimeSpan limit, string problem)
        {
            var start = Stopwatch.GetTimestamp();
            while (!condition())
            {
                if (Stopwatch.GetElapsedTime(start) > limit) throw new TimeoutException(problem + " Viewer: " + status.Text);
                await Task.Delay(65);
            }
        }
        ((CheckBox)Field(panel, "enableSpektraFilm")!).IsChecked = true;
        Quality("720");
        await WaitFor(() => Painted("720"), TimeSpan.FromSeconds(20), "The real initial Viewer failed to settle.");
        var original = Field(spatial, "source")!;
        var savedLook = Look();
        var hostSource = panel.GetType().Assembly.GetType("HostImageSource", true)!;
        var slot = (SemaphoreSlim)hostSource.GetField("SchedulerSlot", Flags)!.GetValue(null)!;
        Check(await slot.WaitAsync(TimeSpan.FromSeconds(8)), "the fixture took the plugin scheduler slot after native rendering settled");
        long failedAt;
        var started = Stopwatch.GetTimestamp();
        try
        {
            Quality("900");
            await WaitFor(() => Field(spatial, "pending") is not null && Field(spatial, "frameCurrent") is false,
                TimeSpan.FromSeconds(10), "The edited Viewer did not queue real native image development.");
            await WaitFor(() => status.Text.Contains("Retrying Capture One image development", StringComparison.Ordinal),
                TimeSpan.FromSeconds(200), "The native development deadline did not schedule recovery.");
            failedAt = Stopwatch.GetTimestamp();
            Check(Field(spatial, "viewerFrame") is null && Prop(viewer, "HasPainted") is false
                && Prop(panel, "ExportBridgeStatus")?.ToString() == "Connected",
                "the real native deadline clears the unprocessed Viewer while the Export bridge stays connected");
        }
        finally { slot.Release(); }
        await WaitFor(() => Painted("900"), TimeSpan.FromSeconds(20),
            "Releasing the native admission slot did not automatically develop the current selected image.");
        var developed = Field(spatial, "source")!;
        Check(!ReferenceEquals(original, developed) && Prop(developed, "BitDepth") is 16 && Look() == savedLook,
            "automatic native deadline recovery paints the existing film look from the requested new RGB16 source");
        File.WriteAllText(Path.Combine(folder, "native-development-deadline.json"), JsonSerializer.Serialize(new
        {
            blockedAdmissionMs = Stopwatch.GetElapsedTime(started, failedAt).TotalMilliseconds,
            repaintMs = Stopwatch.GetElapsedTime(failedAt).TotalMilliseconds,
            configuredLongEdge = 900,
        }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} native development deadline assertions");
    }
}
