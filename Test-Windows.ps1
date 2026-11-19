[CmdletBinding()]
param(
    [switch] $Gpu,
    [switch] $SkipInstaller,
    [string] $PythonPath = 'python',
    [string] $RendererPath,
    [string] $ResultsDirectory
)
$ErrorActionPreference='Stop'
if (!$RendererPath) { $RendererPath = Join-Path $PSScriptRoot 'renderer\SpektraStillRenderer.exe' }
if (!$ResultsDirectory) { $ResultsDirectory = Join-Path $PSScriptRoot '.test-results' }
$ResultsDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ResultsDirectory)
[void][IO.Directory]::CreateDirectory($ResultsDirectory)
$results=New-Object 'System.Collections.Generic.List[object]'
function Invoke-Test([string] $name,[string] $program,[string[]] $arguments) {
    $started=[DateTime]::UtcNow
    $log=Join-Path $ResultsDirectory ($name+'.log')
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=(Get-Command $program -CommandType Application -ErrorAction Stop).Source
    # Quote Windows process arguments, including trailing backslashes, without going through a shell.
    $start.Arguments=($arguments | ForEach-Object { '"' + (($_ -replace '(\\*)"','$1$1\"') -replace '(\\+)$','$1$1') + '"' }) -join ' '
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    if ([IO.Path]::GetFileName($start.FileName) -ieq 'powershell.exe') {
        # ProcessStartInfo does not apply pwsh's Windows PowerShell compatibility
        # environment handling. Its PS7 module path can shadow PS5 built-ins.
        # Let this child reconstruct its own defaults without changing the caller.
        $start.EnvironmentVariables.Remove('PSModulePath')
    }
    $process=[Diagnostics.Process]::Start($start)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if(!$process.WaitForExit(600000)){ $process.Kill(); throw "$name timed out after ten minutes." }
        $code=$process.ExitCode
        $output=$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($log,$output)
        Write-Output $output
    } finally { $process.Dispose() }
    $results.Add([ordered]@{name=$name;exitCode=$code;elapsedMs=([DateTime]::UtcNow-$started).TotalMilliseconds;log=$log})
    if($code -ne 0){throw "$name failed with exit code $code. See $log"}
}
try {
    # No host binaries or running Capture One required for this tier.
    $projects=@(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'tests') -Recurse -Filter '*.csproj' -File |
        Where-Object {$_.FullName -notmatch '[\\/](bin|obj)[\\/]'} | Sort-Object FullName)
    if($projects.Count -eq 0){throw 'No managed contract test projects found.'}
    foreach($project in $projects){Invoke-Test $project.BaseName 'dotnet' @('run','--project',$project.FullName,'-c','Release','-p:TargetPlatformDisplayName=Windows')}
    $powershell=Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    Invoke-Test 'runner-environment' $powershell @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'tests\installer\Test-RunnerEnvironment.ps1'),'-ResultsDirectory',$ResultsDirectory)
    Invoke-Test 'manifest' $powershell @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'tests\installer\Test-Manifest.ps1'),'-ResultsDirectory',$ResultsDirectory)
    Invoke-Test 'launch-status' $powershell @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'tests\installer\Test-LaunchStatus.ps1'),'-ResultsDirectory',$ResultsDirectory)
    Invoke-Test 'reference-selection' $powershell @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'tests\installer\Test-ReferenceSelection.ps1'),'-ResultsDirectory',$ResultsDirectory)
    Invoke-Test 'catalog-backup' $powershell @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'tests\installer\Test-CatalogBackup.ps1'),'-ResultsDirectory',$ResultsDirectory)
    if(!$SkipInstaller){Invoke-Test 'installer' $powershell @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',(Join-Path $PSScriptRoot 'tests\installer\Test-Installer.ps1'),'-ResultsDirectory',$ResultsDirectory)}
    if($Gpu){
        Invoke-Test 'renderer-smoke' $PythonPath @((Join-Path $PSScriptRoot 'ofx-still-renderer\smoke.py'),'--exe',$RendererPath)
    } else {
        $results.Add([ordered]@{name='renderer-smoke';status='not-run';reason='GPU tier requires -Gpu and a built Vulkan renderer.'})
    }
    $results.Add([ordered]@{name='capture-one-integration';status='not-run';reason='Requires exact supported host and a separately backed-up disposable catalog; not part of public no-host suite.'})
    Write-Output 'All requested contract checks passed.'
} finally {
    [IO.File]::WriteAllText((Join-Path $ResultsDirectory 'summary.json'),($results.ToArray()|ConvertTo-Json -Depth 5))
}
