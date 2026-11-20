using System;
using System.Drawing;
using System.Runtime.InteropServices;

internal static class FloatingWindowBounds
{
    private const uint NearestMonitor = 2;
    private const uint PreserveZOrderAndActivation = 0x0004 | 0x0010 | 0x0200;

    internal static void KeepVisible(nint handle)
    {
        if (!TryRead(handle, out var bounds, out var workArea)) return;
        var fitted = Fit(bounds, workArea);
        if (fitted == bounds) return;
        SetWindowPos(handle, 0, fitted.X, fitted.Y, fitted.Width, fitted.Height, PreserveZOrderAndActivation);
    }

    internal static Rectangle Fit(Rectangle bounds, Rectangle workArea)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0 || workArea.Width <= 0 || workArea.Height <= 0)
            return bounds;
        var width = Math.Min(bounds.Width, workArea.Width);
        var height = Math.Min(bounds.Height, workArea.Height);
        return new Rectangle(
            Math.Clamp(bounds.X, workArea.Left, workArea.Right - width),
            Math.Clamp(bounds.Y, workArea.Top, workArea.Bottom - height),
            width, height);
    }

    internal static bool TryRead(nint handle, out Rectangle bounds, out Rectangle workArea)
    {
        bounds = workArea = Rectangle.Empty;
        if (handle == 0 || !GetWindowRect(handle, out var window))
            return false;
        var monitor = MonitorFromRect(ref window, NearestMonitor);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfoW(monitor, ref info))
            return false;
        bounds = window.ToRectangle();
        workArea = info.Work.ToRectangle();
        return bounds.Width > 0 && bounds.Height > 0 && workArea.Width > 0 && workArea.Height > 0;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromRect(ref NativeRect rect, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint order, int x, int y, int width, int height, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        internal int Left, Top, Right, Bottom;
        internal readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal uint Size;
        internal NativeRect Monitor, Work;
        internal uint Flags;
    }
}
