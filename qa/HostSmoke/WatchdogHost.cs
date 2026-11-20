using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

public static partial class StartupHook
{
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
