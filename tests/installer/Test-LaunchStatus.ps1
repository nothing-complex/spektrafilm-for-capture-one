[CmdletBinding()]
param([string] $ResultsDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (!$ResultsDirectory) { $ResultsDirectory = Join-Path $repo '.test-results' }
$fixture = Join-Path $ResultsDirectory ('launch-status-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($fixture)
$launcher = Join-Path $repo 'Start-Spektrafilm.ps1'
$tokens = $null
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($launcher, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Launcher syntax failed.' }
foreach ($name in @('Read-SpektrafilmLaunchStatus','Wait-SpektrafilmPanel')) {
    $definition = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name}, $true)
    . ([scriptblock]::Create($definition.Extent.Text))
}
$count = 0
function Assert([bool] $value, [string] $name) { if (!$value) { throw "FAIL: $name" }; $script:count++ }
function Assert-Failure([scriptblock] $check, [string] $message) {
    $observed = $null
    try { & $check | Out-Null } catch { $observed = $_.Exception.Message }
    Assert ($null -ne $observed -and $observed.Contains($message)) "Expected separate outcome: $message"
}
$path = Join-Path $fixture 'status.json'
$id = [Guid]::NewGuid().ToString('N')
$process = [Diagnostics.Process]::GetCurrentProcess()
$status = [ordered]@{
    SchemaVersion=1;SessionId=$id;ProcessId=$process.Id;UpdatedUtc=[DateTime]::UtcNow.ToString('o')
    Phase='Ready';HostVersion='16.4.6.2414';Export='Unavailable';Viewer='Disabled'
}
function Save-Status { [IO.File]::WriteAllText($path, ($status | ConvertTo-Json -Compress)) }

try {
    Assert-Failure { Wait-SpektrafilmPanel $process $path $id 1 } 'did not report the Spektrafilm startup hook'
    Save-Status
    $observed = Wait-SpektrafilmPanel $process $path $id 1
    Assert ($observed.Phase -eq 'Ready' -and $observed.Export -eq 'Unavailable' -and $observed.Viewer -eq 'Disabled') 'Panel ready can coexist with disabled Viewer and missing native Export'
    Assert ($null -eq (Read-SpektrafilmLaunchStatus $path ([Guid]::NewGuid().ToString('N')) $process.Id)) 'Different launch session rejected'
    Assert ($null -eq (Read-SpektrafilmLaunchStatus $path $id ($process.Id + 1))) 'Different process rejected'
    $status.Phase = 'WaitingForCatalog'
    Save-Status
    Assert-Failure { Wait-SpektrafilmPanel $process $path $id 1 } 'still starting Spektrafilm (WaitingForCatalog)'
    $status.Phase = 'UnsupportedHost'
    Save-Status
    Assert-Failure { Wait-SpektrafilmPanel $process $path $id 1 } 'rejected this Capture One build'
    $status.Phase = 'CatalogUnavailable'
    Save-Status
    Assert-Failure { Wait-SpektrafilmPanel $process $path $id 1 } 'could not access the selected catalog'
    $status.Phase = 'Failed'
    Save-Status
    Assert-Failure { Wait-SpektrafilmPanel $process $path $id 1 } 'startup hook encountered an error'
    $status.Phase = 'Ready'
    $status.Viewer = 'ProcessingError'
    Save-Status
    Assert ((Read-SpektrafilmLaunchStatus $path $id $process.Id).Viewer -eq 'ProcessingError') 'Viewer failure is reported independently'
    [IO.File]::WriteAllText($path, '{"Phase":"Ready"')
    Assert ($null -eq (Read-SpektrafilmLaunchStatus $path $id $process.Id)) 'Malformed readiness file cannot make a launch ready'
    [IO.File]::WriteAllText($path, ('x' * 20000))
    Assert ($null -eq (Read-SpektrafilmLaunchStatus $path $id $process.Id)) 'Oversized report rejected'
} finally { $process.Dispose() }
Write-Output "PASS: $count launch status assertions. Evidence: $fixture"
