[CmdletBinding()]
param(
    [string] $CatalogPath,
    [string] $CaptureOnePath,
    [switch] $CheckOnly,
    [switch] $WaitForPanel,
    [switch] $ShowTool,
    [ValidateRange(1, 180)][int] $PanelTimeoutSeconds = 45,
    [switch] $Shortcut,
    [string] $UserDataDirectory,
    [switch] $SkipCatalogLookRecovery,
    [switch] $RecoverUpdatedCatalogLooks
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'CatalogIntegrity.ps1')
. (Join-Path $PSScriptRoot 'ReferencePython.ps1')
$appName = 'Spektrafilm for Capture One'
$userData = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'FilmToolsForCaptureOne'

function Get-CaptureOneExecutable {
    if ($CaptureOnePath) {
        $candidates = @($CaptureOnePath)
    } else {
        $candidates = @(Join-Path $env:ProgramFiles 'Capture One\Capture One\CaptureOne.exe')
        $keys = @(
            'HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\CaptureOne.exe',
            'HKLM:\Software\Microsoft\Windows\CurrentVersion\App Paths\CaptureOne.exe'
        )
        foreach ($key in $keys) {
            if (Test-Path -LiteralPath $key) {
                $candidate = (Get-Item -LiteralPath $key).GetValue('')
                if ($candidate) { $candidates += $candidate }
            }
        }
    }
    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Get-Item -LiteralPath $candidate).FullName
        }
    }
    if ($CaptureOnePath -or $CheckOnly) { throw "Capture One could not be found. Pass -CaptureOnePath with the path to CaptureOne.exe." }
    return Select-ExistingFile 'Locate CaptureOne.exe' 'Capture One executable|CaptureOne.exe'
}

function Select-ExistingFile([string] $title, [string] $filter) {
    Add-Type -AssemblyName System.Windows.Forms
    $dialog = New-Object System.Windows.Forms.OpenFileDialog
    try {
        $dialog.Title = $title
        $dialog.Filter = $filter
        $dialog.RestoreDirectory = $true
        $dialog.CheckFileExists = $true
        $dialog.CheckPathExists = $true
        if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) { return $dialog.FileName }
    } finally {
        $dialog.Dispose()
    }
}

function Read-SpektrafilmLaunchStatus([string] $Path, [string] $SessionId, [int] $ProcessId) {
    try {
        if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
        if ((Get-Item -LiteralPath $Path).Length -gt 16KB) { return $null }
        $state = [IO.File]::ReadAllText($Path) | ConvertFrom-Json
        if ($state.SchemaVersion -ne 1 -or $state.SessionId -cne $SessionId -or $state.ProcessId -ne $ProcessId -or
            $state.Phase -cnotin @('Starting','UnsupportedHost','CatalogUnavailable','WaitingForCatalog','WaitingForPalette','LoadingControls','Ready','Closed','Failed') -or
            $state.Export -cnotin @('Checking','Connected','Unavailable','Detached') -or
            $state.Viewer -cnotin @('Unchecked','Disabled','WaitingForImage','Rendering','Visible','ProcessingError')) {
            return $null
        }
        return $state
    } catch { return $null }
}

function Wait-SpektrafilmPanel([Diagnostics.Process] $Process, [string] $Path, [string] $SessionId, [int] $Seconds) {
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    $state = $null
    while ($elapsed.Elapsed.TotalSeconds -lt $Seconds) {
        $Process.Refresh()
        if ($Process.HasExited) { throw "Capture One exited before the panel became ready (exit $($Process.ExitCode))." }
        $current = Read-SpektrafilmLaunchStatus $Path $SessionId $Process.Id
        if ($current) { $state = $current }
        if ($state.Phase -eq 'Ready') { return $state }
        switch ($state.Phase) {
            'UnsupportedHost' { throw 'The startup hook rejected this Capture One build.' }
            'CatalogUnavailable' { throw 'The startup hook could not access the selected catalog.' }
            'Closed' { throw 'Capture One closed before the panel became ready.' }
            'Failed' { throw 'The startup hook encountered an error. See panel.log beside the launch status folder.' }
        }
        Start-Sleep -Milliseconds 200
    }
    if (!$state) { throw "Capture One started but did not report the Spektrafilm startup hook within $Seconds seconds." }
    throw "Capture One is still starting Spektrafilm ($($state.Phase)). See the launch status file for updates."
}

function Confirm-RequiredFile([string] $path) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "A release file is missing: $path. Extract the full release ZIP before starting $appName."
    }
}

function Confirm-CatalogIsIdle([string] $path) {
    if (Get-Process -Name CaptureOne -ErrorAction SilentlyContinue) {
        throw "Close every Capture One window before starting $appName."
    }
    foreach ($suffix in @('-wal', '-shm', '-journal')) {
        if (Test-Path -LiteralPath "$path$suffix") {
            throw "Capture One left catalog transaction files. Open the catalog in Capture One normally, close it, then launch $appName again."
        }
    }
}

function Get-CatalogPath([string] $path) {
    $item = Get-Item -LiteralPath $path
    if ($item.PSIsContainer) {
        $databases = @(Get-ChildItem -LiteralPath $item.FullName -File -Filter '*.cocatalogdb')
        if ($databases.Count -ne 1) { throw 'Choose a catalog bundle containing one .cocatalogdb file.' }
        $item = $databases[0]
    }
    if ($item.Extension -ine '.cocatalogdb') { throw 'Choose an existing Capture One .cocatalogdb catalog database.' }
    if (![string]::Equals($item.FullName, (Resolve-Path -LiteralPath $item.FullName).ProviderPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Choose the original catalog database path.'
    }
    return $item.FullName
}

function Get-CatalogLookFiles([string] $key) {
    foreach ($scope in @('Variant looks', 'Look recovery')) {
        $root = Join-Path (Join-Path $userData $scope) $key
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

function Test-CatalogLookState([string] $key) {
    foreach ($scope in @('Variant looks', 'Look recovery')) {
        if (Test-Path -LiteralPath (Join-Path (Join-Path $userData $scope) $key)) { return $true }
    }
    return $false
}

function New-LookBackup([string] $path, [string] $archive) {
    Add-Type -AssemblyName System.IO.Compression
    $key = Get-CatalogKey $path
    $records = New-Object 'System.Collections.Generic.List[object]'
    $zipStream = [IO.File]::Open($archive, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $zip = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            foreach ($file in Get-CatalogLookFiles $key) {
                $input = [IO.File]::Open($file.Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
                try {
                    $entry = $zip.CreateEntry($file.Relative, [IO.Compression.CompressionLevel]::Optimal)
                    $output = $entry.Open()
                    try { $input.CopyTo($output) } finally { $output.Dispose() }
                    $input.Position = 0
                    $digest = (Get-FileHash -InputStream $input -Algorithm SHA256).Hash.ToLowerInvariant()
                    $records.Add([ordered]@{path=$file.Relative;bytes=$input.Length;sha256=$digest})
                } finally { $input.Dispose() }
            }
            $stateDigest = Get-CatalogStateDigest $records.ToArray()
            $manifest = [ordered]@{schemaVersion=2;catalogKey=$key;stateDigest=$stateDigest;files=$records.ToArray()}
            $entry = $zip.CreateEntry('manifest.json')
            $writer = New-Object IO.StreamWriter($entry.Open(), (New-Object Text.UTF8Encoding($false)))
            try { $writer.Write(($manifest | ConvertTo-Json -Depth 6)) } finally { $writer.Dispose() }
        } finally { $zip.Dispose() }
        $zipStream.Flush($true)
    } finally { $zipStream.Dispose() }
    return $manifest
}

function Save-CatalogBackup([string] $path, [scriptblock] $Clock = { [DateTime]::UtcNow }) {
    Confirm-CatalogIsIdle $path
    $restoreReceipt = Get-CatalogRestoreReceiptPath $userData $path
    if (Test-Path -LiteralPath $restoreReceipt) { throw 'Resume the interrupted catalog restore before backing up this catalog.' }
    $key = (Get-CatalogKey $path).Substring(0, 20)
    $folder = Join-Path (Join-Path $userData 'Catalog backups') $key
    [System.IO.Directory]::CreateDirectory($folder) | Out-Null
    $tempPath = Join-Path $folder ([Guid]::NewGuid().ToString('N') + '.tmp')
    $tempLooks = Join-Path $folder ([Guid]::NewGuid().ToString('N') + '.tmp')
    $tempManifest = Join-Path $folder ([Guid]::NewGuid().ToString('N') + '.tmp')
    try {
        $backupLock = [IO.File]::Open((Join-Path $folder '.backup.lock'), [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    } catch [IO.IOException] {
        throw "Could not lock this catalog's backup folder. If another launch is making a backup, wait for it to finish. $($_.Exception.Message)"
    }
    try {
        try {
            $inputDb = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        } catch [IO.IOException] {
            throw "The catalog is busy. Close the program using it before starting $appName. $($_.Exception.Message)"
        }
        try {
            Confirm-CatalogIsIdle $path
            if (Test-Path -LiteralPath $restoreReceipt) { throw 'Resume the interrupted catalog restore before backing up this catalog.' }
            $header = New-Object byte[] 16
            if ($inputDb.Read($header, 0, $header.Length) -ne $header.Length -or
                [Text.Encoding]::ASCII.GetString($header) -cne "SQLite format 3`0") {
                throw 'The selected catalog database does not have a valid SQLite header.'
            }
            $inputDb.Position = 0
            $outputDb = [IO.File]::Open($tempPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try { $inputDb.CopyTo($outputDb); $outputDb.Flush($true) }
            finally { $outputDb.Dispose() }
            $looks = New-LookBackup $path $tempLooks
            Confirm-CatalogIsIdle $path
        } finally {
            $inputDb.Dispose()
        }
        $name = [IO.Path]::GetFileNameWithoutExtension($path)
        Confirm-CatalogIsIdle $path
        $existing = @(Get-ChildItem -LiteralPath $folder -Filter '*.cocatalogdb' -File | Sort-Object LastWriteTimeUtc, Name -Descending)
        $previousState = $null
        if ($existing.Count -gt 0 -and (Test-Path -LiteralPath ($existing[0].FullName + '.manifest.json'))) {
            try { $previousState = [IO.File]::ReadAllText($existing[0].FullName + '.manifest.json') | ConvertFrom-Json } catch { }
        }
        $catalogHash = (Get-FileHash -LiteralPath $tempPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $verifiedDatabase = $existing.Count -gt 0 -and $previousState.databaseIntegrity -eq 'ok' -and
            $previousState.catalogSha256 -eq $catalogHash -and
            $existing[0].Length -eq (Get-Item -LiteralPath $tempPath).Length -and
            $catalogHash -eq (Get-FileHash -LiteralPath $existing[0].FullName -Algorithm SHA256).Hash
        if ($verifiedDatabase -and $previousState.stateDigest -eq $looks.stateDigest -and
            (Test-Path -LiteralPath ($existing[0].FullName + '.spektrafilm.zip')) -and
            (Get-FileHash -LiteralPath ($existing[0].FullName + '.spektrafilm.zip') -Algorithm SHA256).Hash -eq $previousState.looksSha256) {
            return $existing[0].FullName
        }
        if (!$verifiedDatabase) { Confirm-CatalogIntegrity $tempPath }
        $documentId = if ($verifiedDatabase -and $previousState.nativeDocumentId) { $previousState.nativeDocumentId }
            else { Get-CatalogDocumentId $tempPath }
        $created = [DateTime](& $Clock)
        $time = $created.ToString('yyyyMMdd-HHmmss-fff', [Globalization.CultureInfo]::InvariantCulture)
        $stem = "$name-$time"
        $sequence = 0
        $generationPattern = '^' + [Regex]::Escape($stem) + '-(\d{6})\.cocatalogdb(?:\.spektrafilm\.zip|\.manifest\.json)?$'
        foreach ($file in @(Get-ChildItem -LiteralPath $folder -File)) {
            if ($file.Name -match $generationPattern) { $sequence = [Math]::Max($sequence, 1 + [int]$Matches[1]) }
        }
        $ordinal = $sequence.ToString('D6', [Globalization.CultureInfo]::InvariantCulture)
        $destination = Join-Path $folder "$stem-$ordinal.cocatalogdb"
        $joint = [ordered]@{schemaVersion=$looks.schemaVersion;createdUtc=$created.ToString('o');catalogKey=$looks.catalogKey;
            stateDigest=$looks.stateDigest;catalogSha256=$catalogHash;nativeDocumentId=$documentId;databaseIntegrity='ok';
            looksSha256=(Get-FileHash -LiteralPath $tempLooks -Algorithm SHA256).Hash.ToLowerInvariant();files=$looks.files}
        $manifestStream = [IO.File]::Open($tempManifest, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $bytes = [Text.Encoding]::UTF8.GetBytes(($joint | ConvertTo-Json -Depth 6))
            $manifestStream.Write($bytes, 0, $bytes.Length)
            $manifestStream.Flush($true)
        } finally { $manifestStream.Dispose() }
        $published = [Collections.Generic.List[string]]::new()
        try {
            foreach ($part in @(@($tempLooks, ($destination + '.spektrafilm.zip')),
                @($tempManifest, ($destination + '.manifest.json')), @($tempPath, $destination))) {
                [IO.File]::Move($part[0], $part[1])
                $published.Add($part[1])
            }
        } catch {
            foreach ($part in $published) { Remove-Item -LiteralPath $part -ErrorAction SilentlyContinue }
            throw
        }
        foreach ($older in @($existing | Select-Object -Skip 4)) {
            try {
                Remove-Item -LiteralPath $older.FullName -ErrorAction Stop
                foreach ($suffix in @('.spektrafilm.zip','.manifest.json')) {
                    $companion = $older.FullName + $suffix
                    if (Test-Path -LiteralPath $companion) { Remove-Item -LiteralPath $companion -ErrorAction Stop }
                }
            }
            catch { Write-Warning "Could not remove an older backup at $($older.FullName)." }
        }
        return $destination
    } finally {
        try {
            foreach ($temp in @($tempPath, $tempLooks, $tempManifest)) {
                if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp }
            }
        } finally { $backupLock.Dispose() }
    }
}

function Read-LookRecoveryManifest([string] $path) {
    try {
        $file = Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $file.Length -gt 32MB) { return }
        $manifest = [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json
        if ($manifest.schemaVersion -in 1, 2 -and $manifest.databaseIntegrity -eq 'ok' -and
            $manifest.catalogKey -match '^[0-9A-F]{64}$' -and $manifest.catalogSha256 -match '^[0-9A-F]{64}$') {
            return $manifest
        }
    } catch { Write-Verbose "Could not read catalog backup index: $($_.Exception.Message)" }
}

function Find-CatalogLookBackup([string] $path, [string] $backup) {
    $destinationKey = Get-CatalogKey $path
    if (Test-CatalogLookState $destinationKey) { return }
    $selected = Read-LookRecoveryManifest ($backup + '.manifest.json')
    if (!$selected -or $selected.catalogKey -ne $destinationKey) { return }
    $backups = Get-Item -LiteralPath (Join-Path $userData 'Catalog backups') -Force
    if ($backups.Attributes -band [IO.FileAttributes]::ReparsePoint) { return }
    $documentId = $selected.nativeDocumentId
    if (!$documentId) { $documentId = Get-CatalogDocumentId $backup }
    $legacyDocumentIds = @{}
    $exactBySource = @{}
    $relatedBySource = @{}
    foreach ($origin in Get-ChildItem -LiteralPath $backups.FullName -Directory -Force) {
        if ($origin.Name -notmatch '^[0-9A-F]{20}$' -or ($origin.Attributes -band [IO.FileAttributes]::ReparsePoint)) { continue }
        foreach ($candidate in Get-ChildItem -LiteralPath $origin.FullName -Filter '*.cocatalogdb' -File -Force |
                Sort-Object LastWriteTimeUtc, Name -Descending) {
            if ($candidate.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
            $manifest = Read-LookRecoveryManifest ($candidate.FullName + '.manifest.json')
            if (!$manifest -or !$manifest.catalogKey.StartsWith($origin.Name, [StringComparison]::OrdinalIgnoreCase) -or
                !($manifest.files | Where-Object { $_.path -cmatch '^(Variant looks/[0-9a-f]{32}\.json|Look recovery/[0-9a-f]{32}\.pending\.json)$' } |
                    Select-Object -First 1)) { continue }
            $exact = $manifest.catalogSha256 -eq $selected.catalogSha256
            if (!$exact) {
                if (!$documentId) { continue }
                $candidateId = $manifest.nativeDocumentId
                if (!$candidateId) {
                    $digest = $manifest.catalogSha256
                    if (!$legacyDocumentIds.ContainsKey($digest)) {
                        $legacyDocumentIds[$digest] = Get-CatalogDocumentId $candidate.FullName
                    }
                    $candidateId = $legacyDocumentIds[$digest]
                }
                if ($documentId -ne $candidateId) { continue }
            }
            $matchesBySource = if ($exact) { $exactBySource } else { $relatedBySource }
            if (!$matchesBySource.ContainsKey($manifest.catalogKey)) {
                $matchesBySource[$manifest.catalogKey] = [pscustomobject]@{
                    Database = $candidate.FullName
                    Manifest = $manifest
                    Exact = $exact
                }
            }
        }
    }
    # Once a catalog is forked, otherwise identical databases can have different film looks.
    # Never select a different lineage just because its archive was created more recently.
    $lineages = @(@($exactBySource.Keys) + @($relatedBySource.Keys) | Sort-Object -Unique)
    foreach ($key in $lineages) {
        if ($exactBySource.ContainsKey($key)) { $exactBySource[$key] }
        else { $relatedBySource[$key] }
    }
}

function Test-CurrentBackupLookState($manifest) {
    $key = [string]$manifest.catalogKey
    if (!$key -or $key -notmatch '^[0-9A-F]{64}$') { return $false }
    if (!(Test-CatalogLookState $key)) { return $true }
    $live = @(Get-CatalogLookFiles $key)
    if ($live.Count -ne @($manifest.files).Count) { return $false }
    $archives = @{}
    foreach ($record in $manifest.files) { $archives[$record.path] = $record }
    foreach ($file in $live) {
        $record = $archives[$file.Relative]
        if (!$record -or (Get-FileHash -LiteralPath $file.Path -Algorithm SHA256).Hash -ne $record.sha256) { return $false }
    }
    return $true
}

function Restore-DiscoveredCatalogLooks {
    [CmdletBinding()]
    param([string] $path, [string] $backup, [string] $restoreScript,
        [switch] $AcceptUpdated, [switch] $AskAboutUpdated)
    try {
        $candidates = @(Find-CatalogLookBackup $path $backup)
        if ($candidates.Count -eq 0) { return $false }
        if ($candidates.Count -gt 1) {
            Write-Warning ("Several catalog paths have matching Spektrafilm backups. Choose a source using Restore-SpektrafilmCatalog.ps1: " +
                (($candidates | ForEach-Object { $_.Database }) -join '; '))
            return $false
        }
        $match = $candidates[0]
        if (!(Test-CurrentBackupLookState $match.Manifest)) {
            Write-Warning "Saved looks at the source path have changed since the matching backup. No older looks were attached. Backup: $($match.Database)"
            return $false
        }
        $options = @{
            BackupDatabase = $match.Database
            DestinationDatabase = $path
            UserDataRoot = $userData
            SavedLooksOnly = $true
            AllowUpdatedCatalog = !$match.Exact
        }
        if (!$match.Exact -and !$AcceptUpdated) {
            if (!$AskAboutUpdated) {
                Write-Warning "A Spektrafilm backup may match this updated catalog. Launch with -RecoverUpdatedCatalogLooks to verify and restore it: $($match.Database)"
                return $false
            }
            $null = & $restoreScript @options
            Add-Type -AssemblyName System.Windows.Forms
            $answer = [System.Windows.Forms.MessageBox]::Show(
                "Verified saved looks for this updated catalog. Restore them from this backup?`n`n$($match.Database)", $appName,
                [System.Windows.Forms.MessageBoxButtons]::YesNo, [System.Windows.Forms.MessageBoxIcon]::Question,
                [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
            if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { return $false }
        }
        Confirm-CatalogIsIdle $path
        $null = & $restoreScript @options -Apply
        Write-Host "Restored Spektrafilm looks from $($match.Database)"
        return $true
    } catch {
        Write-Warning "Could not recover Spektrafilm looks from a matching backup. $($_.Exception.Message)"
        return $false
    }
}

function Prepare-SpektrafilmCatalog([string] $path, [string] $restoreScript, [switch] $SkipRecovery,
    [switch] $AcceptUpdated, [switch] $AskAboutUpdated) {
    $receipt = Get-CatalogRestoreReceiptPath $userData $path
    $pending = Read-CatalogRestoreReceipt $receipt
    if ($pending) {
        Confirm-CatalogIsIdle $path
        $options = @{
            BackupDatabase=$pending.BackupDatabase; DestinationDatabase=$path; UserDataRoot=$userData
            SavedLooksOnly=$pending.SavedLooksOnly; AllowUpdatedCatalog=$pending.AllowUpdatedCatalog
        }
        $null = & $restoreScript @options -Apply
        Write-Host 'Resumed an interrupted Spektrafilm catalog restore.'
    }
    $backup = Save-CatalogBackup $path
    if (!$SkipRecovery) {
        $recovered = Restore-DiscoveredCatalogLooks $path $backup $restoreScript -AcceptUpdated:$AcceptUpdated -AskAboutUpdated:$AskAboutUpdated
        if (Test-Path -LiteralPath $receipt) { throw 'The saved-look restore was interrupted. Close Capture One and launch Spektrafilm again to resume.' }
        if ($recovered) { $backup = Save-CatalogBackup $path }
    }
    return $backup
}

function Confirm-VulkanRenderer([string] $renderer) {
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $renderer
    $start.Arguments = '1 1'
    $start.WorkingDirectory = Split-Path -LiteralPath $renderer
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($name in @($start.EnvironmentVariables.Keys)) {
        if ($name -like 'SPEKTRAFILM_*' -or $name -eq 'DOTNET_STARTUP_HOOKS') {
            [void] $start.EnvironmentVariables.Remove($name)
        }
    }
    $start.EnvironmentVariables['SPEKTRAFILM_RESOURCE_DIR'] = $start.WorkingDirectory
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = New-Object IO.MemoryStream
        try {
            $stdout = $process.StandardOutput.BaseStream.CopyToAsync($output)
            $stderr = $process.StandardError.ReadToEndAsync()
            $pixel = New-Object byte[] 16
            [Array]::Copy([BitConverter]::GetBytes([single] 1), 0, $pixel, 12, 4)
            $process.StandardInput.BaseStream.Write($pixel, 0, $pixel.Length)
            $process.StandardInput.Close()
            if (!$process.WaitForExit(20000)) { throw 'The Vulkan renderer did not return a test pixel in 20 seconds.' }
            [void] $stdout.GetAwaiter().GetResult()
            $detail = $stderr.GetAwaiter().GetResult().Trim()
            if ($process.ExitCode -ne 0 -or $output.Length -ne 16) {
                throw "The Vulkan renderer could not run on this computer. $detail"
            }
            for ($offset = 0; $offset -lt 16; $offset += 4) {
                $value = [BitConverter]::ToSingle($output.GetBuffer(), $offset)
                if ([single]::IsNaN($value) -or [single]::IsInfinity($value)) { throw 'The Vulkan renderer returned an invalid test pixel.' }
            }
        } finally {
            $output.Dispose()
        }
    } finally {
        if (!$process.HasExited) { $process.Kill() }
        $process.Dispose()
    }
}

try {
    if (![Environment]::Is64BitOperatingSystem) { throw "$appName requires 64-bit Windows." }
    . (Join-Path $PSScriptRoot 'ReleaseManifest.ps1')
    $hostProfiles = @(Get-CaptureOneHostProfiles $PSScriptRoot)
    if ((Test-Path -LiteralPath (Join-Path $PSScriptRoot 'runtime-manifest.required')) -and
        !(Test-Path -LiteralPath (Join-Path $PSScriptRoot 'build-manifest.json'))) {
        throw 'The staged runtime is incomplete. Rebuild or extract the complete release before launching.'
    }
    if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'build-manifest.json')) {
        $buildManifest = Test-ReleaseManifest $PSScriptRoot
    }
    $app = Get-CaptureOneExecutable
    if (!$app) { return }
    if ([IO.Path]::GetFileName($app) -ine 'CaptureOne.exe') { throw 'Select CaptureOne.exe from the Capture One installation.' }
    $appDirectory = Split-Path -LiteralPath $app
    $appVersion = (Get-Item -LiteralPath $app).VersionInfo.FileVersion
    $architecture = Get-WindowsExecutableArchitecture $app
    $appAssembly = Get-ReleaseFileRecord $appDirectory 'CaptureOne.dll'
    $verified = Find-CaptureOneHost $hostProfiles $appVersion $architecture $appAssembly.sha256
    if (!$verified) {
        $versions = (@($hostProfiles.FileVersion | Sort-Object -Unique) -join ', ')
        throw "$appName supports verified Capture One builds $versions (Windows x64). The selected installation ($appVersion) does not match."
    }
    if ($buildManifest) {
        foreach ($dependency in $buildManifest.host.files) {
            $actual = if ($dependency.path -ieq 'CaptureOne.dll') { $appAssembly } else { Get-ReleaseFileRecord $appDirectory $dependency.path }
            if ($actual.sha256 -ne $dependency.sha256) { throw "The panel was built against different Capture One components ($($dependency.path)). Rebuild against this supported installation." }
        }
    }

    $hook = Join-Path $PSScriptRoot 'panel\NativePanel.dll'
    $renderer = Join-Path $PSScriptRoot 'renderer\SpektraStillRenderer.exe'
    Confirm-RequiredFile $hook
    Confirm-RequiredFile $renderer
    foreach ($file in @('stocks.json', 'profiles\preview\Linear Rec.2020.icc', 'profiles\preview\ProPhoto RGB.icc',
        'renderer\SpektraHanatos2025Spectra.f32', 'renderer\SpektraOutputGamutCompression.f32', 'renderer\parameter-capabilities.json')) {
        Confirm-RequiredFile (Join-Path $PSScriptRoot $file)
    }
    foreach ($shader in @('Copy','CurveDevelop','Diffusion','Dir','FilmExposure','FormatConvert','Grain','Halation','PrintScan','ScannerPost')) {
        Confirm-RequiredFile (Join-Path $PSScriptRoot "renderer\shaders\Spektra$shader.comp.spv")
    }

    $python = Get-SpektrafilmReferencePython $PSScriptRoot
    if ($CheckOnly) {
        Confirm-VulkanRenderer $renderer
        Write-Output "Capture One $appVersion ($architecture) matches the supported build. Release files and Vulkan render passed."
        Write-Output ("Verified host capabilities: " + ($verified.Capabilities -join ', '))
        if ($python) { Write-Output "Optional reference/developer Python found at $python" }
        else { Write-Output 'Optional reference/developer Python is not installed; panel and LUT use do not require it.' }
        return
    }

    if (Get-Process -Name CaptureOne -ErrorAction SilentlyContinue) { throw "Close every Capture One window before starting $appName." }
    if (!$CatalogPath) { $CatalogPath = Select-ExistingFile 'Choose an existing Capture One catalog database' 'Capture One catalog database|*.cocatalogdb' }
    if (!$CatalogPath) { return }
    if ($UserDataDirectory) { $userData = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($UserDataDirectory) }
    $database = Get-CatalogPath $CatalogPath
    $restoreScript = Join-Path $PSScriptRoot 'Restore-SpektrafilmCatalog.ps1'
    $recoveryOptions = @{SkipRecovery=$SkipCatalogLookRecovery; AcceptUpdated=$RecoverUpdatedCatalogLooks; AskAboutUpdated=$Shortcut}
    $backup = Prepare-SpektrafilmCatalog $database $restoreScript @recoveryOptions
    Confirm-CatalogIsIdle $database
    $start = New-Object System.Diagnostics.ProcessStartInfo
    $start.FileName = $app
    # Windows file paths cannot contain double quotes and this is always a file, not a trailing directory path.
    $start.Arguments = '"' + $database + '"'
    $start.UseShellExecute = $false
    $start.WorkingDirectory = $appDirectory
    foreach ($name in @($start.EnvironmentVariables.Keys)) {
        if ($name -like 'SPEKTRAFILM_*' -or $name -eq 'DOTNET_STARTUP_HOOKS') {
            [void] $start.EnvironmentVariables.Remove($name)
        }
    }
    $start.EnvironmentVariables['DOTNET_STARTUP_HOOKS'] = $hook
    $start.EnvironmentVariables['SPEKTRAFILM_ROOT'] = $PSScriptRoot
    $start.EnvironmentVariables['SPEKTRAFILM_CATALOG'] = $database
    $start.EnvironmentVariables['SPEKTRAFILM_DATA_ROOT'] = $userData
    $start.EnvironmentVariables['SPEKTRAFILM_PANEL_LOG'] = Join-Path $userData 'panel.log'
    $launchId = [Guid]::NewGuid().ToString('N')
    $launchStatus = Join-Path (Join-Path $userData 'Launch status') ($launchId + '.json')
    $start.EnvironmentVariables['SPEKTRAFILM_LAUNCH_ID'] = $launchId
    if ($ShowTool -or $Shortcut) { $start.EnvironmentVariables['SPEKTRAFILM_SHOW_TOOL'] = '1' }
    if ($python) { $start.EnvironmentVariables['SPEKTRAFILM_PYTHON'] = $python }
    else { [void] $start.EnvironmentVariables.Remove('SPEKTRAFILM_PYTHON') }
    try {
        $upgradeScript = Get-Item -LiteralPath (Join-Path $PSScriptRoot 'InstallUpgradeState.ps1') -Force
        if ($upgradeScript.PSIsContainer -or ($upgradeScript.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Expected a regular installation upgrade script.'
        }
        . $upgradeScript.FullName
        $upgrade = Get-LaunchUpgradeCandidate $PSScriptRoot
    } catch { Write-Warning "Could not read the pending upgrade. Previous installations are retained. $($_.Exception.Message)" }
    $process = [Diagnostics.Process]::Start($start)
    try {
        Write-Output "Started Capture One ($($process.Id)). Catalog backup: $backup"
        Write-Output "Launch status file: $launchStatus"
        if ($WaitForPanel -or $upgrade) {
            try {
                $status = Wait-SpektrafilmPanel $process $launchStatus $launchId $PanelTimeoutSeconds
                Write-Output "Spektrafilm panel ready. Native Export: $($status.Export). Viewer: $($status.Viewer)."
            } catch {
                if ($WaitForPanel) { throw }
                Write-Warning "Launch is not yet confirmed. The previous installation is retained. $($_.Exception.Message)"
            }
            if ($upgrade -and $status) {
                try { Complete-SpektrafilmUpgrade $PSScriptRoot $upgrade $status }
                catch { Write-Warning "The previous installation could not be retired. $($_.Exception.Message)" }
            }
        }
    } finally { $process.Dispose() }
} catch {
    $message = $_.Exception.Message
    [Console]::Error.WriteLine("${appName}: $message")
    if ($Shortcut) {
        Add-Type -AssemblyName System.Windows.Forms
        [void] [System.Windows.Forms.MessageBox]::Show($message, $appName,
            [System.Windows.Forms.MessageBoxButtons]::OK, [System.Windows.Forms.MessageBoxIcon]::Error)
    }
    exit 1
}
