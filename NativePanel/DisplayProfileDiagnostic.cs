using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

internal static class DisplayProfileDiagnostic
{
    // Query the Windows profile association on demand. This does not tell us
    // which color transforms the host or the compositor actually applies.
    internal static string? ReadAssociatedIccSha256(object window)
    {
        if (window is not Form { IsDisposed: false, IsHandleCreated: true } form) return null;
        var device = Screen.FromHandle(form.Handle).DeviceName;
        var context = CreateDCW(device, device, null, IntPtr.Zero);
        if (context == IntPtr.Zero) return null;
        try
        {
            uint characters = 0;
            GetICMProfileW(context, ref characters, null);
            if (characters is 0 or > 32768) return null;
            var name = new StringBuilder((int)characters);
            if (!GetICMProfileW(context, ref characters, name)) return null;
            var profile = name.ToString();
            if (!Path.IsPathFullyQualified(profile)) return null;
            profile = Path.GetFullPath(profile);
            var profileDirectory = Path.Combine(Environment.SystemDirectory, "spool", "drivers", "color");
            if (!string.Equals(Path.GetDirectoryName(profile), profileDirectory, StringComparison.OrdinalIgnoreCase))
                return null;
            using var stream = File.OpenRead(profile);
            if (stream.Length is < 128 or > 16 * 1024 * 1024) return null;
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        finally { DeleteDC(context); }
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr CreateDCW(string driver, string device, string? output, IntPtr deviceMode);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetICMProfileW(IntPtr context, ref uint characters, StringBuilder? fileName);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr context);
}
