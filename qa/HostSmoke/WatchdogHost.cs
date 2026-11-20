using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

public static partial class StartupHook
{
    private static async Task<object> TestWorkspacePaletteRecovery(Type hook, object hosted)
    {
        var lookup = hook.Assembly.GetType("HostDocumentAccess")!.GetMethod("Palette", Flags)!
            .CreateDelegate<Func<Forms.Form?, object?>>();
        using (var unready = new Forms.Form())
            Check(lookup(null) is null && lookup(unready) is null && !unready.IsHandleCreated,
                "palette discovery defers an uncreated window without forcing a handle or reading native properties");
        using (var disposed = new Forms.Form())
        {
            disposed.Dispose();
            Check(lookup(disposed) is null, "palette discovery skips a disposed window");
        }

        var hostedField = hook.GetField("hostedTool", Flags)!;
        var tabField = hook.GetField("tab", Flags)!;
        var exportField = hook.GetField("exportHook", Flags)!;
        var oldExport = exportField.GetValue(null)!;
        var palette = lookup((Forms.Form)Field(hosted, "mainWindow")!)!;
        var oldTab = tabField.GetValue(null)!;
        var tabs = (IList)Prop(palette, "Tabs")!;
        var nativeTab = hook.GetField("previousTab", Flags)!.GetValue(null);
        if (nativeTab is null || !tabs.Contains(nativeTab))
            nativeTab = tabs.Cast<object>().FirstOrDefault(item => !ReferenceEquals(item, oldTab));
        var select = hook.GetMethod("SelectTab", Flags)!;
        var tick = hook.GetMethod("OnTick", Flags)!.CreateDelegate<Action<object?, EventArgs>>();
        select.Invoke(null, new[] { palette, nativeTab });
        Check(!ReferenceEquals(Prop(palette, "Current"), oldTab), "the workspace can deselect the Spektrafilm tab");
        tick(null, EventArgs.Empty);
        Check(ReferenceEquals(hostedField.GetValue(null), hosted) && ReferenceEquals(exportField.GetValue(null), oldExport),
            "hiding Spektrafilm behind a native tool tab preserves the existing tool and Export binding");

        // Exercise the real palette collection after the workspace removes the temporary tab.
        tabs.Remove(oldTab);
        tick(null, EventArgs.Empty);
        var restored = hostedField.GetValue(null) ?? throw new InvalidOperationException("Spektrafilm did not reattach.");
        var restoredTab = tabField.GetValue(null);
        Check(!ReferenceEquals(restored, hosted) && Field(hosted, "disposed") is true
            && restoredTab is not null && tabs.Contains(restoredTab) && !tabs.Contains(oldTab)
            && tabs.Cast<object>().Count(item => item.GetType() == oldTab.GetType()) == 1,
            "the watchdog restores exactly one Spektrafilm tool after the native palette loses its temporary tab");
        Check(ReferenceEquals(Prop(palette, "Current"), nativeTab ?? restoredTab) && Field(oldExport, "disposed") is true
            && exportField.GetValue(null) is { } replacement && !ReferenceEquals(replacement, oldExport)
            && Prop(Field(restored, "panel")!, "ExportBridgeStatus")?.ToString() == "Connected",
            "palette recovery respects native tab selection and binds Export once to the replacement");
        var started = Stopwatch.GetTimestamp();
        while (Prop(Field(restored, "panel")!, "StocksReady") is not true)
        {
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(12))
                throw new TimeoutException("The replacement Spektrafilm panel did not load.");
            await Task.Delay(50);
        }
        return restored;
    }

    private static async Task TestAdaptiveWatchdog(Type hook, object panel, Guid expected)
    {
        var timer = (Forms.Timer)hook.GetField("timer", Flags)!.GetValue(null)!;
        var notify = hook.GetMethod("QueueHostCheck", Flags)!;
        var subscription = hook.GetField("documentChanges", Flags)!;
        var queued = hook.GetField("checkQueued", Flags)!;
        var tick = hook.GetMethod("OnTick", Flags)!.CreateDelegate<Action<object?, EventArgs>>();
        Check(subscription.GetValue(null) is IDisposable, "subscribed to the actual licensed Capture One document manager event");
        var start = Stopwatch.GetTimestamp();
        const int idleChecks = 1000;
        for (var index = 0; index < idleChecks; index++) tick(null, EventArgs.Empty);
        var milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        Check(timer.Interval == 4000 && Equals(Field(panel, "displayedVariant"), expected),
            "stable attachment backs off the defensive watchdog from one second to four");
        notify.Invoke(null, null);
        notify.Invoke(null, null);
        Check(Equals(queued.GetValue(null), 1), "document notifications collapse into a single UI thread check");
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(Equals(queued.GetValue(null), 0) && timer.Interval == 1000,
            "document notification wakes the actual UI dispatcher and restores fast checks");
        for (var index = 0; index < 3; index++) tick(null, EventArgs.Empty);
        Check(timer.Interval == 4000, "quiet attachment returns to the slow watchdog after the event");
        panel.GetType().GetField("displayedVariant", Flags)!.SetValue(panel, null);
        tick(null, EventArgs.Empty);
        Check(Equals(Field(panel, "displayedVariant"), expected), "the defensive watchdog recovers a missed selection event");
        var details = JsonSerializer.Serialize(new { idleChecks, totalMilliseconds = milliseconds, meanMilliseconds = milliseconds / idleChecks,
            recoveryIntervalMs = 1000, stableIntervalMs = timer.Interval }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(folder, "watchdog-" + File.ReadAllText(Path.Combine(folder, "phase.txt")) + ".json"), details);
    }
}
