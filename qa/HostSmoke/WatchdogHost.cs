using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
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

        var file = Path.Combine(folder, "workspace-accepted-export.tif");
        var (job, operation, jobId) = await StartExport(hosted);
        // Exercise the real palette collection after the workspace removes the temporary tab.
        var restored = ResetPalette(oldTab);
        var restoredTab = tabField.GetValue(null);
        Check(!ReferenceEquals(restored, hosted) && Field(hosted, "disposed") is true
            && restoredTab is not null && tabs.Contains(restoredTab) && !tabs.Contains(oldTab)
            && tabs.Cast<object>().Count(item => item.GetType() == oldTab.GetType()) == 1,
            "the watchdog restores exactly one Spektrafilm tool after the native palette loses its temporary tab");
        Check(ReferenceEquals(Prop(palette, "Current"), nativeTab ?? restoredTab) && Field(oldExport, "disposed") is true
            && exportField.GetValue(null) is { } replacement && !ReferenceEquals(replacement, oldExport)
            && Prop(Field(restored, "panel")!, "ExportBridgeStatus")?.ToString() == "Connected",
            "palette recovery respects native tab selection and binds Export once to the replacement");

        var replacementPreview = Preview(restored);
        Check(ReferenceEquals(Prop(Field(replacementPreview, "exportActivity")!, "Active"), operation)
            && ((Button)Field(replacementPreview, "cancelExport")!).Visibility == Visibility.Visible
            && Prop(replacementPreview, "OutputRenderActive") is true
            && !((CancellationToken)Prop(operation, "Token")!).IsCancellationRequested,
            "the replacement panel shows and can cancel the same accepted export while the original panel retires");
        Call(replacementPreview, "SetEnabled", true);
        var duplicate = await Save(replacementPreview, (_, _) => throw new InvalidOperationException("Duplicate Save opened."));
        Check(!duplicate, "a replacement panel refuses a second export while the accepted job is active");
        Check(await job.WaitAsync(TimeSpan.FromSeconds(45)), "the approved export completes after the native palette rebuild");
        var completed = Receipt(replacementPreview)!;
        Check(Equals(Prop(completed, "JobId"), jobId) && Prop(completed, "Stage")?.ToString() == "Completed"
            && Equals(Prop(completed, "Destination"), file)
            && ((TextBlock)Field(replacementPreview, "exportStatus")!).Text.StartsWith("Saved 16-bit", StringComparison.Ordinal),
            "the replacement panel shows the original receipt's completed outcome at its approved path");
        using (var stream = File.OpenRead(file))
        {
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.OnDemand).Frames.Single();
            Check(frame.Format == PixelFormats.Rgb48 && frame.ColorContexts is { Count: 1 },
                "the preserved export reopens with 16 bits per channel and an embedded profile");
        }
        var published = SHA256.HashData(File.ReadAllBytes(file));
        await WaitForStocks(restored);

        var (overwrite, _, overwriteId) = await StartExport(restored);
        var last = ResetPalette(restoredTab!);
        var lastPreview = Preview(last);
        var cancel = (Button)Field(lastPreview, "cancelExport")!;
        Check(cancel.Visibility == Visibility.Visible, "the replacement tool exposes Cancel for the accepted overwrite");
        cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(!await overwrite.WaitAsync(TimeSpan.FromSeconds(45)), "Cancel on the replacement panel stops the in-flight export");
        var canceled = Receipt(lastPreview)!;
        Check(Equals(Prop(canceled, "JobId"), overwriteId) && Prop(canceled, "Stage")?.ToString() == "Canceled"
            && ((TextBlock)Field(lastPreview, "exportStatus")!).Text.Contains("canceled", StringComparison.Ordinal)
            && cancel.Visibility == Visibility.Collapsed && SHA256.HashData(File.ReadAllBytes(file)).SequenceEqual(published),
            "the canceled overwrite preserves all existing bytes and shows the matching terminal receipt on the replacement");
        await WaitForStocks(last);
        return last;

        object ResetPalette(object removing)
        {
            select.Invoke(null, new[] { palette, nativeTab });
            tabs.Remove(removing);
            tick(null, EventArgs.Empty);
            return hostedField.GetValue(null) ?? throw new InvalidOperationException("Spektrafilm did not reattach.");
        }

        static object Preview(object tool) => Field(Field(tool, "panel")!, "opticalPreview")!;
        static object? Receipt(object preview) => Call(Field(preview, "exportJobs")!, "ReadLatest");
        static Task<bool> Save(object preview, Func<SaveFileDialog, Window?, string?> choose) =>
            (Task<bool>)Call(preview, "ExportWithDestinationAsync", false, null, choose)!;

        async Task<(Task<bool> Job, object Operation, Guid JobId)> StartExport(object tool)
        {
            var panel = Field(tool, "panel")!;
            ((CheckBox)Field(panel, "enableSpektraFilm")!).IsChecked = true;
            var strength = Field(panel, "strength")!;
            strength.GetType().GetProperty("Value", Flags)!.SetValue(strength, 100d);
            var preview = Preview(tool);
            var previous = Receipt(preview);
            var task = Save(preview, (_, _) => file);
            var started = Stopwatch.GetTimestamp();
            while (!task.IsCompleted && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(20))
            {
                if (Field(preview, "exportWork") is { } active && Prop(active, "IsAccepted") is true
                    && Receipt(preview) is { } receipt && Prop(receipt, "Finished") is false
                    && Prop(receipt, "JobId") is Guid id && (previous is null || !Equals(Prop(previous, "JobId"), id))
                    && Equals(Prop(receipt, "Destination"), file))
                    return (task, active, id);
                await Task.Delay(10);
            }
            throw new InvalidOperationException("The native palette fixture did not observe an accepted export.");
        }

        static async Task WaitForStocks(object tool)
        {
            var started = Stopwatch.GetTimestamp();
            while (Prop(Field(tool, "panel")!, "StocksReady") is not true)
            {
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(12))
                    throw new TimeoutException("The replacement Spektrafilm panel did not load.");
                await Task.Delay(50);
            }
        }
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
