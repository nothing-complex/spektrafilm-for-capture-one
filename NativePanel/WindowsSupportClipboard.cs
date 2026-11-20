using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class WindowsSupportClipboard
{
    internal static void WriteText(string text)
    {
        var report = new DataObject();
        report.SetText(text, TextDataFormat.UnicodeText);
        Clipboard.SetDataObject(report, copy: true, retryTimes: 0, retryDelay: 0);
    }

    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();
}
