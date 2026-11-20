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
. (Join-Path $repo 'InstallUpgradeState.ps1')
$connected = [pscustomobject]@{ Phase='Ready'; Export='Connected'; Viewer='Disabled' }
function Assert([bool] $condition, [string] $name) {
    if (!$condition) { throw "FAIL: $name" }
    $checks.Add($name)
}
function Write-Fixture([string] $path, [string] $contents) {
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $path))
    [IO.File]::WriteAllText($path, $contents)
}
function Invoke-Installer([bool] $expectSuccess = $true, [bool] $confirm = $true) {
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
    if ($expectSuccess -and $confirm -and ($candidate = Get-LaunchUpgradeCandidate $install)) {
        Complete-SpektrafilmUpgrade $install $candidate $connected | Out-Null
    }
}
Copy-Item -LiteralPath (Join-Path $repo 'Install-Spektrafilm.ps1') -Destination $payload
Copy-Item -LiteralPath (Join-Path $repo 'InstallUpgradeState.ps1') -Destination $payload
$files = @('Start Spektrafilm for Capture One.cmd','Start-Spektrafilm.ps1','CatalogIntegrity.ps1','Restore-SpektrafilmCatalog.ps1','ReferencePython.ps1','capture-one-hosts.json','stocks.json','panel\NativePanel.dll','assets\Spektrafilm.ico',
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
$custom = @('profiles\nested\my look.spektra.json','my-top-level-look.spektra.json','.venv\retained.txt',
    '.reference-python\active.json','assets\custom icon.txt', ('profiles\espace ' + [char]0x00E9 + '\look final.spektra.json'))
foreach ($file in $custom) { Write-Fixture (Join-Path $install $file) "user sentinel: $file" }
Write-Fixture (Join-Path $install 'assets\Spektrafilm.png\notes.txt') 'directory collision sentinel'
Write-Fixture (Join-Path $payload 'assets\Spektrafilm.png') 'new bundled file'
Write-Fixture (Join-Path $install 'capabilities.md') 'user compatibility notes'
Write-Fixture (Join-Path $payload 'CAPABILITIES.md') 'generated capability documentation'
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
$doc = Join-Path $install 'CAPABILITIES.md'
$oldDoc = @(Get-ChildItem -LiteralPath (Join-Path $install 'Recovered files') -Recurse -File -Filter 'CAPABILITIES.md')
Assert ([IO.File]::ReadAllText($doc) -eq 'generated capability documentation') 'Installs the package capability document'
Assert ($oldDoc.Count -eq 1 -and [IO.File]::ReadAllText($oldDoc[0].FullName) -eq 'user compatibility notes') 'Retains pre-existing user notes when the packaged filename differs only by case'
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
Assert ([IO.File]::Exists($shortcut)) 'Rollback preserves the unrelated shortcut'
[IO.File]::Delete($shortcut)

# Locked binary rejects upgrade before the install directory moves.
$lock = [IO.File]::Open((Join-Path $install 'panel\NativePanel.dll'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)
try { Invoke-Installer $false } finally { $lock.Dispose() }
Assert ((Get-FileHash -LiteralPath (Join-Path $install 'stocks.json') -Algorithm SHA256).Hash -eq $before) 'Locked runtime leaves old installation intact'
$normalDesktop=$desktop
$desktop=Join-Path $fixture 'not a desktop directory'
Write-Fixture $desktop 'fixture desktop sentinel'
try { Invoke-Installer $false } finally { $desktop=$normalDesktop }
Assert ((Get-FileHash -LiteralPath (Join-Path $install 'stocks.json') -Algorithm SHA256).Hash -eq $before) 'Unavailable Desktop leaves the old installation intact'
Assert ([IO.File]::ReadAllText((Join-Path $fixture 'not a desktop directory')) -eq 'fixture desktop sentinel') 'Unavailable Desktop does not overwrite a regular file'

$outside=Join-Path $fixture 'Junction target'
[void][IO.Directory]::CreateDirectory($outside)
Write-Fixture (Join-Path $outside 'untouched.txt') 'linked root sentinel'
$nested=Join-Path $install 'profiles\linked user content'
New-Item -ItemType Junction -Path $nested -Target $outside | Out-Null
try { Invoke-Installer $false } finally { [IO.Directory]::Delete($nested) }
Assert ([IO.File]::ReadAllText((Join-Path $outside 'untouched.txt')) -eq 'linked root sentinel') 'Existing install junctions do not alter their targets'
Assert ((Get-FileHash -LiteralPath (Join-Path $install 'stocks.json') -Algorithm SHA256).Hash -eq $before) 'Nested install junction aborts before swap'

$renderer=Join-Path $payload 'renderer'
$realRenderer=Join-Path $fixture 'plain renderer'
[IO.Directory]::Move($renderer,$realRenderer)
try {
    New-Item -ItemType Junction -Path $renderer -Target $realRenderer | Out-Null
    try { Invoke-Installer $false } finally { [IO.Directory]::Delete($renderer) }
} finally { [IO.Directory]::Move($realRenderer,$renderer) }
$log=[IO.File]::ReadAllText((Join-Path $fixture 'installer.log'))
Assert ($log -match 'linked path .*renderer') 'A linked required asset directory aborts even without a release manifest'
Assert ((Get-FileHash -LiteralPath (Join-Path $install 'stocks.json') -Algorithm SHA256).Hash -eq $before) 'Linked payload leaves old install intact'
Assert ([IO.File]::ReadAllText((Join-Path $renderer 'SpektraStillRenderer.exe')) -eq 'inert version one: renderer\SpektraStillRenderer.exe') 'Installer never modifies linked payload targets'

# Create a race only in the inert installer copy. The destination becomes a file after
# the old directory is moved, so the actual stage move and actual rollback must fail.
$inertInstaller=Join-Path $payload 'Install-Spektrafilm.ps1'
$unmodified=[IO.File]::ReadAllBytes($inertInstaller)
$text=[IO.File]::ReadAllText($inertInstaller)
$swap='        [IO.Directory]::Move($stage, $install)'
if (!$text.Contains($swap)) { throw 'Could not find the reviewed installer move checkpoint.' }
$interfering = 'inert external writer owns this file'
$testInstaller=$text.Replace($swap, ('        [IO.File]::WriteAllText($install, ''inert external writer owns this file'')' + [Environment]::NewLine + $swap))
try {
    [IO.File]::WriteAllText($inertInstaller,$testInstaller)
    Invoke-Installer $false
} finally { [IO.File]::WriteAllBytes($inertInstaller,$unmodified) }
$interruptedPrevious=@(Get-ChildItem -LiteralPath $fixture -Directory -Force | Where-Object Name -like '.film-tools-previous-*')
$interruptedStage=@(Get-ChildItem -LiteralPath $fixture -Directory -Force | Where-Object Name -like '.film-tools-stage-*')
Assert ($interruptedPrevious.Count -eq 1 -and $interruptedStage.Count -eq 1) 'A failed publish and rollback retain separate staged and previous installs'
Assert ([IO.File]::ReadAllText($install) -eq $interfering) 'Rollback preserves the file racing the install directory'
$lastLog=[IO.File]::ReadAllText((Join-Path $fixture 'installer.log'))
Assert ($lastLog.Contains($interruptedPrevious[0].FullName) -and $lastLog.Contains($interruptedStage[0].FullName)) 'Rollback failure identifies both recoverable paths'
Assert ((Get-FileHash -LiteralPath (Join-Path $interruptedPrevious[0].FullName 'stocks.json') -Algorithm SHA256).Hash -eq $before) 'Interrupted move retains the exact old runtime'
foreach ($file in $custom) {
    Assert ([IO.File]::ReadAllText((Join-Path $interruptedPrevious[0].FullName $file)) -eq "user sentinel: $file") "Failed rollback retains $file"
}
Invoke-Installer $false
Assert ([IO.File]::ReadAllText($install) -eq $interfering) 'Interrupted recovery never overwrites an unrelated item at the final destination'
[IO.File]::Delete($install)
Write-Fixture (Join-Path $payload 'stocks.json') 'legacy-compatible replacement'
Invoke-Installer
Assert ([IO.File]::ReadAllText((Join-Path $install 'stocks.json')) -eq 'legacy-compatible replacement') 'Rerunning the installer recovers a failed rollback and completes the requested update'
Assert (![IO.Directory]::Exists($interruptedPrevious[0].FullName) -and [IO.Directory]::Exists($interruptedStage[0].FullName)) 'Automatically restored old directory while retaining the interrupted candidate for recovery'

$transactionLock = @(Get-ChildItem -LiteralPath $fixture -File -Force -Filter '.film-tools-upgrade-*.lock')
Assert ($transactionLock.Count -eq 1) 'Installer uses one stable per-destination process lock'
$held = [IO.File]::Open($transactionLock[0].FullName,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try { Invoke-Installer $false } finally { $held.Dispose() }
Assert (([IO.File]::ReadAllText((Join-Path $fixture 'installer.log'))) -match 'Another installer is working on this destination') 'A concurrent installer cannot enter recovery or swap'

$newStock = Join-Path $install 'stocks.json'
$candidateRelative = 'profiles\interrupted-candidate-user-file.txt'
$cleanScript = [IO.File]::ReadAllText($inertInstaller)
foreach ($checkpoint in @('        [IO.Directory]::Move($stage, $install)', '        $installed = $true')) {
    $candidatePublished = $checkpoint -eq '        $installed = $true'
    Assert ($cleanScript.Contains($checkpoint)) "Inert hard-exit checkpoint present: $checkpoint"
    $modifiedScript = $cleanScript.Replace($checkpoint, ('        Stop-Process -Id $PID -Force' + [Environment]::NewLine + $checkpoint))
    Write-Fixture (Join-Path $payload 'stocks.json') "terminated candidate $candidatePublished"
    $oldHash = (Get-FileHash -LiteralPath $newStock -Algorithm SHA256).Hash
    try {
        [IO.File]::WriteAllText($inertInstaller,$modifiedScript)
        Invoke-Installer $false
    } finally { [IO.File]::WriteAllBytes($inertInstaller,$unmodified) }
    $journal = @(Get-ChildItem -LiteralPath $fixture -File -Force -Filter '.film-tools-upgrade-*.json' |
        Where-Object Name -cmatch '^\.film-tools-upgrade-[0-9a-f]{24}\.json$')
    Assert ($journal.Count -eq 1) "Terminating installer leaves one durable upgrade journal (published=$candidatePublished)"
    $receipt = [IO.File]::ReadAllText($journal[0].FullName) | ConvertFrom-Json
    $checkpointPrevious = Join-Path $fixture ('.film-tools-previous-' + $receipt.id)
    $checkpointStage = Join-Path $fixture ('.film-tools-stage-' + $receipt.id)
    Assert ((Get-FileHash -LiteralPath (Join-Path $checkpointPrevious 'stocks.json') -Algorithm SHA256).Hash -eq $oldHash) 'Interrupted upgrade preserves byte-exact previous runtime'
    $candidate = if ($candidatePublished) { $install } else { $checkpointStage }
    $candidateReceipt = Join-Path $candidate '.film-tools-owned.json'
    $candidateReceiptBytes = [IO.File]::ReadAllBytes($candidateReceipt)
    Write-Fixture (Join-Path $candidate $candidateRelative) "user content from interrupted candidate $candidatePublished"
    Write-Fixture $candidateReceipt 'unverifiable candidate receipt'
    Invoke-Installer $false
    Assert ([IO.File]::Exists($journal[0].FullName) -and [IO.Directory]::Exists($checkpointPrevious) -and [IO.Directory]::Exists($candidate)) 'Verification failure leaves all recovery paths intact'
    [IO.File]::WriteAllBytes($candidateReceipt,$candidateReceiptBytes)
    Write-Fixture (Join-Path $payload 'stocks.json') "successful retry $candidatePublished"
    Invoke-Installer
    Assert (![IO.File]::Exists($journal[0].FullName) -and ![IO.Directory]::Exists($checkpointPrevious)) 'Complete recovery clears the exact transaction and old rename path'
    Assert ([IO.File]::ReadAllText($newStock) -eq "successful retry $candidatePublished") 'Actual retry publishes a fully installed runtime'
    Assert ([IO.File]::ReadAllText((Join-Path $checkpointStage $candidateRelative)) -eq "user content from interrupted candidate $candidatePublished") 'Recovery leaves files written to the abandoned install available for retrieval'
    foreach ($file in $custom) {
        Assert ([IO.File]::ReadAllText((Join-Path $install $file)) -eq "user sentinel: $file") "Hard-exit recovery preserves $file"
    }
}

$preserved = Join-Path $install 'profiles\nested\keep-until-native-ready.txt'
Write-Fixture $preserved 'verified previous generation contents'
Write-Fixture (Join-Path $payload 'stocks.json') 'awaiting a real launch acknowledgment'
Invoke-Installer $true $false
$awaiting = Get-LaunchUpgradeCandidate $install
Assert ($awaiting.Item.Name -like '*.pending-*.json') 'Successful shortcut and publication leave a pending launch confirmation'
$previous = Join-Path $fixture ('.film-tools-previous-' + $awaiting.Receipt.id)
$previousCopy = Join-Path $previous 'profiles\nested\keep-until-native-ready.txt'
Assert ([IO.File]::ReadAllText($previousCopy) -eq 'verified previous generation contents') 'Verified previous version and user files remain beside the upgraded generation'
$unready = @(
    @{Phase='Starting';Export='Checking';Viewer='Unchecked'},
    @{Phase='Ready';Export='Unavailable';Viewer='Disabled'},
    @{Phase='Ready';Export='Connected';Viewer='ProcessingError'}
)
foreach ($status in $unready) { Complete-SpektrafilmUpgrade $install $awaiting ([pscustomobject]$status) }
$stale = @{} + $awaiting
$stale.Hash = ('A' * 64)
Complete-SpektrafilmUpgrade $install $stale $connected
Assert ([IO.File]::Exists($awaiting.Item.FullName) -and [IO.File]::Exists($previousCopy)) 'Unready, partial and stale-generation launches cannot retire the prior version'
Write-Fixture $previousCopy 'user modified the previous copy while awaiting launch'
Complete-SpektrafilmUpgrade $install $awaiting $connected -WarningAction SilentlyContinue
$ready = Get-LaunchUpgradeCandidate $install
Assert ($ready.Item.Name -like '*.ready-*.json' -and [IO.Directory]::Exists($previous)) 'A valid launch persists acknowledgment but retains a previous copy with unmirrored custom contents'
Write-Fixture $preserved 'user modified the previous copy while awaiting launch'
Complete-SpektrafilmUpgrade $install $ready $connected | Out-Null
Assert (!(Get-LaunchUpgradeCandidate $install) -and ![IO.Directory]::Exists($previous)) 'Confirmed cleanup retires an exact previous generation after all custom contents exist in the current install'
Assert ([IO.File]::ReadAllText($preserved) -eq 'user modified the previous copy while awaiting launch') 'Cleanup leaves the exact user file in the current tree'

$before = (Get-FileHash -LiteralPath $newStock -Algorithm SHA256).Hash

Write-Fixture (Join-Path $payload 'runtime-manifest.required') 'Incomplete build fixture'
Invoke-Installer $false
Assert ((Get-FileHash -LiteralPath (Join-Path $install 'stocks.json') -Algorithm SHA256).Hash -eq $before) 'Missing required manifest rejects partial staged runtime'
$result = [ordered]@{ status='pass'; checks=$checks.Count; assertions=$checks.ToArray(); fixture=$fixture; catalogOrRealDesktopUsed=$false }
$result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $fixture 'results.json') -Encoding utf8
Write-Output "PASS: $($checks.Count) installer assertions. Evidence: $fixture"
