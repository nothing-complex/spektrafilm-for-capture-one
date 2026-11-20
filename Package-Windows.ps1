[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+([.-][a-zA-Z0-9.]+)?$')]
    [string] $Version,
    [switch] $Development
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
. (Join-Path $root 'ReleaseManifest.ps1')
$build = Test-ReleaseManifest $root
$sourceIdentity = Get-ReleaseSourceIdentity $root
if ($sourceIdentity.treeDigest -ne $build.source.treeDigest -or $sourceIdentity.commit -ne $build.source.commit) {
    throw 'Source no longer matches staged runtime. Run Build-Windows.ps1 -BuildPanel -StageRuntime again.'
}
$releaseValidation=$null
if (!$Development) {
    if ($sourceIdentity.dirty -or $build.source.dirty) { throw 'Release packaging requires committed clean source. Use -Development for an explicitly uncommitted test package.' }
    if ($build.validation.rendererSmoke -ne 'passed' -or $build.validation.panelBuild -ne 'passed') { throw 'Release packaging requires a successful renderer smoke test and panel build.' }
    $tag = & git -C $root rev-parse --verify "refs/tags/v$Version^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0 -or $tag -ne $sourceIdentity.commit) { throw "Tag v$Version must identify the exact tested source commit before release packaging." }
    $results=Join-Path $root ('.test-results/release-' + [Guid]::NewGuid().ToString('N'))
    & (Join-Path $root 'Test-Windows.ps1') -Gpu -ResultsDirectory $results
    $summaryPath=Join-Path $results 'summary.json'
    $summary=Get-Content -Raw -LiteralPath $summaryPath | ConvertFrom-Json
    $passed=Confirm-ReleaseContractSummary @($summary)
    $stillCurrent=Get-ReleaseSourceIdentity $root
    if ($stillCurrent.dirty -or $stillCurrent.commit -ne $sourceIdentity.commit -or $stillCurrent.treeDigest -ne $sourceIdentity.treeDigest) {
        throw 'Source changed while release contracts were running. Rebuild and rerun the release check.'
    }
    $releaseValidation=[ordered]@{ verifiedContracts=$passed; rendererSmoke='passed'; licensedCaptureOne='separate validation required';
        summarySha256=(Get-FileHash -LiteralPath $summaryPath -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$dist = Join-Path $root 'dist'
$archiveName = "Spektrafilm-for-Capture-One-v$Version-windows-x64.zip"
$archive = Join-Path $dist $archiveName
$stage = Join-Path $dist ('stage-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $stage 'Spektrafilm for Capture One'
$files = @(
    'Install Spektrafilm for Capture One.cmd', 'Install-Spektrafilm.ps1', 'InstallUpgradeState.ps1',
    'ReleaseManifest.ps1', 'CatalogIntegrity.ps1', 'Restore-SpektrafilmCatalog.ps1', 'build-manifest.json', 'runtime-manifest.required',
    'Start Spektrafilm for Capture One.cmd', 'Start-Spektrafilm.ps1',
    'Setup LUT Export.cmd', 'Setup-LutExport.ps1', 'ReferencePython.ps1', 'lut_worker.py', 'requirements-lut.txt',
    'README.md', 'LICENSE', 'THIRD-PARTY.md', 'UPSTREAM-CHANGES.md', 'capture-one-hosts.json','stocks.json',
    'assets\Spektrafilm.ico', 'assets\Spektrafilm.png', 'panel\NativePanel.dll',
    'profiles\preview\Linear Rec.2020.icc', 'profiles\preview\ProPhoto RGB.icc',
    'renderer\SpektraStillRenderer.exe', 'renderer\SpektraHanatos2025Spectra.f32',
    'renderer\SpektraOutputGamutCompression.f32', 'renderer\parameter-capabilities.json', 'upstream-ofx\LICENSE.txt',
    'upstream-ofx\Legal\THIRD_PARTY_NOTICES.txt', 'upstream-ofx\Legal\SPEKTRAFILM_OFX_LICENSE.txt',
    'upstream-ofx\Legal\SPEKTRAFILM_OFX_LUT_LICENSE.txt'
)
$shaders = 'Copy', 'CurveDevelop', 'Diffusion', 'Dir', 'FilmExposure', 'FormatConvert', 'Grain', 'Halation', 'PrintScan', 'ScannerPost'
$files += $shaders | ForEach-Object { "renderer\shaders\Spektra$_.comp.spv" }
$files += 'upstream-spektrafilm\LICENSE', 'upstream-spektrafilm\SPEKTRAFILM_LICENSE.txt',
    'upstream-spektrafilm\CITATION.cff', 'upstream-spektrafilm\README.md'
$pythonSource = Join-Path $root 'upstream-spektrafilm\src\spektrafilm'
$pythonFiles = Get-ChildItem -LiteralPath $pythonSource -Recurse -File |
    Where-Object { $_.Extension -in '.py', '.json', '.npy', '.csv', '.lut', '.txt', '.md' -and $_.FullName -notmatch '[\\/]__pycache__[\\/]' }
$files += $pythonFiles | ForEach-Object { $_.FullName.Substring($root.Length + 1) }

try {
    foreach ($relative in $files) {
        $source = Get-Item -LiteralPath (Join-Path $root $relative)
        if ($source.PSIsContainer -or ($source.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Expected an ordinary file at $relative"
        }
        $destination = Join-Path $package $relative
        [void] [IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
        [IO.File]::Copy($source.FullName, $destination)
    }
    $sourceRef = if ($Development) { $sourceIdentity.commit } else { "v$Version" }
    $sourceUrl = "https://github.com/nothing-complex/spektrafilm-for-capture-one/tree/$sourceRef"
    @(
        "Spektrafilm for Capture One v$Version"
        $(if ($Development) { 'Development package; uncommitted changes may be present. See build-manifest.json.' } else { 'Verified source release.' })
        "Corresponding source and build instructions"
        $sourceUrl
    ) | Set-Content -LiteralPath (Join-Path $package 'SOURCE.txt') -Encoding ascii
    $capabilities = New-ReleaseCapabilityMarkdown $package $build.parameterContract
    [IO.File]::WriteAllText((Join-Path $package 'CAPABILITIES.md'), $capabilities, [Text.UTF8Encoding]::new($false))
    $packageManifest = [ordered]@{
        schemaVersion=1; kind='package'; version=$Version; development=[bool]$Development
        source=$sourceIdentity; rendererProtocol=$build.rendererProtocol; parameterContract=$build.parameterContract
        files=@(($files + 'SOURCE.txt' + 'CAPABILITIES.md') | Sort-Object -Unique | ForEach-Object { Get-ReleaseFileRecord $package $_ })
    }
    if ($releaseValidation) { $packageManifest.validation=$releaseValidation }
    [IO.File]::WriteAllText((Join-Path $package 'package-manifest.json'),($packageManifest | ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($false)))
    [void](Test-ReleaseManifest $package 'package-manifest.json')
    $temporaryArchive = Join-Path $stage $archiveName
    Compress-Archive -LiteralPath $package -DestinationPath $temporaryArchive -CompressionLevel Optimal
    Move-Item -LiteralPath $temporaryArchive -Destination $archive -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $archiveName" | Set-Content -LiteralPath "$archive.sha256" -Encoding ascii
    Write-Output $archive
} finally {
    if (Test-Path -LiteralPath $stage -PathType Container) {
        $stageItem = Get-Item -LiteralPath $stage
        $distItem = Get-Item -LiteralPath $dist
        if ($stageItem.Parent.FullName -eq $distItem.FullName -and
            $stageItem.Name -match '^stage-[a-f0-9]{32}$' -and
            !(($stageItem.Attributes -bor $distItem.Attributes) -band [IO.FileAttributes]::ReparsePoint)) {
            Remove-Item -LiteralPath $stageItem.FullName -Recurse -Force
        }
    }
}
