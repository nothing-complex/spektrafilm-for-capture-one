function Get-CatalogStateDigest([object[]] $records) {
    [string[]] $lines = @($records | ForEach-Object { $_.path + ':' + $_.sha256 })
    [Array]::Sort($lines, [StringComparer]::Ordinal)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Initialize-CatalogIntegrity {
    if (!('Spektrafilm.CatalogIntegrity' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
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
            string result = null;
            Result read = delegate(IntPtr state, int count, IntPtr values, IntPtr names)
            {
                if (count == 1) result = ReadValue(values, 0);
                return 0;
            };
            const string operation = "SQLite integrity verification";
            ExecuteReadOnly(path, "PRAGMA integrity_check(1)", read, operation);
            if (!String.Equals(result, "ok", StringComparison.Ordinal))
                throw Failure(operation, result ?? "SQLite did not report an integrity result.");
        }

        public static void VerifyRelated(string backup, string destination, Guid[] variants)
        {
            const string operation = "Capture One catalog identity verification";
            var required = new HashSet<Guid>(variants);
            if (required.Contains(Guid.Empty)) throw Failure(operation, "A saved variant identity is empty.");
            var original = ReadIdentity(backup, required, operation);
            var updated = ReadIdentity(destination, required, operation);
            if (original.Document != updated.Document)
                throw Failure(operation, "The existing catalog has a different native document identity.");
            foreach (var variant in required)
                if (original.ImagesByVariant[variant] != updated.ImagesByVariant[variant])
                    throw Failure(operation, "A saved variant belongs to a different native image in the existing catalog.");
        }

        private static Identity ReadIdentity(string path, HashSet<Guid> required, string operation)
        {
            var identity = new Identity();
            int documentRows = 0;
            bool invalidMapping = false;
            Result read = delegate(IntPtr state, int count, IntPtr values, IntPtr names)
            {
                if (count == 1)
                {
                    documentRows++;
                    Guid.TryParse(ReadValue(values, 0), out identity.Document);
                }
                else if (count == 2)
                {
                    Guid variant, image;
                    if (Guid.TryParse(ReadValue(values, 0), out variant) && required.Contains(variant))
                    {
                        if (!Guid.TryParse(ReadValue(values, 1), out image) || image == Guid.Empty || identity.ImagesByVariant.ContainsKey(variant))
                            invalidMapping = true;
                        else
                            identity.ImagesByVariant.Add(variant, image);
                    }
                }
                return 0;
            };
            var query = "SELECT ZDOCUMENTUUID FROM ZDOCUMENTCONTENT;";
            if (required.Count != 0)
                query += "SELECT v.ZVARIANTUUID, i.ZIMAGEUUID FROM ZVARIANT v LEFT JOIN ZIMAGE i ON i.Z_PK = v.ZIMAGE;";
            ExecuteReadOnly(path, query, read, operation);
            if (documentRows != 1 || identity.Document == Guid.Empty)
                throw Failure(operation, "The catalog does not contain one valid native document identity.");
            if (invalidMapping || identity.ImagesByVariant.Count != required.Count)
                throw Failure(operation, "The catalog does not uniquely identify each saved variant and its native image.");
            return identity;
        }

        private sealed class Identity
        {
            internal Guid Document;
            internal readonly Dictionary<Guid, Guid> ImagesByVariant = new Dictionary<Guid, Guid>();
        }

        private static void ExecuteReadOnly(string path, string query, Result read, string operation)
        {
            // The caller verifies the database is closed and holds it read-only.
            // Immutable URI mode prevents SQLite journal reads or writes.
            var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?mode=ro&immutable=1";
            IntPtr database;
            int open = sqlite3_open_v2(Utf8(uri), out database, 0x41, IntPtr.Zero); // READONLY | URI
            try
            {
                if (open != 0) throw Failure(operation, ReadError(database));
                var clock = Stopwatch.StartNew();
                Progress progress = delegate { return clock.ElapsedMilliseconds >= 120000 ? 1 : 0; };
                sqlite3_progress_handler(database, 50000, progress, IntPtr.Zero);
                int status = sqlite3_exec(database, Utf8(query), read, IntPtr.Zero, IntPtr.Zero);
                GC.KeepAlive(progress);
                if (status == 9) throw new IOException(operation + " timed out. Existing backups were kept.");
                if (status != 0) throw Failure(operation, ReadError(database));
            }
            finally { if (database != IntPtr.Zero) sqlite3_close(database); }
        }

        private static byte[] Utf8(string text) { return Encoding.UTF8.GetBytes(text + "\0"); }
        private static string ReadValue(IntPtr values, int index) { return Marshal.PtrToStringAnsi(Marshal.ReadIntPtr(values, index * IntPtr.Size)); }
        private static string ReadError(IntPtr database)
        {
            return database == IntPtr.Zero ? "SQLite could not open the copy." : Marshal.PtrToStringAnsi(sqlite3_errmsg(database));
        }
        private static IOException Failure(string operation, string detail)
        {
            return new IOException("The catalog copy failed " + operation + ". Existing backups were kept. " + detail);
        }
    }
}
'@
    }
}

function Confirm-CatalogIntegrity([string] $path) {
    Initialize-CatalogIntegrity
    [Spektrafilm.CatalogIntegrity]::Verify($path)
}

function Confirm-RelatedCatalogs([string] $backup, [string] $destination, [Guid[]] $variants) {
    Initialize-CatalogIntegrity
    [Spektrafilm.CatalogIntegrity]::VerifyRelated($backup, $destination, $variants)
}
