[CmdletBinding(SupportsShouldProcess)]
param(
    [string] $Destination,
    [string] $DesktopDirectory = ([Environment]::GetFolderPath('DesktopDirectory'))
)

$ErrorActionPreference = 'Stop'
$appName = 'Spektrafilm for Capture One'
$markerName = '.film-tools-install'
$markerText = 'Spektrafilm for Capture One portable installation'
$legacyMarkerText = 'Film Tools for Capture One portable installation'
$ownedManifestName = '.film-tools-owned.json'
$lockedMessage = "Close $appName to update, then run the installer again."

function Test-OwnedInstall([string] $path) {
    if (!(Test-Path -LiteralPath $path -PathType Container)) { return $false }
    Confirm-PlainItem (Get-Item -LiteralPath $path -Force)
    $marker = Join-Path $path $markerName
    if (!(Test-Path -LiteralPath $marker -PathType Leaf)) { return $false }
    Confirm-PlainItem (Get-Item -LiteralPath $marker -Force)
    return [IO.File]::ReadAllText($marker) -cin @($markerText, $legacyMarkerText)
}

function Test-SamePath([string] $first, [string] $second) {
    return [string]::Equals($first, $second, [StringComparison]::OrdinalIgnoreCase)
}

function Test-BelowPath([string] $child, [string] $parent) {
    return $child.StartsWith($parent.TrimEnd('\', '/') + '\', [StringComparison]::OrdinalIgnoreCase)
}

function Confirm-PlainItem($item) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Choose a regular file or folder instead of the linked path $($item.FullName)."
    }
}

function Confirm-PlainAsset($item, [string] $root) {
    Confirm-PlainItem $item
    if (!(Test-BelowPath $item.FullName $root)) { throw "The installation asset lies outside its root: $($item.FullName)" }
    $directory = if ($item.PSIsContainer) { $item.Parent } else { $item.Directory }
    while ($directory -and !(Test-SamePath $directory.FullName $root)) {
        Confirm-PlainItem $directory
        $directory = $directory.Parent
    }
    if (!$directory) { throw "Could not verify the installation asset root: $root" }
}

function Test-FileInUse([Exception] $error) {
    $cause = $error.GetBaseException()
    return ($cause.HResult -band 0xffff) -in @(32, 33)
}

function Confirm-UpdateFilesAvailable([string] $install) {
    foreach ($relative in @('panel\NativePanel.dll', 'renderer\SpektraStillRenderer.exe')) {
        $path = Join-Path $install $relative
        if (![IO.File]::Exists($path)) { continue }
        Confirm-PlainAsset (Get-Item -LiteralPath $path -Force) $install
        try {
            $file = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
            $file.Dispose()
        } catch {
            if (Test-FileInUse $_.Exception) {
                throw $lockedMessage
            }
            throw
        }
    }
}

function Remove-TemporaryInstall([string] $path, [string] $parent) {
    if (![IO.Directory]::Exists($path)) { return }
    $item = Get-Item -LiteralPath $path -Force
    Confirm-PlainItem $item
    if (!(Test-BelowPath $item.FullName $parent) -or !(Test-SamePath $item.Parent.FullName $parent) -or
        $item.Name -notmatch '^\.film-tools-(stage|previous)-[a-f0-9]{32}$') {
        throw "Could not verify temporary installation path $path."
    }
    # .NET removes nested links themselves without traversing their targets.
    [IO.Directory]::Delete($item.FullName, $true)
}

function Get-UpgradeFiles([string] $install) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($install.ToUpperInvariant())
        $key = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').Substring(0, 24).ToLowerInvariant()
    } finally { $sha.Dispose() }
    $base = Join-Path (Split-Path -LiteralPath $install) ('.film-tools-upgrade-' + $key)
    return @{ Lock = $base + '.lock'; Journal = $base + '.json' }
}

function Get-UpgradeFingerprint([string] $directory, [string] $asset) {
    if (!(Test-OwnedInstall $directory)) { throw "Could not verify the owned installation at $directory." }
    $item = Get-Item -LiteralPath (Join-Path $directory $asset) -Force
    Confirm-PlainAsset $item $directory
    if ($item.PSIsContainer) { throw "Expected an installation receipt at $($item.FullName)." }
    return (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
}

function Save-UpgradeJournal([string] $journal, [string] $id, [string] $install, [string] $stage) {
    $receipt = [ordered]@{
        schema = 1
        id = $id
        previousMarker = Get-UpgradeFingerprint $install $markerName
        candidateReceipt = Get-UpgradeFingerprint $stage $ownedManifestName
    }
    $temporary = $journal + '.' + $id
    $bytes = [Text.Encoding]::UTF8.GetBytes(($receipt | ConvertTo-Json -Compress))
    $created = $false
    try {
        $file = [IO.FileStream]::new($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
            [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
        $created = $true
        try { $file.Write($bytes, 0, $bytes.Length); $file.Flush($true) }
        finally { $file.Dispose() }
        [IO.File]::Move($temporary, $journal)
    } finally {
        if ($created -and [IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

function Restore-InterruptedUpgrade([string] $journal, [string] $install, [string] $parent) {
    if (!(Test-Path -LiteralPath $journal)) { return }
    $item = Get-Item -LiteralPath $journal -Force
    Confirm-PlainItem $item
    if ($item.PSIsContainer -or $item.Length -gt 4096) { throw "Could not read the upgrade journal at $journal." }
    $receipt = [IO.File]::ReadAllText($journal) | ConvertFrom-Json
    if ($receipt.schema -ne 1 -or $receipt.id -cnotmatch '^[a-f0-9]{32}$' -or
        $receipt.previousMarker -cnotmatch '^[A-F0-9]{64}$' -or $receipt.candidateReceipt -cnotmatch '^[A-F0-9]{64}$') {
        throw "Could not verify the upgrade journal at $journal."
    }
    $previous = Join-Path $parent ('.film-tools-previous-' + $receipt.id)
    $stage = Join-Path $parent ('.film-tools-stage-' + $receipt.id)
    $hasPrevious = Test-Path -LiteralPath $previous
    $hasStage = Test-Path -LiteralPath $stage
    $hasCurrent = Test-Path -LiteralPath $install
    if ($hasPrevious -and $hasStage -and $hasCurrent) {
        throw "An unexpected item occupies $install. The interrupted upgrade remains at $stage and $previous."
    }
    $oldPath = if ($hasPrevious) { $previous } else { $install }
    $candidatePath = if ($hasStage) { $stage } else { $install }
    if ($oldPath -eq $candidatePath -or (Get-UpgradeFingerprint $oldPath $markerName) -cne $receipt.previousMarker -or
        (Get-UpgradeFingerprint $candidatePath $ownedManifestName) -cne $receipt.candidateReceipt) {
        throw "Could not verify the interrupted upgrade. Recovery files remain at $stage and $previous."
    }
    if ($hasPrevious) {
        if ($hasCurrent) {
            Confirm-UpdateFilesAvailable $install
            [IO.Directory]::Move($install, $stage)
        }
        [IO.Directory]::Move($previous, $install)
    }
    [IO.File]::Delete($journal)
    Write-Output "Recovered the previous installation at $install. Interrupted upgrade files retained at $stage."
}

function Read-OwnedFiles([string] $install) {
    $owned = @{}
    $path = Join-Path $install $ownedManifestName
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { return $owned }
    Confirm-PlainItem (Get-Item -LiteralPath $path -Force)
    try {
        $manifest = [IO.File]::ReadAllText($path) | ConvertFrom-Json
        if ($manifest.schema -ne 1) { return $owned }
        foreach ($file in $manifest.files) {
            if ($file.path -is [string] -and $file.sha256 -match '^[a-fA-F0-9]{64}$') {
                $owned[$file.path.Replace('/', '\')] = $file.sha256
            }
        }
    } catch {
        # A missing/corrupt ownership receipt never grants permission to discard a file.
        Write-Warning 'The previous installation receipt could not be read; changed files will be retained.'
    }
    return $owned
}

function Copy-PlainTree([string] $sourcePath, [string] $destinationPath) {
    $item = Get-Item -LiteralPath $sourcePath -Force
    Confirm-PlainItem $item
    if ($item.PSIsContainer) {
        [void] [IO.Directory]::CreateDirectory($destinationPath)
        foreach ($child in Get-ChildItem -LiteralPath $item.FullName -Force) {
            Copy-PlainTree $child.FullName (Join-Path $destinationPath $child.Name)
        }
    } else {
        [void] [IO.Directory]::CreateDirectory((Split-Path -Parent $destinationPath))
        [IO.File]::Copy($item.FullName, $destinationPath)
    }
}

function Get-PlainFiles([string] $directory) {
    Confirm-PlainItem (Get-Item -LiteralPath $directory -Force)
    foreach ($child in Get-ChildItem -LiteralPath $directory -Force) {
        Confirm-PlainItem $child
        if ($child.PSIsContainer) { Get-PlainFiles $child.FullName }
        else { $child }
    }
}

function Merge-UserFiles([string] $previousRoot, [string] $stageRoot, [string] $relative, [hashtable] $owned, [string] $recoveryRoot) {
    $directory = if ($relative) { Join-Path $previousRoot $relative } else { $previousRoot }
    foreach ($item in Get-ChildItem -LiteralPath $directory -Force) {
        Confirm-PlainItem $item
        $name = if ($relative) { Join-Path $relative $item.Name } else { $item.Name }
        if (!$relative -and $item.Name -in @($markerName, $ownedManifestName)) { continue }
        $target = Join-Path $stageRoot $name
        if (!(Test-Path -LiteralPath $target)) {
            if (!$item.PSIsContainer -and $owned.ContainsKey($name) -and
                (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash -eq $owned[$name]) { continue }
            Copy-PlainTree $item.FullName $target
        } elseif ($item.PSIsContainer -and (Get-Item -LiteralPath $target -Force).PSIsContainer) {
            Merge-UserFiles $previousRoot $stageRoot $name $owned $recoveryRoot
        } elseif (!$item.PSIsContainer -and (Test-Path -LiteralPath $target -PathType Leaf)) {
            $oldHash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
            $newHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
            if ($oldHash -eq $newHash) { continue }
            # Only an unmodified file recorded by the old installer is replaceable.
            if ($owned.ContainsKey($name) -and $owned[$name] -eq $oldHash) { continue }
            Copy-PlainTree $item.FullName (Join-Path $recoveryRoot $name)
        } else {
            # A file/directory collision can contain arbitrary user data.
            Copy-PlainTree $item.FullName (Join-Path $recoveryRoot $name)
        }
    }
}

function Get-ShortcutArguments([string] $launcher) {
    return '-NoLogo -NoProfile -STA -ExecutionPolicy Bypass -File "' + $launcher + '"'
}

function Save-DesktopShortcut([string] $path, [string] $target, [string] $launcher, [string] $install, [bool] $upgrade) {
    $baseArguments = Get-ShortcutArguments $launcher
    $arguments = $baseArguments + ' -Shortcut'
    $shell = New-Object -ComObject WScript.Shell
    try {
        if (Test-Path -LiteralPath $path) {
            Confirm-PlainItem (Get-Item -LiteralPath $path -Force)
            $existing = $shell.CreateShortcut($path)
            try {
                if (!(Test-SamePath $existing.TargetPath $target) -or
                    ($existing.Arguments -cne $arguments -and $existing.Arguments -cne $baseArguments)) {
                    throw "A different Desktop shortcut already uses the name $appName. Rename it before running the installer."
                }
            } finally {
                [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($existing)
            }
        }
        $desktop = Split-Path -LiteralPath $path
        $temporaryLink = Join-Path $desktop ('.film-tools-' + [Guid]::NewGuid().ToString('N') + '.lnk')
        $previousLink = Join-Path $desktop ('.film-tools-' + [Guid]::NewGuid().ToString('N') + '.lnk')
        try {
            $shortcut = $shell.CreateShortcut($temporaryLink)
            try {
                $shortcut.TargetPath = $target
                $shortcut.Arguments = $arguments
                $shortcut.WorkingDirectory = $install
                $shortcut.IconLocation = (Join-Path $install 'assets\Spektrafilm.ico') + ',0'
                $shortcut.Description = "Open a Capture One catalog with $appName"
                $shortcut.Save()
            } finally {
                [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut)
            }
            if ([IO.File]::Exists($path)) { [IO.File]::Replace($temporaryLink, $path, $previousLink) }
            else { [IO.File]::Move($temporaryLink, $path) }
        } finally {
            if ([IO.File]::Exists($temporaryLink)) { [IO.File]::Delete($temporaryLink) }
            if ([IO.File]::Exists($previousLink)) { [IO.File]::Delete($previousLink) }
        }
        if ($upgrade) {
            $legacyArguments = Get-ShortcutArguments (Join-Path $install 'Start-FilmTools.ps1')
            foreach ($name in @('Film Tools for Capture One.lnk', 'Capture One with spektrafilm.lnk')) {
                $legacyPath = Join-Path $desktop $name
                if (!(Test-Path -LiteralPath $legacyPath -PathType Leaf)) { continue }
                try {
                    Confirm-PlainItem (Get-Item -LiteralPath $legacyPath -Force)
                    $legacy = $shell.CreateShortcut($legacyPath)
                    try {
                        $owned = (Test-SamePath $legacy.TargetPath $target) -and
                            ($legacy.Arguments -ceq $legacyArguments -or $legacy.Arguments -ceq ($legacyArguments + ' -Shortcut'))
                    } finally {
                        [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($legacy)
                    }
                    if ($owned) { Remove-Item -LiteralPath $legacyPath }
                } catch {
                    Write-Warning "Could not migrate the existing shortcut at $legacyPath."
                }
            }
        }
    } finally {
        [void] [Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }
}

try {
    $sourceRoot = Get-Item -LiteralPath $PSScriptRoot -Force
    Confirm-PlainItem $sourceRoot
    $source = $sourceRoot.FullName.TrimEnd('\', '/')
    if ((Test-Path -LiteralPath (Join-Path $source 'runtime-manifest.required')) -and
        !(Test-Path -LiteralPath (Join-Path $source 'build-manifest.json'))) {
        throw 'The release contains an incomplete staged runtime. Extract or rebuild the complete release.'
    }
    if ((Test-Path -LiteralPath (Join-Path $source 'build-manifest.json')) -or
        (Test-Path -LiteralPath (Join-Path $source 'package-manifest.json'))) {
        $validator = Get-Item -LiteralPath (Join-Path $source 'ReleaseManifest.ps1') -Force
        Confirm-PlainAsset $validator $source
        . $validator.FullName
        if (Test-Path -LiteralPath (Join-Path $source 'package-manifest.json')) { $releasePackage = Test-ReleaseManifest $source 'package-manifest.json' }
        [void](Test-ReleaseManifest $source)
    }
    if (!$Destination) {
        $programs = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs'
        $Destination = Join-Path $programs $appName
        $legacyInstall = Join-Path $programs 'Film Tools for Capture One'
        $legacyJournal = (Get-UpgradeFiles $legacyInstall).Journal
        if (!(Test-Path -LiteralPath $Destination) -and ((Test-OwnedInstall $legacyInstall) -or
            (Test-Path -LiteralPath $legacyJournal))) { $Destination = $legacyInstall }
    }
    $install = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Destination).TrimEnd('\', '/')
    $desktop = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($DesktopDirectory).TrimEnd('\', '/')
    $parent = Split-Path -LiteralPath $install
    if (!$parent -or (Test-SamePath $install $source) -or (Test-BelowPath $source $install) -or (Test-BelowPath $install $source) -or
        (Test-SamePath $desktop $install) -or (Test-BelowPath $desktop $install)) {
        throw 'Choose an installation folder separate from the extracted ZIP and the Desktop.'
    }
    if (!(Test-Path -LiteralPath $desktop -PathType Container)) { throw "The Desktop folder is unavailable: $desktop" }

    $required = @(
        'Start Spektrafilm for Capture One.cmd', 'Start-Spektrafilm.ps1', 'CatalogIntegrity.ps1', 'Restore-SpektrafilmCatalog.ps1', 'ReferencePython.ps1',
        'capture-one-hosts.json','stocks.json', 'panel\NativePanel.dll', 'assets\Spektrafilm.ico',
        'profiles\preview\Linear Rec.2020.icc', 'profiles\preview\ProPhoto RGB.icc',
        'renderer\SpektraStillRenderer.exe', 'renderer\SpektraHanatos2025Spectra.f32', 'renderer\SpektraOutputGamutCompression.f32',
        'renderer\parameter-capabilities.json'
    )
    foreach ($shader in @('Copy','CurveDevelop','Diffusion','Dir','FilmExposure','FormatConvert','Grain','Halation','PrintScan','ScannerPost')) {
        $required += "renderer\shaders\Spektra$shader.comp.spv"
    }
    $optional = @(
        'Install Spektrafilm for Capture One.cmd', 'Install-Spektrafilm.ps1', 'Setup LUT Export.cmd',
        'ReleaseManifest.ps1', 'build-manifest.json', 'package-manifest.json', 'runtime-manifest.required',
        'Setup-LutExport.ps1', 'lut_worker.py', 'requirements-lut.txt',
        'README.md', 'CAPABILITIES.md', 'LICENSE', 'SOURCE.txt', 'THIRD-PARTY.md', 'UPSTREAM-CHANGES.md', 'assets\Spektrafilm.png',
        'upstream-ofx\LICENSE.txt', 'upstream-ofx\Legal\THIRD_PARTY_NOTICES.txt',
        'upstream-ofx\Legal\SPEKTRAFILM_OFX_LICENSE.txt', 'upstream-ofx\Legal\SPEKTRAFILM_OFX_LUT_LICENSE.txt'
    )
    $files = @()
    foreach ($relative in $required + $optional) {
        $origin = Join-Path $source $relative
        if (!(Test-Path -LiteralPath $origin -PathType Leaf)) {
            if ($required -contains $relative) { throw "The extracted release is missing $relative." }
            continue
        }
        $item = Get-Item -LiteralPath $origin -Force
        Confirm-PlainAsset $item $source
        if ($item.Length -eq 0) { throw "The extracted release contains an empty $relative." }
        $files += $relative
    }
    foreach ($directory in @('licenses', 'upstream-spektrafilm')) {
        $optionalTree = Join-Path $source $directory
        if (!(Test-Path -LiteralPath $optionalTree -PathType Container)) { continue }
        Confirm-PlainItem (Get-Item -LiteralPath $optionalTree)
        foreach ($item in Get-PlainFiles $optionalTree) {
            if ($item.Extension -in '.pyc', '.pyo', '.pyd', '.exe', '.dll' -or $item.FullName -match '[\\/]__pycache__[\\/]') { continue }
            Confirm-PlainItem $item
            $files += $item.FullName.Substring($source.Length + 1)
        }
    }
    if ($releasePackage) {
        $declared = @{}
        foreach ($entry in $releasePackage.files) { $declared[$entry.path.Replace('\','/')] = $true }
        foreach ($relative in $files) {
            if ($relative -ne 'package-manifest.json' -and !$declared.ContainsKey($relative.Replace('\','/'))) {
                throw "The package contains an undeclared installation file: $relative"
            }
        }
    }

    $shortcutPath = Join-Path $desktop "$appName.lnk"
    if (!$PSCmdlet.ShouldProcess("$install and $shortcutPath", "Install $appName and create its Desktop shortcut")) { return }

    [void] [IO.Directory]::CreateDirectory($parent)
    Confirm-PlainItem (Get-Item -LiteralPath $parent -Force)
    $upgradeFiles = Get-UpgradeFiles $install
    if (Test-Path -LiteralPath $upgradeFiles.Lock) { Confirm-PlainItem (Get-Item -LiteralPath $upgradeFiles.Lock -Force) }
    try {
        $installLock = [IO.File]::Open($upgradeFiles.Lock, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    } catch {
        if (Test-FileInUse $_.Exception) { throw 'Another installer is working on this destination.' }
        throw
    }
    Restore-InterruptedUpgrade $upgradeFiles.Journal $install $parent
    $upgrade = Test-Path -LiteralPath $install
    if ($upgrade -and !(Test-OwnedInstall $install)) {
        throw "The destination already exists and is not a $appName installation: $install"
    }
    if ($upgrade) { Confirm-UpdateFilesAvailable $install }
    $id = [Guid]::NewGuid().ToString('N')
    $stage = Join-Path $parent ('.film-tools-stage-' + $id)
    $previous = Join-Path $parent ('.film-tools-previous-' + $id)
    $movedPrevious = $false
    $installed = $false
    $keepStage = $false
    $journalSaved = $false
    $recoveryRelative = Join-Path 'Recovered files' $id
    try {
        [void] [IO.Directory]::CreateDirectory($stage)
        foreach ($relative in $files) {
            $target = Join-Path $stage $relative
            [void] [IO.Directory]::CreateDirectory((Split-Path -LiteralPath $target))
            $asset = Get-Item -LiteralPath (Join-Path $source $relative) -Force
            Confirm-PlainAsset $asset $source
            [IO.File]::Copy($asset.FullName, $target)
        }
        if ($releasePackage) { [void](Test-ReleaseManifest $stage 'package-manifest.json') }
        if (Test-Path -LiteralPath (Join-Path $stage 'build-manifest.json') -PathType Leaf) {
            [void](Test-ReleaseManifest $stage)
        }
        [IO.File]::WriteAllText((Join-Path $stage $markerName), $markerText)
        $receipt = [ordered]@{ schema = 1; files = @($files | ForEach-Object {
            [ordered]@{ path = $_.Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath (Join-Path $stage $_) -Algorithm SHA256).Hash.ToLowerInvariant() }
        }) }
        [IO.File]::WriteAllText((Join-Path $stage $ownedManifestName), ($receipt | ConvertTo-Json -Depth 5))
        if ($upgrade) {
            $owned = Read-OwnedFiles $install
            # Copy user data before moving the original. Any copy failure leaves it intact.
            Merge-UserFiles $install $stage '' $owned (Join-Path $stage $recoveryRelative)
            Save-UpgradeJournal $upgradeFiles.Journal $id $install $stage
            $journalSaved = $true
            try {
                [IO.Directory]::Move($install, $previous)
            } catch {
                if (Test-FileInUse $_.Exception) {
                    throw $lockedMessage
                }
                if (($_.Exception.GetBaseException().HResult -band 0xffff) -eq 5) {
                    throw "Could not replace the installation folder. Close $appName to update. If it is already closed, check that you can write to the installation folder."
                }
                throw
            }
            $movedPrevious = $true
        }
        [IO.Directory]::Move($stage, $install)
        $installed = $true
        $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        Save-DesktopShortcut $shortcutPath $powershell (Join-Path $install 'Start-Spektrafilm.ps1') $install $upgrade
        if ($journalSaved) { [IO.File]::Delete($upgradeFiles.Journal) }
    } catch {
        $installError = $_
        try {
            if ($installed) { [IO.Directory]::Move($install, $stage) }
            if ($movedPrevious) {
                [IO.Directory]::Move($previous, $install)
            }
            if ($journalSaved) { [IO.File]::Delete($upgradeFiles.Journal) }
        } catch {
            $keepStage = $true
            throw "Installation failed and could not be rolled back. The previous folder is $previous. The staged folder is $stage. The destination is $install. $($installError.Exception.Message) $($_.Exception.Message)"
        }
        throw $installError
    } finally {
        if (!$keepStage) { Remove-TemporaryInstall $stage $parent }
    }
    if ($movedPrevious) {
        try { Remove-TemporaryInstall $previous $parent }
        catch { Write-Warning "An older installation remains at $previous." }
    }
    Write-Output "Installed to $install"
    Write-Output "Desktop shortcut created at $shortcutPath"
    if (Test-Path -LiteralPath (Join-Path $install $recoveryRelative)) {
        Write-Output "Previously modified or conflicting files retained at $(Join-Path $install $recoveryRelative)"
    }
} catch {
    [Console]::Error.WriteLine("${appName}: $($_.Exception.Message)")
    exit 1
} finally {
    if ($installLock) { $installLock.Dispose() }
}
