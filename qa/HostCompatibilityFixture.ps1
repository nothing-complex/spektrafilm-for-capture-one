function Get-CaptureOneCompatibilityCandidate([string] $ExecutablePath) {
    $exe = Get-Item -LiteralPath $ExecutablePath -Force
    if ($exe.PSIsContainer -or $exe.Name -cne 'CaptureOne.exe' -or
        (($exe.Attributes -bor $exe.Directory.Attributes) -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Pass the genuine CaptureOne.exe in an ordinary candidate application directory.'
    }

    # This exact candidate comes from Capture One's signed 16.7.8 installer.
    # The production host manifest is deliberately independent of this fixture.
    $version = '16.7.8.3529'
    $exeHash = 'D0A1257A2BC0F94FCF18066AD16FAD25EC607323052CE620389B3B7D57D65E91'
    $assemblyHash = '805CBE9C9B5884DB5F61B9DDFE6E7F83441DE54B321972BA88672BD1EF90EADA'
    if ($exe.VersionInfo.FileVersion -cne $version -or (Get-FileHash -LiteralPath $exe.FullName).Hash -cne $exeHash) {
        throw "The compatibility fixture requires exact Windows Capture One $version."
    }
    $assembly = Get-Item -LiteralPath (Join-Path $exe.DirectoryName 'CaptureOne.dll') -Force
    if ($assembly.PSIsContainer -or ($assembly.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        (Get-FileHash -LiteralPath $assembly.FullName).Hash -cne $assemblyHash) {
        throw 'The candidate Capture One assembly does not match the verified installer.'
    }
    [pscustomobject]@{
        Version = $version
        Directory = $exe.DirectoryName
        ExecutablePath = $exe.FullName
        ExecutableSha256 = $exeHash
        AssemblyPath = $assembly.FullName
        AssemblySha256 = $assemblyHash
    }
}

function Read-HostCompatibilityFixture([string] $Directory, [string] $ExecutablePath) {
    $candidate = Get-CaptureOneCompatibilityCandidate $ExecutablePath
    $root = Get-Item -LiteralPath $Directory -Force
    if (!$root.PSIsContainer -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Pass the ordinary directory emitted by Build-HostCompatibilityFixture.ps1.'
    }
    $receipt = Get-Content -Raw -LiteralPath (Join-Path $root.FullName 'host-compatibility-fixture.json') | ConvertFrom-Json
    $panel = Get-Item -LiteralPath (Join-Path $root.FullName 'NativePanel.dll') -Force
    if ($receipt.SchemaVersion -ne 1 -or $receipt.Status -cne 'built-for-licensed-qa' -or
        $receipt.HostVersion -cne $candidate.Version -or $receipt.Architecture -cne 'x64' -or
        $receipt.ExecutableSha256 -cne $candidate.ExecutableSha256 -or $receipt.AssemblySha256 -cne $candidate.AssemblySha256 -or
        $panel.PSIsContainer -or ($panel.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        (Get-FileHash -LiteralPath $panel.FullName).Hash -cne $receipt.PanelSha256) {
        throw 'The QA panel or candidate identity does not match its build receipt.'
    }
    return $panel
}
