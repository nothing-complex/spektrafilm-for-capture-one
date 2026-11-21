[CmdletBinding()]
param([string] $ResultsDirectory)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (!$ResultsDirectory) { $ResultsDirectory = Join-Path $repo '.test-results' }
$ResultsDirectory=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ResultsDirectory)
$root=Join-Path $ResultsDirectory ('backup-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($root)
$userData=Join-Path $root 'app-data'
$appName='Spektrafilm backup fixture'
# Load only these production function definitions, never execute the launcher entry point.
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'Start-Spektrafilm.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Launcher syntax failed.'}
. (Join-Path $repo 'CatalogIntegrity.ps1')
foreach($name in @('Test-CatalogLookState','New-LookBackup','Save-CatalogBackup',
        'Read-LookRecoveryManifest','Find-CatalogLookBackup','Test-CurrentBackupLookState','Restore-DiscoveredCatalogLooks',
        'Prepare-SpektrafilmCatalog')) {
    $definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true)
    . ([scriptblock]::Create($definition.Extent.Text))
}
# Source is synthetic and never opened by C1; bypass only the process probe in this isolated scope.
function Confirm-CatalogIsIdle([string] $path) {
    if(!$path.StartsWith($root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Test attempted access outside its fixture.'}
}
$count=0
function Assert([bool] $condition,[string] $name){if(!$condition){throw "FAIL: $name"};$script:count++}
$db=Join-Path $root 'test.cocatalogdb'
# Two SQLite pages, generated with page_size=512 and a sample table containing two synthetic rows.
$compressed = [Convert]::FromBase64String('H4sIAAAAAAAC/wsO9MksSVVIyy/KTSxRMGZgYmBkZHBQUGCAACYGBGAEYhY0PlGAF6SYcRkDEI0CskAEI7u4pCRjI3dJYlJOanFibgGMZHIOcnUMcVUIcXTycVWAiGlkpihk5pWkpqcWKRQUZeYmFlUqZKdW6ijkJeamKpSkVpQo5OUDcWlOjiYobpgYbzIwvgcSo2D4ABEmZgb9zNzcUnCSUUjLrCgpLUrlZ2RmUC1JLS5RSE4sSczJTwcAru1jjQAEAAA=')
$compressedStream = New-Object IO.MemoryStream(,$compressed)
$decoder = New-Object IO.Compression.GZipStream($compressedStream, [IO.Compression.CompressionMode]::Decompress)
$database = New-Object IO.MemoryStream
try { $decoder.CopyTo($database); $bytes = $database.ToArray() }
finally { $decoder.Dispose(); $compressedStream.Dispose(); $database.Dispose() }
[IO.File]::WriteAllBytes($db,$bytes)
$originalHash=(Get-FileHash -LiteralPath $db -Algorithm SHA256).Hash
$sha=[Security.Cryptography.SHA256]::Create()
try{$key=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($db).ToUpperInvariant())))).Replace('-','')}finally{$sha.Dispose()}
$look=Join-Path (Join-Path (Join-Path $userData 'Variant looks') $key) '11111111111111111111111111111111.json'
$recovery=Join-Path (Join-Path (Join-Path $userData 'Look recovery') $key) '11111111111111111111111111111111.pending.json'
foreach($file in @($look,$recovery)){[void][IO.Directory]::CreateDirectory((Split-Path -Parent $file))}
[IO.File]::WriteAllText($look,'{"value":1}')
[IO.File]::WriteAllText($recovery,'{"pending":true}')
$clock = { [DateTime]::new(2026, 11, 19, 12, 0, 0, [DateTimeKind]::Utc) }
$first=Save-CatalogBackup $db -Clock $clock
Assert (Test-Path -LiteralPath ($first+'.spektrafilm.zip')) 'Joint look archive created'
Assert (Test-Path -LiteralPath ($first+'.manifest.json')) 'Joint manifest created'
$manifest=[IO.File]::ReadAllText($first+'.manifest.json')|ConvertFrom-Json
Assert ($manifest.files.Count -eq 2) 'Both saved and recovery records included'
Assert ($manifest.catalogSha256 -eq $originalHash) 'DB fingerprint matches original'
Assert ($manifest.schemaVersion -eq 2) 'New backup uses portable file ordering'
Assert ($manifest.databaseIntegrity -eq 'ok') 'Passed full SQLite integrity check before publishing'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($first+'.spektrafilm.zip')
try {
    foreach($entryName in @('Variant looks/11111111111111111111111111111111.json','Look recovery/11111111111111111111111111111111.pending.json')) {
        Assert ($null -ne $zip.GetEntry($entryName)) "Archive contains $entryName"
    }
} finally {$zip.Dispose()}
$same=Save-CatalogBackup $db -Clock $clock
Assert ($same -eq $first) 'Unchanged DB and state reuse complete backup'
[IO.File]::WriteAllText($look,'{"value":2}')
$second=Save-CatalogBackup $db -Clock $clock
Assert ($second -ne $first) 'Look-only edit creates a new joint backup even in the same clock tick'
Assert ($first -like '*-000000.cocatalogdb' -and $second -like '*-000001.cocatalogdb') 'Same-tick generations have distinct increasing names'
$zip=[IO.Compression.ZipFile]::OpenRead($second+'.spektrafilm.zip')
try {
    $reader=New-Object IO.StreamReader($zip.GetEntry('Variant looks/11111111111111111111111111111111.json').Open())
    try {Assert ($reader.ReadToEnd() -eq '{"value":2}') 'Restorable archive contains latest look'}finally{$reader.Dispose()}
}finally{$zip.Dispose()}
[IO.File]::WriteAllText($second+'.spektrafilm.zip','corrupted backup archive')
$third=Save-CatalogBackup $db -Clock $clock
Assert ($third -ne $second) 'Corrupted prior archive is not accepted for deduplication'
for($i=3;$i -le 7;$i++){[IO.File]::WriteAllText($look,"{`"value`":$i}");$null=Save-CatalogBackup $db -Clock $clock}
$folder=Split-Path -Parent $third
$databases=@(Get-ChildItem -LiteralPath $folder -Filter '*.cocatalogdb' -File)
Assert ($databases.Count -eq 5) 'Retains five complete DB generations'
Assert (@(Get-ChildItem -LiteralPath $folder -Filter '*.spektrafilm.zip' -File).Count -eq 5) 'Companion archives rotate together'
Assert (@(Get-ChildItem -LiteralPath $folder -Filter '*.manifest.json' -File).Count -eq 5) 'Companion manifests rotate together'
Assert ((Get-FileHash -LiteralPath $db -Algorithm SHA256).Hash -eq $originalHash) 'Original DB remains unchanged'
Assert (@(Get-ChildItem -LiteralPath $folder -Filter '*.tmp' -File).Count -eq 0) 'No incomplete temporary backups remain'
$same=Save-CatalogBackup $db -Clock $clock
Assert ($same -like '*-000007.cocatalogdb') 'Same-tick rotation retains and deduplicates the latest generation'

$held = [IO.File]::Open((Join-Path $folder '.backup.lock'), [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try {
    $failure=$null
    try { $null=Save-CatalogBackup $db -Clock $clock } catch { $failure=$_.Exception.Message }
    Assert ($failure -like '*Could not lock this catalog*') 'A competing backup cannot stage or rotate another generation'
} finally { $held.Dispose() }

$expectedNext=$same.Replace('-000007.cocatalogdb','-000008.cocatalogdb')
$obstruction=$expectedNext+'.manifest.json'
[void][IO.Directory]::CreateDirectory($obstruction)
[IO.File]::WriteAllText($look,'{"value":8}')
try {
    $failure=$null
    try { $null=Save-CatalogBackup $db -Clock $clock } catch { $failure=$_.Exception.Message }
    Assert ($null -ne $failure) 'A blocked manifest fails publication'
    Assert ((Test-Path -LiteralPath $obstruction -PathType Container) -and !(Test-Path -LiteralPath ($expectedNext+'.spektrafilm.zip')) -and
        !(Test-Path -LiteralPath $expectedNext)) 'Partial publication removes only the archive it created'
    Assert (@(Get-ChildItem -LiteralPath $folder -Filter '*.cocatalogdb' -File).Count -eq 5) 'Failed publication preserved all complete generations'
} finally { Remove-Item -LiteralPath $obstruction }
$verifiedBeforeFailure = @($databases | Get-FileHash -Algorithm SHA256 | ForEach-Object Hash)
try {
    # Preserve the SQLite signature and schema page while cutting off live data cells.
    [IO.File]::WriteAllBytes($db, [byte[]]($bytes[0..($bytes.Length-201)]))
    $failure = $null
    try { $null = Save-CatalogBackup $db } catch { $failure = $_.Exception.Message }
    Assert ($null -ne $failure -and $failure.Contains('SQLite integrity verification')) 'Valid header with corrupt database is refused'
    $afterFailure = @(Get-ChildItem -LiteralPath $folder -Filter '*.cocatalogdb' -File)
    Assert ($afterFailure.Count -eq 5 -and @(Compare-Object $verifiedBeforeFailure @($afterFailure | Get-FileHash -Algorithm SHA256 | ForEach-Object Hash)).Count -eq 0) 'Integrity failure preserves all prior verified databases'
    Assert (@(Get-ChildItem -LiteralPath $folder -Filter '*.tmp' -File).Count -eq 0) 'Integrity failure discards only temporary copies'
} finally { [IO.File]::WriteAllBytes($db,$bytes) }
$userData=Resolve-Path -LiteralPath $userData -Relative
$relativeArchive=Join-Path $root 'relative-state.zip'
$relativeManifest=New-LookBackup $db $relativeArchive
Assert (@($relativeManifest.files | Where-Object path -eq 'Variant looks/11111111111111111111111111111111.json').Count -eq 1) 'Relative data root produces canonical archive paths'
$zip=[IO.Compression.ZipFile]::OpenRead($relativeArchive)
try {Assert ($null -ne $zip.GetEntry('Look recovery/11111111111111111111111111111111.pending.json')) 'Relative data root preserves recovery entry name'}finally{$zip.Dispose()}

# Use schema-valid managed state to exercise restoration and catalog-key rebasing.
$base = [ordered]@{
    Version=1;Enabled=$true
    Look=[ordered]@{Film='kodak_portra_400';Paper='kodak_portra_endura';Exposure=0.5;PrintExposure=0;Strength=1;
        Mode='print';SpectralMethod='hanatos2025';ScanInvert=$false;Development=@{};FilmPushPullMode='standard'}
    Optics=[ordered]@{Numbers=@{};Selections=@{};Switches=@{}}
}
[IO.File]::WriteAllText($look, ($base | ConvertTo-Json -Depth 10 -Compress))
$baseHash=(Get-FileHash -LiteralPath $look -Algorithm SHA256).Hash
$changed = [IO.File]::ReadAllText($look) | ConvertFrom-Json
$changed.Look.Exposure = 2.2
$pending = [ordered]@{Version=1;CatalogKey=$key;Variant='11111111-1111-1111-1111-111111111111';BaseHash=$baseHash;Look=$changed}
[IO.File]::WriteAllText($recovery, ($pending | ConvertTo-Json -Depth 12 -Compress))
$grain = Join-Path (Split-Path -Parent $recovery) '11111111111111111111111111111111.grain.json'
$grainReceipt = [ordered]@{Version=1;Variant='11111111-1111-1111-1111-111111111111';
    Original=@{Type=1;Impact=7;Granularity=30};Applied=@{Type=2;Impact=35;Granularity=60};PreviousApplied=$null}
[IO.File]::WriteAllText($grain, ($grainReceipt | ConvertTo-Json -Depth 4 -Compress))
$previousFile = [IO.Path]::ChangeExtension($look, '.previous.look')
$previousPayload = [IO.File]::ReadAllBytes($look)
$sha = [Security.Cryptography.SHA256]::Create()
try { $previousDigest = $sha.ComputeHash($previousPayload) } finally { $sha.Dispose() }
$previous = [Text.Encoding]::ASCII.GetBytes('SFL1') + ([Guid]::Parse($pending.Variant)).ToByteArray() + $previousDigest + $previousPayload
[IO.File]::WriteAllBytes($previousFile, $previous)
$restorable = Save-CatalogBackup $db
$manifest = [IO.File]::ReadAllText($restorable + '.manifest.json') | ConvertFrom-Json
$reversed = @($manifest.files)
[Array]::Reverse($reversed)
Assert ((Get-CatalogStateDigest $reversed) -eq $manifest.stateDigest) 'File enumeration order does not change the state digest'
$restore = Join-Path $repo 'Restore-SpektrafilmCatalog.ps1'
$restored = Join-Path $root ('restored\test ' + [char]0x00C8 + '.cocatalogdb')
$restoredData = Join-Path $root 'restored-state'
$preview = & $restore -BackupDatabase $restorable -DestinationDatabase $restored -UserDataRoot $restoredData
Assert (($preview -join ' ').Contains('preview verified')) 'Restore preview verifies checksums'
Assert (!(Test-Path -LiteralPath (Split-Path -Parent $restored)) -and !(Test-Path -LiteralPath $restoredData)) 'Restore preview has no filesystem side effect'
$restoreResult = & $restore -BackupDatabase $restorable -DestinationDatabase $restored -UserDataRoot $restoredData -Apply
Assert (($restoreResult -join ' ').Contains('Restored verified catalog')) 'Restore committed the catalog and look files'
Assert ((Get-FileHash -LiteralPath $restored -Algorithm SHA256).Hash -eq $originalHash) 'Catalog restored byte for byte'
$sha=[Security.Cryptography.SHA256]::Create()
try{$restoredKey=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($restored.ToUpperInvariant())))).Replace('-','')}finally{$sha.Dispose()}
$restoredLook=Join-Path (Join-Path (Join-Path $restoredData 'Variant looks') $restoredKey) '11111111111111111111111111111111.json'
$restoredPending=Join-Path (Join-Path (Join-Path $restoredData 'Look recovery') $restoredKey) '11111111111111111111111111111111.pending.json'
Assert ((Get-FileHash -LiteralPath $restoredLook -Algorithm SHA256).Hash -eq $baseHash) 'Committed look bytes preserved under the new catalog key'
$restoredPrevious = [IO.Path]::ChangeExtension($restoredLook, '.previous.look')
Assert ((Get-FileHash -LiteralPath $restoredPrevious -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $previousFile -Algorithm SHA256).Hash) 'Previous verified look moves byte for byte with the catalog'
$pendingCopy=[IO.File]::ReadAllText($restoredPending)|ConvertFrom-Json
Assert ($pendingCopy.CatalogKey -eq $restoredKey -and $pendingCopy.BaseHash -eq $baseHash -and $pendingCopy.Look.Look.Exposure -eq 2.2) 'Pending recovery rebased without changing its look or base hash'
Assert (([IO.File]::ReadAllText($recovery)|ConvertFrom-Json).CatalogKey -eq $key) 'Original pending checkpoint unchanged'
$restoredGrain = Join-Path (Split-Path -Parent $restoredPending) '11111111111111111111111111111111.grain.json'
Assert ((Get-FileHash -LiteralPath $restoredGrain).Hash -eq (Get-FileHash -LiteralPath $grain).Hash) 'Native grain restore record moves with its saved variant and database'
$failure = $null
try { $null = & $restore -BackupDatabase $restorable -DestinationDatabase $restored -UserDataRoot $restoredData -Apply }
catch { $failure = $_.Exception.Message }
Assert ($failure -match 'unused destination') 'Restore refuses to overwrite a catalog'
Assert ((Get-FileHash -LiteralPath $restored -Algorithm SHA256).Hash -eq $originalHash) 'Failed overwrite leaves restored catalog unchanged'
$movedRoot = Join-Path $root 'moved'
[void][IO.Directory]::CreateDirectory($movedRoot)
$moved = Join-Path $movedRoot 'existing.cocatalogdb'
[IO.File]::Copy($restorable, $moved)
$movedData = Join-Path $root 'moved-state'
$looksOnly = @{BackupDatabase=$restorable; DestinationDatabase=$moved; UserDataRoot=$movedData; SavedLooksOnly=$true}
$preview = & $restore @looksOnly
Assert (($preview -join ' ').Contains('preview verified') -and !(Test-Path -LiteralPath $movedData)) 'Saved-look restore preview verifies the existing catalog without creating state'
$failure = $null
try {
    $locked = [IO.File]::Open($moved, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { $null = & $restore @looksOnly -Apply }
    finally { $locked.Dispose() }
} catch { $failure = $_.Exception.Message }
Assert ($failure -and !(Test-Path -LiteralPath $movedData)) 'A locked existing catalog refuses saved-look publication'
$sidecar = $moved + '-wal'
try {
    [IO.File]::WriteAllText($sidecar, 'isolated sidecar')
    $failure = $null
    try { $null = & $restore @looksOnly -Apply }
    catch { $failure = $_.Exception.Message }
    Assert ($failure -match 'SQLite sidecar' -and !(Test-Path -LiteralPath $movedData)) 'Saved-look restore refuses a stale SQLite sidecar'
} finally { Remove-Item -LiteralPath $sidecar }
$unmatched = Join-Path $movedRoot 'unmatched.cocatalogdb'
[IO.File]::Copy($moved, $unmatched)
[IO.File]::AppendAllText($unmatched, 'different generation')
$differentHash = (Get-FileHash -LiteralPath $unmatched).Hash
$failure = $null
try { $null = & $restore -BackupDatabase $restorable -DestinationDatabase $unmatched -UserDataRoot $movedData -SavedLooksOnly -Apply }
catch { $failure = $_.Exception.Message }
Assert ($failure -match 'bytes differ' -and !(Test-Path -LiteralPath $movedData) -and
    (Get-FileHash -LiteralPath $unmatched).Hash -eq $differentHash) 'Saved-look restore refuses a nonmatching existing database without editing it'
$failure = $null
try { $null = & $restore -BackupDatabase $restorable -DestinationDatabase (Join-Path $movedRoot 'missing.cocatalogdb') -UserDataRoot $movedData -SavedLooksOnly -Apply }
catch { $failure = $_.Exception.Message }
Assert ($failure -match 'existing catalog copy' -and !(Test-Path -LiteralPath $movedData)) 'Saved-look restore requires an existing catalog copy'
$movedAttributes = [IO.File]::GetAttributes($moved)
try {
    [IO.File]::SetAttributes($moved, $movedAttributes -bor [IO.FileAttributes]::ReadOnly)
    $applyLooks = & $restore @looksOnly -Apply
    Assert (([IO.File]::GetAttributes($moved) -band [IO.FileAttributes]::ReadOnly) -ne 0) 'Saved-look restore retains a read-only matching database attribute'
} finally { [IO.File]::SetAttributes($moved, $movedAttributes) }
Assert (($applyLooks -join ' ').Contains('Restored saved looks for the verified catalog')) 'Saved-look restore commits against an existing matching catalog'
Assert ((Get-FileHash -LiteralPath $moved).Hash -eq $originalHash -and
    @(Get-ChildItem -LiteralPath $movedRoot -Filter '.spektra-restore-*.tmp').Count -eq 0) 'Saved-look restore preserves database bytes and writes no database stage'
$sha = [Security.Cryptography.SHA256]::Create()
try { $movedKey = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($moved.ToUpperInvariant())))).Replace('-', '') }
finally { $sha.Dispose() }
$movedLook = Join-Path (Join-Path (Join-Path $movedData 'Variant looks') $movedKey) '11111111111111111111111111111111.json'
$movedPending = Join-Path (Join-Path (Join-Path $movedData 'Look recovery') $movedKey) '11111111111111111111111111111111.pending.json'
$movedPrevious = [IO.Path]::ChangeExtension($movedLook, '.previous.look')
$movedGrain = Join-Path (Split-Path -Parent $movedPending) '11111111111111111111111111111111.grain.json'
$movedRecovery = [IO.File]::ReadAllText($movedPending) | ConvertFrom-Json
Assert ((Get-FileHash -LiteralPath $movedLook).Hash -eq $baseHash -and
    (Get-FileHash -LiteralPath $movedPrevious).Hash -eq (Get-FileHash -LiteralPath $previousFile).Hash -and
    (Get-FileHash -LiteralPath $movedGrain).Hash -eq (Get-FileHash -LiteralPath $grain).Hash -and
    $movedRecovery.CatalogKey -eq $movedKey -and $movedRecovery.BaseHash -eq $baseHash -and
    $movedRecovery.Look.Look.Exposure -eq 2.2) 'All verified look and native grain bytes publish under the moved catalog identity with recovery rebased'
$failure = $null
try { $null = & $restore @looksOnly -Apply }
catch { $failure = $_.Exception.Message }
Assert ($failure -match 'Saved looks already exist' -and (Get-FileHash -LiteralPath $movedLook).Hash -eq $baseHash -and
    (Get-FileHash -LiteralPath $moved).Hash -eq $originalHash) 'Saved-look restore preserves existing state and catalog on repeat'
$failure = $null
try { $null = & $restore -BackupDatabase $restorable -DestinationDatabase ($moved.ToUpperInvariant()) -UserDataRoot $movedData -SavedLooksOnly -Apply }
catch { $failure = $_.Exception.Message }
Assert ($failure -match 'Saved looks already exist') 'Changing only the Windows path case cannot create a second state identity'
$blocked = Join-Path $root 'blocked\test.cocatalogdb'
$archivePath = $restorable + '.spektrafilm.zip'
$archiveBytes = [IO.File]::ReadAllBytes($archivePath)
try {
    [IO.File]::AppendAllText($archivePath, 'corruption')
    $failure = $null
    try { $null = & $restore -BackupDatabase $restorable -DestinationDatabase $blocked -UserDataRoot $restoredData -Apply }
    catch { $failure = $_.Exception.Message }
    Assert ($failure -match 'checksum failed' -and !(Test-Path -LiteralPath $blocked)) 'Corrupt saved-look archive cannot publish a catalog'
} finally { [IO.File]::WriteAllBytes($archivePath, $archiveBytes) }
# Emulate an earlier backup whose shell computed a different file ordering.
$legacy = Join-Path $root 'legacy.cocatalogdb'
$legacyZip = $legacy + '.spektrafilm.zip'
$legacyManifestPath = $legacy + '.manifest.json'
[IO.File]::Copy($restorable, $legacy)
[IO.File]::Copy($archivePath, $legacyZip)
$legacyState = $manifest
$legacyState.schemaVersion = 1
$legacyState.stateDigest = 'AB' * 32
$archive = [IO.Compression.ZipFile]::Open($legacyZip, [IO.Compression.ZipArchiveMode]::Update)
try {
    $archive.GetEntry('manifest.json').Delete()
    $internal = [ordered]@{schemaVersion=1;catalogKey=$legacyState.catalogKey;stateDigest=$legacyState.stateDigest;files=$legacyState.files}
    $writer = New-Object IO.StreamWriter($archive.CreateEntry('manifest.json').Open(), (New-Object Text.UTF8Encoding($false)))
    try { $writer.Write(($internal | ConvertTo-Json -Depth 6)) }
    finally { $writer.Dispose() }
} finally { $archive.Dispose() }
$legacyState.looksSha256 = (Get-FileHash -LiteralPath $legacyZip).Hash
[IO.File]::WriteAllText($legacyManifestPath, ($legacyState | ConvertTo-Json -Depth 6))
$preview = & $restore -BackupDatabase $legacy -DestinationDatabase $blocked -UserDataRoot $restoredData
Assert (($preview -join ' ').Contains('preview verified')) 'Version 1 resolves shell-dependent ordering using the paired ZIP manifest and per-file hashes'
$legacyState.stateDigest = '21' * 32
[IO.File]::WriteAllText($legacyManifestPath, ($legacyState | ConvertTo-Json -Depth 6))
$failure = $null
try { $null = & $restore -BackupDatabase $legacy -DestinationDatabase $blocked -UserDataRoot $restoredData }
catch { $failure = $_.Exception.Message }
Assert ($failure -match 'different catalog or state generation') 'Version 1 refuses a mismatched pairing even with valid ZIP and database hashes'

Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

public static class CatalogIdentityFixture
{
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(byte[] file, out IntPtr database, int flags, IntPtr vfs);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_exec(IntPtr database, byte[] query, IntPtr result, IntPtr state, IntPtr error);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close(IntPtr database);

    public static void Create(string path, string query)
    {
        if (File.Exists(path)) throw new IOException("Choose a new disposable catalog path.");
        IntPtr database;
        int status = sqlite3_open_v2(Encoding.UTF8.GetBytes(Path.GetFullPath(path) + "\0"), out database, 6, IntPtr.Zero);
        try
        {
            if (status != 0 || sqlite3_exec(database, Encoding.UTF8.GetBytes(query + "\0"), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0)
                throw new IOException("Could not create the synthetic catalog identity fixture.");
        }
        finally { if (database != IntPtr.Zero) sqlite3_close(database); }
    }
}
'@
$catalogSchema = @'
CREATE TABLE ZDOCUMENTCONTENT (Z_PK INTEGER PRIMARY KEY, ZDOCUMENTUUID VARCHAR, ZREVISION INTEGER);
CREATE TABLE ZIMAGE (Z_PK INTEGER PRIMARY KEY, ZIMAGEUUID VARCHAR);
CREATE TABLE ZVARIANT (Z_PK INTEGER PRIMARY KEY, ZVARIANTUUID VARCHAR, ZIMAGE INTEGER);
INSERT INTO ZDOCUMENTCONTENT VALUES (1, '22222222-2222-2222-2222-222222222222', 1);
INSERT INTO ZIMAGE VALUES (1, '44444444-4444-4444-4444-444444444444'), (2, '55555555-5555-5555-5555-555555555555');
INSERT INTO ZVARIANT VALUES (1, '11111111-1111-1111-1111-111111111111', 1);
'@
$catalogFixtures = [ordered]@{
    original = ''
    updated = "UPDATE ZDOCUMENTCONTENT SET ZREVISION=2; INSERT INTO ZVARIANT VALUES (2, '33333333-3333-3333-3333-333333333333', 2);"
    otherDocument = "UPDATE ZDOCUMENTCONTENT SET ZDOCUMENTUUID='99999999-9999-9999-9999-999999999999';"
    remappedImage = 'UPDATE ZVARIANT SET ZIMAGE=2;'
    missingVariant = 'DELETE FROM ZVARIANT;'
    duplicateVariant = "INSERT INTO ZVARIANT VALUES (2, '11111111-1111-1111-1111-111111111111', 2);"
}
$identityFixtures = Join-Path $root 'native-catalog-identities'
[void][IO.Directory]::CreateDirectory($identityFixtures)
$databasesByName = @{}
foreach ($name in $catalogFixtures.Keys) {
    $file = Join-Path $identityFixtures ($name + '.cocatalogdb')
    [CatalogIdentityFixture]::Create($file, ($catalogSchema + "`n" + $catalogFixtures[$name]))
    $databasesByName[$name] = $file
}
# Reuse the state already associated with this test-only source path.
[IO.File]::Copy($databasesByName.original, $db, $true)
$identityBackup = Save-CatalogBackup $db
$updated = $databasesByName.updated
$updatedHash = (Get-FileHash -LiteralPath $updated).Hash
$updatedState = Join-Path $identityFixtures 'state'
$restoreUpdated = @{BackupDatabase=$identityBackup;DestinationDatabase=$updated;UserDataRoot=$updatedState;SavedLooksOnly=$true}
$failure = $null
try { $null = & $restore @restoreUpdated -Apply }
catch { $failure = $_.Exception.Message }
Assert ($failure -match 'catalog bytes differ' -and !(Test-Path -LiteralPath $updatedState)) 'Updated database requires the explicit identity fallback'
$preview = & $restore @restoreUpdated -AllowUpdatedCatalog
Assert (($preview -join ' ') -match 'Verified the updated catalog identity' -and !(Test-Path -LiteralPath $updatedState)) 'The updated catalog preview verifies identity without writing'
$result = & $restore @restoreUpdated -AllowUpdatedCatalog -Apply
Assert (($result -join ' ') -match 'Restored saved looks' -and (Get-FileHash -LiteralPath $updated).Hash -eq $updatedHash) 'Restoring updated catalog state leaves every catalog byte exact'
$updatedLooks = @(Get-ChildItem -LiteralPath (Join-Path $updatedState 'Variant looks') -Recurse -File -Filter '11111111111111111111111111111111.json')
Assert ($updatedLooks.Count -eq 1 -and (Get-FileHash -LiteralPath $updatedLooks[0].FullName).Hash -eq $baseHash) 'The matching native variant receives exactly the verified archived look'
$updatedPending = @(Get-ChildItem -LiteralPath (Join-Path $updatedState 'Look recovery') -Recurse -File -Filter '*.pending.json')
$rebased = Get-Content -Raw -LiteralPath $updatedPending[0].FullName | ConvertFrom-Json
Assert ($updatedPending.Count -eq 1 -and $rebased.CatalogKey -eq $updatedLooks[0].Directory.Name -and $rebased.Look.Look.Exposure -eq 2.2) 'Updated catalog recovery keeps the pending value and rebases its catalog key'
$failures = @{
    otherDocument = 'different native document identity'
    remappedImage = 'different native image'
    missingVariant = 'does not uniquely identify'
    duplicateVariant = 'does not uniquely identify'
}
foreach ($name in $failures.Keys) {
    $target = $databasesByName[$name]
    $hash = (Get-FileHash -LiteralPath $target).Hash
    $refusedState = Join-Path $identityFixtures ($name + '-state')
    $failure = $null
    try { $null = & $restore -BackupDatabase $identityBackup -DestinationDatabase $target -UserDataRoot $refusedState -SavedLooksOnly -AllowUpdatedCatalog -Apply }
    catch { $failure = $_.Exception.Message }
    Assert ($failure -match $failures[$name] -and !(Test-Path -LiteralPath $refusedState) -and (Get-FileHash -LiteralPath $target).Hash -eq $hash) "Reject $name without publishing state or changing catalog bytes"
}

# The launcher backs up the selected closed copy, then discovers a source without
# changing any database or overwriting path-specific looks.
$userData = Join-Path $root 'launcher-state'
$autoSource = $databasesByName.original
$autoKey = Get-CatalogKey $autoSource
$autoLook = Join-Path (Join-Path (Join-Path $userData 'Variant looks') $autoKey) '11111111111111111111111111111111.json'
$autoRecovery = Join-Path (Join-Path (Join-Path $userData 'Look recovery') $autoKey) '11111111111111111111111111111111.pending.json'
foreach ($state in @($autoLook, $autoRecovery)) { [void][IO.Directory]::CreateDirectory((Split-Path -Parent $state)) }
[IO.File]::Copy($look, $autoLook)
$pending.CatalogKey = $autoKey
[IO.File]::WriteAllText($autoRecovery, ($pending | ConvertTo-Json -Depth 12 -Compress))
$launchBackup = Save-CatalogBackup $autoSource
$launchManifest = Read-LookRecoveryManifest ($launchBackup + '.manifest.json')
Assert ($launchManifest.nativeDocumentId -eq '22222222-2222-2222-2222-222222222222') 'A new generation indexes its one verified native document ID'

$clone = Join-Path $identityFixtures ('renamed ' + [char]0x00C8 + '.cocatalogdb')
[IO.File]::Copy($autoSource, $clone)
$cloneHash = (Get-FileHash -LiteralPath $clone).Hash
$cloneCopy = Prepare-SpektrafilmCatalog $clone $restore -SkipRecovery
Assert (!(Test-CatalogLookState (Get-CatalogKey $clone))) 'A launch that skips recovery still makes a verified catalog backup'
$matches = @(Find-CatalogLookBackup $clone $cloneCopy)
Assert ($matches.Count -eq 1 -and $matches[0].Exact -and $matches[0].Database -eq $launchBackup) 'A renamed copy finds the exact backup with saved state'

$launcherState = $userData
$userData = Join-Path $root 'update-state'
$seed = Join-Path (Join-Path $userData 'Catalog backups') $autoKey.Substring(0, 20)
[void][IO.Directory]::CreateDirectory($seed)
foreach ($suffix in @('', '.manifest.json', '.spektrafilm.zip')) {
    Copy-Item -LiteralPath ($launchBackup + $suffix) -Destination $seed
}
$updateSource = Join-Path $seed (Split-Path -Leaf $launchBackup)
$updatedCopy = Save-CatalogBackup $updated
$matches = @(Find-CatalogLookBackup $updated $updatedCopy)
Assert ($matches.Count -eq 1 -and !$matches[0].Exact) 'An updated catalog discovers the archived native document identity'
$indexPath = $updateSource + '.manifest.json'
$indexBytes = [IO.File]::ReadAllBytes($indexPath)
try {
    $launchManifest.PSObject.Properties.Remove('nativeDocumentId')
    [IO.File]::WriteAllText($indexPath, ($launchManifest | ConvertTo-Json -Depth 6))
    $legacyMatches = @(Find-CatalogLookBackup $updated $updatedCopy)
    Assert ($legacyMatches.Count -eq 1 -and $legacyMatches[0].Database -eq $updateSource) 'Discovery also reads native identity from pre-index backup generations'
} finally { [IO.File]::WriteAllBytes($indexPath, $indexBytes) }
Assert (!(Restore-DiscoveredCatalogLooks $updated $updatedCopy $restore -WarningAction SilentlyContinue) -and
    !(Test-CatalogLookState (Get-CatalogKey $updated))) 'Unattended launch leaves an updated candidate untouched until approved'
$wrong = $databasesByName.remappedImage
$wrongCopy = Save-CatalogBackup $wrong
Assert (!(Restore-DiscoveredCatalogLooks $wrong $wrongCopy $restore -AcceptUpdated -WarningAction SilentlyContinue) -and
    !(Test-CatalogLookState (Get-CatalogKey $wrong))) 'An indexed document UUID cannot authorize a remapped native variant'
Assert (Restore-DiscoveredCatalogLooks $updated $updatedCopy $restore -AcceptUpdated) 'An approved updated catalog passes variant verification and restores the looks'
$userData = $launcherState

$fresh = Join-Path $identityFixtures 'another copy.cocatalogdb'
[IO.File]::Copy($autoSource, $fresh)
$freshCopy = Save-CatalogBackup $fresh
$unknownVariant = Join-Path (Split-Path -Parent $autoLook) '33333333333333333333333333333333.json'
try {
    [IO.File]::WriteAllText($unknownVariant, ($changed | ConvertTo-Json -Depth 10 -Compress))
    Assert (!(Restore-DiscoveredCatalogLooks $fresh $freshCopy $restore -WarningAction SilentlyContinue) -and
        !(Test-CatalogLookState (Get-CatalogKey $fresh))) 'A newer local variant absent from the archived native database blocks automatic migration'
} finally { [IO.File]::Delete($unknownVariant) }
$sourceArchive = $launchBackup + '.spektrafilm.zip'
$archiveBytes = [IO.File]::ReadAllBytes($sourceArchive)
try {
    [IO.File]::WriteAllText($sourceArchive, 'invalid fixture archive')
    Assert (!(Restore-DiscoveredCatalogLooks $fresh $freshCopy $restore -WarningAction SilentlyContinue) -and
        !(Test-CatalogLookState (Get-CatalogKey $fresh))) 'A corrupt archive discovered by metadata cannot publish state'
} finally { [IO.File]::WriteAllBytes($sourceArchive, $archiveBytes) }

$prepared = Prepare-SpektrafilmCatalog $clone $restore
$preparedManifest = Read-LookRecoveryManifest ($prepared + '.manifest.json')
Assert ($prepared -ne $cloneCopy -and $preparedManifest.files.Count -eq 2) 'The launcher restores the sole verified exact match and backs up the recovered state before opening'
$cloneKey = Get-CatalogKey $clone
$cloneLook = Join-Path (Join-Path (Join-Path $userData 'Variant looks') $cloneKey) '11111111111111111111111111111111.json'
$cloneRecovery = Join-Path (Join-Path (Join-Path $userData 'Look recovery') $cloneKey) '11111111111111111111111111111111.pending.json'
Assert ((Get-FileHash -LiteralPath $clone).Hash -eq $cloneHash -and
    (Get-FileHash -LiteralPath $cloneLook).Hash -eq $baseHash -and
    ([IO.File]::ReadAllText($cloneRecovery) | ConvertFrom-Json).CatalogKey -eq $cloneKey) 'Exact recovery rebases the pending edit and preserves catalog and committed look bytes'
Assert (@(Find-CatalogLookBackup ($clone.ToUpperInvariant()) $cloneCopy).Count -eq 0) 'The case alias finds existing path-specific state and does not look for a replacement'

[IO.File]::WriteAllText($cloneLook, ($changed | ConvertTo-Json -Depth 10 -Compress))
$null = Save-CatalogBackup $clone
$forks = @(Find-CatalogLookBackup $fresh $freshCopy)
Assert ($forks.Count -eq 2 -and $forks[0].Manifest.catalogKey -ne $forks[1].Manifest.catalogKey) 'The latest snapshots of two divergent catalog paths remain separate candidates'
Assert (!(Restore-DiscoveredCatalogLooks $fresh $freshCopy $restore -WarningAction SilentlyContinue) -and
    !(Test-CatalogLookState (Get-CatalogKey $fresh))) 'Identical catalog bytes never select silently between divergent looks'
Assert ((Get-FileHash -LiteralPath $autoLook).Hash -eq $baseHash -and
    (Get-FileHash -LiteralPath $cloneLook).Hash -ne $baseHash -and
    (Get-FileHash -LiteralPath $autoSource).Hash -eq $cloneHash -and
    (Get-FileHash -LiteralPath $clone).Hash -eq $cloneHash -and
    (Get-FileHash -LiteralPath $updated).Hash -eq $updatedHash) 'The exact and updated copies retain independent looks and leave both source and copied catalogs unchanged'

$userData = Join-Path $root 'unarchived'
$oldDatabase = Join-Path $identityFixtures 'prior name.cocatalogdb'
$newDatabase = Join-Path $identityFixtures 'moved name.cocatalogdb'
[IO.File]::Copy($autoSource, $oldDatabase)
$firstLaunchBackup = Save-CatalogBackup $oldDatabase
$firstLaunchManifest = Read-LookRecoveryManifest ($firstLaunchBackup + '.manifest.json')
Assert (@($firstLaunchManifest.files).Count -eq 0) 'The first-launch anchor predates all Spektrafilm edits'
$oldDatabaseKey = Get-CatalogKey $oldDatabase
$newerLook = Join-Path (Join-Path (Join-Path $userData 'Variant looks') $oldDatabaseKey) '11111111111111111111111111111111.json'
$newerPending = Join-Path (Join-Path (Join-Path $userData 'Look recovery') $oldDatabaseKey) '11111111111111111111111111111111.pending.json'
foreach ($state in @($newerLook, $newerPending)) { [void][IO.Directory]::CreateDirectory((Split-Path -Parent $state)) }
[IO.File]::WriteAllText($newerLook, ($changed | ConvertTo-Json -Depth 10 -Compress))
$newerHash = (Get-FileHash -LiteralPath $newerLook).Hash
$nextEdit = [IO.File]::ReadAllText($newerLook) | ConvertFrom-Json
$nextEdit.Look.Exposure = 2.7
$pendingEdit = @{Version=1; CatalogKey=$oldDatabaseKey; Variant=$pending.Variant; BaseHash=$newerHash; Look=$nextEdit}
[IO.File]::WriteAllText($newerPending, ($pendingEdit | ConvertTo-Json -Depth 12 -Compress))
$pendingHash = (Get-FileHash -LiteralPath $newerPending).Hash
[IO.File]::Move($oldDatabase, $newDatabase)
$newDatabaseKey = Get-CatalogKey $newDatabase
$currentOptions = @{BackupDatabase=$firstLaunchBackup; DestinationDatabase=$newDatabase; UserDataRoot=$userData; SavedLooksOnly=$true; LatestLocalLooks=$true}
$preview = & $restore @currentOptions
Assert (($preview -join ' ') -match '2 current local files' -and !(Test-CatalogLookState $newDatabaseKey)) 'Newer-local preview verifies the variant mapping and does not write destination state'
$prepared = Prepare-SpektrafilmCatalog $newDatabase $restore
$latestManifest = Read-LookRecoveryManifest ($prepared + '.manifest.json')
$newerFiles = @(Get-CatalogLookFiles $userData $newDatabaseKey)
$carriedLook = $newerFiles | Where-Object Relative -like 'Variant looks/*'
$carriedPending = $newerFiles | Where-Object Relative -like 'Look recovery/*.pending.json'
$checkpoint = [IO.File]::ReadAllText($carriedPending.Path) | ConvertFrom-Json
Assert ($latestManifest.files.Count -eq 2 -and (Get-FileHash -LiteralPath $carriedLook.Path).Hash -eq $newerHash -and
    $checkpoint.CatalogKey -eq $newDatabaseKey -and $checkpoint.BaseHash -eq $newerHash -and $checkpoint.Look.Look.Exposure -eq 2.7) 'The launcher carries the current look and pending edit even though the archived look ZIP was empty'
Assert (!(Test-Path -LiteralPath $oldDatabase) -and (Get-FileHash -LiteralPath $newDatabase).Hash -eq $cloneHash -and
    (Get-FileHash -LiteralPath $newerLook).Hash -eq $newerHash -and (Get-FileHash -LiteralPath $newerPending).Hash -eq $pendingHash) 'Recovery leaves the genuinely moved catalog and both source state files unchanged'

$newVariantId = '33333333-3333-3333-3333-333333333333'
$newVariantLeaf = ([Guid]$newVariantId).ToString('N')
$createdLook = Join-Path (Split-Path -Parent $newerLook) ($newVariantLeaf + '.json')
$createdIdentity = Join-Path (Split-Path -Parent $newerPending) ($newVariantLeaf + '.identity.json')
[IO.File]::WriteAllText($createdLook, ($nextEdit | ConvertTo-Json -Depth 10 -Compress))
$createdTarget = Join-Path $identityFixtures 'with-new-variant.cocatalogdb'
[IO.File]::Copy($updated, $createdTarget)
$createdOptions = @{BackupDatabase=$firstLaunchBackup; DestinationDatabase=$createdTarget; UserDataRoot=$userData
    SavedLooksOnly=$true; AllowUpdatedCatalog=$true; LatestLocalLooks=$true}
$createdKey = Get-CatalogKey $createdTarget
$failure = $null
try { $null = & $restore @createdOptions -Apply } catch { $failure = $_.Exception.Message }
Assert ($failure -match 'does not uniquely identify' -and !(Test-CatalogLookState $createdKey)) 'A variant created after the backup needs a saved native identity'
$proof = [ordered]@{Version=1;Document='22222222-2222-2222-2222-222222222222';Variant=$newVariantId;Image='44444444-4444-4444-4444-444444444444'}
[IO.File]::WriteAllText($createdIdentity, ($proof | ConvertTo-Json -Compress))
$failure = $null
try { $null = & $restore @createdOptions -Apply } catch { $failure = $_.Exception.Message }
Assert ($failure -match 'does not match the destination' -and !(Test-CatalogLookState $createdKey)) 'A recorded variant UUID cannot authorize a different destination RAW'
$proof.Image = '55555555-5555-5555-5555-555555555555'
$proof.Document = '99999999-9999-9999-9999-999999999999'
[IO.File]::WriteAllText($createdIdentity, ($proof | ConvertTo-Json -Compress))
$failure = $null
try { $null = & $restore @createdOptions -Apply } catch { $failure = $_.Exception.Message }
Assert ($failure -match 'does not match the destination' -and !(Test-CatalogLookState $createdKey)) 'A recorded variant identity cannot cross native document lineages'
$proof.Document = '22222222-2222-2222-2222-222222222222'
[IO.File]::WriteAllText($createdIdentity, ($proof | ConvertTo-Json -Compress))
$preview = & $restore @createdOptions
Assert (($preview -join ' ') -match '4 current local files' -and !(Test-CatalogLookState $createdKey)) 'A matching identity verifies a new variant without modifying the destination in preview'
$null = & $restore @createdOptions -Apply
$newVariantFiles = @(Get-CatalogLookFiles $userData $createdKey)
$carriedCreatedLook = ($newVariantFiles | Where-Object Relative -CEQ "Variant looks/$newVariantLeaf.json").Path
$carriedCreatedIdentity = ($newVariantFiles | Where-Object Relative -CEQ "Look recovery/$newVariantLeaf.identity.json").Path
Assert ((Get-FileHash -LiteralPath $createdTarget).Hash -eq $updatedHash -and
    (Get-FileHash -LiteralPath $carriedCreatedLook).Hash -eq (Get-FileHash -LiteralPath $createdLook).Hash -and
    (Get-FileHash -LiteralPath $carriedCreatedIdentity).Hash -eq (Get-FileHash -LiteralPath $createdIdentity).Hash) 'Verified current look and identity bytes for the new variant travel together'
$withIdentityBackup = Save-CatalogBackup $createdTarget
$identityRestoreData = Join-Path $root 'identity-restored'
$identityRestoreCatalog = Join-Path $identityFixtures 'identity-restored.cocatalogdb'
$null = & $restore -BackupDatabase $withIdentityBackup -DestinationDatabase $identityRestoreCatalog -UserDataRoot $identityRestoreData -Apply
$identityRestoreFiles = @(Get-CatalogLookFiles $identityRestoreData (Get-CatalogKey $identityRestoreCatalog))
Assert (@($identityRestoreFiles | Where-Object Relative -CEQ "Look recovery/$newVariantLeaf.identity.json").Count -eq 1) 'A subsequent full backup and restore retains the native identity'

# Pause a separate process at the real publication boundaries. The pause only
# exists in the disposable script copy. The retry uses the unmodified entry point.
$crashFixture = Join-Path $root 'crash'
[void][IO.Directory]::CreateDirectory($crashFixture)
Copy-Item -LiteralPath (Join-Path $repo 'CatalogIntegrity.ps1') -Destination $crashFixture
$fixtureScript = Join-Path $crashFixture 'pause.ps1'
$restorable = (Resolve-Path -LiteralPath $restorable).ProviderPath
$instrumented = [IO.File]::ReadAllText($restore)
$afterScope = '[IO.Directory]::Move($staging[$scope], $targets[$scope])'
$afterCatalog = '[IO.File]::Move($stageCatalog, $destination)'
Assert ($instrumented.Contains($afterScope) -and $instrumented.Contains($afterCatalog)) 'The interruption fixture found both production publication boundaries'
$instrumented = $instrumented.Replace($afterScope, ($afterScope + '; ' + @'
if (($env:SPEKTRA_TEST_RESTORE_STOP -in 'looks', 'latest' -and $scope -eq 'Variant looks') -or
    ($env:SPEKTRA_TEST_RESTORE_STOP -eq 'before-db' -and $scope -eq 'Look recovery')) {
    [IO.File]::WriteAllText($env:SPEKTRA_TEST_RESTORE_MARKER, $scope)
    [Threading.Thread]::Sleep(120000)
}
'@))
$instrumented = $instrumented.Replace($afterCatalog, ($afterCatalog + '; ' + @'
if ($env:SPEKTRA_TEST_RESTORE_STOP -eq 'after-db') {
    [IO.File]::WriteAllText($env:SPEKTRA_TEST_RESTORE_MARKER, 'catalog')
    [Threading.Thread]::Sleep(120000)
}
'@))
[IO.File]::WriteAllText($fixtureScript, $instrumented)

foreach ($phase in @('looks', 'before-db', 'after-db', 'latest')) {
    $userData = Join-Path $crashFixture ($phase + '-data')
    $database = Join-Path $crashFixture ($phase + '.cocatalogdb')
    $catalogKey = Get-CatalogKey $database
    $localSnapshot = $phase -eq 'latest'
    $stateOnly = $phase -in 'looks', 'latest'
    $backupToRestore = $restorable
    $expectedLookHash = $baseHash
    $expectedDatabaseHash = $originalHash
    $expectedPendingExposure = 2.2
    if ($localSnapshot) {
        $backupToRestore = $firstLaunchBackup
        $expectedLookHash = $newerHash
        $expectedDatabaseHash = $cloneHash
        $expectedPendingExposure = 2.7
        $localLook = Join-Path (Join-Path (Join-Path $userData 'Variant looks') $oldDatabaseKey) (Split-Path -Leaf $newerLook)
        $localPending = Join-Path (Join-Path (Join-Path $userData 'Look recovery') $oldDatabaseKey) (Split-Path -Leaf $newerPending)
        foreach ($state in @($localLook, $localPending)) { [void][IO.Directory]::CreateDirectory((Split-Path -Parent $state)) }
        [IO.File]::Copy($newerLook, $localLook)
        [IO.File]::Copy($newerPending, $localPending)
    }
    if ($stateOnly) { [IO.File]::Copy($backupToRestore, $database) }
    $marker = Join-Path $crashFixture ($phase + '.marker')
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $start.WorkingDirectory = $crashFixture
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['SPEKTRA_TEST_RESTORE_STOP'] = $phase
    $start.EnvironmentVariables['SPEKTRA_TEST_RESTORE_MARKER'] = $marker
    $start.Arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $fixtureScript + '" -BackupDatabase "' +
        $backupToRestore + '" -DestinationDatabase "' + $database + '" -UserDataRoot "' + $userData + '" -Apply'
    if ($stateOnly) { $start.Arguments += ' -SavedLooksOnly' }
    if ($localSnapshot) { $start.Arguments += ' -LatestLocalLooks' }
    $child = [Diagnostics.Process]::Start($start)
    $receiptPath = Get-CatalogRestoreReceiptPath $userData $database
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (!(Test-Path -LiteralPath $marker) -and !$child.WaitForExit(50) -and [DateTime]::UtcNow -lt $deadline) {}
        if (!(Test-Path -LiteralPath $marker)) {
            $errorText = if ($child.HasExited) { $child.StandardError.ReadToEnd() } else { 'Timed out before publication.' }
            throw "Interruption fixture failed at $phase. $errorText"
        }
        Assert (!$child.HasExited -and (Test-Path -LiteralPath $receiptPath -PathType Leaf)) "$phase can stop only after a durable restore receipt exists"
    } finally {
        if (!$child.HasExited) { $child.Kill(); $child.WaitForExit() }
        $child.Dispose()
    }
    $job = Read-CatalogRestoreReceipt $receiptPath
    $publishedLook = Join-Path (Join-Path (Join-Path $userData 'Variant looks') $catalogKey) '11111111111111111111111111111111.json'
    $publishedRecovery = Join-Path (Join-Path (Join-Path $userData 'Look recovery') $catalogKey) '11111111111111111111111111111111.pending.json'
    Assert ($job.SavedLooksOnly -eq $stateOnly -and $job.LatestLocalLooks -eq $localSnapshot -and
        (Get-FileHash -LiteralPath $publishedLook).Hash -eq $expectedLookHash -and
        (Test-Path -LiteralPath $database) -eq ($phase -ne 'before-db') -and
        (Test-Path -LiteralPath $publishedRecovery) -eq !$stateOnly) "$phase leaves only the planned parts visible at interruption"
    $restoreArgs = @{BackupDatabase=$backupToRestore; DestinationDatabase=$database; UserDataRoot=$userData
        SavedLooksOnly=$stateOnly; LatestLocalLooks=$localSnapshot}
    $preview = & $restore @restoreArgs
    Assert (($preview -join ' ') -match 'resume the interrupted restore' -and (Test-Path -LiteralPath $receiptPath)) "$phase preview verifies the original backup without completing publication"

    if ($localSnapshot) {
        [IO.File]::AppendAllText($localPending, 'changed after the approved snapshot')
        $modified = (Get-FileHash -LiteralPath $localPending).Hash
        $null = Prepare-SpektrafilmCatalog $database $restore -SkipRecovery
        Assert ((Get-FileHash -LiteralPath $localPending).Hash -eq $modified) 'Latest-local retry preserves a later change at the old path while finishing the already verified snapshot'
    } elseif ($stateOnly) {
        $failure = $null
        try { $null = Save-CatalogBackup $database } catch { $failure = $_.Exception.Message }
        Assert ($failure -match 'Resume the interrupted catalog restore') 'The launcher cannot back up a partly published catalog'
        $databaseBytes = [IO.File]::ReadAllBytes($database)
        try {
            [IO.File]::AppendAllText($database, 'external change')
            $modified = (Get-FileHash -LiteralPath $database).Hash
            $failure = $null
            try { $null = & $restore @restoreArgs -Apply } catch { $failure = $_.Exception.Message }
            Assert ($failure -match 'destination catalog changed' -and (Get-FileHash -LiteralPath $database).Hash -eq $modified -and
                !(Test-Path -LiteralPath $publishedRecovery) -and (Test-Path -LiteralPath $receiptPath)) 'Resume refuses a catalog changed since the original approval'
        } finally { [IO.File]::WriteAllBytes($database, $databaseBytes) }
        $lookBytes = [IO.File]::ReadAllBytes($publishedLook)
        try {
            [IO.File]::AppendAllText($publishedLook, 'unrelated edit in the destination')
            $modified = (Get-FileHash -LiteralPath $publishedLook).Hash
            $failure = $null
            try { $null = Prepare-SpektrafilmCatalog $database $restore -SkipRecovery } catch { $failure = $_.Exception.Message }
            Assert ($failure -match 'Variant looks changed' -and (Get-FileHash -LiteralPath $publishedLook).Hash -eq $modified -and
                !(Test-Path -LiteralPath $publishedRecovery) -and (Test-Path -LiteralPath $receiptPath)) 'Retry protects a changed published look and keeps the receipt'
        } finally { [IO.File]::WriteAllBytes($publishedLook, $lookBytes) }
        $prepared = Prepare-SpektrafilmCatalog $database $restore -SkipRecovery
        $preparedManifest = Read-LookRecoveryManifest ($prepared + '.manifest.json')
        Assert (@($preparedManifest.files).Count -eq 4) 'Launcher resumes the previously accepted looks before taking a complete backup'
    } elseif ($phase -eq 'before-db') {
        $null = & $restore @restoreArgs -Apply
    } else {
        $null = Prepare-SpektrafilmCatalog $database $restore -SkipRecovery
    }
    $resumedPending = [IO.File]::ReadAllText($publishedRecovery) | ConvertFrom-Json
    Assert (!(Test-Path -LiteralPath $receiptPath) -and (Get-FileHash -LiteralPath $database).Hash -eq $expectedDatabaseHash -and
        (Get-FileHash -LiteralPath $publishedLook).Hash -eq $expectedLookHash -and $resumedPending.CatalogKey -eq $catalogKey -and
        $resumedPending.Look.Look.Exposure -eq $expectedPendingExposure) "$phase resumes exactly once with the original database, looks and rebased pending edit"
}
$stateContracts = Join-Path $repo 'tests\state\bin\Release\net8.0\StateContracts.exe'
if (!(Test-Path -LiteralPath $stateContracts -PathType Leaf)) { throw 'Build the Release StateContracts fixture before testing a journal-backed catalog.' }
$userData = Join-Path $root 'journal-donor-data'
$journalDonor = Join-Path $root 'generation-donor.cocatalogdb'
[IO.File]::WriteAllBytes($journalDonor, $bytes)
$journalKey = Get-CatalogKey $journalDonor
$journalVariant = '11111111111111111111111111111111'
$journalDir = Join-Path (Join-Path $userData 'Look generations') $journalKey
$mirrorDir = Join-Path (Join-Path $userData 'Variant looks') $journalKey
[void][IO.Directory]::CreateDirectory($mirrorDir)
$mirror = Join-Path $mirrorDir ($journalVariant + '.json')
$latestLook = Join-Path $root 'journal-current.json'
[IO.File]::WriteAllText($mirror, ($base | ConvertTo-Json -Depth 10 -Compress))
[IO.File]::WriteAllText($latestLook, ($changed | ConvertTo-Json -Depth 10 -Compress))
$beforeJournalBackup = Save-CatalogBackup $journalDonor -Clock $clock
$previousHash = (Get-FileHash -LiteralPath $mirror).Hash
$currentHash = (Get-FileHash -LiteralPath $latestLook).Hash
$marker = Join-Path $root 'journal-commit.ready'
$arguments = @('--hold-journal-for-backup',$journalDir,$journalKey,$journalVariant,$latestLook,$mirror,$marker)
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = $stateContracts
$start.Arguments = ($arguments | ForEach-Object { '"' + $_ + '"' }) -join ' '
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
[void]$start.EnvironmentVariables.Remove('DOTNET_STARTUP_HOOKS')
$child = [Diagnostics.Process]::Start($start)
$journalFile = Join-Path $journalDir ($journalVariant + '.generations')
try {
    $wait = [Diagnostics.Stopwatch]::StartNew()
    while (!(Test-Path -LiteralPath $marker) -and !$child.HasExited -and $wait.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 20 }
    Assert ((Test-Path -LiteralPath $marker) -and !$child.HasExited) 'Separate writer reaches the boundary after the durable pair and before compatibility publication'
    $pairHash = (Get-FileHash -LiteralPath $journalFile).Hash
    Assert ($pairHash -eq [IO.File]::ReadAllText($marker)) 'The fixture committed the exact C# generation pair'
    $failure = $null
    try { $null = Save-CatalogBackup $journalDonor -Clock $clock } catch { $failure = $_.Exception }
    Assert ($null -ne $failure -and (Test-Path -LiteralPath $beforeJournalBackup)) 'Backup fails without replacing prior generations while the writer owns the journal'
} finally {
    if (!$child.HasExited) { $child.Kill() }
    if (!$child.WaitForExit(10000)) { throw 'The journal fixture did not stop.' }
    $child.Dispose()
}
$journalBackup = Save-CatalogBackup $journalDonor -Clock $clock
$journalManifest = Read-LookRecoveryManifest ($journalBackup + '.manifest.json')
$beforeJournalManifest = Read-LookRecoveryManifest ($beforeJournalBackup + '.manifest.json')
Assert ($journalBackup -ne $beforeJournalBackup -and $journalManifest.files.Count -eq 2 -and
    (Test-CurrentBackupLookState $journalManifest) -and !(Test-CurrentBackupLookState $beforeJournalManifest)) 'Backup and deduplication use the acknowledged pair even when the primary still has the old value'
Assert ((Get-FileHash -LiteralPath $mirror).Hash -eq $previousHash -and (Get-FileHash -LiteralPath $journalFile).Hash -eq $pairHash) 'Backing up the interrupted pair leaves both source files unchanged'
$recoveredCatalog = Join-Path $root 'journal-recovered.cocatalogdb'
$recoveredData = Join-Path $root 'journal-recovered-state'
$recoveredKey = Get-CatalogKey $recoveredCatalog
$null = & $restore -BackupDatabase $journalBackup -DestinationDatabase $recoveredCatalog -UserDataRoot $recoveredData -Apply
$recoveredGenerationDir = Join-Path (Join-Path $recoveredData 'Variant looks') $recoveredKey
$recoveredLook = Join-Path $recoveredGenerationDir ($journalVariant + '.json')
$recoveredPair = Join-Path $recoveredGenerationDir ($journalVariant + '.previous.look')
Assert ((Get-FileHash -LiteralPath $recoveredLook).Hash -eq $currentHash -and
    (Get-FileHash -LiteralPath $recoveredCatalog).Hash -eq $originalHash) 'Restore opens the exact newest journal look and original database at a new path'
$previousBytes = [IO.File]::ReadAllBytes($recoveredPair)
Assert ([Text.Encoding]::ASCII.GetString($previousBytes,0,4) -ceq 'SFL1' -and
    [BitConverter]::ToString($previousBytes,4,16) -ceq [BitConverter]::ToString(([Guid]$journalVariant).ToByteArray()) -and
    [BitConverter]::ToString($previousBytes,20,32).Replace('-','') -eq $previousHash -and
    [Convert]::ToBase64String($previousBytes,52,$previousBytes.Length-52) -ceq [Convert]::ToBase64String([IO.File]::ReadAllBytes($mirror))) 'Restore preserves the predecessor as an independently verified SFL1 file'

[IO.File]::AppendAllText($mirror, ' ')
$conflictHash = (Get-FileHash -LiteralPath $mirror).Hash
$failure = $null
try { $null = Save-CatalogBackup $journalDonor -Clock $clock } catch { $failure = $_.Exception.Message }
Assert ($failure -match 'conflicts with its verified journal' -and (Get-FileHash -LiteralPath $mirror).Hash -eq $conflictHash -and
    (Get-FileHash -LiteralPath $journalFile).Hash -eq $pairHash) 'Unknown external primary bytes abort backup and preserve both versions'
[IO.File]::Move($mirror, (Join-Path $root 'conflicting-mirror.json'))
Assert ((Save-CatalogBackup $journalDonor -Clock $clock) -eq $journalBackup) 'Missing compatibility files still resolve to the same acknowledged pair'

$reservedDestination = Join-Path $root 'journal-reserved.cocatalogdb'
$reservedDir = Join-Path (Join-Path $recoveredData 'Look generations') (Get-CatalogKey $reservedDestination)
[void][IO.Directory]::CreateDirectory($reservedDir)
$reservedPair = Join-Path $reservedDir ([IO.Path]::GetFileName($journalFile))
[IO.File]::Copy($journalFile, $reservedPair)
[IO.File]::Copy($journalFile + '.lock', $reservedPair + '.lock')
$failure = $null
try { $null = & $restore -BackupDatabase $journalBackup -DestinationDatabase $reservedDestination -UserDataRoot $recoveredData -Apply }
catch { $failure = $_.Exception.Message }
Assert ($failure -match 'Saved look generations already exist' -and !(Test-Path -LiteralPath $reservedDestination) -and
    (Get-FileHash -LiteralPath $reservedPair).Hash -eq $pairHash) 'Restore protects a destination with journal-only state'
$failure = $null
try { $null = @(Get-CatalogLookFiles $recoveredData (Get-CatalogKey $reservedDestination)) } catch { $failure = $_.Exception.Message }
Assert ($failure -match 'belongs to another catalog or variant') 'Backup rejects a pair copied under a different catalog identity'

$record = [IO.File]::ReadAllBytes($journalFile)
$record[$record.Length - 1] = $record[$record.Length - 1] -bxor 1
[IO.File]::WriteAllBytes($journalFile, $record)
$failure = $null
try { $null = Save-CatalogBackup $journalDonor -Clock $clock } catch { $failure = $_.Exception.Message }
Assert ($failure -match 'failed its checksum' -and (Test-Path -LiteralPath ($journalBackup + '.spektrafilm.zip'))) 'A damaged pair fails backup and the earlier verified archive remains available'
Write-Output "PASS: $count joint backup assertions. Evidence: $root"
