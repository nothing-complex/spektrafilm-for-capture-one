function Get-ReferencePythonSelection([string] $Root) {
    $environmentRoot = Join-Path $Root '.reference-python'
    $active = Join-Path $environmentRoot 'active.json'
    try {
        if (!(Test-Path -LiteralPath $active -PathType Leaf) -or (Get-Item -LiteralPath $active).Length -gt 4KB) { return $null }
        $selection = [IO.File]::ReadAllText($active) | ConvertFrom-Json
        if ($selection.Version -ne 1 -or $selection.Environment -notmatch '^py-[a-f0-9]{16}$' -or
            $selection.RequirementsHash -notmatch '^[a-f0-9]{64}$') { return $null }
        $path = Join-Path (Join-Path $environmentRoot $selection.Environment) 'Scripts\python.exe'
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
        return [pscustomobject]@{Python=$path;RequirementsHash=$selection.RequirementsHash}
    } catch { return $null }
}

function Get-SpektrafilmReferencePython([string] $Root) {
    $selection = Get-ReferencePythonSelection $Root
    if ($selection) { return $selection.Python }
    $legacy = Join-Path $Root '.venv\Scripts\python.exe'
    if (Test-Path -LiteralPath $legacy -PathType Leaf) { return $legacy }
    return $null
}
