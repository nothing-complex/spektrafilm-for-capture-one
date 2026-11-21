[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $CaptureOnePath,
    [Parameter(Mandatory)][string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'HostCompatibilityFixture.ps1')
$candidate = Get-CaptureOneCompatibilityCandidate $CaptureOnePath
foreach ($path in @($candidate.ExecutablePath, $candidate.AssemblyPath)) {
    $signature = Get-AuthenticodeSignature -LiteralPath $path
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.GetNameInfo(
        [System.Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) -cne 'Capture One A/S') {
        throw 'The candidate files must have valid Capture One signatures.'
    }
}

$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
$fixture = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $fixture) { throw 'Use a new directory for the compatibility fixture.' }
$revision = git -C $root rev-parse HEAD
if ($LASTEXITCODE) { throw 'Could not read the source revision for the fixture.' }
$project = Join-Path $fixture 'NativePanel'
$panelFolder = Join-Path $fixture 'panel'
[void][IO.Directory]::CreateDirectory($project)
$sourceRoot = Join-Path $root 'NativePanel'
$sourceFiles = [ordered]@{}
foreach ($file in (Get-ChildItem -LiteralPath $sourceRoot -File | Where-Object Extension -in '.cs', '.csproj' | Sort-Object Name)) {
    if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unexpected redirected panel source.' }
    $target = Join-Path $project $file.Name
    Copy-Item -LiteralPath $file.FullName -Destination $target
    $sourceFiles[$file.Name] = (Get-FileHash -LiteralPath $target).Hash
}
$stableManifest = Get-Content -Raw -LiteralPath (Join-Path $root 'capture-one-hosts.json') | ConvertFrom-Json
$baselineHosts = @($stableManifest.Hosts | Where-Object FileVersion -ceq '16.4.6.2414')
if ($baselineHosts.Count -ne 1) { throw 'The baseline host capability inventory is missing.' }
$manifest = @{SchemaVersion = 1; Hosts = @(@{
    FileVersion = $candidate.Version
    Architecture = 'x64'
    AssemblySha256 = $candidate.AssemblySha256
    Capabilities = $baselineHosts[0].Capabilities
})} | ConvertTo-Json -Depth 5
[IO.File]::WriteAllText((Join-Path $fixture 'capture-one-hosts.json'), $manifest)

dotnet build (Join-Path $project 'NativePanel.csproj') -c Release --output $panelFolder "-p:CaptureOneDirectory=$($candidate.Directory)" --nologo --verbosity quiet
if ($LASTEXITCODE) { throw 'The candidate fixture did not build.' }
$panel = Join-Path $panelFolder 'NativePanel.dll'
[ordered]@{
    SchemaVersion = 1
    Status = 'built-for-licensed-qa'
    HostVersion = $candidate.Version
    Architecture = 'x64'
    ExecutableSha256 = $candidate.ExecutableSha256
    AssemblySha256 = $candidate.AssemblySha256
    PanelSha256 = (Get-FileHash -LiteralPath $panel).Hash
    SourceRevision = $revision
    SourceFiles = $sourceFiles
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $panelFolder 'host-compatibility-fixture.json') -Encoding UTF8
[void](Read-HostCompatibilityFixture $panelFolder $candidate.ExecutablePath)
Write-Output $panelFolder
