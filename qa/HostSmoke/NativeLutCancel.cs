using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

public static partial class StartupHook
{
    private static async Task TestNativeLutCancel(object panel, object spatial)
    {
        var toggle = (CheckBox)Field(panel, "enableSpektraFilm")!;
        var enabled = toggle.IsChecked;
        var cancel = (Button)Field(panel, "cancelLut")!;
        var status = (TextBlock)Field(panel, "status")!;
        var root = Path.GetFullPath(Environment.GetEnvironmentVariable("SPEKTRAFILM_ROOT")!);
        var executable = Path.Combine(root, "renderer", "SpektraStillRenderer.exe");
        Check(Path.GetDirectoryName(root) == Path.GetFullPath(folder), "only the private runtime supplies the LUT cancellation worker");
        var phase = File.ReadAllText(Path.Combine(folder, "phase.txt"));
        var output = Path.Combine(folder, phase + "-guarded-optical.cube");
        byte[] previous = { 3, 3, 6, 5, 41 };
        File.WriteAllBytes(output, previous);
        try
        {
            toggle.IsChecked = true;
            Call(spatial, "SetEnabled", false);
            await (Task)Call(spatial, "DrainPreviewWorkersAsync", CancellationToken.None)!;
            var accepted = 0;
            var bake = (Task)Call(panel, "ExportLutWithDestinationAsync", 65, (Func<int, string[], string?>)((size, _) =>
            {
                accepted = size;
                return output;
            }))!;
            Check(accepted == 65 && cancel.Visibility == Visibility.Visible && Field(spatial, "lutRenderActive") is true,
                "the actual LUT output path reserved a cancellable 65-point job");
            var token = ((CancellationTokenSource)Field(panel, "lutWork")!).Token;
            using var worker = await WaitForLutWorker(bake, executable);
            Check(worker is { HasExited: false } && !bake.IsCompleted, "a private Vulkan worker is active for the accepted LUT");
            var signaled = Stopwatch.GetTimestamp();
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, cancel));
            await bake.WaitAsync(TimeSpan.FromSeconds(12));
            var cancelMs = Stopwatch.GetElapsedTime(signaled).TotalMilliseconds;
            Check(token.IsCancellationRequested && status.Text == "LUT export canceled." && cancel.Visibility == Visibility.Collapsed
                && Field(spatial, "lutRenderActive") is false && worker!.HasExited,
                "the native Cancel LUT export button drains the worker and releases its control state");
            Check(File.ReadAllBytes(output).SequenceEqual(previous) && !Directory.EnumerateFiles(folder, ".spektra-*.tmp").Any(),
                "canceling the active 65-point LUT preserves the prior destination and leaves no staging file");

            await ((Task)Call(panel, "ExportLutWithDestinationAsync", 33,
                (Func<int, string[], string?>)((_, _) => output))!).WaitAsync(TimeSpan.FromSeconds(20));
            var cube = File.ReadAllText(output);
            Check(status.Text == "LUT exported." && cube.Contains("LUT_3D_SIZE 33\n", StringComparison.Ordinal)
                && cube.Contains("# Engine parameter contract: ", StringComparison.Ordinal),
                "the next LUT completes through the same guarded output after cancellation");
            File.WriteAllText(Path.Combine(folder, phase + "-native-lut-cancellation.json"),
                JsonSerializer.Serialize(new { workerId = worker!.Id, cancelMs, priorBytesPreserved = true, followUpSize = 33 },
                    new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            toggle.IsChecked = enabled;
            Call(spatial, "SetEnabled", enabled == true);
        }
    }

    private static async Task<Process?> WaitForLutWorker(Task bake, string executable)
    {
        var started = Stopwatch.GetTimestamp();
        while (!bake.IsCompleted && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(8))
        {
            foreach (var candidate in Process.GetProcessesByName("SpektraStillRenderer"))
            {
                using (candidate)
                {
                    try
                    {
                        if (!candidate.HasExited && string.Equals(candidate.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase))
                            return Process.GetProcessById(candidate.Id);
                    }
                    catch (Exception error) when (error is InvalidOperationException or ArgumentException or Win32Exception) { }
                }
            }
            await Task.Delay(10);
        }
        return null;
    }
}
