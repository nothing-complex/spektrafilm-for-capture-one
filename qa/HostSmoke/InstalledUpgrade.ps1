# Called by Run-HostSmoke after copying the catalog and verifying its backups.
function Test-NativeInstalledUpgrade([string] $previousPackage, [string] $currentPackage, [string] $testFolder,
    [string] $testCatalog, [string] $captureOne) {
    $installed = Join-Path $testFolder 'Install'
    $desktop = Join-Path $testFolder 'Desktop'
    $data = Join-Path $testFolder 'Plugin data'
    [void][IO.Directory]::CreateDirectory($desktop)
    . (Join-Path $currentPackage 'InstallUpgradeState.ps1')
    $shell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    foreach ($package in @($previousPackage,$currentPackage)) {
        $log = if ($package -eq $currentPackage) { 'current-install.log' } else { 'previous-install.log' }
        & $shell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $package 'Install-Spektrafilm.ps1') -Destination $installed -DesktopDirectory $desktop |
            Set-Content -LiteralPath (Join-Path $testFolder $log)
        if ($LASTEXITCODE) { throw "Isolated upgrade installation exited $LASTEXITCODE." }
        if ($package -eq $previousPackage) {
            $userLook = Join-Path $installed 'profiles\nested\retained-user-look.spektra.json'
            [void][IO.Directory]::CreateDirectory((Split-Path -LiteralPath $userLook))
            [IO.File]::WriteAllText($userLook,'licensed upgrade retains this user look')
            $userHash = (Get-FileHash -LiteralPath $userLook).Hash
            $oldHash = (Get-FileHash -LiteralPath (Join-Path $installed 'build-manifest.json')).Hash
        }
    }
    $candidate = Get-LaunchUpgradeCandidate $installed
    if (!$candidate -or $candidate.Item.Name -notlike '*.pending-*.json') { throw 'Installation did not retain the expected pending launch receipt.' }
    $previous = Join-Path $testFolder ('.film-tools-previous-' + $candidate.Receipt.id)
    if ((Get-FileHash -LiteralPath (Join-Path $previous 'build-manifest.json')).Hash -ne $oldHash -or
        (Get-FileHash -LiteralPath $userLook).Hash -ne $userHash) { throw 'The prior version or migrated look was altered.' }

    $launcher = Join-Path $installed 'Start-Spektrafilm.ps1'
    & $shell -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $launcher -CaptureOnePath $captureOne -CheckOnly |
        Set-Content -LiteralPath (Join-Path $testFolder 'installed-check-only.log')
    if ($LASTEXITCODE -or !$candidate.Item.Exists -or ![IO.Directory]::Exists($previous)) {
        throw 'CheckOnly failed or consumed the previous installation before a licensed launch.'
    }

    $launchedAt = [DateTime]::UtcNow
    $stdout = Join-Path $testFolder 'native-launch.log'
    $stderr = Join-Path $testFolder 'native-launch.errors'
    # All these are absolute file paths without a trailing slash.
    $arguments = '-NoLogo -NoProfile -NonInteractive -STA -ExecutionPolicy Bypass -File "' + $launcher +
        '" -CaptureOnePath "' + $captureOne + '" -CatalogPath "' + $testCatalog + '" -UserDataDirectory "' + $data + '" -PanelTimeoutSeconds 90'
    $launcherProcess = Start-Process -FilePath $shell -ArgumentList $arguments -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $native = $null
    try {
        if (!$launcherProcess.WaitForExit(110000)) { throw 'Installed launcher did not finish its readiness check.' }
        $output = [IO.File]::ReadAllText($stdout)
        if ($output -match 'Started Capture One \((\d+)\)') { $native = Get-Process -Id ([int]$Matches[1]) -ErrorAction SilentlyContinue }
        if ($launcherProcess.ExitCode -or !$native -or $native.ProcessName -cne 'CaptureOne' -or
            $output -notmatch 'Spektrafilm panel ready\. Native Export: Connected\.' -or
            $output -notmatch 'retired the verified previous runtime') {
            throw "Installed launcher did not report authenticated readiness and retirement. See $stdout and $stderr."
        }
        $reports = @(Get-ChildItem -LiteralPath (Join-Path $data 'Launch status') -File -Filter '*.json')
        if ($reports.Count -ne 1) { throw 'Expected exactly one isolated licensed launch report.' }
        $state = Read-SpektrafilmLaunchStatus $reports[0].FullName $reports[0].BaseName $native.Id
        if (!$state -or $state.Phase -cne 'Ready' -or $state.Export -cne 'Connected') { throw 'The real host launch receipt is invalid.' }
        if ([IO.Directory]::Exists($previous) -or [IO.File]::Exists($candidate.Item.FullName) -or
            (Get-LaunchUpgradeCandidate $installed) -or (Get-FileHash -LiteralPath $userLook).Hash -ne $userHash) {
            throw 'The prior runtime was not retired correctly or the migrated look did not survive.'
        }
        $receipt = [ordered]@{
            status = 'passed'; checkOnlyRetainedPrior = $true; launch = $state; previousBuildHash = $oldHash
            currentBuildHash = (Get-FileHash -LiteralPath (Join-Path $installed 'build-manifest.json')).Hash
            previousRetiredAfterNativeReady = $true; retainedCustomFileHash = $userHash
        }
        $receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $testFolder 'installed-upgrade-receipt.json') -Encoding UTF8
        Write-Output 'PASS installed upgrade retained its prior runtime until the licensed panel and native Export were ready.'
    } finally {
        if (!$launcherProcess.HasExited) { $launcherProcess.Kill(); $launcherProcess.WaitForExit() }
        $launcherProcess.Dispose()
        if (!$native -and (Test-Path -LiteralPath $stdout) -and
            [IO.File]::ReadAllText($stdout) -match 'Started Capture One \((\d+)\)') {
            $native = Get-Process -Id ([int]$Matches[1]) -ErrorAction SilentlyContinue
        }
        if ($native) {
            try {
                if (!$native.HasExited -and $native.ProcessName -ceq 'CaptureOne' -and $native.StartTime.ToUniversalTime() -ge $launchedAt) {
                    if (!$native.CloseMainWindow() -or !$native.WaitForExit(6000)) { Stop-Process -Id $native.Id; $native.WaitForExit() }
                }
            } finally { $native.Dispose() }
        }
        Clear-TestCrashMarkers $launchedAt 'installed-upgrade'
    }
}
