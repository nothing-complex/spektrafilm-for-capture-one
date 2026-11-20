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
foreach($name in @('New-LookBackup','Save-CatalogBackup')) {
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
Write-Output "PASS: $count joint backup assertions. Evidence: $root"
