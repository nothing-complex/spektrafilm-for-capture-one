function Get-CatalogKey([string] $path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($path).ToUpperInvariant())))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Get-CatalogStateDigest([object[]] $records) {
    [string[]] $lines = @($records | ForEach-Object { $_.path + ':' + $_.sha256 })
    [Array]::Sort($lines, [StringComparer]::Ordinal)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($lines -join "`n"))))).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose() }
}

function Get-LooseCatalogLookFiles([string] $dataRoot, [string] $key) {
    foreach ($scope in @('Variant looks', 'Look recovery')) {
        $root = Join-Path (Join-Path $dataRoot $scope) $key
        if (!(Test-Path -LiteralPath $root -PathType Container)) { continue }
        $root = (Get-Item -LiteralPath $root -Force).FullName
        $directories = New-Object 'System.Collections.Generic.Queue[string]'
        $directories.Enqueue($root)
        while ($directories.Count -gt 0) {
            $directory = Get-Item -LiteralPath $directories.Dequeue() -Force
            if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Saved-look backup cannot follow linked folders.' }
            foreach ($item in Get-ChildItem -LiteralPath $directory.FullName -Force | Sort-Object Name) {
                if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Saved-look backup cannot follow linked files.' }
                if ($item.PSIsContainer) { $directories.Enqueue($item.FullName); continue }
                [pscustomobject]@{
                    Relative = $scope + '/' + $item.FullName.Substring($root.Length + 1).Replace('\', '/')
                    Path = $item.FullName
                }
            }
        }
    }
}

function Read-LookGenerationCapsule([byte[]] $record, [int] $offset, [string] $magic, [string] $key, [Guid] $variant) {
    # Same bounded wire format as VariantLookGenerationJournal. This reader also runs on Windows PowerShell 5.1.
    if ($record.Length - $offset -le 96 -or [Text.Encoding]::ASCII.GetString($record, $offset, 4) -cne $magic) {
        throw 'A saved look generation is truncated or has an unknown format.'
    }
    $catalog = [BitConverter]::ToString($record, $offset + 4, 32).Replace('-', '')
    $variantBytes = [BitConverter]::ToString($variant.ToByteArray())
    if ($catalog -ne $key -or [BitConverter]::ToString($record, $offset + 36, 16) -cne $variantBytes) {
        throw 'A saved look generation belongs to another catalog or variant.'
    }
    $generation = [BitConverter]::ToInt64($record, $offset + 52)
    $length = [BitConverter]::ToInt32($record, $offset + 60)
    if ($generation -lt 0 -or $length -lt 1 -or $length -gt 64KB -or $length + 96 -gt $record.Length - $offset) {
        throw 'A saved look generation has invalid fields.'
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $digest = $sha.ComputeHash($record, $offset, 64 + $length)
        $payloadDigest = $sha.ComputeHash($record, $offset + 64, $length)
    }
    finally { $sha.Dispose() }
    if ([BitConverter]::ToString($digest) -cne [BitConverter]::ToString($record, $offset + 64 + $length, 32)) {
        throw 'A saved look generation failed its checksum.'
    }
    $payload = New-Object byte[] $length
    [Array]::Copy($record, $offset + 64, $payload, 0, $length)
    return [pscustomobject]@{ Generation=$generation; Payload=$payload; PayloadDigest=$payloadDigest; Next=$offset + $length + 96 }
}

function Get-CatalogLookFiles([string] $dataRoot, [string] $key) {
    $parent = Join-Path $dataRoot 'Look generations'
    $root = Join-Path $parent $key
    if (!(Test-Path -LiteralPath $root)) { Get-LooseCatalogLookFiles $dataRoot $key; return }
    foreach ($path in $parent,$root) {
        $directory = Get-Item -LiteralPath $path -Force
        if (!$directory.PSIsContainer -or ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Saved-look backup cannot follow a linked or invalid generation directory.'
        }
    }
    $files = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in @(Get-LooseCatalogLookFiles $dataRoot $key)) { $files.Add($file.Relative, $file) }
    foreach ($journal in Get-ChildItem -LiteralPath $root -Filter '*.generations' -Force) {
        if ($journal.PSIsContainer -or $journal.Name -cnotmatch '^([0-9a-f]{32})\.generations$' -or
            ($journal.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Saved-look backup found an invalid generation file.'
        }
        $leaf = $Matches[1]
        $variant = [Guid]::ParseExact($leaf, 'N')
        if ($variant -eq [Guid]::Empty) { throw 'A saved look generation has no variant identity.' }
        $lock = Get-Item -LiteralPath ($journal.FullName + '.lock') -Force -ErrorAction Stop
        if ($lock.PSIsContainer -or ($lock.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Saved-look backup found an invalid generation lock.'
        }
        $lease = [IO.File]::Open($lock.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        try {
            $journalStream = [IO.File]::OpenRead($journal.FullName)
            try {
                if ($journalStream.Length -le 96 -or $journalStream.Length -gt (2 * (64KB + 96))) { throw 'A saved generation pair has an invalid size.' }
                $reader = [IO.BinaryReader]::new($journalStream)
                $record = $reader.ReadBytes([int]$journalStream.Length)
            } finally { $journalStream.Dispose() }
            $previous = if ([Text.Encoding]::ASCII.GetString($record, 0, 4) -ceq 'SLGP') {
                Read-LookGenerationCapsule $record 0 'SLGP' $key $variant
            }
            $offset = if ($previous) { $previous.Next } else { 0 }
            $expected = if ($previous) { $previous.Generation + 1 } else { 1 }
            $current = Read-LookGenerationCapsule $record $offset 'SLGC' $key $variant
            if ($current.Generation -ne $expected -or $current.Next -ne $record.Length) {
                throw 'A saved generation pair is out of order or contains unexpected records.'
            }

            $primary = 'Variant looks/' + $leaf + '.json'
            $priorPath = 'Variant looks/' + $leaf + '.previous.look'
            if ($files.ContainsKey($primary)) {
                $looseHash = (Get-FileHash -LiteralPath $files[$primary].Path -Algorithm SHA256).Hash
                if ($looseHash -ne [BitConverter]::ToString($current.PayloadDigest).Replace('-', '') -and
                    (!$previous -or $looseHash -ne [BitConverter]::ToString($previous.PayloadDigest).Replace('-', ''))) {
                    throw "The saved look for variant $leaf conflicts with its verified journal. Both versions were preserved."
                }
            }
            $files[$primary] = [pscustomobject]@{ Relative=$primary; GenerationBytes=$current.Payload }
            if ($previous) {
                $priorBytes = [byte[]]([Text.Encoding]::ASCII.GetBytes('SFL1') + $variant.ToByteArray() + $previous.PayloadDigest + $previous.Payload)
                $files[$priorPath] = [pscustomobject]@{ Relative=$priorPath; GenerationBytes=$priorBytes }
            } elseif ($files.ContainsKey($priorPath)) {
                throw "The previous look for variant $leaf conflicts with its verified journal. Both versions were preserved."
            }
        } finally { $lease.Dispose() }
    }
    return $files.Values
}

function Open-CatalogLookEntry($file) {
    if ($file.PSObject.Properties['GenerationBytes']) { return [IO.MemoryStream]::new($file.GenerationBytes, $false) }
    return [IO.File]::Open($file.Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
}

function Get-CatalogLookEntryHash($file) {
    $stream = Open-CatalogLookEntry $file
    try { return (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash }
    finally { $stream.Dispose() }
}

function Write-CatalogLookArchive([IO.Stream] $stream, [string] $key, [object[]] $files) {
    Add-Type -AssemblyName System.IO.Compression
    $records = New-Object 'System.Collections.Generic.List[object]'
    $zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($file in $files) {
            $sourceStream = Open-CatalogLookEntry $file
            try {
                $entry = $zip.CreateEntry($file.Relative, [IO.Compression.CompressionLevel]::Optimal)
                $output = $entry.Open()
                try { $sourceStream.CopyTo($output) } finally { $output.Dispose() }
                $sourceStream.Position = 0
                $digest = (Get-FileHash -InputStream $sourceStream -Algorithm SHA256).Hash.ToLowerInvariant()
                $records.Add([ordered]@{path=$file.Relative;bytes=$sourceStream.Length;sha256=$digest})
            } finally { $sourceStream.Dispose() }
        }
        $manifest = [ordered]@{schemaVersion=2;catalogKey=$key;stateDigest=(Get-CatalogStateDigest $records.ToArray());files=$records.ToArray()}
        $entry = $zip.CreateEntry('manifest.json')
        $writer = New-Object IO.StreamWriter($entry.Open(), (New-Object Text.UTF8Encoding($false)))
        try { $writer.Write(($manifest | ConvertTo-Json -Depth 6)) } finally { $writer.Dispose() }
    } finally { $zip.Dispose() }
    return $manifest
}

function Test-HasRecoverableLook([string[]] $paths) {
    foreach ($path in $paths) {
        if ($path -cmatch '^(Variant looks/[0-9a-f]{32}\.json|Look recovery/[0-9a-f]{32}\.pending\.json)$') { return $true }
    }
    return $false
}

function Get-CatalogRestoreReceiptPath([string] $dataRoot, [string] $catalog) {
    Join-Path (Join-Path $dataRoot 'Catalog restores') ((Get-CatalogKey $catalog) + '.json')
}

function Read-CatalogRestoreReceipt([string] $path) {
    if (!(Test-Path -LiteralPath $path)) { return }
    $file = Get-Item -LiteralPath $path -Force
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $file.Length -gt 64KB) {
        throw 'The interrupted catalog restore receipt is invalid.'
    }
    $receipt = [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json
    if ($receipt.Version -ne 1 -or $receipt.Id -cnotmatch '^[0-9a-f]{32}$' -or
        $receipt.CatalogKey -ne [IO.Path]::GetFileNameWithoutExtension($path) -or
        $receipt.BackupDatabase -isnot [string] -or ![IO.Path]::IsPathRooted($receipt.BackupDatabase) -or
        $receipt.BackupHash -notmatch '^[0-9A-F]{64}$' -or $receipt.ArchiveHash -notmatch '^[0-9A-F]{64}$' -or
        $receipt.DestinationHash -notmatch '^[0-9A-F]{64}$' -or $receipt.SavedLooksOnly -isnot [bool] -or
        $receipt.AllowUpdatedCatalog -isnot [bool] -or !$receipt.Scopes -or
        ($receipt.PSObject.Properties['LatestLocalLooks'] -and $receipt.LatestLocalLooks -isnot [bool]) -or
        ($receipt.LatestLocalLooks -and !$receipt.SavedLooksOnly)) {
        throw 'The interrupted catalog restore receipt is invalid.'
    }
    foreach ($scope in @('Variant looks', 'Look recovery')) {
        if (!$receipt.Scopes.PSObject.Properties[$scope] -or
            ($null -ne $receipt.Scopes.$scope -and $receipt.Scopes.$scope -notmatch '^[0-9A-F]{64}$')) {
            throw 'The interrupted catalog restore receipt has an invalid saved-look checksum.'
        }
    }
    return $receipt
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

        public sealed class VariantProof
        {
            internal readonly Guid Variant;
            internal readonly Guid Document;
            internal readonly Guid Image;

            public VariantProof(Guid variant, Guid document, Guid image)
            {
                Variant = variant;
                Document = document;
                Image = image;
            }
        }

        public static void VerifyRelated(string backup, string destination, Guid[] variants, VariantProof[] proofs)
        {
            const string operation = "Capture One catalog identity verification";
            var required = new HashSet<Guid>(variants);
            if (required.Contains(Guid.Empty)) throw Failure(operation, "A saved variant identity is empty.");
            var live = new Dictionary<Guid, VariantProof>();
            foreach (var proof in proofs)
            {
                if (!required.Contains(proof.Variant)) continue;
                if (proof.Document == Guid.Empty || proof.Image == Guid.Empty || live.ContainsKey(proof.Variant))
                    throw Failure(operation, "A saved native variant identity is invalid or duplicated.");
                live.Add(proof.Variant, proof);
            }
            var original = ReadIdentity(backup, required, operation, live.Count == 0);
            var updated = ReadIdentity(destination, required, operation, true);
            if (original.Document != updated.Document)
                throw Failure(operation, "The existing catalog has a different native document identity.");
            foreach (var variant in required)
            {
                var destinationImage = updated.ImagesByVariant[variant];
                VariantProof added;
                bool hasProof = live.TryGetValue(variant, out added);
                if (hasProof && (added.Document != original.Document || added.Image != destinationImage))
                    throw Failure(operation, "The saved native variant identity does not match the destination catalog or image.");
                Guid archived;
                if (original.ImagesByVariant.TryGetValue(variant, out archived))
                {
                    if (archived != destinationImage)
                        throw Failure(operation, "A saved variant belongs to a different native image in the existing catalog.");
                }
                else if (!hasProof)
                    throw Failure(operation, "The archived catalog does not identify a saved variant and no live identity was recorded.");
            }
        }

        public static Guid DocumentId(string path)
        {
            return ReadIdentity(path, new HashSet<Guid>(), "Capture One catalog identity verification", true).Document;
        }

        private static Identity ReadIdentity(string path, HashSet<Guid> required, string operation, bool requireAll)
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
            if (invalidMapping || (requireAll && identity.ImagesByVariant.Count != required.Count))
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

function Confirm-RelatedCatalogs([string] $backup, [string] $destination, [Guid[]] $variants, [object[]] $proofs) {
    Initialize-CatalogIntegrity
    $native = foreach ($proof in $proofs) {
        [Spektrafilm.CatalogIntegrity+VariantProof]::new($proof.Variant, $proof.Document, $proof.Image)
    }
    [Spektrafilm.CatalogIntegrity]::VerifyRelated($backup, $destination, $variants, [Spektrafilm.CatalogIntegrity+VariantProof[]]@($native))
}

function Get-CatalogDocumentId([string] $path) {
    try {
        Initialize-CatalogIntegrity
        return [Spektrafilm.CatalogIntegrity]::DocumentId($path).ToString('D')
    } catch {
        Write-Verbose "Catalog document identity unavailable: $($_.Exception.Message)"
        return $null
    }
}
