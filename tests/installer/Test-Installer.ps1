[CmdletBinding()]
param([string] $ResultsDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if (!$ResultsDirectory) { $ResultsDirectory = Join-Path $repo '.test-results' }
$fixture = Join-Path $ResultsDirectory ('installer-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $fixture 'payload'
$install = Join-Path $fixture 'installed'
$desktop = Join-Path $fixture 'desktop'
[void][IO.Directory]::CreateDirectory($payload)
[void][IO.Directory]::CreateDirectory($desktop)
$shellExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$checks = New-Object 'System.Collections.Generic.List[string]'
function Assert([bool] $condition, [string] $name) {
    if (!$condition) { throw "FAIL: $name" }
    $checks.Add($name)
}
function Write-Fixture([string] $path, [string] $contents) {
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $path))
    [IO.File]::WriteAllText($path, $contents)
}
function Invoke-Installer([bool] $expectSuccess = $true) {
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$shellExe
    $start.Arguments='-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + (Join-Path $payload 'Install-Spektrafilm.ps1') + '" -Destination "' + $install + '" -DesktopDirectory "' + $desktop + '"'
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($start)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if(!$process.WaitForExit(30000)){ $process.Kill(); throw 'Installer fixture timed out.' }
        $code=$process.ExitCode
        [IO.File]::AppendAllText((Join-Path $fixture 'installer.log'),$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult())
    } finally { $process.Dispose() }
    Assert (($code -eq 0) -eq $expectSuccess) "Installer exit $code matches expected success=$expectSuccess"
}
Copy-Item -LiteralPath (Join-Path $repo 'Install-Spektrafilm.ps1') -Destination $payload
$files = @('Start Spektrafilm for Capture One.cmd','Start-Spektrafilm.ps1','CatalogIntegrity.ps1','Restore-SpektrafilmCatalog.ps1','capture-one-hosts.json','stocks.json','panel\NativePanel.dll','assets\Spektrafilm.ico',
    'profiles\preview\Linear Rec.2020.icc','profiles\preview\ProPhoto RGB.icc',
    'renderer\SpektraStillRenderer.exe','renderer\SpektraHanatos2025Spectra.f32','renderer\SpektraOutputGamutCompression.f32',
    'renderer\parameter-capabilities.json')
foreach ($shader in @('Copy','CurveDevelop','Diffusion','Dir','FilmExposure','FormatConvert','Grain','Halation','PrintScan','ScannerPost')) {
    $files += "renderer\shaders\Spektra$shader.comp.spv"
}
foreach ($file in $files) { Write-Fixture (Join-Path $payload $file) "inert version one: $file" }
Invoke-Installer
Assert (Test-Path -LiteralPath (Join-Path $desktop 'Spektrafilm for Capture One.lnk')) 'Shortcut is created only on fixture Desktop'
Assert (Test-Path -LiteralPath (Join-Path $install '.film-tools-owned.json')) 'Owned-file receipt is generated'
$custom = @('profiles\nested\my look.spektra.json','my-top-level-look.spektra.json','.venv\retained.txt','assets\custom icon.txt')
foreach ($file in $custom) { Write-Fixture (Join-Path $install $file) "user sentinel: $file" }
Write-Fixture (Join-Path $install 'assets\Spektrafilm.png\notes.txt') 'directory collision sentinel'
Write-Fixture (Join-Path $payload 'assets\Spektrafilm.png') 'new bundled file'
[void][IO.Directory]::CreateDirectory((Join-Path $install 'profiles\empty user folder'))
Write-Fixture (Join-Path $install 'profiles\preview\ProPhoto RGB.icc') 'modified user profile'
Write-Fixture (Join-Path $payload 'profiles\preview\Linear Rec.2020.icc') 'inert version two'
Invoke-Installer
foreach ($file in $custom) {
    Assert ([IO.File]::ReadAllText((Join-Path $install $file)) -eq "user sentinel: $file") "Preserves $file"
}
Assert ([IO.File]::ReadAllText((Join-Path $install 'profiles\preview\Linear Rec.2020.icc')) -eq 'inert version two') 'Updates unchanged owned files'
Assert (Test-Path -LiteralPath (Join-Path $install 'profiles\empty user folder') -PathType Container) 'Preserves empty user directories'
$collision = @(Get-ChildItem -LiteralPath (Join-Path $install 'Recovered files') -Recurse -File | Where-Object { $_.Name -eq 'notes.txt' })
Assert ($collision.Count -eq 1 -and [IO.File]::ReadAllText($collision[0].FullName) -eq 'directory collision sentinel') 'Preserves file-directory collision subtree'
$recovered = @(Get-ChildItem -LiteralPath (Join-Path $install 'Recovered files') -Recurse -File | Where-Object { $_.Name -eq 'ProPhoto RGB.icc' })
Assert ($recovered.Count -eq 1 -and [IO.File]::ReadAllText($recovered[0].FullName) -eq 'modified user profile') 'Retains modified owned file in recovery folder'
Invoke-Installer
Assert ([IO.File]::ReadAllText($recovered[0].FullName) -eq 'modified user profile') 'Recovery files survive subsequent update'

# Simulate a legacy receipt-free installation; changed collision must be retained conservatively.
[IO.File]::Delete((Join-Path $install '.film-tools-owned.json'))
Write-Fixture (Join-Path $install 'stocks.json') 'legacy customized stock list'
Invoke-Installer
$legacy = @(Get-ChildItem -LiteralPath (Join-Path $install 'Recovered files') -Recurse -File | Where-Object { $_.Name -eq 'stocks.json' -and [IO.File]::ReadAllText($_.FullName) -eq 'legacy customized stock list' })
Assert ($legacy.Count -eq 1) 'Legacy collision is backed up without ownership receipt'

# Failure after swap must restore the original directory including nested user files.
$shortcut = Join-Path $desktop 'Spektrafilm for Capture One.lnk'
[IO.File]::Delete($shortcut)
$com = New-Object -ComObject WScript.Shell
try {
    $link = $com.CreateShortcut($shortcut)
    try { $link.TargetPath = $shellExe; $link.Arguments = '-NoProfile -Command exit'; $link.Save() }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
} finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($com) }
$before = (Get-FileHash -LiteralPath (Join-Path $install 'stocks.json') -Algorithm SHA256).Hash
Write-Fixture (Join-Path $payload 'stocks.json') 'third version'
Invoke-Installer $false
Assert ((Get-FileHash -LiteralPath (Join-Path $install 'stocks.json') -Algorithm SHA256).Hash -eq $before) 'Shortcut collision rolls back runtime'
foreach ($file in $custom) { Assert ([IO.File]::ReadAllText((Join-Path $install $file)) -eq "user sentinel: $file") "Rollback retains $file" }

# Locked binary rejects upgrade before the install directory moves.
$lock = [IO.File]::Open((Join-Path $install 'panel\NativePanel.dll'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
try { Invoke-Installer $false } finally { $lock.Dispose() }
Assert ((Get-FileHash -LiteralPath (Join-Path $install 'stocks.json') -Algorithm SHA256).Hash -eq $before) 'Locked runtime leaves old installation intact'
Write-Fixture (Join-Path $payload 'runtime-manifest.required') 'Incomplete build fixture'
Invoke-Installer $false
Assert ((Get-FileHash -LiteralPath (Join-Path $install 'stocks.json') -Algorithm SHA256).Hash -eq $before) 'Missing required manifest rejects partial staged runtime'
$result = [ordered]@{ status='pass'; checks=$checks.Count; assertions=$checks.ToArray(); fixture=$fixture; catalogOrRealDesktopUsed=$false }
$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $fixture 'results.json') -Encoding utf8
Write-Output "PASS: $($checks.Count) installer assertions. Evidence: $fixture"
