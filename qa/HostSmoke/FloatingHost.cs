using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using PixelRect = System.Drawing.Rectangle;

public static partial class StartupHook
{
    private delegate bool ReadMonitor(nint handle, out PixelRect window, out PixelRect workArea);

    private static async Task TestNativeWorkspaceSwitch(Type hook, object hosted)
    {
        Check(File.Exists(Path.Combine(folder, "Capture One user.config.before-proofing"))
            && File.Exists(Path.Combine(folder, "native-workspace-backup.json")), "native workspace changes start with verified settings and workspace backups");
        var manager = Field(hosted, "workspaceManager") ?? throw new InvalidOperationException("The native workspace manager is unavailable.");
        var contract = manager.GetType().GetInterface("P1.C1.ViewModels.Workspace.IWorkspaceManager")!;
        var active = contract.GetProperty("ActiveWorkspace")!;
        var workspaceContract = active.PropertyType;
        var name = workspaceContract.GetProperty("Name")!;
        var text = workspaceContract.GetProperty("Text")!;
        var settingsType = workspaceContract.GetProperty("SettingsType")!;
        var apply = contract.GetMethod("ApplyWorkspace")!;
        var change = contract.GetEvent("ActiveWorkspaceChanged")!;
        var original = active.GetValue(manager)!;
        string Selector(object workspace) => (string)(Equals(name.GetValue(workspace), "CustomWorkspace")
            ? text.GetValue(workspace) : name.GetValue(workspace))!;
        string Scope(object workspace) => Equals(name.GetValue(workspace), "CustomWorkspace")
            ? $"{settingsType.GetValue(workspace)}|CustomWorkspace|{Selector(workspace)}"
            : $"{settingsType.GetValue(workspace)}|{name.GetValue(workspace)}";
        string NormalizedName(object workspace) => ((string)name.GetValue(workspace)!).Replace(" ", "");
        var groups = new[] { "ApplicationPackageWorkspaces", "ChWorkspaces", "EnterpriseWorkspaces", "CustomWorkspaces" }.ToDictionary(group => group,
            group => ((IEnumerable)contract.GetProperty(group)!.GetValue(manager)!).Cast<object>().ToArray());
        var presets = groups.Values.SelectMany(items => items).DistinctBy(Scope).ToArray();
        File.WriteAllText(Path.Combine(folder, "native-workspace-presets.json"), JsonSerializer.Serialize(new
        {
            original = Scope(original),
            presets = groups.ToDictionary(group => group.Key,
                group => group.Value.Select(item => new { scope = Scope(item), label = text.GetValue(item) })),
        }, new JsonSerializerOptions { WriteIndented = true }));
        var firstPreset = presets.FirstOrDefault(item => NormalizedName(item) is "DefaultWorkspace" or "Default");
        var secondPreset = presets.FirstOrDefault(item => NormalizedName(item) is "SimplifiedWorkspace" or "Simplified");
        if (firstPreset is null || secondPreset is null)
        {
            var available = groups["CustomWorkspaces"].DistinctBy(Scope).Take(2).ToArray();
            if (available.Length != 2) throw new InvalidOperationException("The host did not expose two native workspace presets.");
            firstPreset = available[0];
            secondPreset = available[1];
        }
        var hostedField = hook.GetField("hostedTool", Flags)!;
        var changes = 0;
        EventHandler observed = (_, _) => changes++;
        var listener = observed.Method.CreateDelegate(change.EventHandlerType!, observed.Target);
        change.AddEventHandler(manager, listener);
        try
        {
            var first = await Switch(firstPreset);
            if (Field(first, "floating") is Window unexpected) unexpected.Close();
            Call(first, "ShowFloating");
            var originalFloater = (Window)Field(first, "floating")!;
            Check(originalFloater.IsVisible && originalFloater.ShowActivated, "the first real workspace opens an explicit native floater");

            var second = await Switch(secondPreset);
            Check(!originalFloater.IsVisible && Field(second, "floating") is null,
                "the workspace manager closes the old floater and the second real workspace starts docked");
            Call(second, "ShowFloating");
            ((Window)Field(second, "floating")!).Close();

            var returned = await Switch(firstPreset, expectFloating: true);
            var restored = (Window)Field(returned, "floating")!;
            Check(!restored.ShowActivated && !restored.IsActive
                && new WindowInteropHelper(restored).Handle != GetForegroundWindow(),
                $"returning to the first real workspace restores its native floater without activating it (active {restored.IsActive})");
            Check(changes >= 2, "the licensed workspace manager raised change notifications for both transitions");
            var livePanel = Field(returned, "panel")!;
            await TestNativeEffectExport(hook.GetField("exportHook", Flags)!, returned, livePanel,
                Field(livePanel, "opticalPreview")!, Field(returned, "mainWindow")!, "returned-workspace");
            restored!.Close();
            Check(ReferenceEquals(((ContentControl)Prop(returned, "Holder")!).Content, livePanel)
                && Prop(livePanel, "ExportBridgeStatus")?.ToString() == "Connected",
                "docking after the real workspace transitions preserves the panel and connected native Export bridge");
            File.WriteAllText(Path.Combine(folder, "native-workspace-switch.json"), JsonSerializer.Serialize(new
            {
                original = Scope(original), first = Scope(firstPreset), second = Scope(secondPreset),
                changes, openedWithoutFocus = true, exportedAppliedEffects = true,
            }));
        }
        finally
        {
            change.RemoveEventHandler(manager, listener);
            if (Scope(active.GetValue(manager)!) != Scope(original) && presets.Any(item => Scope(item) == Scope(original)))
                apply.Invoke(manager, new[] { Selector(original) });
        }
        File.WriteAllText(Path.Combine(folder, "first.passed"), $"PASS {checks} real native workspace switch checks");

        async Task<object> Switch(object preset, bool expectFloating = false)
        {
            var key = Scope(preset);
            if (Scope(active.GetValue(manager)!) != key) apply.Invoke(manager, new[] { Selector(preset) });
            var started = Stopwatch.GetTimestamp();
            object? settled = null;
            var stableReads = 0;
            while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(12))
            {
                if (hostedField.GetValue(null) is { } current && Equals(Field(current, "workspaceKey"), key)
                    && Field(current, "panel") is { } content && Prop(content, "StocksReady") is true
                    && (Field(current, "floating") is Window { IsVisible: true }) == expectFloating)
                {
                    if (!ReferenceEquals(settled, current)) stableReads = 0;
                    settled = current;
                    if (++stableReads == 8) return current;
                }
                else stableReads = 0;
                await Task.Delay(100);
            }
            throw new TimeoutException($"Capture One's active workspace did not settle on '{key}' (active '{Scope(active.GetValue(manager)!)}').");
        }
    }

    private static async Task TestFloatingWorkspaceLayout(object hosted, object panel)
    {
        const string other = "999|Disposable layout fixture";
        var phase = File.ReadAllText(Path.Combine(folder, "phase.txt"));
        var before = Path.Combine(folder, "workspace-float-first.json");
        var dispatcher = ((FrameworkElement)panel).Dispatcher;
        var holder = (ContentControl)Prop(hosted, "Holder")!;
        await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Check(Field(hosted, "workspaceManager") is not null && Field(hosted, "workspaceKey") is string,
            "the floating tool binds a typed active Capture One workspace");
        var key = (string)Field(hosted, "workspaceKey")!;
        var ownerHandle = ((System.Windows.Forms.Form)Field(hosted, "mainWindow")!).Handle;

        if (phase == "first")
        {
            Check(Field(hosted, "floating") is null && ReferenceEquals(holder.Content, panel),
                "previous preferences default to a docked tool");
            Call(hosted, "ShowFloating");
            var window = (Window)Field(hosted, "floating")!;
            Check(window.IsVisible && window.ShowActivated && ReferenceEquals(Scroll().Content, panel),
                "explicit Float Tool opens Capture One's native floating panel");
            var work = SystemParameters.WorkArea;
            // A native WPF resize switches an auto-sized window to manual sizing.
            window.SizeToContent = SizeToContent.Manual;
            window.Left = work.Left + 96;
            window.Top = work.Top + 82;
            window.Width = 366;
            window.Height = 300;
            var filmSection = ((Panel)Field(panel, "controls")!).Children.OfType<Expander>().First();
            filmSection.IsExpanded = true;
            window.UpdateLayout();
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Scroll().ScrollToVerticalOffset(96);
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var expectedBounds = window.RestoreBounds;
            var expectedScroll = Scroll().VerticalOffset;
            Check(!expectedBounds.IsEmpty && expectedScroll > 0,
                $"the real floating tool uses a distinct position and scroll offset (bounds {expectedBounds}, offset {expectedScroll}, "
                + $"scrollable {Scroll().ScrollableHeight}, viewport {Scroll().ViewportHeight}, extent {Scroll().ExtentHeight})");

            Call(hosted, "ApplyWorkspaceLayout", other);
            Check(Field(hosted, "floating") is null && ReferenceEquals(holder.Content, panel)
                && Saved(key).GetProperty("Open").GetBoolean(), "leaving the first workspace keeps its mode and a new workspace starts docked");
            Call(hosted, "ShowFloating");
            var otherWindow = (Window)Field(hosted, "floating")!;
            otherWindow.Width = 462;
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            otherWindow.Close();
            Check(!Saved(other).GetProperty("Open").GetBoolean() && Saved(key).GetProperty("Open").GetBoolean(),
                "explicit Dock Tool changes only that workspace's remembered mode");

            Call(hosted, "ApplyActiveWorkspaceLayout");
            await dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            AssertPassiveWindow(expectedBounds, expectedScroll);
            File.WriteAllText(before, JsonSerializer.Serialize(new
            {
                key, Left = expectedBounds.Left, Top = expectedBounds.Top,
                Width = expectedBounds.Width, Height = expectedBounds.Height, scroll = expectedScroll,
            }));
        }
        else
        {
            using var expected = JsonDocument.Parse(File.ReadAllText(before));
            var state = expected.RootElement;
            Check(key == state.GetProperty("key").GetString(), "the same native workspace identity survives Capture One restart");
            AssertPassiveWindow(new Rect(state.GetProperty("Left").GetDouble(), state.GetProperty("Top").GetDouble(),
                state.GetProperty("Width").GetDouble(), state.GetProperty("Height").GetDouble()), state.GetProperty("scroll").GetDouble());
            ((Window)Field(hosted, "floating")!).Close();
            Check(ReferenceEquals(holder.Content, panel) && !Saved(key).GetProperty("Open").GetBoolean()
                && !Saved(other).GetProperty("Open").GetBoolean(), "explicit close docks the restored tool and both saved workspace modes remain independent");
        }
        File.WriteAllText(Path.Combine(folder, phase + ".passed"), $"PASS {checks} native workspace floating checks");

        ScrollViewer Scroll() => (ScrollViewer)Field(hosted, "floatingScroll")!;

        JsonElement Saved(string workspace)
        {
            using var data = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "Panel preferences.json")));
            return data.RootElement.GetProperty("FloatingWorkspaces").GetProperty(workspace).Clone();
        }

        void AssertPassiveWindow(Rect expected, double scroll)
        {
            var window = (Window?)Field(hosted, "floating") ?? throw new InvalidOperationException("The saved floating window did not reopen.");
            var interop = new WindowInteropHelper(window);
            Check(window.GetType().Name == "DockableWindow" && window.IsVisible && !window.ShowActivated && !window.IsActive
                && interop.Owner == ownerHandle && interop.Handle != GetForegroundWindow() && ReferenceEquals(Scroll().Content, panel),
                "automatic restore opens the real owned floating tool without activating it");
            var bounds = window.RestoreBounds;
            Check(Math.Abs(bounds.Left - expected.Left) <= 2 && Math.Abs(bounds.Top - expected.Top) <= 2
                && Math.Abs(bounds.Width - expected.Width) <= 2 && Math.Abs(bounds.Height - expected.Height) <= 2
                && Math.Abs(Scroll().VerticalOffset - scroll) <= 2, "automatic restore preserves the first workspace placement and scroll");
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    private static async Task TestFloatingWorkArea(Assembly assembly, object hosted, object panel)
    {
        var nativeBounds = assembly.GetType("FloatingWindowBounds", true)!;
        var read = nativeBounds.GetMethod("TryRead", Flags)!.CreateDelegate<ReadMonitor>();
        var saved = hosted.GetType().GetField("floatingBounds", Flags)!;
        var holder = (ContentControl)Prop(hosted, "Holder")!;
        var work = SystemParameters.WorkArea;
        saved.SetValue(hosted, new Rect(work.Right - 16, work.Bottom - 16, 395, work.Height + 150));

        async Task<(Window Window, PixelRect Rect)> Open()
        {
            Call(hosted, "ShowFloating");
            var window = (Window)Field(hosted, "floating")!;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var scroll = (ScrollViewer)Field(hosted, "floatingScroll")!;
            Check(window.IsVisible && window.GetType().Name == "DockableWindow" && holder.Content is null
                && ReferenceEquals(scroll.Content, panel), "floating uses Capture One's native DockableWindow and retains the actual panel instance");
            var handle = new WindowInteropHelper(window).Handle;
            Check(read(handle, out var bounds, out var area) && area.Contains(bounds),
                $"actual floating window fits its work area ({bounds.Width} × {bounds.Height} within {area.Width} × {area.Height})");
            return (window, bounds);
        }
        try
        {
            var first = await Open();
            first.Window.Close();
            Check(Field(hosted, "floating") is null && ReferenceEquals(holder.Content, panel),
                "closing the native floater docks the same controls");
            var preferences = Path.Combine(folder, "Panel preferences.json");
            Check(File.Exists(preferences), "closing the native floater saves disposable preferences");
            using var placement = JsonDocument.Parse(File.ReadAllText(preferences));
            Check(placement.RootElement.GetProperty("Floating").ValueKind == JsonValueKind.Object,
                "disposable preferences contain a native floating placement");
            var second = await Open();
            Check(Math.Abs(second.Rect.Left - first.Rect.Left) <= 2 && Math.Abs(second.Rect.Top - first.Rect.Top) <= 2
                && Math.Abs(second.Rect.Width - first.Rect.Width) <= 2 && Math.Abs(second.Rect.Height - first.Rect.Height) <= 2,
                "reopening the docked tool restores the corrected native floating bounds");
        }
        finally
        {
            if (Field(hosted, "floating") is Window stillOpen) stillOpen.Close();
        }
        Check(ReferenceEquals(holder.Content, panel), "the disposable tool is back in its original docked owner");
    }
}
