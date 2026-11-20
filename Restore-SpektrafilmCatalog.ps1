[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $BackupDatabase,
    [Parameter(Mandatory)][string] $DestinationDatabase,
    [string] $UserDataRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FilmToolsForCaptureOne'),
    [switch] $Apply
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CatalogIntegrity.ps1')
Add-Type -AssemblyName System.IO.Compression

function Get-PlainPath([string] $path, [switch] $AllowMissing) {
    $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($path)
    $directory = $full
    # Walk ancestors as well as the target. A linked root could send a staging
    # file or rollback operation to a location outside the restore destination.
    while ($directory) {
        if (Test-Path -LiteralPath $directory) {
            $item = Get-Item -LiteralPath $directory -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Choose an ordinary path without links: $directory"
            }
        }
        $directory = [IO.Path]::GetDirectoryName($directory)
    }
    if (!$AllowMissing -and !(Test-Path -LiteralPath $full -PathType Leaf)) { throw "Missing backup file: $full" }
    return $full
}

function Get-CatalogKey([string] $path) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($path.ToUpperInvariant())))).Replace('-', '') }
    finally { $sha.Dispose() }
}

function Read-Manifest([string] $path) {
    if ((Get-Item -LiteralPath $path).Length -gt 32MB) { throw 'The backup manifest is unexpectedly large.' }
    $document = [IO.File]::ReadAllText($path) | ConvertFrom-Json
    if ($document.schemaVersion -notin 1, 2 -or $document.catalogKey -notmatch '^[0-9a-fA-F]{64}$' -or
        $document.catalogSha256 -notmatch '^[0-9a-fA-F]{64}$' -or $document.looksSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        $document.stateDigest -notmatch '^[0-9a-fA-F]{64}$' -or $null -eq $document.files) {
        throw 'This is not a supported joint catalog backup manifest.'
    }
    $names = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
    foreach ($record in $document.files) {
        if ($record.path -cnotmatch '^(Variant looks/[0-9a-f]{32}(\.json|\.previous\.look)|Look recovery/[0-9a-f]{32}\.(pending|grain)\.json|Look recovery/Preserved originals/[0-9a-f]{32}-[A-Za-z0-9-]+\.json)$' -or
            !$names.Add($record.path) -or $record.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            ($record.bytes -isnot [int] -and $record.bytes -isnot [long]) -or $record.bytes -lt 0) {
            throw 'The backup manifest has an invalid or duplicate state entry.'
        }
    }
    if ($document.schemaVersion -eq 2 -and (Get-CatalogStateDigest @($document.files)) -ne $document.stateDigest) {
        throw 'The saved-look manifest digest does not match.'
    }
    return $document
}

function Copy-VerifiedState($zip, $manifest, [string] $catalogKey, [hashtable] $staging) {
    $writing = $staging.Count -ne 0
    $entries = @($zip.Entries)
    $manifestEntry = $zip.GetEntry('manifest.json')
    if ($entries.Count -ne 1 + @($manifest.files).Count -or $null -eq $manifestEntry -or $manifestEntry.Length -gt 32MB) {
        throw 'The saved-look archive does not match its manifest.'
    }
    # Version 1 calculated its digest with a shell-dependent order. Match the
    # embedded record in the hash-verified ZIP and reverify every file instead.
    $reader = New-Object IO.StreamReader($manifestEntry.Open(), (New-Object Text.UTF8Encoding($false, $true)))
    try { $embedded = $reader.ReadToEnd() | ConvertFrom-Json }
    finally { $reader.Dispose() }
    if ($embedded.schemaVersion -ne $manifest.schemaVersion -or $embedded.catalogKey -ne $manifest.catalogKey -or
        $embedded.stateDigest -ne $manifest.stateDigest -or @($embedded.files).Count -ne @($manifest.files).Count -or
        (Get-CatalogStateDigest @($embedded.files)) -ne (Get-CatalogStateDigest @($manifest.files))) {
        throw 'The saved-look archive contains a different catalog or state generation.'
    }
    foreach ($record in $manifest.files) {
        $entry = $zip.GetEntry($record.path)
        if ($null -eq $entry -or $entry.Length -ne $record.bytes) { throw "Missing or invalid backup entry: $($record.path)" }
        $stream = $entry.Open()
        try { $hash = (Get-FileHash -InputStream $stream -Algorithm SHA256).Hash }
        finally { $stream.Dispose() }
        if ($hash -ne $record.sha256) { throw "Saved-look backup entry checksum failed: $($record.path)" }

        $parts = $record.path -split '/', 2
        $path = if ($writing) { Join-Path $staging[$parts[0]] $parts[1] }
        if ($record.path.EndsWith('.previous.look', [StringComparison]::Ordinal) -and
            ($entry.Length -le 52 -or $entry.Length -gt (64KB + 52))) { throw "Invalid previous look size: $($record.path)" }
        if ($record.path.EndsWith('.pending.json', [StringComparison]::Ordinal)) {
            if ($entry.Length -gt 64KB) { throw "Recovery checkpoint is too large: $($record.path)" }
            $reader = New-Object IO.StreamReader($entry.Open(), (New-Object Text.UTF8Encoding($false, $true)))
            try { $checkpoint = $reader.ReadToEnd() | ConvertFrom-Json }
            finally { $reader.Dispose() }
            if ($checkpoint.Version -ne 1 -or $checkpoint.CatalogKey -ne $manifest.catalogKey -or
                $null -eq $checkpoint.Look -or !$checkpoint.PSObject.Properties['BaseHash'] -or
                ([Guid]::Parse($checkpoint.Variant)).ToString('N') + '.pending.json' -cne $parts[1]) {
                throw "Recovery checkpoint identity is invalid: $($record.path)"
            }
            if ($writing) {
                $checkpoint.CatalogKey = $catalogKey
                [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
                [IO.File]::WriteAllText($path, ($checkpoint | ConvertTo-Json -Depth 32 -Compress), (New-Object Text.UTF8Encoding($false)))
            }
            continue
        }
        if ($writing) {
            [IO.Directory]::CreateDirectory((Split-Path -Parent $path)) | Out-Null
            $stream = $entry.Open()
            try {
                $output = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $stream.CopyTo($output); $output.Flush($true) }
                finally { $output.Dispose() }
            } finally { $stream.Dispose() }
        }
    }
}

$backup = Get-PlainPath $BackupDatabase
$destination = Get-PlainPath $DestinationDatabase -AllowMissing
$dataRoot = Get-PlainPath $UserDataRoot -AllowMissing
if ([IO.Path]::GetExtension($backup) -ine '.cocatalogdb' -or [IO.Path]::GetExtension($destination) -ine '.cocatalogdb') {
    throw 'Choose .cocatalogdb files for the backup and destination.'
}
if (Test-Path -LiteralPath $destination) { throw 'Choose a new catalog filename. Restore requires an unused destination.' }
foreach ($suffix in '-wal', '-shm', '-journal') {
    if ((Test-Path -LiteralPath ($destination + $suffix)) -or (Test-Path -LiteralPath ($backup + $suffix))) {
        throw 'Close the catalog and resolve its SQLite sidecar files before restoring.'
    }
}
$manifest = Read-Manifest (Get-PlainPath ($backup + '.manifest.json'))
$lookArchive = Get-PlainPath ($backup + '.spektrafilm.zip')
$catalogKey = Get-CatalogKey $destination
$scopes = @('Variant looks', 'Look recovery')
$targets = @{}
foreach ($scope in $scopes) {
    $targets[$scope] = Get-PlainPath (Join-Path (Join-Path $dataRoot $scope) $catalogKey) -AllowMissing
    if (Test-Path -LiteralPath $targets[$scope]) { throw 'Saved looks already exist for that catalog path. Choose a new destination.' }
}

$id = [Guid]::NewGuid().ToString('N')
$staging = @{}
$published = New-Object 'System.Collections.Generic.List[string]'
$verified = $false
$stageCatalog = Join-Path (Split-Path -Parent $destination) ('.spektra-restore-' + $id + '.tmp')
$archiveStream = [IO.File]::Open($lookArchive, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $databaseStream = [IO.File]::Open($backup, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        if ((Get-FileHash -InputStream $databaseStream -Algorithm SHA256).Hash -ne $manifest.catalogSha256 -or
            (Get-FileHash -InputStream $archiveStream -Algorithm SHA256).Hash -ne $manifest.looksSha256) {
            throw 'Catalog or saved-look archive checksum failed.'
        }
        $archiveStream.Position = 0
        $zip = New-Object IO.Compression.ZipArchive($archiveStream, [IO.Compression.ZipArchiveMode]::Read, $true)
        try {
            if ($Apply) {
                [IO.Directory]::CreateDirectory((Split-Path -Parent $destination)) | Out-Null
                $databaseStream.Position = 0
                $file = [IO.File]::Open($stageCatalog, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                try { $databaseStream.CopyTo($file); $file.Flush($true) }
                finally { $file.Dispose() }
                foreach ($scope in $scopes) { $staging[$scope] = Join-Path (Split-Path -Parent $targets[$scope]) ('.spektra-restore-' + $id) }
            }
            Copy-VerifiedState $zip $manifest $catalogKey $staging
        } finally { $zip.Dispose() }
    } finally { $databaseStream.Dispose() }
    if (!$Apply) {
        Write-Output "Restore preview verified backup checksums for $(@($manifest.files).Count) saved-look and recovery files."
        Write-Output "Destination: $destination"
        Write-Output 'Run the same command with -Apply to restore into this new location.'
        return
    }
    Confirm-CatalogIntegrity $stageCatalog
    foreach ($scope in $scopes) {
        if (!(Test-Path -LiteralPath $staging[$scope])) { continue }
        [IO.Directory]::Move($staging[$scope], $targets[$scope])
        $published.Add($targets[$scope])
    }
    # The database's exclusive publish is the visible commit. State already
    # exists at this point, so a subsequent launcher can find it immediately.
    [IO.File]::Move($stageCatalog, $destination)
    $verified = $true
    Write-Output "Restored verified catalog and saved looks: $destination"
} finally {
    $archiveStream.Dispose()
    if (Test-Path -LiteralPath $stageCatalog -PathType Leaf) { Remove-Item -LiteralPath $stageCatalog }
    $cleanup = @($staging.Values)
    if (!$verified) { $cleanup += @($published.ToArray()) }
    foreach ($scratch in $cleanup) {
        if (!(Test-Path -LiteralPath $scratch -PathType Container)) { continue }
        $plain = Get-PlainPath $scratch -AllowMissing
        $parent = Split-Path -Parent $plain
        $intendedParents = @($scopes | ForEach-Object { Join-Path $dataRoot $_ })
        if ($parent -notin $intendedParents -or (Split-Path -Leaf $plain) -notin @('.spektra-restore-' + $id, $catalogKey)) {
            throw "Unexpected restore cleanup path: $plain"
        }
        Remove-Item -LiteralPath $plain -Recurse -Force
    }
}
