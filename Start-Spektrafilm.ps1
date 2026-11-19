[CmdletBinding()]
param(
    [string] $CatalogPath,
    [string] $CaptureOnePath,
    [switch] $CheckOnly,
    [switch] $WaitForPanel,
    [ValidateRange(1, 180)][int] $PanelTimeoutSeconds = 45,
    [switch] $Shortcut
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

function New-LookBackup([string] $path, [string] $archive) {
    Add-Type -AssemblyName System.IO.Compression
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $key = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($path).ToUpperInvariant())))).Replace('-', '') }
    finally { $sha.Dispose() }
    $records = New-Object 'System.Collections.Generic.List[object]'
    $zipStream = [IO.File]::Open($archive, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $zip = New-Object IO.Compression.ZipArchive($zipStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
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
                        $relative = $scope + '/' + $item.FullName.Substring($root.Length + 1).Replace('\', '/')
                        $input = [IO.File]::Open($item.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
                        try {
                            $entry = $zip.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
                            $output = $entry.Open()
                            try { $input.CopyTo($output) } finally { $output.Dispose() }
                            $input.Position = 0
                            $hash = [Security.Cryptography.SHA256]::Create()
                            try { $digest = ([BitConverter]::ToString($hash.ComputeHash($input))).Replace('-', '').ToLowerInvariant() }
                            finally { $hash.Dispose() }
                            $records.Add([ordered]@{path=$relative;bytes=$input.Length;sha256=$digest})
                        } finally { $input.Dispose() }
                    }
                }
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
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $encoded = [System.Text.Encoding]::UTF8.GetBytes($path.ToUpperInvariant())
        $key = ([BitConverter]::ToString($sha.ComputeHash($encoded))).Replace('-', '').Substring(0, 20)
    } finally {
        $sha.Dispose()
    }
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
            stateDigest=$looks.stateDigest;catalogSha256=$catalogHash;databaseIntegrity='ok';
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
    $database = Get-CatalogPath $CatalogPath
    $backup = Save-CatalogBackup $database
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
    $start.EnvironmentVariables['SPEKTRAFILM_PANEL_LOG'] = Join-Path $userData 'panel.log'
    $launchId = [Guid]::NewGuid().ToString('N')
    $launchStatus = Join-Path (Join-Path $userData 'Launch status') ($launchId + '.json')
    $start.EnvironmentVariables['SPEKTRAFILM_LAUNCH_ID'] = $launchId
    if ($python) { $start.EnvironmentVariables['SPEKTRAFILM_PYTHON'] = $python }
    else { [void] $start.EnvironmentVariables.Remove('SPEKTRAFILM_PYTHON') }
    $process = [Diagnostics.Process]::Start($start)
    try {
        Write-Output "Started Capture One ($($process.Id)). Catalog backup: $backup"
        Write-Output "Launch status file: $launchStatus"
        if ($WaitForPanel) {
            $status = Wait-SpektrafilmPanel $process $launchStatus $launchId $PanelTimeoutSeconds
            Write-Output "Spektrafilm panel ready. Native Export: $($status.Export). Viewer: $($status.Viewer)."
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
