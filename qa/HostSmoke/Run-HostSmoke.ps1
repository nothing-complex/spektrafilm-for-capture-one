[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SourceCatalog,
    [string] $CaptureOnePath='C:\Program Files\Capture One\Capture One\CaptureOne.exe',
    [string] $TestHookPath=(Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\HostSmoke.dll'),
    [switch] $SkipRestart,
    [string] $ResultsDirectory
)
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).ProviderPath
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
$source=(Get-Item -LiteralPath $SourceCatalog).FullName
if([IO.Path]::GetExtension($source) -ine '.cocatalogdb'){throw 'A catalog database is required.'}
foreach($suffix in '-wal','-shm','-journal'){if(Test-Path -LiteralPath ($source+$suffix)){throw 'Source catalog has transaction sidecars; close it cleanly first.'}}
if(!$ResultsDirectory){$ResultsDirectory=Join-Path $root ('.test-results\host-'+[guid]::NewGuid().ToString('N'))}
$folder=[IO.Path]::GetFullPath($ResultsDirectory)
if(Test-Path -LiteralPath $folder){throw 'Use a new empty results directory.'}
[void][IO.Directory]::CreateDirectory($folder)
$catalog=Join-Path $folder 'Host Smoke.cocatalogdb'
Copy-Item -LiteralPath $source -Destination $catalog
$sourceHash=(Get-FileHash -LiteralPath $source).Hash
[IO.File]::WriteAllText((Join-Path $folder 'disposable-catalog.marker'),$sourceHash)
$runtime=Join-Path $folder 'runtime'
[void][IO.Directory]::CreateDirectory($runtime)
Copy-Item -LiteralPath (Join-Path $root 'stocks.json') -Destination $runtime
Copy-Item -LiteralPath (Join-Path $root 'profiles') -Destination $runtime -Recurse
Copy-Item -LiteralPath (Join-Path $root 'build\still-renderer\Release') -Destination (Join-Path $runtime 'renderer') -Recurse
Copy-Item -LiteralPath (Join-Path $root 'NativePanel\bin\Release\net8.0-windows\NativePanel.dll') -Destination $runtime
$testHook=$TestHookPath
if(!(Test-Path -LiteralPath $testHook)){throw 'Build qa/HostSmoke and NativePanel first.'}
Copy-Item -LiteralPath $testHook -Destination $runtime
$testHook=Join-Path $runtime 'HostSmoke.dll'
$names=@('DOTNET_STARTUP_HOOKS','SPEKTRAFILM_ROOT','SPEKTRAFILM_CATALOG','SPEKTRAFILM_PANEL_LOG','SPEKTRAFILM_HOST_SMOKE')
$prior=@{}
foreach($name in $names){$prior[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
try {
    $env:DOTNET_STARTUP_HOOKS=(Join-Path $runtime 'NativePanel.dll')+';'+$testHook
    $env:SPEKTRAFILM_ROOT=$runtime
    $env:SPEKTRAFILM_CATALOG=$catalog
    $env:SPEKTRAFILM_PANEL_LOG=Join-Path $folder 'panel.log'
    $env:SPEKTRAFILM_HOST_SMOKE=$folder
    $phases=if($SkipRestart){@('first')}else{@('first','restart')}
    if (!$SkipRestart) { [IO.File]::WriteAllText((Join-Path $folder 'verify-grain-restart.marker'), 'test only') }
    foreach($phase in $phases){
        if($phase -eq 'restart'){Start-Sleep -Seconds 10}
        $phaseStarted=[DateTime]::UtcNow
        # This is an interactive host UI integration test, not a background helper.
        $process=Start-Process -FilePath $CaptureOnePath -ArgumentList ('"'+$catalog+'"') -WindowStyle Normal -PassThru
        [IO.File]::WriteAllText((Join-Path $folder ($phase+'.pid')),[string]$process.Id)
        try {
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
    if((Get-FileHash -LiteralPath $source).Hash -ne $sourceHash){throw 'Source catalog changed unexpectedly.'}
    Write-Output "Host test passed; source catalog unchanged. Evidence: $folder"
} finally {
    foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$prior[$name],'Process')}
    Write-Output "Host evidence directory: $folder"
}
