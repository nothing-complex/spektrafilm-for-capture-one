# Shared build/package integrity checks. Compatible with Windows PowerShell 5.1.
function Get-ReleaseRuntimeFiles {
    $paths = @('panel/NativePanel.dll','renderer/SpektraStillRenderer.exe',
        'renderer/SpektraHanatos2025Spectra.f32','renderer/SpektraOutputGamutCompression.f32','renderer/parameter-capabilities.json',
        'stocks.json','capture-one-hosts.json','profiles/preview/Linear Rec.2020.icc','profiles/preview/ProPhoto RGB.icc')
    foreach ($shader in @('Copy','CurveDevelop','Diffusion','Dir','FilmExposure','FormatConvert','Grain','Halation','PrintScan','ScannerPost')) {
        $paths += "renderer/shaders/Spektra$shader.comp.spv"
    }
    return $paths
}

function Get-CaptureOneHostProfiles([string] $Root) {
    $path = Join-Path $Root 'capture-one-hosts.json'
    $file = Get-Item -LiteralPath $path -Force
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -or $file.Length -gt 64KB) {
        throw 'The Capture One host compatibility manifest must be a small regular file.'
    }
    $profiles = [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json
    if ($profiles.SchemaVersion -ne 1 -or @($profiles.Hosts).Count -lt 1 -or @($profiles.Hosts).Count -gt 32) {
        throw 'The Capture One host compatibility manifest has an unsupported schema.'
    }
    $seen = @{}
    foreach ($profile in $profiles.Hosts) {
        $key = "$($profile.FileVersion):$($profile.Architecture):$($profile.AssemblySha256)"
        if ($profile.FileVersion -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $profile.Architecture -cne 'x64' -or
            $profile.AssemblySha256 -notmatch '^[0-9a-fA-F]{64}$' -or @($profile.Capabilities).Count -lt 1 -or
            @($profile.Capabilities).Count -gt 20 -or $seen.ContainsKey($key)) {
            throw 'The Capture One host compatibility manifest contains a malformed or duplicate profile.'
        }
        $names = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
        foreach ($name in $profile.Capabilities) {
            if ($name -isnot [string] -or [string]::IsNullOrWhiteSpace($name) -or $name.Length -gt 64 -or !$names.Add($name)) {
                throw 'The Capture One host profile contains an invalid capability list.'
            }
        }
        $seen[$key] = $true
    }
    return $profiles.Hosts
}

function Find-CaptureOneHost($Profiles, [string] $Version, [string] $Architecture, [string] $Sha256) {
    return $Profiles | Where-Object { $_.FileVersion -ceq $Version -and $_.Architecture -ceq $Architecture -and $_.AssemblySha256 -ieq $Sha256 } |
        Select-Object -First 1
}

function New-ReleaseCapabilityMarkdown([string] $Root, [string] $ParameterContract) {
    $hosts = @(Get-CaptureOneHostProfiles $Root)
    $relative = 'renderer/parameter-capabilities.json'
    if ((Get-ReleaseFileRecord $Root $relative).bytes -gt 256KB) { throw 'Renderer capability description is too large.' }
    $renderer = [IO.File]::ReadAllText((Join-Path $Root $relative)) | ConvertFrom-Json
    if ($renderer.schemaVersion -ne 1 -or $renderer.rendererProtocol -ne 2 -or
        $renderer.parameterContract -notmatch '^[a-fA-F0-9]{64}$' -or $renderer.parameterContract -ine $ParameterContract -or
        @($renderer.parameters).Count -lt 1 -or @($renderer.parameters).Count -gt 256) {
        throw 'Renderer capability documentation requires the exact compiled parameter contract.'
    }
    $lines = [Collections.Generic.List[string]]::new()
    $lines.AddRange([string[]]@(
        '# Spektrafilm for Capture One capabilities',
        '',
        'Generated for this package from the verified Capture One host manifest and the compiled Vulkan renderer.',
        '',
        '## Capture One hosts',
        '',
        '| Capture One version | Platform | Verified integration capabilities |',
        '| --- | --- | --- |'
    ))
    foreach ($hostProfile in $hosts) {
        if (@($hostProfile.Capabilities | Where-Object { $_ -cnotmatch '^[a-z0-9-]+$' }).Count) {
            throw 'Capture One capability labels must be plain manifest identifiers.'
        }
        $lines.Add(('| {0} | Windows {1} | {2} |' -f $hostProfile.FileVersion, $hostProfile.Architecture, ($hostProfile.Capabilities -join ', ')))
    }
    $lines.AddRange([string[]]@(
        '',
        '## Vulkan worker options',
        '',
        'These are worker options and worker defaults. The Capture One panel applies its own defaults and offers controls that fit the still-image workflow. See the README for input, export and Viewer behavior.',
        '',
        ('Parameter contract `{0}` on worker protocol {1}.' -f $renderer.parameterContract, $renderer.rendererProtocol),
        '',
        '| Option | Type | Default | Range or choices |',
        '| --- | --- | --- | --- |'
    ))
    $format = [Globalization.CultureInfo]::InvariantCulture
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($parameter in $renderer.parameters) {
        $key = $parameter.key
        if ($key -cnotmatch '^[a-z0-9-]+$' -or !$seen.Add($key)) { throw 'Invalid or duplicate renderer option name.' }
        $default = [Convert]::ToString($parameter.default, $format)
        switch -CaseSensitive ($parameter.kind) {
            { $_ -in 'number','integer' } {
                if ($parameter.minimum -isnot [ValueType] -or $parameter.maximum -isnot [ValueType] -or
                    $parameter.default -isnot [ValueType] -or $parameter.default -lt $parameter.minimum -or
                    $parameter.default -gt $parameter.maximum) { throw "Invalid number contract for $key." }
                $range = '{0} to {1}' -f [Convert]::ToString($parameter.minimum, $format), [Convert]::ToString($parameter.maximum, $format)
            }
            'boolean' {
                if ($parameter.default -isnot [bool]) { throw "Invalid boolean contract for $key." }
                $default = $default.ToLowerInvariant()
                $range = 'true, false'
            }
            'choice' {
                if ($default -cnotin $parameter.choices -or @($parameter.choices | Where-Object { $_ -cnotmatch '^[a-z0-9._-]+$' }).Count) {
                    throw "Invalid choice contract for $key."
                }
                $range = $parameter.choices -join ', '
            }
            default { throw "Unknown renderer option type for $key." }
        }
        $lines.Add(('| `--{0}` | {1} | {2} | {3} |' -f $key, $parameter.kind, $default, $range))
    }
    return ($lines -join "`n") + "`n"
}

function Get-WindowsExecutableArchitecture([string] $Path) {
    $reader = New-Object IO.BinaryReader([IO.File]::OpenRead($Path))
    try {
        if ($reader.BaseStream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) { return $null }
        $reader.BaseStream.Position = 0x3c
        $header = $reader.ReadUInt32()
        if ($header -lt 64 -or $header -gt $reader.BaseStream.Length - 6) { return $null }
        $reader.BaseStream.Position = $header
        if ($reader.ReadUInt32() -ne 0x00004550) { return $null }
        switch ($reader.ReadUInt16()) {
            0x8664 { return 'x64' }
            0x014c { return 'x86' }
            0xaa64 { return 'arm64' }
        }
        return $null
    } finally { $reader.Dispose() }
}

function Get-ReleaseFileRecord([string] $Root, [string] $Relative) {
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative.Contains(':') -or $Relative -match '(^|[\\/])\.\.([\\/]|$)') {
        throw "Invalid manifest path: $Relative"
    }
    $path = [IO.Path]::GetFullPath((Join-Path $base $Relative))
    if (!$path.StartsWith($base + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Manifest path escapes its root: $Relative" }
    $file = Get-Item -LiteralPath $path -Force
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Expected plain manifest file: $Relative" }
    $parent = $file.Directory
    while ($parent -and ![string]::Equals($parent.FullName,$base,[StringComparison]::OrdinalIgnoreCase)) {
        if ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Linked manifest directory: $Relative" }
        $parent = $parent.Parent
    }
    return [ordered]@{ path=$Relative.Replace('\','/'); bytes=$file.Length; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}

function Get-ReleaseSourceIdentity([string] $Root) {
    $commit = & git -C $Root rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Build provenance requires a Git checkout.' }
    $dirty = [bool](& git -C $Root status --porcelain=v1)
    $paths = @(& git -C $Root -c core.quotepath=false ls-files --cached --others --exclude-standard)
    if ($LASTEXITCODE -ne 0) { throw 'Could not enumerate source files.' }
    $records = @($paths | Sort-Object -Unique | ForEach-Object { Get-ReleaseFileRecord $Root $_ })
    $identityText = ($records | ForEach-Object { $_.path + ':' + $_.sha256 }) -join "`n"
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest=([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($identityText)))).Replace('-','').ToLowerInvariant() }
    finally { $sha.Dispose() }
    return [ordered]@{ commit=$commit.Trim(); dirty=$dirty; treeDigest=$digest }
}

function Test-ReleaseManifest([string] $Root, [string] $Name = 'build-manifest.json') {
    $record = Get-ReleaseFileRecord $Root $Name
    if ($record.bytes -gt 8MB) { throw 'Release manifest is too large.' }
    $manifest = [IO.File]::ReadAllText((Join-Path $Root $Name)) | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1 -or @($manifest.files).Count -eq 0) { throw "Unsupported or empty $Name." }
    $seen = @{}
    foreach ($expected in $manifest.files) {
        if ($expected.path -isnot [string] -or $expected.sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid release file record.' }
        $key = $expected.path.Replace('\','/')
        if ($seen.ContainsKey($key)) { throw "Duplicate manifest path: $key" }
        $seen[$key]=$expected
        $actual=Get-ReleaseFileRecord $Root $expected.path
        if ($actual.bytes -ne $expected.bytes -or $actual.sha256 -ne $expected.sha256) { throw "Release file does not match its build: $($expected.path). Extract or rebuild the complete release." }
    }
    if ($Name -eq 'build-manifest.json') {
        if ($manifest.kind -ne 'build' -or $manifest.rendererProtocol -ne 2 -or $manifest.parameterContract -notmatch '^[a-fA-F0-9]{64}$') {
            throw 'The renderer build contract is missing or incompatible.'
        }
        foreach ($required in Get-ReleaseRuntimeFiles) {
            if (!$seen.ContainsKey($required)) { throw "Build manifest omits $required." }
            if ($seen[$required].bytes -le 0) { throw "Build manifest has an empty runtime file: $required." }
        }
    }
    return $manifest
}

function Write-ReleaseBuildManifest([string] $Root, $SourceIdentity, [string] $CaptureOneDirectory, [string] $SmokeStatus, [string] $PythonPath, [string] $VulkanSdkPath) {
    $hostFiles = @('CaptureOne.dll','P1.AppCore.dll','P1.C1.Core.dll','P1.C1.Framework.dll','P1.C1.ImageCoreWrapper.dll','P1.C1.ModelEntities.dll','P1.C1.ViewModels.dll')
    $hostRecords = @($hostFiles | ForEach-Object { Get-ReleaseFileRecord $CaptureOneDirectory $_ })
    $exe = Join-Path $CaptureOneDirectory 'CaptureOne.exe'
    $version = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
    $profiles = @(Get-CaptureOneHostProfiles $Root)
    $verified = Find-CaptureOneHost $profiles $version (Get-WindowsExecutableArchitecture $exe) $hostRecords[0].sha256
    if (!$verified) { throw "Capture One $version does not match a verified host profile. A licensed host matrix is required before staging it." }
    $manifest = [ordered]@{
        schemaVersion=1; kind='build'; createdUtc=[DateTime]::UtcNow.ToString('o'); source=$SourceIdentity
        rendererProtocol=2
        parameterContract=(Get-FileHash -LiteralPath (Join-Path $Root 'upstream-ofx/src/SpektraParameters.h') -Algorithm SHA256).Hash.ToLowerInvariant()
        host=[ordered]@{ version=$version; architecture=$verified.Architecture; capabilities=$verified.Capabilities; files=$hostRecords }
        tools=[ordered]@{ dotnet=(& dotnet --version); cmake=((& cmake --version | Select-Object -First 1)); python=(& $PythonPath --version); vulkanSdk=$VulkanSdkPath }
        validation=[ordered]@{ rendererSmoke=$SmokeStatus; panelBuild='passed' }
        files=@(Get-ReleaseRuntimeFiles | ForEach-Object { Get-ReleaseFileRecord $Root $_ })
    }
    $destination = Join-Path $Root 'build-manifest.json'
    $temporary = $destination + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    [IO.File]::WriteAllText($temporary,($manifest | ConvertTo-Json -Depth 8),(New-Object Text.UTF8Encoding($false)))
    try { Move-Item -LiteralPath $temporary -Destination $destination -Force }
    finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
    [void](Test-ReleaseManifest $Root)
    Write-Output $destination
}
