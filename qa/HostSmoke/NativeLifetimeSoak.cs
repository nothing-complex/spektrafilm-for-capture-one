using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;

public static partial class StartupHook
{
    private static async Task TestNativeLifetimeSoak(Type hook, FrameworkElement original, int cycles)
    {
        var hostedTool = hook.GetField("hostedTool", Flags)!;
        var detach = hook.GetMethod("Detach", Flags)!;
        var checkHost = hook.GetMethod("OnTick", Flags)!;
        var dispatcher = original.Dispatcher;
        using var process = Process.GetCurrentProcess();
        var windows = new List<WeakReference>(cycles);
        var panels = new List<WeakReference>(cycles);
        var tools = new List<WeakReference>(cycles);
        var snapshots = new List<object>();
        const int warmups = 8;
        for (var warm = 0; warm < warmups; warm++) Cycle(redock: warm % 2 == 0);
        await Snapshot(0);
        var started = Stopwatch.GetTimestamp();
        for (var index = 1; index <= cycles; index++)
        {
            var retired = Cycle(redock: index % 2 == 0);
            windows.Add(retired.Window);
            panels.Add(retired.Panel);
            tools.Add(retired.Tool);
            if (index % 5 == 0)
                await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (index == cycles / 2 || index == cycles)
                await Snapshot(index);
        }
        var alive = new { windows = windows.Count(item => item.IsAlive), panels = panels.Count(item => item.IsAlive),
            tools = tools.Count(item => item.IsAlive) };
        File.WriteAllText(Path.Combine(folder, "native-tool-lifetime.json"), JsonSerializer.Serialize(new
        {
            cycles, warmups, redocked = cycles / 2, detachedWhileFloating = cycles - cycles / 2,
            elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds, alive, snapshots,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Check(alive.windows == 0 && alive.panels == 0 && alive.tools == 0,
            "retired native windows, panel controls and hosted tools are collectible");
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} native lifetime checks over {cycles} measured cycles");

        (WeakReference Window, WeakReference Panel, WeakReference Tool) Cycle(bool redock)
        {
            var active = hostedTool.GetValue(null) ?? throw new InvalidOperationException("Capture One lost the active tool before a lifecycle cycle.");
            var panel = Field(active, "panel")!;
            var spatial = Field(panel, "opticalPreview")!;
            Call(spatial, "SetEnabled", false);
            Call(active, "ShowFloating");
            var window = (Window)Field(active, "floating")!;
            var handle = new WindowInteropHelper(window).Handle;
            Check(window.GetType().Name == "DockableWindow" && window.IsVisible && handle != 0
                && ReferenceEquals(((ScrollViewer)Field(active, "floatingScroll")!).Content, panel),
                "the real native floater hosts this panel and owns a window handle");
            if (redock)
            {
                window.Close();
                Check(Field(active, "floating") is null && !IsWindow(handle)
                    && ReferenceEquals(((ContentControl)Prop(active, "Holder")!).Content, panel),
                    "closing the floater destroys its handle and redocks the same panel");
            }
            Call(panel, "ScheduleSelectionSync");
            var pending = (DispatcherOperation)Field(panel, "selectionSync")!;
            Check(pending.Status == DispatcherOperationStatus.Pending, "the retiring tool has a queued native selection callback");
            detach.Invoke(null, new object[] { false });
            Check(hostedTool.GetValue(null) is null && Field(active, "disposed") is true && Field(panel, "disposed") is true
                && Field(spatial, "disposed") is true && Field(spatial, "viewerFrame") is null,
                "detach disposes the retired tool and clears all of its paint state");
            Check(Field(active, "floating") is null && !IsWindow(handle) && ((ContentControl)Prop(active, "Holder")!).Content is null,
                "detach clears the dock content and destroys any remaining native floater handle");
            Check(pending.Status == DispatcherOperationStatus.Aborted && Field(panel, "selectionSync") is null,
                "detach aborts the retired tool's queued native selection callback");
            checkHost.Invoke(null, new object?[] { null, EventArgs.Empty });
            var replacement = hostedTool.GetValue(null);
            Check(replacement is not null && !ReferenceEquals(active, replacement)
                && Prop(Field(replacement!, "panel")!, "ExportBridgeStatus")?.ToString() == "Connected",
                "the real watchdog attaches a new native panel and Export bridge");
            Call(Field(Field(replacement!, "panel")!, "opticalPreview")!, "SetEnabled", false);
            return (new WeakReference(window), new WeakReference(panel), new WeakReference(active));
        }

        async Task Snapshot(int afterCycles)
        {
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(100);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            GC.Collect();
            process.Refresh();
            snapshots.Add(new
            {
                afterCycles, handles = process.HandleCount, gdi = GetGuiResources(process.Handle, 0),
                user = GetGuiResources(process.Handle, 1), committedBytes = process.PrivateMemorySize64,
                managedBytes = GC.GetTotalMemory(false), activeWindow = hostedTool.GetValue(null) is not null,
            });
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetGuiResources(nint process, uint flags);
}
