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
$first=Save-CatalogBackup $db
Assert (Test-Path -LiteralPath ($first+'.spektrafilm.zip')) 'Joint look archive created'
Assert (Test-Path -LiteralPath ($first+'.manifest.json')) 'Joint manifest created'
$manifest=[IO.File]::ReadAllText($first+'.manifest.json')|ConvertFrom-Json
Assert ($manifest.files.Count -eq 2) 'Both saved and recovery records included'
Assert ($manifest.catalogSha256 -eq $originalHash) 'DB fingerprint matches original'
Assert ($manifest.databaseIntegrity -eq 'ok') 'Passed full SQLite integrity check before publishing'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip=[IO.Compression.ZipFile]::OpenRead($first+'.spektrafilm.zip')
try {
    foreach($entryName in @('Variant looks/11111111111111111111111111111111.json','Look recovery/11111111111111111111111111111111.pending.json')) {
        Assert ($null -ne $zip.GetEntry($entryName)) "Archive contains $entryName"
    }
} finally {$zip.Dispose()}
$same=Save-CatalogBackup $db
Assert ($same -eq $first) 'Unchanged DB and state reuse complete backup'
[IO.File]::WriteAllText($look,'{"value":2}')
$second=Save-CatalogBackup $db
Assert ($second -ne $first) 'Look-only edit creates new joint backup'
$zip=[IO.Compression.ZipFile]::OpenRead($second+'.spektrafilm.zip')
try {
    $reader=New-Object IO.StreamReader($zip.GetEntry('Variant looks/11111111111111111111111111111111.json').Open())
    try {Assert ($reader.ReadToEnd() -eq '{"value":2}') 'Restorable archive contains latest look'}finally{$reader.Dispose()}
}finally{$zip.Dispose()}
[IO.File]::WriteAllText($second+'.spektrafilm.zip','corrupted backup archive')
$third=Save-CatalogBackup $db
Assert ($third -ne $second) 'Corrupted prior archive is not accepted for deduplication'
for($i=3;$i -le 7;$i++){[IO.File]::WriteAllText($look,"{`"value`":$i}");$null=Save-CatalogBackup $db}
$folder=Split-Path -Parent $third
$databases=@(Get-ChildItem -LiteralPath $folder -Filter '*.cocatalogdb' -File)
Assert ($databases.Count -eq 5) 'Retains five complete DB generations'
Assert (@(Get-ChildItem -LiteralPath $folder -Filter '*.spektrafilm.zip' -File).Count -eq 5) 'Companion archives rotate together'
Assert (@(Get-ChildItem -LiteralPath $folder -Filter '*.manifest.json' -File).Count -eq 5) 'Companion manifests rotate together'
Assert ((Get-FileHash -LiteralPath $db -Algorithm SHA256).Hash -eq $originalHash) 'Original DB remains unchanged'
Assert (@(Get-ChildItem -LiteralPath $folder -Filter '*.tmp' -File).Count -eq 0) 'No incomplete temporary backups remain'
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
$restorable = Save-CatalogBackup $db
$restore = Join-Path $repo 'Restore-SpektrafilmCatalog.ps1'
$restored = Join-Path $root 'restored\test È.cocatalogdb'
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
$pendingCopy=[IO.File]::ReadAllText($restoredPending)|ConvertFrom-Json
Assert ($pendingCopy.CatalogKey -eq $restoredKey -and $pendingCopy.BaseHash -eq $baseHash -and $pendingCopy.Look.Look.Exposure -eq 2.2) 'Pending recovery rebased without changing its look or base hash'
Assert (([IO.File]::ReadAllText($recovery)|ConvertFrom-Json).CatalogKey -eq $key) 'Original pending checkpoint unchanged'
$failure = $null
try { $null = & $restore -BackupDatabase $restorable -DestinationDatabase $restored -UserDataRoot $restoredData -Apply }
catch { $failure = $_.Exception.Message }
Assert ($failure -match 'unused destination') 'Restore refuses to overwrite a catalog'
Assert ((Get-FileHash -LiteralPath $restored -Algorithm SHA256).Hash -eq $originalHash) 'Failed overwrite leaves restored catalog unchanged'
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
Write-Output "PASS: $count joint backup assertions. Evidence: $root"
