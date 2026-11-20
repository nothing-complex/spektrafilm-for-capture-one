[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OutputDirectory,
    [ValidatePattern('^[0-9a-fA-F]{7,40}$')][string] $HostSourceRevision
)

$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath
$fixture=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if(Test-Path -LiteralPath $fixture){throw 'Choose a new directory for the disposable development fixture.'}
$project=Join-Path $fixture 'NativePanel'
[void][IO.Directory]::CreateDirectory($project)
Get-ChildItem -LiteralPath (Join-Path $root 'NativePanel') -File |
    Where-Object {$_.Extension -in '.cs','.csproj'} |
    Copy-Item -Destination $project
Copy-Item -LiteralPath (Join-Path $root 'capture-one-hosts.json') -Destination $fixture

$path=Join-Path $project 'HostImageSource.cs'
$source=[IO.File]::ReadAllText($path)
if($HostSourceRevision){
    $historical=git -C $root show ("${HostSourceRevision}:NativePanel/HostImageSource.cs")
    if($LASTEXITCODE -ne 0){throw 'Could not read the historical profile owner for comparison.'}
    $source=$historical -join "`n"
}
function Replace-Anchor([string] $Source,[string] $Anchor,[string] $Replacement){
    $first=$Source.IndexOf($Anchor,[StringComparison]::Ordinal)
    if($first -lt 0 -or $Source.LastIndexOf($Anchor,[StringComparison]::Ordinal) -ne $first){
        throw 'The native development fixture boundary changed.'
    }
    return $Source.Remove($first,$Anchor.Length).Insert($first,$Replacement)
}
$timeout='private static readonly TimeSpan NativeScheduleTimeout = TimeSpan.FromMinutes(3);'
$source=Replace-Anchor $source $timeout 'private static readonly TimeSpan NativeScheduleTimeout = TimeSpan.FromSeconds(3);'
$slot='private static readonly SemaphoreSlim SchedulerSlot = new(1, 1);'
$fields=@'
private static readonly SemaphoreSlim SchedulerSlot = new(1, 1);
    private static readonly ManualResetEventSlim DevelopmentLeaseEntered = new(false);
    private static readonly ManualResetEventSlim DevelopmentLeaseContinue = new(false);
    private static int developmentLeaseArmed = 0;
'@
$source=Replace-Anchor $source $slot $fields
$develop='var result = DevelopNativeBitmap(bitmap, image, renderSettings, CancelNativeRender);'
$pause=@'
var result = DevelopNativeBitmap(bitmap, image, renderSettings, CancelNativeRender);
            if (result == IC_Result.resOK && Interlocked.CompareExchange(ref developmentLeaseArmed, 0, 1) == 1)
            {
                DevelopmentLeaseEntered.Set();
                if (!DevelopmentLeaseContinue.Wait(TimeSpan.FromSeconds(14)))
                    throw new TimeoutException("The disposable development fixture did not release the callback.");
            }
'@
$source=Replace-Anchor $source $develop $pause
[IO.File]::WriteAllText($path,$source)
$output=Join-Path $fixture 'runtime'
dotnet build (Join-Path $project 'NativePanel.csproj') -c Release -o $output --nologo --verbosity quiet
if($LASTEXITCODE -ne 0){throw 'The disposable development fixture did not build.'}
$panel=Join-Path $output 'NativePanel.dll'
(Get-FileHash -LiteralPath $panel -Algorithm SHA256).Hash |
    Set-Content -LiteralPath (Join-Path $output 'development-lease-fixture.sha256') -Encoding ascii
Write-Output $output
