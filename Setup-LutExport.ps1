[CmdletBinding()]
param([string] $PythonPath, [string] $Wheelhouse, [switch] $Rebuild)

$ErrorActionPreference = 'Stop'
$requirements = Join-Path $PSScriptRoot 'requirements-lut.txt'
$worker = Join-Path $PSScriptRoot 'lut_worker.py'
$environmentRoot = Join-Path $PSScriptRoot '.reference-python'
$versionCheck = "import platform, sys; sys.exit(not (sys.platform == 'win32' and platform.machine().upper() == 'AMD64' and sys.version_info[:2] == (3, 13) and sys.implementation.name == 'cpython'))"
. (Join-Path $PSScriptRoot 'ReferencePython.ps1')

function Confirm-ReferenceWorker([string] $Python) {
    $environment = Split-Path -Parent (Split-Path -Parent $Python)
    $oldMpl = $env:MPLCONFIGDIR
    $oldNumba = $env:NUMBA_CACHE_DIR
    try {
        $env:MPLCONFIGDIR = Join-Path $environment 'cache\matplotlib'
        $env:NUMBA_CACHE_DIR = Join-Path $environment 'cache\numba'
        $request = '{"command":"export_lut","size":33,"strength":0.5,"film":"kodak_portra_400","paper":"kodak_portra_endura"}'
        $reply = $request | & $Python -I $worker
        if ($LASTEXITCODE -ne 0) { throw 'The optional Python reference worker failed its import check.' }
        $response = $reply | ConvertFrom-Json
        if (!$response.ok -or $response.cube -notmatch '(?m)^LUT_3D_SIZE 33$') {
            throw "The optional Python reference worker did not return a valid test LUT. $($response.error)"
        }
    } finally {
        $env:MPLCONFIGDIR = $oldMpl
        $env:NUMBA_CACHE_DIR = $oldNumba
    }
}

$lock = $null
$candidateDirectory = $null
$published = $false
try {
    foreach ($file in @($requirements, $worker, (Join-Path $PSScriptRoot 'upstream-spektrafilm\src\spektrafilm\__init__.py'))) {
        if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing optional LUT source file: $file" }
    }
    if ($Wheelhouse) { $Wheelhouse = (Resolve-Path -LiteralPath $Wheelhouse).ProviderPath }
    $requirementsHash = (Get-FileHash -LiteralPath $requirements -Algorithm SHA256).Hash.ToLowerInvariant()
    [void][IO.Directory]::CreateDirectory($environmentRoot)
    $resolvedRoot = Get-Item -LiteralPath $environmentRoot -Force
    if ($resolvedRoot.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The reference environment folder must be an ordinary directory.' }
    try {
        $lock = [IO.File]::Open((Join-Path $environmentRoot 'setup.lock'), [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    } catch [IO.IOException] { throw 'Another reference setup may be running. Wait for it to finish and try again.' }

    $selection = Get-ReferencePythonSelection $PSScriptRoot
    if (!$Rebuild -and !$PythonPath -and $selection -and $selection.RequirementsHash -eq $requirementsHash) {
        try {
            Confirm-ReferenceWorker $selection.Python
            Write-Host 'The verified optional reference runtime is already ready.'
            return
        } catch { Write-Warning ('Rebuilding the selected optional reference runtime. ' + $_.Exception.Message) }
    }

    $candidate = $PythonPath
    $launcherArgs = @()
    if (!$candidate) {
        $localPython = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\Python\Python313\python.exe'
        if (Test-Path -LiteralPath $localPython -PathType Leaf) { $candidate = $localPython }
    }
    if (!$candidate) {
        $launcher = Get-Command 'py.exe' -ErrorAction SilentlyContinue
        if ($launcher) {
            $candidate = $launcher.Source
            $launcherArgs = @('-3.13')
        } else {
            $pythonCommand = Get-Command 'python.exe' -ErrorAction SilentlyContinue
            if ($pythonCommand) { $candidate = $pythonCommand.Source }
        }
    }
    if (!$candidate) { throw 'Install Windows x64 CPython 3.13 from python.org, or pass -PythonPath.' }
    & $candidate @launcherArgs -I -c $versionCheck
    if ($LASTEXITCODE -ne 0) { throw 'Choose Windows x64 CPython 3.13. Use -PythonPath for a custom installation.' }
    # Short paths keep native wheel dependencies under Windows DLL loader limits.
    $name = 'py-' + [Guid]::NewGuid().ToString('N').Substring(0, 16)
    $candidateDirectory = Join-Path $environmentRoot $name
    & $candidate @launcherArgs -I -m venv --copies $candidateDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Python could not create the candidate reference environment.' }
    $python = Join-Path $candidateDirectory 'Scripts\python.exe'
    $pip = @('-I', '-m', 'pip', '--isolated', '--disable-pip-version-check')
    $install = @('install', '--no-input', '--no-cache-dir', '--require-hashes', '--only-binary=:all:', '-r', $requirements)
    if ($Wheelhouse) { $install += @('--no-index','--find-links',$Wheelhouse) }
    else { $install += @('--index-url','https://pypi.org/simple') }
    & $python @pip @install
    if ($LASTEXITCODE -ne 0) { throw 'The locked Python wheels could not be installed. Check network access or provide a complete -Wheelhouse. The previous selection is preserved.' }
    & $python @pip check
    if ($LASTEXITCODE -ne 0) { throw 'The installed reference packages did not pass dependency verification.' }
    Confirm-ReferenceWorker $python

    $active = Join-Path $environmentRoot 'active.json'
    $temporary = Join-Path $environmentRoot ($name + '.json.tmp')
    try {
        $record = [ordered]@{Version=1;Environment=$name;RequirementsHash=$requirementsHash}
        $bytes = [Text.Encoding]::UTF8.GetBytes(($record | ConvertTo-Json -Compress))
        $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) }
        finally { $stream.Dispose() }
        if ([IO.File]::Exists($active)) { [IO.File]::Replace($temporary, $active, ($active + '.previous')) }
        else { [IO.File]::Move($temporary, $active) }
        $published = $true
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
    Write-Host 'The verified optional reference runtime is ready. Restart from the Spektrafilm shortcut to use it.'
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
} finally {
    try {
        if (!$published -and $candidateDirectory -and (Test-Path -LiteralPath $candidateDirectory -PathType Container)) {
            $failed = Get-Item -LiteralPath $candidateDirectory -Force
            if ($failed.FullName -eq [IO.Path]::Combine($resolvedRoot.FullName, $failed.Name) -and
                $failed.Name -match '^py-[a-f0-9]{16}$' -and !($failed.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                Remove-Item -LiteralPath $failed.FullName -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    } finally { if ($lock) { $lock.Dispose() } }
}
