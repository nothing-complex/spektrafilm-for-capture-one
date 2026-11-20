[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SourceCatalog,
    [string] $CaptureOnePath='C:\Program Files\Capture One\Capture One\CaptureOne.exe',
    [string] $TestHookPath=(Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\HostSmoke.dll'),
    [string] $PackagedRuntime,
    [Alias('ProofingSettingsConfig')][string] $NativeSettingsConfig,
    [switch] $CatalogIsolation,
    [switch] $ViewerLayouts,
    [switch] $DurableSliderEdits,
    [switch] $SkipRestart,
    [switch] $CheckOrdinaryLaunch,
    [switch] $ShowTool,
    [switch] $ColorMatrix,
    [string] $ResultsDirectory
)
$ErrorActionPreference='Stop'
if ($CatalogIsolation -and ($SkipRestart -or $CheckOrdinaryLaunch -or $ColorMatrix)) {
    throw 'The catalog isolation fixture controls all four launches.'
}
if ($ViewerLayouts -and (!$NativeSettingsConfig -or $CatalogIsolation -or $ColorMatrix -or $CheckOrdinaryLaunch)) {
    throw 'The Viewer layouts fixture requires a verified preferences backup and runs as a separate launch.'
}
if ($DurableSliderEdits -and ($CatalogIsolation -or $ViewerLayouts -or $SkipRestart -or $ColorMatrix -or $CheckOrdinaryLaunch)) {
    throw 'The durable slider fixture requires its own first launch and restart.'
}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).ProviderPath
$tokens=$null
$parseErrors=$null
$launcher=[Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'Start-Spektrafilm.ps1'), [ref]$tokens, [ref]$parseErrors)
if($parseErrors.Count){throw 'Launcher syntax failed.'}
foreach($name in 'Read-SpektrafilmLaunchStatus','Wait-SpektrafilmPanel'){
    $definition=$launcher.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name}, $true)
    . ([scriptblock]::Create($definition.Extent.Text))
}
if(Get-Process CaptureOne -ErrorAction SilentlyContinue){throw 'Close Capture One before running the isolated host test.'}
$stabilityRoot=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CaptureOne\Stability\16.4.6.2414'
$markerNames=@('TerminatedWhileRunning.crh','TerminatedByBsodOrPower.crh')
function Get-CrashMarkers {
    if(Test-Path -LiteralPath $stabilityRoot){Get-ChildItem -LiteralPath $stabilityRoot -Recurse -File | Where-Object {$_.Name -in $markerNames}}
}
if(@(Get-CrashMarkers).Count){throw 'Capture One has pre-existing crash markers. Resolve its startup recovery before this test; pre-existing markers will not be changed.'}
function Clear-TestCrashMarkers([datetime] $started,[string] $phase) {
    if(Get-Process CaptureOne -ErrorAction SilentlyContinue){throw 'A Capture One process remains; crash markers will not be touched.'}
    $backup=Join-Path $folder ($phase+'-crash-markers-backup')
    foreach($marker in @(Get-CrashMarkers)){
        if($marker.LastWriteTimeUtc -lt $started){throw 'Crash marker predates this test; refusing cleanup.'}
        if(!$marker.FullName.StartsWith([IO.Path]::GetFullPath($stabilityRoot)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or ($marker.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unexpected crash marker path.'}
        [void][IO.Directory]::CreateDirectory($backup)
        $copy=Join-Path $backup ($marker.Directory.Name+'-'+$marker.Name)
        Copy-Item -LiteralPath $marker.FullName -Destination $copy
        if((Get-FileHash -LiteralPath $copy).Hash -ne (Get-FileHash -LiteralPath $marker.FullName).Hash){throw 'Crash marker backup failed.'}
        Remove-Item -LiteralPath $marker.FullName
    }
}
function Restore-TestCatalogPreferences([string] $profile,[string] $backup,[string[]] $catalogs,[string] $folder) {
    if(Get-Process CaptureOne -ErrorAction SilentlyContinue){throw 'Capture One is still active. Its verified preference backup was retained.'}
    $original=(Get-FileHash -LiteralPath $backup).Hash
    $observed=(Get-FileHash -LiteralPath $profile).Hash
    if($original -ne $observed){
        $before=[xml](Get-Content -Raw -LiteralPath $backup)
        $xmlPath="//setting[@name='DocumentManagerSettings']/value//string"
        if(@($before.SelectNodes($xmlPath) | Where-Object {$catalogs -ccontains $_.InnerText}).Count){
            throw 'The disposable catalog was already in the settings backup.'
        }
        $captured=Join-Path $folder 'Capture One user.config.after-proofing'
        Copy-Item -LiteralPath $profile -Destination $captured
        if((Get-FileHash -LiteralPath $captured).Hash -ne $observed){throw 'Could not verify changed preferences.'}
        $after=[xml](Get-Content -Raw -LiteralPath $captured)
        $entries=@($after.SelectNodes($xmlPath) | Where-Object {$catalogs -ccontains $_.InnerText})
        if(@($entries | Group-Object InnerText | Where-Object Count -gt 1).Count){
            throw 'Several recent entries matched one disposable catalog.'
        }
        if($entries.Count -eq 0){
            [ordered]@{before=$original;afterTest=$observed;restored=$observed;removedRecentCatalog=$false} |
                ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $folder 'proof-settings-receipt.json')
            return
        }
        foreach($entry in $entries){[void]$entry.ParentNode.RemoveChild($entry)}
        $id=[Guid]::NewGuid().ToString('N')
        $temp=$profile+'.'+$id+'.restore'
        $replaced=$profile+'.'+$id+'.previous'
        try {
            if($before.OuterXml -ceq $after.OuterXml){
                [IO.File]::Copy($backup,$temp)
            } else {
                # A host launch can advance its run counters. Keep all host changes except this catalog entry.
                $preserved=[xml]::new()
                $preserved.PreserveWhitespace=$true
                $preserved.Load($captured)
                foreach($entry in @($preserved.SelectNodes($xmlPath) | Where-Object {$catalogs -ccontains $_.InnerText})){
                    $previous=$entry.PreviousSibling
                    if($null -ne $previous -and $previous.NodeType -eq [Xml.XmlNodeType]::Whitespace){
                        [void]$entry.ParentNode.RemoveChild($previous)
                    }
                    [void]$entry.ParentNode.RemoveChild($entry)
                }
                $preserved.Save($temp)
                $candidate=[xml](Get-Content -Raw -LiteralPath $temp)
                if($candidate.OuterXml -cne $after.OuterXml){throw 'The candidate did not preserve other Capture One preferences.'}
            }
            $expected=(Get-FileHash -LiteralPath $temp).Hash
            if((Get-FileHash -LiteralPath $profile).Hash -ne $observed -or (Get-Process CaptureOne -ErrorAction SilentlyContinue)){
                throw 'Preferences changed again before restoration.'
            }
            [IO.File]::Replace($temp,$profile,$replaced,$true)
            if((Get-FileHash -LiteralPath $profile).Hash -ne $expected -or (Get-FileHash -LiteralPath $replaced).Hash -ne $observed){
                throw 'Restored preferences did not match the verified snapshot.'
            }
            [IO.File]::Delete($replaced)
        } finally { if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)} }
    }
    [ordered]@{before=$original;afterTest=$observed;restored=(Get-FileHash -LiteralPath $profile).Hash;removedRecentCatalog=($original -ne $observed)} |
        ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $folder 'proof-settings-receipt.json')
}
$source=(Get-Item -LiteralPath $SourceCatalog).FullName
if([IO.Path]::GetExtension($source) -ine '.cocatalogdb'){throw 'A catalog database is required.'}
foreach($suffix in '-wal','-shm','-journal'){if(Test-Path -LiteralPath ($source+$suffix)){throw 'Source catalog has transaction sidecars; close it cleanly first.'}}
$runtimeSource=$root
$panelBuild=Join-Path $root 'NativePanel\bin\Release\net8.0-windows\NativePanel.dll'
$rendererBuild=Join-Path $root 'build\still-renderer\Release'
if ($PackagedRuntime) {
    $runtimeSource=(Resolve-Path -LiteralPath $PackagedRuntime).ProviderPath
    . (Join-Path $root 'ReleaseManifest.ps1')
    [void](Test-ReleaseManifest $runtimeSource)
    $panelBuild=Join-Path $runtimeSource 'panel\NativePanel.dll'
    $rendererBuild=Join-Path $runtimeSource 'renderer'
}
if(!(Test-Path -LiteralPath (Join-Path $rendererBuild 'parameter-capabilities.json') -PathType Leaf)){
    throw 'Run Build-Windows.ps1 to build the renderer and generate its parameter capabilities before testing the host.'
}
if(!$ResultsDirectory){$ResultsDirectory=Join-Path $root ('.test-results\host-'+[guid]::NewGuid().ToString('N'))}
$folder=[IO.Path]::GetFullPath($ResultsDirectory)
if(Test-Path -LiteralPath $folder){throw 'Use a new empty results directory.'}
[void][IO.Directory]::CreateDirectory($folder)
if ($NativeSettingsConfig) {
    $profile=(Get-Item -LiteralPath $NativeSettingsConfig).FullName
    if ([IO.Path]::GetFileName($profile) -ne 'user.config') { throw 'Pass the existing Capture One user.config to back up before testing.' }
    $backup=Join-Path $folder 'Capture One user.config.before-proofing'
    Copy-Item -LiteralPath $profile -Destination $backup
    if ((Get-FileHash -LiteralPath $profile).Hash -ne (Get-FileHash -LiteralPath $backup).Hash) {
        throw 'Could not verify the Capture One preferences backup before proof tests.'
    }
}
$catalog=Join-Path $folder 'Host Smoke.cocatalogdb'
$sourceHash=(Get-FileHash -LiteralPath $source).Hash
$fixtureCatalogs=@($catalog)
if($CatalogIsolation){
    $fixtureCatalogs=foreach($copy in 'A','B'){
        $directory=Join-Path $folder $copy
        [void][IO.Directory]::CreateDirectory($directory)
        $copyPath=Join-Path $directory 'Host Smoke.cocatalogdb'
        Copy-Item -LiteralPath $source -Destination $copyPath
        if((Get-FileHash -LiteralPath $copyPath).Hash -ne $sourceHash){throw 'Isolated catalog bytes did not match the original.'}
        $copyPath
    }
} else {
    Copy-Item -LiteralPath $source -Destination $catalog
}
[IO.File]::WriteAllText((Join-Path $folder 'disposable-catalog.marker'),$sourceHash)
$runtime=Join-Path $folder 'runtime'
[void][IO.Directory]::CreateDirectory($runtime)
Copy-Item -LiteralPath (Join-Path $runtimeSource 'stocks.json') -Destination $runtime
Copy-Item -LiteralPath (Join-Path $runtimeSource 'profiles') -Destination $runtime -Recurse
Copy-Item -LiteralPath $rendererBuild -Destination (Join-Path $runtime 'renderer') -Recurse
Copy-Item -LiteralPath $panelBuild -Destination $runtime
if ($PackagedRuntime) {
    Copy-Item -LiteralPath (Join-Path $runtimeSource 'build-manifest.json') -Destination $runtime
}
$testHook=$TestHookPath
if(!(Test-Path -LiteralPath $testHook)){throw 'Build qa/HostSmoke and NativePanel first.'}
Copy-Item -LiteralPath $testHook -Destination $runtime
$testHook=Join-Path $runtime 'HostSmoke.dll'
$names=@('DOTNET_STARTUP_HOOKS','SPEKTRAFILM_ROOT','SPEKTRAFILM_CATALOG','SPEKTRAFILM_DATA_ROOT','SPEKTRAFILM_PANEL_LOG','SPEKTRAFILM_HOST_SMOKE','SPEKTRAFILM_LAUNCH_ID','SPEKTRAFILM_HOST_SMOKE_COLOR_MATRIX','SPEKTRAFILM_SHOW_TOOL','SPEKTRAFILM_HOST_PROOF_TEST','SPEKTRAFILM_HOST_CATALOG_ISOLATION','SPEKTRAFILM_HOST_VIEWER_LAYOUTS','SPEKTRAFILM_HOST_DURABLE_EDITS')
$prior=@{}
foreach($name in $names){$prior[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
$testError=$null
try {
    $env:DOTNET_STARTUP_HOOKS=(Join-Path $runtime 'NativePanel.dll')+';'+$testHook
    $env:SPEKTRAFILM_ROOT=$runtime
    $env:SPEKTRAFILM_CATALOG=$catalog
    $env:SPEKTRAFILM_DATA_ROOT=$folder
    $env:SPEKTRAFILM_PANEL_LOG=Join-Path $folder 'panel.log'
    $env:SPEKTRAFILM_HOST_SMOKE=$folder
    $env:SPEKTRAFILM_HOST_SMOKE_COLOR_MATRIX=if($ColorMatrix){'1'}else{$null}
    $env:SPEKTRAFILM_SHOW_TOOL=if($ShowTool){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_PROOF_TEST=if($NativeSettingsConfig -and !$CatalogIsolation -and !$ViewerLayouts -and !$DurableSliderEdits){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_CATALOG_ISOLATION=if($CatalogIsolation){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_VIEWER_LAYOUTS=if($ViewerLayouts){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_DURABLE_EDITS=if($DurableSliderEdits){'1'}else{$null}
    $phases=if($CatalogIsolation){@('A-first','B-first','A-restart','B-restart')}elseif($SkipRestart -or $ViewerLayouts){@('first')}else{@('first','restart')}
    if (!$SkipRestart -and !$CatalogIsolation -and !$ViewerLayouts -and !$DurableSliderEdits) { [IO.File]::WriteAllText((Join-Path $folder 'verify-grain-restart.marker'), 'test only') }
    foreach($phase in $phases){
        [IO.File]::WriteAllText((Join-Path $folder 'phase.txt'),$phase)
        if($phase -ne $phases[0]){Start-Sleep -Seconds 10}
        if($CatalogIsolation){$catalog=$fixtureCatalogs[[int]($phase.StartsWith('B'))]}
        $env:SPEKTRAFILM_CATALOG=$catalog
        $phaseStarted=[DateTime]::UtcNow
        $env:SPEKTRAFILM_LAUNCH_ID=[Guid]::NewGuid().ToString('N')
        [IO.File]::WriteAllText((Join-Path $folder ($phase+'.launch')), $env:SPEKTRAFILM_LAUNCH_ID)
        # This is an interactive host UI integration test, not a background helper.
        $process=Start-Process -FilePath $CaptureOnePath -ArgumentList ('"'+$catalog+'"') -WindowStyle Normal -PassThru
        [IO.File]::WriteAllText((Join-Path $folder ($phase+'.pid')),[string]$process.Id)
        try {
            $status=Join-Path (Join-Path $folder 'Launch status') ($env:SPEKTRAFILM_LAUNCH_ID+'.json')
            $ready=Wait-SpektrafilmPanel $process $status $env:SPEKTRAFILM_LAUNCH_ID 90
            $ready | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $folder ($phase+'.ready.json'))
            $deadline=[DateTime]::UtcNow.AddMinutes(4)
            $result=Join-Path $folder ($phase+'.passed')
            $failure=Join-Path $folder 'failed.txt'
            while(!(Test-Path -LiteralPath $result)){
                if(Test-Path -LiteralPath $failure){throw ([IO.File]::ReadAllText($failure))}
                if($process.HasExited){throw "Test Capture One exited early: $($process.ExitCode)"}
                if([DateTime]::UtcNow -gt $deadline){throw "Timed out. Inspect $folder"}
                Start-Sleep -Milliseconds 500
                $process.Refresh()
            }
            Get-Content -LiteralPath $result
        } finally {
            # Deliberate crash/restart test of this newly launched disposable process.
            # No pre-existing Capture One process can reach this branch.
            if(!$process.HasExited){Stop-Process -Id $process.Id; $process.WaitForExit()}
            $process.Dispose()
            Clear-TestCrashMarkers $phaseStarted $phase
        }
    }
    if($CheckOrdinaryLaunch){
        Start-Sleep -Seconds 10
        $phaseStarted=[DateTime]::UtcNow
        [Environment]::SetEnvironmentVariable('DOTNET_STARTUP_HOOKS',$null,'Process')
        $env:SPEKTRAFILM_LAUNCH_ID=[Guid]::NewGuid().ToString('N')
        $status=Join-Path (Join-Path $folder 'Launch status') ($env:SPEKTRAFILM_LAUNCH_ID+'.json')
        $process=Start-Process -FilePath $CaptureOnePath -ArgumentList ('"'+$catalog+'"') -WindowStyle Normal -PassThru
        try {
            $deadline=[DateTime]::UtcNow.AddSeconds(90)
            do {
                $process.Refresh()
                if($process.HasExited){throw 'Ordinary Capture One exited before opening the test catalog.'}
                $title=$process.MainWindowTitle
                if($title -like '*Host Smoke*'){break}
                if([DateTime]::UtcNow -gt $deadline){throw "Ordinary Capture One did not open the disposable catalog. Last title: $title"}
                Start-Sleep -Milliseconds 300
            } while($true)
            $failure=$null
            try { Wait-SpektrafilmPanel $process $status $env:SPEKTRAFILM_LAUNCH_ID 2 | Out-Null } catch { $failure=$_.Exception.Message }
            if($failure -notlike '*did not report the Spektrafilm startup hook*' -or (Test-Path -LiteralPath $status)){
                throw "Ordinary launch unexpectedly reported startup state: $failure"
            }
            $message="PASS ordinary Capture One opened $title without a Spektrafilm startup report."
            [IO.File]::WriteAllText((Join-Path $folder 'ordinary.passed'),$message)
            Write-Output $message
        } finally {
            if(!$process.HasExited){Stop-Process -Id $process.Id; $process.WaitForExit()}
            $process.Dispose()
            Clear-TestCrashMarkers $phaseStarted 'ordinary'
        }
    }
    if((Get-FileHash -LiteralPath $source).Hash -ne $sourceHash){throw 'Source catalog changed unexpectedly.'}
} catch {
    $testError=$_
    throw
} finally {
    try {
        if ($NativeSettingsConfig) { Restore-TestCatalogPreferences $profile $backup $fixtureCatalogs $folder }
    } catch {
        if ($null -eq $testError) { throw }
        Write-Warning "The host test and its recent catalog cleanup both failed. Preferences backup retained. $($_.Exception.Message)"
    } finally {
        foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$prior[$name],'Process')}
        Write-Output "Host evidence directory: $folder"
    }
}
Write-Output "Host test passed; source catalog unchanged. Evidence: $folder"
