[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $OutputDirectory,
    [string] $HarnessDirectory,
    [string] $VulkanSdkPath = $env:VULKAN_SDK
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).ProviderPath
if (!$HarnessDirectory) { $HarnessDirectory = Join-Path $root 'build\ofx-harness' }
$harness = (Resolve-Path -LiteralPath $HarnessDirectory).ProviderPath
$fixture = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $fixture) { throw 'Choose a new directory for the disposable worker.' }
[void][IO.Directory]::CreateDirectory($fixture)
Copy-Item -LiteralPath (Join-Path $root 'upstream-ofx\src') -Destination $fixture -Recurse

$sourcePath = Join-Path $fixture 'src\SpektraVulkanRenderer.cpp'
$source = [IO.File]::ReadAllText($sourcePath)
$anchor = '  if (uploadSource) coreFrame.sourceRevision = sourceRevision;'
$anchorIndex = $source.IndexOf($anchor, [StringComparison]::Ordinal)
if ($anchorIndex -lt 0 -or $source.LastIndexOf($anchor, [StringComparison]::Ordinal) -ne $anchorIndex) {
    throw 'The tested renderer command boundary changed.'
}
$at = $source.LastIndexOf('  const PerfClock::time_point commandEnd = PerfClock::now();', $anchorIndex, [StringComparison]::Ordinal)
$fence = '  result = vkWaitForFences(device, 1, &fence, VK_TRUE, UINT64_MAX);'
if ($at -lt 0 -or !$source.Substring(0, $at).TrimEnd().EndsWith($fence, [StringComparison]::Ordinal)) {
    throw 'The renderer fence boundary changed.'
}
$simulation = @'
  // The test fence completed successfully before substituting a device-loss reply.
  static std::atomic<unsigned> completedTestFences{0u};
  if (result == VK_SUCCESS && completedTestFences.fetch_add(1) == 1u) {
    result = VK_ERROR_DEVICE_LOST;
  }
'@
[IO.File]::WriteAllText($sourcePath, $source.Insert($at, $simulation + [Environment]::NewLine))

$oldSdk = $env:VULKAN_SDK
try {
    if ($VulkanSdkPath) { $env:VULKAN_SDK = (Resolve-Path -LiteralPath $VulkanSdkPath).ProviderPath }
    $build = Join-Path $fixture 'build'
    cmake -S (Join-Path $root 'ofx-still-renderer') -B $build -G 'Visual Studio 17 2022' -A x64 "-DOFX_SOURCE=$fixture" "-DOFX_BUILD=$harness"
    if ($LASTEXITCODE -ne 0) { throw 'Device-loss fixture configuration failed.' }
    cmake --build $build --config Release --target SpektraStillRenderer --parallel 4
    if ($LASTEXITCODE -ne 0) { throw 'Device-loss fixture build failed.' }
    $executable = Join-Path $build 'Release\SpektraStillRenderer.exe'
    $description = & $executable --describe
    if ($LASTEXITCODE -ne 0 -or @($description).Count -ne 1) { throw 'The fixture worker did not describe its parameter contract.' }
    $runtime = Split-Path -Parent $executable
    [IO.File]::WriteAllText((Join-Path $runtime 'parameter-capabilities.json'), $description + [Environment]::NewLine)
    (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash |
        Set-Content -LiteralPath (Join-Path $runtime 'device-loss-fixture.sha256') -Encoding ascii
    Write-Output $executable
} finally { $env:VULKAN_SDK = $oldSdk }
