# Shared by installer and launcher. The state files live beside the exact install.
function Get-UpgradeFiles([string] $install) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($install.ToUpperInvariant())
        $key = [BitConverter]::ToString($sha.ComputeHash($bytes)).Replace('-', '').Substring(0, 24).ToLowerInvariant()
    } finally { $sha.Dispose() }
    $parent = Split-Path -LiteralPath $install
    $name = '.film-tools-upgrade-' + $key
    $base = Join-Path $parent $name
    return @{ Lock = $base + '.lock'; Journal = $base + '.json'; Base = $base; Name = $name; Parent = $parent }
}

function Read-UpgradeReceipt([IO.FileInfo] $item) {
    if (!$item -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $item.Length -gt 4096) {
        throw 'Expected a small regular upgrade receipt.'
    }
    $receipt = [IO.File]::ReadAllText($item.FullName) | ConvertFrom-Json
    if ($receipt.schema -ne 1 -or $receipt.id -cnotmatch '^[a-f0-9]{32}$' -or
        $receipt.previousMarker -cnotmatch '^[A-F0-9]{64}$' -or $receipt.candidateReceipt -cnotmatch '^[A-F0-9]{64}$' -or
        ($receipt.previousOwnedReceipt -and $receipt.previousOwnedReceipt -cnotmatch '^[A-F0-9]{64}$')) {
        throw "Could not verify the upgrade receipt at $($item.FullName)."
    }
    return $receipt
}

function Find-PlainUpgradeItem([string] $root, [string] $relative) {
    if (!$root) { return }
    $item = Get-Item -LiteralPath $root -Force -ErrorAction SilentlyContinue
    foreach ($part in $relative.Split('\')) {
        if (!$item -or !$item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return }
        $item = Get-Item -LiteralPath (Join-Path $item.FullName $part) -Force -ErrorAction SilentlyContinue
    }
    if ($item -and !($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { return $item }
}

function Get-LaunchUpgradeCandidate([string] $install) {
    $marker = Find-PlainUpgradeItem $install '.film-tools-install'
    if (!$marker -or $marker.PSIsContainer -or $marker.Length -gt 128 -or
        [IO.File]::ReadAllText($marker.FullName) -cne 'Spektrafilm for Capture One portable installation') { return }
    $owned = Find-PlainUpgradeItem $install '.film-tools-owned.json'
    if (!$owned -or $owned.PSIsContainer) { return }
    $files = Get-UpgradeFiles $install
    $stateFiles = @(Get-ChildItem -LiteralPath $files.Parent -Force -File -Filter ($files.Name + '.*-*.json') |
        Where-Object {$_.Name -cmatch '^\.film-tools-upgrade-[a-f0-9]{24}\.(pending|ready)-[a-f0-9]{32}\.json$'})
    if (!$stateFiles.Count) { return }
    $digest = (Get-FileHash -LiteralPath $owned.FullName -Algorithm SHA256).Hash
    foreach ($file in $stateFiles) {
        try { $receipt = Read-UpgradeReceipt $file } catch { Write-Warning $_.Exception.Message; continue }
        if ($receipt.candidateReceipt -ceq $digest -and
            $file.Name -cin @("$($files.Name).pending-$($receipt.id).json", "$($files.Name).ready-$($receipt.id).json")) {
            return @{ Item = $file; Hash = $digest; Receipt = $receipt; Files = $files }
        }
    }
}

function Test-PreviousUpgradeContent([string] $old, [string] $install, [string] $archive, [hashtable] $owned, [string] $relative) {
    $directory = if ($relative) { Join-Path $old $relative } else { $old }
    $children = @(Get-ChildItem -LiteralPath $directory -Force)
    if ($relative -and !$children.Count) {
        return (Find-PlainUpgradeItem $install $relative) -is [IO.DirectoryInfo] -or
            (Find-PlainUpgradeItem $archive $relative) -is [IO.DirectoryInfo]
    }
    foreach ($item in $children) {
        if (!$relative -and $item.Name -in '.film-tools-install', '.film-tools-owned.json') { continue }
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { return $false }
        $name = if ($relative) { Join-Path $relative $item.Name } else { $item.Name }
        if ($item.PSIsContainer) {
            if (!(Test-PreviousUpgradeContent $old $install $archive $owned $name)) { return $false }
            continue
        }
        $hash = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash
        if ($owned[$name] -eq $hash) { continue }
        $matched = $false
        foreach ($location in @($install, $archive)) {
            $copy = Find-PlainUpgradeItem $location $name
            if ($copy -and !$copy.PSIsContainer -and (Get-FileHash -LiteralPath $copy.FullName -Algorithm SHA256).Hash -eq $hash) {
                $matched = $true
                break
            }
        }
        if (!$matched) { return $false }
    }
    return $true
}

function Complete-SpektrafilmUpgrade([string] $install, [hashtable] $candidate, $launch) {
    if ($launch.Phase -cne 'Ready' -or $launch.Export -cne 'Connected' -or $launch.Viewer -ceq 'ProcessingError') { return }
    $lockFile = Get-Item -LiteralPath $candidate.Files.Lock -Force
    if ($lockFile.PSIsContainer -or ($lockFile.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The installation lock path is not an ordinary file.'
    }
    $locked = [IO.File]::Open($lockFile.FullName, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $current = Get-LaunchUpgradeCandidate $install
        if (!$current -or $current.Hash -cne $candidate.Hash -or $current.Receipt.id -cne $candidate.Receipt.id) { return }
        $id = $current.Receipt.id
        $previous = Join-Path $current.Files.Parent ('.film-tools-previous-' + $id)
        $ready = $current.Files.Base + '.ready-' + $id + '.json'
        if ($current.Item.FullName -ine $ready) { [IO.File]::Move($current.Item.FullName, $ready) }
        if (!(Test-Path -LiteralPath $previous)) { [IO.File]::Delete($ready); return }
        $marker = Find-PlainUpgradeItem $previous '.film-tools-install'
        if (!$marker -or $marker.PSIsContainer -or
            (Get-FileHash -LiteralPath $marker.FullName -Algorithm SHA256).Hash -cne $current.Receipt.previousMarker) {
            Write-Warning "The previous installation could not be verified and remains at $previous."
            return
        }
        $owned = @{}
        if ($current.Receipt.previousOwnedReceipt) {
            $oldReceipt = Find-PlainUpgradeItem $previous '.film-tools-owned.json'
            if (!$oldReceipt -or $oldReceipt.PSIsContainer -or
                (Get-FileHash -LiteralPath $oldReceipt.FullName -Algorithm SHA256).Hash -cne $current.Receipt.previousOwnedReceipt) {
                Write-Warning "The previous ownership receipt could not be verified. The installation remains at $previous."
                return
            }
            $parsed = [IO.File]::ReadAllText($oldReceipt.FullName) | ConvertFrom-Json
            if ($parsed.schema -eq 1) {
                foreach ($file in $parsed.files) {
                    if ($file.path -is [string] -and $file.sha256 -match '^[a-fA-F0-9]{64}$') {
                        $owned[$file.path.Replace('/', '\')] = $file.sha256
                    }
                }
            }
        }
        $archive = Find-PlainUpgradeItem $install ("Recovered files\" + $id)
        if (!(Test-PreviousUpgradeContent $previous $install $archive.FullName $owned '')) {
            Write-Warning "Custom files could not be verified in the current installation. The previous copy remains at $previous."
            return
        }
        [IO.Directory]::Delete($previous, $true)
        [IO.File]::Delete($ready)
        Write-Output 'Confirmed the upgraded Capture One panel and retired the verified previous runtime.'
    } finally { $locked.Dispose() }
}
