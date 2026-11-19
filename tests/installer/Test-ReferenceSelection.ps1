[CmdletBinding()]
param([string] $ResultsDirectory)
$ErrorActionPreference='Stop'
$repo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
. (Join-Path $repo 'ReferencePython.ps1')
if (!$ResultsDirectory) { $ResultsDirectory=Join-Path $repo '.test-results' }
$root=Join-Path $ResultsDirectory ('reference-selection-'+[Guid]::NewGuid().ToString('N'))
$reference=Join-Path $root '.reference-python'
$active=Join-Path $reference 'active.json'
[void][IO.Directory]::CreateDirectory($reference)
$checks=0
function Assert([bool] $valid,[string] $name) {
    if (!$valid) { throw "FAIL: $name" }
    $script:checks++
}
function Write-Fixture([string] $path,[string] $text) {
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $path))
    [IO.File]::WriteAllText($path,$text)
}
$legacy=Join-Path $root '.venv\Scripts\python.exe'
Assert ($null -eq (Get-SpektrafilmReferencePython $root)) 'No optional environment is a normal state'
Write-Fixture $legacy 'inert legacy python fixture'
Assert ((Get-SpektrafilmReferencePython $root) -eq $legacy) 'Legacy developer environment is still discoverable'
$environment='py-'+('a'*16)
$candidate=Join-Path (Join-Path $reference $environment) 'Scripts\python.exe'
$record=[ordered]@{Version=1;Environment=$environment;RequirementsHash=('a'*64)}
function Save-Selection { [IO.File]::WriteAllText($active,($record|ConvertTo-Json -Compress)) }
Save-Selection
Assert ((Get-SpektrafilmReferencePython $root) -eq $legacy) 'Incomplete candidate cannot supersede legacy'
Write-Fixture $candidate 'inert versioned python fixture'
Assert ((Get-SpektrafilmReferencePython $root) -eq $candidate) 'Valid selection has priority over legacy'
Assert ((Get-ReferencePythonSelection $root).RequirementsHash -ceq ('a'*64)) 'Selected runtime records its lock hash'
$record.Environment='..\..\outside'
Save-Selection
Assert ((Get-SpektrafilmReferencePython $root) -eq $legacy) 'External environment path is refused'
$record.Environment=$environment
$record.Version=2
Save-Selection
Assert ((Get-SpektrafilmReferencePython $root) -eq $legacy) 'Unknown selection version is refused'
[IO.File]::WriteAllText($active,'{')
Assert ((Get-SpektrafilmReferencePython $root) -eq $legacy) 'Malformed selection leaves legacy available'
[IO.File]::WriteAllText($active,('x'*5000))
Assert ((Get-SpektrafilmReferencePython $root) -eq $legacy) 'Oversized selection leaves legacy available'
Write-Output "PASS: $checks optional reference selection assertions. Evidence: $root"
