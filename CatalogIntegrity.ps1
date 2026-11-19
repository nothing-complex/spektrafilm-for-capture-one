function Confirm-CatalogIntegrity([string] $path) {
    if (!('Spektrafilm.CatalogIntegrity' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Spektrafilm
{
    public static class CatalogIntegrity
    {
        private const string Sqlite = "winsqlite3.dll";
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Result(IntPtr unused, int count, IntPtr values, IntPtr names);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int Progress(IntPtr unused);

        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_open_v2(byte[] file, out IntPtr database, int flags, IntPtr vfs);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_exec(IntPtr database, byte[] statement, Result result, IntPtr state, IntPtr error);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
        private static extern void sqlite3_progress_handler(IntPtr database, int steps, Progress progress, IntPtr state);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr sqlite3_errmsg(IntPtr database);
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [DllImport(Sqlite, CallingConvention = CallingConvention.Cdecl)]
        private static extern int sqlite3_close(IntPtr database);

        public static void Verify(string path)
        {
            // The file is a closed, private backup candidate. Immutable URI mode
            // prevents journal reads or writes and leaves the live catalog alone.
            var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?mode=ro&immutable=1";
            IntPtr database;
            int open = sqlite3_open_v2(Utf8(uri), out database, 0x41, IntPtr.Zero); // READONLY | URI
            try
            {
                if (open != 0) throw Failure(database, null);
                var clock = Stopwatch.StartNew();
                Progress progress = delegate { return clock.ElapsedMilliseconds >= 120000 ? 1 : 0; };
                sqlite3_progress_handler(database, 50000, progress, IntPtr.Zero);
                string result = null;
                Result read = delegate(IntPtr state, int count, IntPtr values, IntPtr names)
                {
                    if (count == 1) result = Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(values));
                    return 0;
                };
                int status = sqlite3_exec(database, Utf8("PRAGMA integrity_check(1)"), read, IntPtr.Zero, IntPtr.Zero);
                GC.KeepAlive(progress);
                if (status == 9) throw new IOException("Catalog integrity verification timed out. Existing backups were kept.");
                if (status != 0 || !String.Equals(result, "ok", StringComparison.Ordinal)) throw Failure(database, result);
            }
            finally { if (database != IntPtr.Zero) sqlite3_close(database); }
        }

        private static byte[] Utf8(string text) { return Encoding.UTF8.GetBytes(text + "\0"); }
        private static IOException Failure(IntPtr database, string result)
        {
            string detail = result ?? (database == IntPtr.Zero ? "SQLite could not open the copy." : Marshal.PtrToStringAnsi(sqlite3_errmsg(database)));
            return new IOException("The catalog copy failed SQLite integrity verification. Existing backups were kept. " + detail);
        }
    }
}
'@
    }
    [Spektrafilm.CatalogIntegrity]::Verify($path)
}
