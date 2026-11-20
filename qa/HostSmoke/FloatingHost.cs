using System;
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
