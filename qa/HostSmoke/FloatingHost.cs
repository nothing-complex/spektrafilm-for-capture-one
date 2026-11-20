using System;
using System.IO;
using System.Reflection;
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
