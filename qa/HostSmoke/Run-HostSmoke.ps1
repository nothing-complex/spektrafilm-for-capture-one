[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $SourceCatalog,
    [string] $CaptureOnePath='C:\Program Files\Capture One\Capture One\CaptureOne.exe',
    [string] $TestHookPath=(Join-Path $PSScriptRoot 'bin\Release\net8.0-windows\HostSmoke.dll'),
    [string] $PackagedRuntime,
    [string] $UpgradeFromPackage,
    [Alias('ProofingSettingsConfig')][string] $NativeSettingsConfig,
    [string] $SavedLooksBackup,
    [switch] $SavedLooksFromUpdatedCatalog,
    [switch] $CatalogIsolation,
    [switch] $ConcurrentCatalogs,
    [switch] $SessionIsolation,
    [switch] $ViewerLayouts,
    [switch] $FloatingWorkspaceLayout,
    [switch] $NativeWorkspaceSwitch,
    [ValidateRange(0,200)][int] $ViewerChurn=0,
    [ValidateSet('1200','2048','4096','native')][string] $ViewerChurnResolution='2048',
    [ValidateRange(0,600)][int] $ViewerTelemetrySeconds=0,
    [ValidateRange(0,200)][int] $NativeLifetimeCycles=0,
    [switch] $ViewerNativeBaseline,
    [switch] $DurableSliderEdits,
    [switch] $NativeLookBatch,
    [switch] $NativeLookBatchCrash,
    [switch] $NativeVariantLineage,
    [string] $MetadataFixtureRoot,
    [switch] $SkipRestart,
    [switch] $CheckOrdinaryLaunch,
    [switch] $ShowTool,
    [switch] $ColorMatrix,
    [switch] $NativeSourceMutations,
    [switch] $ViewerStress,
    [switch] $PanelInventory,
    [switch] $NativeEffectExport,
    [switch] $NativeEffectExportMatrix,
    [switch] $NativeEffectGrain,
    [switch] $NativeEffectCompression,
    [switch] $NativeEffectOutputProfiles,
    [switch] $NativeEffectFolders,
    [switch] $NativeEffectPanelModes,
    [switch] $NativeEffectSourceRevision,
    [switch] $NativeEffectClone,
    [ValidateSet('DevelopingSource','RenderingFilm','Encoding')][string] $NativeExportInterruption,
    [switch] $OriginalAvailabilityProbe,
    [switch] $NativeDevelopmentDeadline,
    [string] $DevelopmentLeaseFixtureDirectory,
    [string] $DeviceLossFixtureDirectory,
    [string] $CalibrationChart,
    [string] $ResultsDirectory
)
$ErrorActionPreference='Stop'
if ($NativeLookBatchCrash) { $NativeLookBatch=$true }
if ($NativeVariantLineage -and (!$NativeSettingsConfig -or $SkipRestart -or $PackagedRuntime -or $UpgradeFromPackage -or
    $CatalogIsolation -or $ConcurrentCatalogs -or $DurableSliderEdits -or $NativeLookBatch -or $ViewerChurn -or
    $ViewerLayouts -or $ColorMatrix -or $NativeEffectExport -or $CheckOrdinaryLaunch)) {
    throw 'Native variant lineage needs its own disposable launch and restart with a verified preferences backup.'
}
if ($NativeLookBatch -and (!$NativeSettingsConfig -or $SkipRestart -or $PackagedRuntime -or $UpgradeFromPackage -or
    $CatalogIsolation -or $ConcurrentCatalogs -or $DurableSliderEdits -or $ViewerLayouts -or $ViewerChurn -or
    $ColorMatrix -or $NativeEffectExport -or $CheckOrdinaryLaunch)) {
    throw 'Native batch copy needs its own disposable launch and restart with a verified preferences backup.'
}
if ($SavedLooksFromUpdatedCatalog -and !$SavedLooksBackup) { throw 'An updated catalog fixture needs its saved-look backup.' }
if ($SavedLooksBackup -and (!$NativeSettingsConfig -or $UpgradeFromPackage -or $CatalogIsolation -or $ConcurrentCatalogs -or $SessionIsolation)) {
    throw 'Moved-catalog look verification requires a single disposable catalog and a native preferences backup.'
}
if ($NativeEffectClone) { $NativeEffectGrain=$true; $SkipRestart=$true }
$isolatedEffects=@($NativeEffectCompression,$NativeEffectOutputProfiles,$NativeEffectFolders,$NativeEffectPanelModes,$NativeEffectSourceRevision) | Where-Object { $_ }
if ($isolatedEffects -and (!$NativeSettingsConfig -or $NativeEffectGrain -or $NativeEffectExportMatrix -or
    $UpgradeFromPackage -or @($isolatedEffects).Count -ne 1)) {
    throw 'A native recipe or workspace fixture needs its own selected recipe and a verified preferences backup.'
}
if ($NativeEffectGrain -and $NativeEffectExportMatrix) {
    throw 'The native grain comparison uses one RAW in its own effects Export fixture.'
}
if ($NativeEffectExportMatrix -or $NativeEffectGrain -or $isolatedEffects) { $NativeEffectExport=$true }
if (($FloatingWorkspaceLayout -or $NativeWorkspaceSwitch) -and (!$NativeSettingsConfig -or $SkipRestart -or $UpgradeFromPackage -or $ViewerLayouts -or
    ($FloatingWorkspaceLayout -and $NativeWorkspaceSwitch) -or
    $NativeEffectExport -or $NativeExportInterruption -or $CatalogIsolation -or $ConcurrentCatalogs -or $SessionIsolation -or
    $ViewerChurn -or $ViewerTelemetrySeconds -or $NativeLifetimeCycles -or $ViewerStress -or $PanelInventory -or
    $SavedLooksBackup -or $DurableSliderEdits -or $MetadataFixtureRoot -or $CalibrationChart -or $ColorMatrix -or
    $NativeSourceMutations -or $OriginalAvailabilityProbe -or $NativeDevelopmentDeadline -or
    $DevelopmentLeaseFixtureDirectory -or $DeviceLossFixtureDirectory)) {
    throw 'Workspace verification requires a separate disposable catalog and native preferences backup.'
}
if ($NativeWorkspaceSwitch) { $SkipRestart=$true }
if ($NativeExportInterruption -and (!$NativeSettingsConfig -or $SkipRestart -or $NativeEffectExport -or $ColorMatrix -or
    $CatalogIsolation -or $ConcurrentCatalogs -or $SessionIsolation -or $ViewerLayouts -or $ViewerChurn -or
    $ViewerTelemetrySeconds -or $NativeLifetimeCycles -or $DurableSliderEdits -or $MetadataFixtureRoot -or
    $ViewerStress -or $PanelInventory -or $CheckOrdinaryLaunch -or $CalibrationChart -or $UpgradeFromPackage -or
    $NativeDevelopmentDeadline -or $DevelopmentLeaseFixtureDirectory -or $DeviceLossFixtureDirectory)) {
    throw 'The abrupt export interruption fixture requires its own disposable first launch, restart and native preferences backup.'
}
if ($ViewerTelemetrySeconds) {
    if ($ViewerTelemetrySeconds -lt 20 -or !$NativeSettingsConfig -or $UpgradeFromPackage -or $CatalogIsolation -or
        $ConcurrentCatalogs -or $SessionIsolation -or $ViewerLayouts -or $ViewerChurn -or $DurableSliderEdits -or
        $MetadataFixtureRoot -or $CheckOrdinaryLaunch -or $ColorMatrix -or $NativeSourceMutations -or $ViewerStress -or
        $PanelInventory -or $NativeEffectExport -or $OriginalAvailabilityProbe -or $NativeDevelopmentDeadline -or
        $DevelopmentLeaseFixtureDirectory -or $DeviceLossFixtureDirectory -or $CalibrationChart) {
        throw 'The Viewer telemetry fixture requires a separate disposable launch of at least 20 seconds and a native preferences backup.'
    }
    $SkipRestart=$true
}
if ($NativeLifetimeCycles) {
    if (!$NativeSettingsConfig -or $UpgradeFromPackage -or $CatalogIsolation -or $ConcurrentCatalogs -or $SessionIsolation -or
        $ViewerLayouts -or $ViewerChurn -or $DurableSliderEdits -or $MetadataFixtureRoot -or $CheckOrdinaryLaunch -or
        $ColorMatrix -or $NativeSourceMutations -or $ViewerStress -or $PanelInventory -or $NativeEffectExport -or
        $OriginalAvailabilityProbe -or $NativeDevelopmentDeadline -or $DevelopmentLeaseFixtureDirectory -or
        $DeviceLossFixtureDirectory -or $CalibrationChart) {
        throw 'The native tool lifetime fixture requires its own disposable launch and native preferences backup.'
    }
    $SkipRestart=$true
}
if ($DevelopmentLeaseFixtureDirectory) {
    if (!$NativeSettingsConfig -or $PackagedRuntime -or $UpgradeFromPackage -or $NativeDevelopmentDeadline -or
        $DeviceLossFixtureDirectory -or $NativeEffectExport -or $ColorMatrix -or $NativeSourceMutations -or $CatalogIsolation -or
        $SessionIsolation -or $ViewerLayouts -or $ViewerChurn -or $DurableSliderEdits -or $MetadataFixtureRoot -or
        $ViewerStress -or $PanelInventory -or $CheckOrdinaryLaunch -or $CalibrationChart) {
        throw 'The development lease fixture requires its own disposable launch and native preferences backup.'
    }
    $leaseFixture=Get-Item -LiteralPath $DevelopmentLeaseFixtureDirectory -Force
    if (!$leaseFixture.PSIsContainer -or ($leaseFixture.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The development lease fixture must use a regular isolated directory.'
    }
    $leasePanel=Get-Item -LiteralPath (Join-Path $leaseFixture.FullName 'NativePanel.dll') -Force
    $leaseReceipt=Join-Path $leaseFixture.FullName 'development-lease-fixture.sha256'
    $leaseHash=[IO.File]::ReadAllText($leaseReceipt).Trim()
    if ($leasePanel.PSIsContainer -or ($leasePanel.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        $leaseHash -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $leasePanel.FullName).Hash -ine $leaseHash) {
        throw 'The test panel must match its isolated development fixture receipt.'
    }
    $SkipRestart=$true
}
if ($OriginalAvailabilityProbe -and !$CalibrationChart) {
    throw 'The original availability fixture requires a private calibrated RAW chart.'
}
if ($NativeDevelopmentDeadline) {
    if (!$NativeSettingsConfig -or $NativeEffectExport -or $ColorMatrix -or $NativeSourceMutations -or $CatalogIsolation -or
        $ConcurrentCatalogs -or $ViewerLayouts -or $ViewerChurn -or $DurableSliderEdits -or $MetadataFixtureRoot -or
        $ViewerStress -or $PanelInventory -or $CheckOrdinaryLaunch -or $CalibrationChart -or $UpgradeFromPackage) {
        throw 'The native development deadline fixture requires its own launch and a native preferences backup.'
    }
    $SkipRestart=$true
}
if ($DeviceLossFixtureDirectory) {
    if (!$NativeSettingsConfig -or $PackagedRuntime -or $UpgradeFromPackage -or $NativeDevelopmentDeadline -or
        $NativeEffectExport -or $ColorMatrix -or $NativeSourceMutations -or $CatalogIsolation -or $ConcurrentCatalogs -or
        $ViewerLayouts -or $ViewerChurn -or $DurableSliderEdits -or $MetadataFixtureRoot -or $ViewerStress -or
        $PanelInventory -or $CheckOrdinaryLaunch -or $CalibrationChart) {
        throw 'The injected device-loss fixture requires its own development launch and native preferences backup.'
    }
    $fixtureDirectory=Get-Item -LiteralPath $DeviceLossFixtureDirectory -Force
    if (!$fixtureDirectory.PSIsContainer -or ($fixtureDirectory.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The device-loss worker must be in a regular fixture directory.'
    }
    $fixtureExecutable=Join-Path $fixtureDirectory.FullName 'SpektraStillRenderer.exe'
    $fixtureReceipt=Join-Path $fixtureDirectory.FullName 'device-loss-fixture.sha256'
    $expectedHash=[IO.File]::ReadAllText($fixtureReceipt).Trim()
    if ($expectedHash -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $fixtureExecutable).Hash -ine $expectedHash) {
        throw 'The injected worker does not match its disposable build receipt.'
    }
    $SkipRestart=$true
}
if ($NativeSourceMutations) {
    if (!$NativeSettingsConfig) { throw 'The native source mutation matrix requires a verified preferences backup.' }
    $ColorMatrix=$true
    $SkipRestart=$true
}
if ($ViewerStress -and (!$NativeSettingsConfig -or $ColorMatrix -or $CatalogIsolation -or $ConcurrentCatalogs -or
    $SessionIsolation -or $ViewerLayouts -or $ViewerChurn -or $DurableSliderEdits -or $MetadataFixtureRoot -or
    $CheckOrdinaryLaunch -or $CalibrationChart -or $UpgradeFromPackage)) {
    throw 'The Viewer stress fixture is a separate launch and requires a native preferences backup.'
}
if ($PanelInventory) {
    if (!$NativeSettingsConfig -or $CatalogIsolation -or $ConcurrentCatalogs -or $ViewerLayouts -or $ViewerChurn -or
        $DurableSliderEdits -or $MetadataFixtureRoot -or $ColorMatrix -or $ViewerStress -or $CheckOrdinaryLaunch -or
        $CalibrationChart -or $UpgradeFromPackage) {
        throw 'The panel inventory fixture is a separate launch and requires a native preferences backup.'
    }
    $SkipRestart=$true
}
if ($NativeEffectExport -and (!$NativeSettingsConfig -or $ColorMatrix -or $CatalogIsolation -or $ConcurrentCatalogs -or
    $ViewerLayouts -or $ViewerChurn -or $DurableSliderEdits -or $MetadataFixtureRoot -or $ViewerStress -or $PanelInventory -or
    $CheckOrdinaryLaunch -or $CalibrationChart -or $UpgradeFromPackage)) {
    throw 'The native effects export fixture is a separate launch and requires a native preferences backup.'
}
if ($UpgradeFromPackage -and (!$PackagedRuntime -or !$NativeSettingsConfig -or $CatalogIsolation -or $ConcurrentCatalogs -or
    $ViewerLayouts -or $ViewerChurn -or $DurableSliderEdits -or $MetadataFixtureRoot -or $ColorMatrix -or $CheckOrdinaryLaunch)) {
    throw 'The installed-upgrade fixture requires two extracted packages, a native preferences backup and a separate launch.'
}
if ($CatalogIsolation -and ($SkipRestart -or $CheckOrdinaryLaunch -or $ColorMatrix)) {
    throw 'The catalog isolation fixture controls all four launches.'
}
if ($ViewerLayouts -and (!$NativeSettingsConfig -or $CatalogIsolation -or $ColorMatrix -or $CheckOrdinaryLaunch)) {
    throw 'The Viewer layouts fixture requires a verified preferences backup and runs as a separate launch.'
}
if ($DurableSliderEdits -and ($CatalogIsolation -or $ViewerLayouts -or $SkipRestart -or $ColorMatrix -or $CheckOrdinaryLaunch)) {
    throw 'The durable slider fixture requires its own first launch and restart.'
}
if ($ConcurrentCatalogs) {
    if (!$NativeSettingsConfig -or $CatalogIsolation -or $ViewerLayouts -or $DurableSliderEdits -or $ColorMatrix -or $ViewerChurn -or $MetadataFixtureRoot -or $CheckOrdinaryLaunch) {
        throw 'The concurrent catalog fixture controls both disposable catalogs in one licensed launch.'
    }
    $SkipRestart=$true
}
if ($SessionIsolation -and !$ConcurrentCatalogs) {
    throw 'Session isolation extends the dedicated concurrent-catalog fixture.'
}
if ($ViewerChurn) {
    if (!$NativeSettingsConfig -or $CatalogIsolation -or $ViewerLayouts -or $DurableSliderEdits -or $ColorMatrix -or $CheckOrdinaryLaunch -or $MetadataFixtureRoot) {
        throw 'The Viewer churn probe requires a dedicated launch and a verified native settings backup.'
    }
    $SkipRestart=$true
}
if ($ViewerNativeBaseline -and !$ViewerChurn) {
    throw 'The native Viewer baseline uses the same navigation sequence as -ViewerChurn.'
}
if ($CalibrationChart) {
    if (!$NativeSettingsConfig -or $CatalogIsolation -or $ConcurrentCatalogs -or $ViewerLayouts -or $ViewerChurn -or
        $DurableSliderEdits -or $MetadataFixtureRoot -or $ColorMatrix -or $CheckOrdinaryLaunch -or $UpgradeFromPackage) {
        throw 'The calibration chart requires its own disposable catalog launch and native preferences backup.'
    }
    $chartSource = Get-Item -LiteralPath $CalibrationChart -Force
    $chartReference = Get-Item -LiteralPath (Join-Path $chartSource.Directory.FullName 'reference.json') -Force
    if ($chartSource.PSIsContainer -or $chartReference.PSIsContainer -or $chartSource.Extension -ine '.dng' -or
        $chartSource.Length -lt 1 -or $chartSource.Length -gt 32MB -or $chartReference.Length -gt 64KB -or
        ($chartSource.Attributes -bor $chartReference.Attributes) -band [IO.FileAttributes]::ReparsePoint) {
        throw 'Pass the regular DNG and reference.json generated by the calibration fixture.'
    }
    $SkipRestart = $true
}
if ($MetadataFixtureRoot) {
    if (!$NativeSettingsConfig -or $CatalogIsolation -or $ViewerLayouts -or $DurableSliderEdits -or $ColorMatrix -or $CheckOrdinaryLaunch) {
        throw 'The native metadata fixture requires a settings backup and a separate launch.'
    }
    $MetadataFixtureRoot=(Resolve-Path -LiteralPath $MetadataFixtureRoot).ProviderPath
    if (!(Test-Path -LiteralPath (Join-Path $MetadataFixtureRoot 'metadata-fixture.marker') -PathType Leaf)) {
        throw 'The metadata fixture requires a dedicated copy of the catalog and RAW files.'
    }
    $SkipRestart=$true
}
$root=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).ProviderPath
$tokens=$null
$parseErrors=$null
$launcher=[Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'Start-Spektrafilm.ps1'), [ref]$tokens, [ref]$parseErrors)
if($parseErrors.Count){throw 'Launcher syntax failed.'}
foreach($name in 'Read-SpektrafilmLaunchStatus','Wait-SpektrafilmPanel'){
    $definition=$launcher.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name}, $true)
    . ([scriptblock]::Create($definition.Extent.Text))
}
if(Get-Process CaptureOne -ErrorAction SilentlyContinue){throw 'Close Capture One before running the isolated host test.'}
$stabilityRoot=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CaptureOne\Stability\16.4.6.2414'
$markerNames=@('TerminatedWhileRunning.crh','TerminatedByBsodOrPower.crh')
function Get-CrashMarkers {
    if(Test-Path -LiteralPath $stabilityRoot){Get-ChildItem -LiteralPath $stabilityRoot -Recurse -File | Where-Object {$_.Name -in $markerNames}}
}
if(@(Get-CrashMarkers).Count){throw 'Capture One has pre-existing crash markers. Resolve its startup recovery before this test; pre-existing markers will not be changed.'}
function Clear-TestCrashMarkers([datetime] $started,[string] $phase) {
    if(Get-Process CaptureOne -ErrorAction SilentlyContinue){throw 'A Capture One process remains; crash markers will not be touched.'}
    $backup=Join-Path $folder ($phase+'-crash-markers-backup')
    foreach($marker in @(Get-CrashMarkers)){
        if($marker.LastWriteTimeUtc -lt $started){throw 'Crash marker predates this test; refusing cleanup.'}
        if(!$marker.FullName.StartsWith([IO.Path]::GetFullPath($stabilityRoot)+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or ($marker.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Unexpected crash marker path.'}
        [void][IO.Directory]::CreateDirectory($backup)
        $copy=Join-Path $backup ($marker.Directory.Name+'-'+$marker.Name)
        Copy-Item -LiteralPath $marker.FullName -Destination $copy
        if((Get-FileHash -LiteralPath $copy).Hash -ne (Get-FileHash -LiteralPath $marker.FullName).Hash){throw 'Crash marker backup failed.'}
        Remove-Item -LiteralPath $marker.FullName
    }
}
function Restore-TestCatalogPreferences([string] $profile,[string] $backup,[string[]] $catalogs,[string] $folder,
    [bool] $restoreWindowPreference=$false,[bool] $restoreWorkspacePreferences=$false) {
    if(Get-Process CaptureOne -ErrorAction SilentlyContinue){throw 'Capture One is still active. Its verified preference backup was retained.'}
    $original=(Get-FileHash -LiteralPath $backup).Hash
    $observed=(Get-FileHash -LiteralPath $profile).Hash
    $removedFixtureCatalogs=$false
    $rewroteWindowPreference=$false
    $rewroteWorkspacePreferences=$false
    if($original -ne $observed){
        $before=[xml](Get-Content -Raw -LiteralPath $backup)
        $container="//setting[@name='DocumentManagerSettings']/value/DocumentManagerSettings"
        $xmlPath="$container/RecentlyUsedDocuments/internalList/string | $container/LastOpenDocumentList/string"
        if(@($before.SelectNodes($xmlPath) | Where-Object {$catalogs -ccontains $_.InnerText}).Count){
            throw 'The disposable catalog was already in the settings backup.'
        }
        $captured=Join-Path $folder 'Capture One user.config.after-proofing'
        Copy-Item -LiteralPath $profile -Destination $captured
        if((Get-FileHash -LiteralPath $captured).Hash -ne $observed){throw 'Could not verify changed preferences.'}
        $after=[xml](Get-Content -Raw -LiteralPath $captured)
        $entries=@($after.SelectNodes($xmlPath) | Where-Object {$catalogs -ccontains $_.InnerText})
        $removedFixtureCatalogs=$entries.Count -gt 0
        $settingParent='/configuration/userSettings/P1.C1.ApplicationSettings'
        $settingNames=@()
        if ($restoreWindowPreference) { $settingNames+='MultipleSessions' }
        if ($restoreWorkspacePreferences) { $settingNames+=@('WorkspaceCustomSettings','WorkspaceActiveSettings') }
        $changed=@{}
        foreach ($name in $settingNames) {
            $path="$settingParent/setting[@name='$name']"
            $prior=$before.SelectSingleNode($path)
            if ($prior.OuterXml -cne $after.SelectSingleNode($path).OuterXml) { $changed[$name]=$prior }
        }
        $rewroteWindowPreference=$changed.ContainsKey('MultipleSessions')
        $rewroteWorkspacePreferences=$changed.ContainsKey('WorkspaceActiveSettings') -or $changed.ContainsKey('WorkspaceCustomSettings')
        function Restore-ScopedSettings([xml] $document) {
            foreach ($name in $settingNames) {
                if (!$changed.ContainsKey($name)) { continue }
                $existing=$document.SelectSingleNode("$settingParent/setting[@name='$name']")
                if ($changed[$name]) {
                    $node=$document.ImportNode($changed[$name],$true)
                    if ($existing) { [void]$existing.ParentNode.ReplaceChild($node,$existing) }
                    else {
                        $parent=$document.SelectSingleNode($settingParent)
                        if (!$parent) { throw 'The native settings group is missing.' }
                        [void]$parent.AppendChild($node)
                    }
                } elseif ($existing) {
                    $blank=$existing.PreviousSibling
                    if ($null -ne $blank -and $blank.NodeType -eq [Xml.XmlNodeType]::Whitespace) {
                        [void]$existing.ParentNode.RemoveChild($blank)
                    }
                    [void]$existing.ParentNode.RemoveChild($existing)
                }
            }
        }
        if(@($entries | Group-Object -Property {$_.ParentNode.LocalName}, InnerText | Where-Object Count -gt 1).Count){
            throw 'Several entries in one document list matched a single disposable catalog.'
        }
        if(!$removedFixtureCatalogs -and !$changed.Count){
            [ordered]@{before=$original;afterTest=$observed;restored=$observed;removedRecentCatalog=$false;restoredWindowPreference=$false;restoredWorkspacePreferences=$false} |
                ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $folder 'proof-settings-receipt.json')
            return
        }
        foreach($entry in $entries){[void]$entry.ParentNode.RemoveChild($entry)}
        Restore-ScopedSettings $after
        $id=[Guid]::NewGuid().ToString('N')
        $temp=$profile+'.'+$id+'.restore'
        $replaced=$profile+'.'+$id+'.previous'
        try {
            if($before.OuterXml -ceq $after.OuterXml){
                [IO.File]::Copy($backup,$temp)
            } else {
                # Restore the fixture catalog entries and scoped settings. Keep independent host changes.
                $preserved=[xml]::new()
                $preserved.PreserveWhitespace=$true
                $preserved.Load($captured)
                foreach($entry in @($preserved.SelectNodes($xmlPath) | Where-Object {$catalogs -ccontains $_.InnerText})){
                    $previous=$entry.PreviousSibling
                    if($null -ne $previous -and $previous.NodeType -eq [Xml.XmlNodeType]::Whitespace){
                        [void]$entry.ParentNode.RemoveChild($previous)
                    }
                    [void]$entry.ParentNode.RemoveChild($entry)
                }
                Restore-ScopedSettings $preserved
                $preserved.Save($temp)
                $candidate=[xml](Get-Content -Raw -LiteralPath $temp)
                if($candidate.OuterXml -cne $after.OuterXml){throw 'The candidate did not preserve other Capture One preferences.'}
            }
            $expected=(Get-FileHash -LiteralPath $temp).Hash
            if((Get-FileHash -LiteralPath $profile).Hash -ne $observed -or (Get-Process CaptureOne -ErrorAction SilentlyContinue)){
                throw 'Preferences changed again before restoration.'
            }
            [IO.File]::Replace($temp,$profile,$replaced,$true)
            if((Get-FileHash -LiteralPath $profile).Hash -ne $expected -or (Get-FileHash -LiteralPath $replaced).Hash -ne $observed){
                throw 'Restored preferences did not match the verified snapshot.'
            }
            [IO.File]::Delete($replaced)
        } finally { if([IO.File]::Exists($temp)){[IO.File]::Delete($temp)} }
    }
    [ordered]@{before=$original;afterTest=$observed;restored=(Get-FileHash -LiteralPath $profile).Hash;removedRecentCatalog=$removedFixtureCatalogs;restoredWindowPreference=$rewroteWindowPreference;restoredWorkspacePreferences=$rewroteWorkspacePreferences} |
        ConvertTo-Json | Set-Content -Encoding UTF8 -LiteralPath (Join-Path $folder 'proof-settings-receipt.json')
}
function Test-FixtureRawFiles([string] $root, $marker) {
    foreach ($entry in $marker.raw_sha256.PSObject.Properties) {
        if ([IO.Path]::GetFileName($entry.Name) -cne $entry.Name -or $entry.Value -notmatch '^[0-9a-f]{64}$') {
            throw 'The metadata fixture contains an invalid RAW receipt.'
        }
        $raw=Get-Item -LiteralPath (Join-Path (Join-Path $root 'Raw') $entry.Name)
        if ($raw.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The fixture RAW must be a private copy.' }
        if ((Get-FileHash -LiteralPath $raw.FullName).Hash -ne $entry.Value) { throw 'A metadata fixture RAW fingerprint changed.' }
    }
}
function Assert-CurrentManagedBuild([string] $binary, [string] $projectDirectory, [string[]] $additionalSources) {
    $assembly=Get-Item -LiteralPath $binary -ErrorAction SilentlyContinue
    $inputs=@(Get-ChildItem -LiteralPath $projectDirectory -File |
        Where-Object { $_.Extension -in '.cs','.csproj' })
    foreach($inputFile in $additionalSources){ $inputs+=Get-Item -LiteralPath $inputFile }
    if(!$assembly -or ($inputs | Where-Object { $_.LastWriteTimeUtc -gt $assembly.LastWriteTimeUtc } | Select-Object -First 1)){
        throw "The Release build in '$projectDirectory' is missing or older than its source. Run dotnet build -c Release for that project."
    }
}
$source=(Get-Item -LiteralPath $SourceCatalog).FullName
if([IO.Path]::GetExtension($source) -ine '.cocatalogdb'){throw 'A catalog database is required.'}
foreach($suffix in '-wal','-shm','-journal'){if(Test-Path -LiteralPath ($source+$suffix)){throw 'Source catalog has transaction sidecars; close it cleanly first.'}}
if ($MetadataFixtureRoot) {
    $metadataMarker=Get-Content -Raw -LiteralPath (Join-Path $MetadataFixtureRoot 'metadata-fixture.marker') | ConvertFrom-Json
    if ((Split-Path -Parent $source) -ne $MetadataFixtureRoot -or (Get-FileHash -LiteralPath $source).Hash -ne $metadataMarker.catalog_sha256) {
        throw 'The closed source catalog must be the verified metadata fixture copy.'
    }
    Test-FixtureRawFiles $MetadataFixtureRoot $metadataMarker
}
$runtimeSource=$root
$panelBuild=Join-Path $root 'NativePanel\bin\Release\net8.0-windows\NativePanel.dll'
if ($DevelopmentLeaseFixtureDirectory) { $panelBuild=$leasePanel.FullName }
$rendererBuild=Join-Path $root 'build\still-renderer\Release'
if ($DeviceLossFixtureDirectory) { $rendererBuild=$fixtureDirectory.FullName }
if ($PackagedRuntime) {
    $runtimeSource=(Resolve-Path -LiteralPath $PackagedRuntime).ProviderPath
    . (Join-Path $root 'ReleaseManifest.ps1')
    [void](Test-ReleaseManifest $runtimeSource)
    $panelBuild=Join-Path $runtimeSource 'panel\NativePanel.dll'
    $rendererBuild=Join-Path $runtimeSource 'renderer'
}
if (!$PackagedRuntime -and !$DevelopmentLeaseFixtureDirectory) {
    Assert-CurrentManagedBuild $panelBuild (Join-Path $root 'NativePanel') @((Join-Path $root 'capture-one-hosts.json'))
}
if (!$UpgradeFromPackage -and !$PSBoundParameters.ContainsKey('TestHookPath')) {
    Assert-CurrentManagedBuild $TestHookPath $PSScriptRoot @()
}
if(!(Test-Path -LiteralPath (Join-Path $rendererBuild 'parameter-capabilities.json') -PathType Leaf)){
    throw 'Run Build-Windows.ps1 to build the renderer and generate its parameter capabilities before testing the host.'
}
if(!$ResultsDirectory){$ResultsDirectory=Join-Path $root ('.test-results\host-'+[guid]::NewGuid().ToString('N'))}
$folder=[IO.Path]::GetFullPath($ResultsDirectory)
if(Test-Path -LiteralPath $folder){throw 'Use a new empty results directory.'}
[void][IO.Directory]::CreateDirectory($folder)
if ($NativeSettingsConfig) {
    $profile=(Get-Item -LiteralPath $NativeSettingsConfig).FullName
    if ([IO.Path]::GetFileName($profile) -ne 'user.config') { throw 'Pass the existing Capture One user.config to back up before testing.' }
    $backup=Join-Path $folder 'Capture One user.config.before-proofing'
    Copy-Item -LiteralPath $profile -Destination $backup
    if ((Get-FileHash -LiteralPath $profile).Hash -ne (Get-FileHash -LiteralPath $backup).Hash) {
        throw 'Could not verify the Capture One preferences backup before proof tests.'
    }
}
$nativeRecipes=$null
$recipeHashes=@{}
if ($NativeEffectCompression -or $NativeEffectOutputProfiles -or $NativeEffectFolders -or $NativeEffectSourceRevision) {
    $nativeRecipes=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CaptureOne\Recipes143'
    $recipeRoot=Get-Item -LiteralPath $nativeRecipes -Force
    if (!$recipeRoot.PSIsContainer -or ($recipeRoot.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The exact 16.4 native recipe directory was not found.'
    }
    $recipeBackup=Join-Path $folder 'native-recipes-before-test'
    [void][IO.Directory]::CreateDirectory($recipeBackup)
    foreach ($item in Get-ChildItem -LiteralPath $nativeRecipes -Force -Recurse) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unexpected redirected recipe path.' }
        if ($item.PSIsContainer) { continue }
        $relative=$item.FullName.Substring($recipeRoot.FullName.Length+1)
        $target=Join-Path $recipeBackup $relative
        [void][IO.Directory]::CreateDirectory((Split-Path -Parent $target))
        Copy-Item -LiteralPath $item.FullName -Destination $target
        $digest=(Get-FileHash -LiteralPath $target).Hash
        if ((Get-FileHash -LiteralPath $item.FullName).Hash -ne $digest) { throw 'Native recipe backup did not verify.' }
        $recipeHashes[$relative]=$digest
    }
    if (!$recipeHashes.Count) { throw 'Native recipe backup did not contain any files.' }
    $recipeHashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'native-recipe-backup.json')
}
function Test-NativeRecipeHashes([string] $phase) {
    if (!$nativeRecipes) { return }
    $actual=@(Get-ChildItem -LiteralPath $nativeRecipes -Force -Recurse -File)
    if ($actual.Count -ne $recipeHashes.Count) { throw "Native recipe file inventory changed. Verified backup: $recipeBackup" }
    foreach ($item in $actual) {
        $relative=$item.FullName.Substring($recipeRoot.FullName.Length+1)
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            $recipeHashes[$relative] -ne (Get-FileHash -LiteralPath $item.FullName).Hash) {
            throw "Native recipe bytes changed. Verified backup: $recipeBackup"
        }
    }
    $recipeHashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder "$phase-native-recipe-verified.json")
    Write-Output "PASS $($recipeHashes.Count) native recipe files match their verified backup after $phase."
}
$workspaceHashes=@{}
if ($NativeWorkspaceSwitch) {
    $workspaceRoot=Get-Item -LiteralPath (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'CaptureOne\Workspaces130') -Force
    if (!$workspaceRoot.PSIsContainer -or ($workspaceRoot.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'The exact Capture One workspace directory was not found.'
    }
    $workspaceBackup=Join-Path $folder 'native-workspaces-before-test'
    [void][IO.Directory]::CreateDirectory($workspaceBackup)
    foreach ($item in Get-ChildItem -LiteralPath $workspaceRoot.FullName -Force) {
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Unexpected redirected or nested native workspace path.'
        }
        $target=Join-Path $workspaceBackup $item.Name
        Copy-Item -LiteralPath $item.FullName -Destination $target
        $digest=(Get-FileHash -LiteralPath $target).Hash
        if ((Get-FileHash -LiteralPath $item.FullName).Hash -ne $digest) { throw 'Native workspace backup did not verify.' }
        $workspaceHashes[$item.Name]=$digest
    }
    $workspaceHashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'native-workspace-backup.json')
}
function Test-NativeWorkspaceFiles {
    if (!$NativeWorkspaceSwitch) { return }
    $actual=@(Get-ChildItem -LiteralPath $workspaceRoot.FullName -Force)
    if ($actual.Count -ne $workspaceHashes.Count) { throw "Native workspace inventory changed. Verified backup: $workspaceBackup" }
    foreach ($item in $actual) {
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
            $workspaceHashes[$item.Name] -ne (Get-FileHash -LiteralPath $item.FullName).Hash) {
            throw "Native workspace file changed. Verified backup: $workspaceBackup"
        }
    }
    $workspaceHashes | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'native-workspace-files-verified.json')
    Write-Output "PASS $($workspaceHashes.Count) native workspace files match their verified backup."
}
$catalog=Join-Path $folder 'Host Smoke.cocatalogdb'
$sourceHash=(Get-FileHash -LiteralPath $source).Hash
$fixtureCatalogs=@($catalog)
if($CatalogIsolation -or $ConcurrentCatalogs){
    $fixtureCatalogs=foreach($copy in 'A','B'){
        $directory=Join-Path $folder $copy
        [void][IO.Directory]::CreateDirectory($directory)
        $copyPath=Join-Path $directory 'Host Smoke.cocatalogdb'
        Copy-Item -LiteralPath $source -Destination $copyPath
        if((Get-FileHash -LiteralPath $copyPath).Hash -ne $sourceHash){throw 'Isolated catalog bytes did not match the original.'}
        $copyPath
    }
} else {
    Copy-Item -LiteralPath $source -Destination $catalog
}
if ($SavedLooksBackup) {
    $SavedLooksBackup=(Resolve-Path -LiteralPath $SavedLooksBackup).ProviderPath
    $allowUpdated=@{AllowUpdatedCatalog=[bool]$SavedLooksFromUpdatedCatalog}
    & (Join-Path $runtimeSource 'Restore-SpektrafilmCatalog.ps1') -BackupDatabase $SavedLooksBackup -DestinationDatabase $catalog `
        -UserDataRoot $folder -SavedLooksOnly -Apply @allowUpdated
    if ((Get-FileHash -LiteralPath $catalog).Hash -ne $sourceHash) { throw 'The saved-look restore wrote to the copied catalog.' }
}
if($ConcurrentCatalogs){$catalog=$fixtureCatalogs[0]}
$sessionDatabase=$null
if($SessionIsolation){
    $sessionFolder=Join-Path $folder 'Session'
    $sessionDatabase=Join-Path $sessionFolder 'Spektrafilm Session.cosessiondb'
    $fixtureCatalogs+=@($sessionDatabase,$sessionFolder)
}
[IO.File]::WriteAllText((Join-Path $folder 'disposable-catalog.marker'),$sourceHash)
$fixtureChart=$null
if ($CalibrationChart) {
    $calibration=Join-Path $folder 'Calibration'
    [void][IO.Directory]::CreateDirectory($calibration)
    $fixtureChart=Join-Path $calibration 'Calibrated Bayer chart.dng'
    $chartHash=(Get-FileHash -LiteralPath $chartSource.FullName).Hash
    Copy-Item -LiteralPath $chartSource.FullName -Destination $fixtureChart
    Copy-Item -LiteralPath $chartReference.FullName -Destination (Join-Path $calibration 'reference.json')
    if ((Get-FileHash -LiteralPath $fixtureChart).Hash -ne $chartHash) { throw 'Calibration DNG was not copied exactly.' }
}
if ($UpgradeFromPackage) {
    $priorPackage=(Resolve-Path -LiteralPath $UpgradeFromPackage).ProviderPath
    if ($priorPackage -eq $runtimeSource) { throw 'Choose two distinct extracted package roots for the upgrade.' }
    [void](Test-ReleaseManifest $priorPackage 'package-manifest.json')
    [void](Test-ReleaseManifest $runtimeSource 'package-manifest.json')
    . (Join-Path $PSScriptRoot 'InstalledUpgrade.ps1')
    $upgradeError=$null
    try {
        Test-NativeInstalledUpgrade $priorPackage $runtimeSource $folder $catalog $CaptureOnePath
        if((Get-FileHash -LiteralPath $source).Hash -ne $sourceHash){throw 'Source catalog changed unexpectedly.'}
        Write-Output "Host test passed; source catalog unchanged. Evidence: $folder"
    } catch {
        $upgradeError=$_
        throw
    } finally {
        try { Restore-TestCatalogPreferences $profile $backup $fixtureCatalogs $folder }
        catch {
            if (!$upgradeError) { throw }
            Write-Warning "The upgrade test and its recent catalog cleanup both failed. Preferences backup retained. $($_.Exception.Message)"
        }
    }
    return
}
$runtime=Join-Path $folder 'runtime'
[void][IO.Directory]::CreateDirectory($runtime)
Copy-Item -LiteralPath (Join-Path $runtimeSource 'stocks.json') -Destination $runtime
Copy-Item -LiteralPath (Join-Path $runtimeSource 'profiles') -Destination $runtime -Recurse
Copy-Item -LiteralPath $rendererBuild -Destination (Join-Path $runtime 'renderer') -Recurse
Copy-Item -LiteralPath $panelBuild -Destination $runtime
if ($PackagedRuntime) {
    Copy-Item -LiteralPath (Join-Path $runtimeSource 'build-manifest.json') -Destination $runtime
}
$testHook=$TestHookPath
if(!(Test-Path -LiteralPath $testHook)){throw 'Build qa/HostSmoke and NativePanel first.'}
Copy-Item -LiteralPath $testHook -Destination $runtime
$testHook=Join-Path $runtime 'HostSmoke.dll'
$names=@(
    'DOTNET_STARTUP_HOOKS','SPEKTRAFILM_ROOT','SPEKTRAFILM_CATALOG','SPEKTRAFILM_DATA_ROOT',
    'SPEKTRAFILM_PANEL_LOG','SPEKTRAFILM_HOST_SMOKE','SPEKTRAFILM_LAUNCH_ID','SPEKTRAFILM_HOST_SMOKE_COLOR_MATRIX',
    'SPEKTRAFILM_HOST_VIEWER_STRESS','SPEKTRAFILM_HOST_PANEL_INVENTORY','SPEKTRAFILM_EXPECTED_CONTROL_INVENTORY',
    'SPEKTRAFILM_HOST_NATIVE_EFFECT_EXPORT','SPEKTRAFILM_HOST_MOVED_LOOK_BACKUP',
    'SPEKTRAFILM_SHOW_TOOL','SPEKTRAFILM_HOST_PROOF_TEST','SPEKTRAFILM_HOST_CATALOG_ISOLATION',
    'SPEKTRAFILM_HOST_VIEWER_LAYOUTS','SPEKTRAFILM_HOST_FLOATING_WORKSPACE','SPEKTRAFILM_HOST_NATIVE_WORKSPACE_SWITCH',
    'SPEKTRAFILM_HOST_DURABLE_EDITS','SPEKTRAFILM_HOST_LOOK_BATCH','SPEKTRAFILM_HOST_VARIANT_LINEAGE','SPEKTRAFILM_HOST_METADATA_FIXTURE',
    'SPEKTRAFILM_HOST_VIEWER_CHURN','SPEKTRAFILM_HOST_VIEWER_CHURN_RESOLUTION','SPEKTRAFILM_HOST_VIEWER_TELEMETRY_SECONDS','SPEKTRAFILM_HOST_EXPORT_INTERRUPTION',
    'SPEKTRAFILM_HOST_NATIVE_VIEWER_BASELINE','SPEKTRAFILM_HOST_OTHER_CATALOG',
    'SPEKTRAFILM_HOST_NATIVE_LIFETIME',
    'SPEKTRAFILM_HOST_SESSION','SPEKTRAFILM_HOST_CALIBRATION','SPEKTRAFILM_HOST_ORIGINAL_AVAILABILITY_PROBE',
    'SPEKTRAFILM_HOST_NATIVE_DEVELOPMENT_DEADLINE','SPEKTRAFILM_HOST_DEVICE_LOSS','SPEKTRAFILM_HOST_DEVELOPMENT_LEASE'
)
$prior=@{}
foreach($name in $names){$prior[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
$testError=$null
$captureOneDirectory=Split-Path -LiteralPath (Resolve-Path -LiteralPath $CaptureOnePath).ProviderPath
try {
    $env:DOTNET_STARTUP_HOOKS=if($ViewerNativeBaseline){$testHook}else{(Join-Path $runtime 'NativePanel.dll')+';'+$testHook}
    $env:SPEKTRAFILM_ROOT=$runtime
    $env:SPEKTRAFILM_CATALOG=$catalog
    $env:SPEKTRAFILM_DATA_ROOT=$folder
    $env:SPEKTRAFILM_PANEL_LOG=Join-Path $folder 'panel.log'
    $env:SPEKTRAFILM_HOST_SMOKE=$folder
    $env:SPEKTRAFILM_HOST_SMOKE_COLOR_MATRIX=if($NativeSourceMutations){'source-mutations'}elseif($ColorMatrix){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_VIEWER_STRESS=if($ViewerStress){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_PANEL_INVENTORY=if($PanelInventory){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_NATIVE_EFFECT_EXPORT=if($NativeEffectExportMatrix){'matrix'}elseif($NativeEffectClone){'clone'}elseif($NativeEffectGrain){'grain'}elseif($NativeEffectCompression){'compression'}elseif($NativeEffectOutputProfiles){'profiles'}elseif($NativeEffectFolders){'folders'}elseif($NativeEffectPanelModes){'panel-modes'}elseif($NativeEffectSourceRevision){'source-revision'}elseif($NativeEffectExport){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_MOVED_LOOK_BACKUP=$SavedLooksBackup
    $env:SPEKTRAFILM_EXPECTED_CONTROL_INVENTORY=Join-Path $root 'qa\panel-controls.json'
    $env:SPEKTRAFILM_SHOW_TOOL=if($ShowTool){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_PROOF_TEST=if($NativeSettingsConfig -and !$CatalogIsolation -and !$ViewerLayouts -and !$DurableSliderEdits -and !$MetadataFixtureRoot -and !$ViewerChurn -and !$ConcurrentCatalogs){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_CATALOG_ISOLATION=if($CatalogIsolation){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_VIEWER_LAYOUTS=if($ViewerLayouts){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_FLOATING_WORKSPACE=if($FloatingWorkspaceLayout){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_NATIVE_WORKSPACE_SWITCH=if($NativeWorkspaceSwitch){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_DURABLE_EDITS=if($DurableSliderEdits){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_LOOK_BATCH=if($NativeLookBatchCrash){'crash'}elseif($NativeLookBatch){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_VARIANT_LINEAGE=if($NativeVariantLineage){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_METADATA_FIXTURE=$MetadataFixtureRoot
    $env:SPEKTRAFILM_HOST_VIEWER_CHURN=if($ViewerChurn){[string]$ViewerChurn}else{$null}
    $env:SPEKTRAFILM_HOST_VIEWER_CHURN_RESOLUTION=if($ViewerChurn){$ViewerChurnResolution}else{$null}
    $env:SPEKTRAFILM_HOST_VIEWER_TELEMETRY_SECONDS=if($ViewerTelemetrySeconds){[string]$ViewerTelemetrySeconds}else{$null}
    $env:SPEKTRAFILM_HOST_EXPORT_INTERRUPTION=$NativeExportInterruption
    $env:SPEKTRAFILM_HOST_NATIVE_LIFETIME=if($NativeLifetimeCycles){[string]$NativeLifetimeCycles}else{$null}
    $env:SPEKTRAFILM_HOST_NATIVE_VIEWER_BASELINE=if($ViewerNativeBaseline){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_OTHER_CATALOG=if($ConcurrentCatalogs){$fixtureCatalogs[1]}else{$null}
    $env:SPEKTRAFILM_HOST_SESSION=$sessionDatabase
    $env:SPEKTRAFILM_HOST_CALIBRATION=$fixtureChart
    $env:SPEKTRAFILM_HOST_ORIGINAL_AVAILABILITY_PROBE=if($OriginalAvailabilityProbe){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_NATIVE_DEVELOPMENT_DEADLINE=if($NativeDevelopmentDeadline){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_DEVELOPMENT_LEASE=if($DevelopmentLeaseFixtureDirectory){'1'}else{$null}
    $env:SPEKTRAFILM_HOST_DEVICE_LOSS=if($DeviceLossFixtureDirectory){'1'}else{$null}
    $phases=if($CatalogIsolation){@('A-first','B-first','A-restart','B-restart')}elseif($SkipRestart -or $ViewerLayouts){@('first')}else{@('first','restart')}
    if (!$SkipRestart -and !$CatalogIsolation -and !$ViewerLayouts -and !$DurableSliderEdits) { [IO.File]::WriteAllText((Join-Path $folder 'verify-grain-restart.marker'), 'test only') }
    foreach($phase in $phases){
        [IO.File]::WriteAllText((Join-Path $folder 'phase.txt'),$phase)
        if($phase -ne $phases[0]){Start-Sleep -Seconds 10}
        if($CatalogIsolation){$catalog=$fixtureCatalogs[[int]($phase.StartsWith('B'))]}
        $env:SPEKTRAFILM_CATALOG=$catalog
        $phaseStarted=[DateTime]::UtcNow
        $env:SPEKTRAFILM_LAUNCH_ID=[Guid]::NewGuid().ToString('N')
        [IO.File]::WriteAllText((Join-Path $folder ($phase+'.launch')), $env:SPEKTRAFILM_LAUNCH_ID)
        # This is an interactive host UI integration test, not a background helper.
        $process=Start-Process -FilePath $CaptureOnePath -WorkingDirectory $captureOneDirectory -ArgumentList ('"'+$catalog+'"') -WindowStyle Normal -PassThru
        [IO.File]::WriteAllText((Join-Path $folder ($phase+'.pid')),[string]$process.Id)
        try {
            $status=Join-Path (Join-Path $folder 'Launch status') ($env:SPEKTRAFILM_LAUNCH_ID+'.json')
            if (!$ViewerNativeBaseline) {
                $ready=Wait-SpektrafilmPanel $process $status $env:SPEKTRAFILM_LAUNCH_ID 90
                $ready | ConvertTo-Json -Compress | Set-Content -LiteralPath (Join-Path $folder ($phase+'.ready.json'))
            }
            $deadline=[DateTime]::UtcNow.AddMinutes($(if($ViewerTelemetrySeconds){$ViewerTelemetrySeconds / 60 + 4}elseif($ViewerChurn -or $NativeLifetimeCycles){10}elseif($NativeDevelopmentDeadline){6}else{4}))
            $result=Join-Path $folder ($phase+'.passed')
            $failure=Join-Path $folder 'failed.txt'
            while(!(Test-Path -LiteralPath $result)){
                if(Test-Path -LiteralPath $failure){throw ([IO.File]::ReadAllText($failure))}
                if($process.HasExited -and !(Test-Path -LiteralPath $result)){throw "Test Capture One exited early: $($process.ExitCode)"}
                if([DateTime]::UtcNow -gt $deadline){throw "Timed out. Inspect $folder"}
                Start-Sleep -Milliseconds 500
                $process.Refresh()
            }
            Get-Content -LiteralPath $result
            if ($NativeVariantLineage -and $phase -eq 'first' -and !$process.HasExited) {
                # Capture One owns persistence of the newly created native
                # variants. Let it finish its normal catalog close.
                if (!$process.CloseMainWindow() -or !$process.WaitForExit(20000) -or $process.ExitCode -ne 0) {
                    throw 'Capture One did not finish a clean close of the disposable clone catalog.'
                }
            }
        } finally {
            # Deliberate crash/restart test of this newly launched disposable process.
            # No pre-existing Capture One process can reach this branch.
            if(!$process.HasExited){Stop-Process -Id $process.Id; $process.WaitForExit()}
            $process.Dispose()
            if($NativeExportInterruption -and $phase -eq 'first'){
                # The fixture terminates itself. The process handle can signal while
                # enumeration still reports the terminating host briefly.
                $settle=[DateTime]::UtcNow.AddSeconds(8)
                while ([DateTime]::UtcNow -lt $settle -and (Get-Process CaptureOne -ErrorAction SilentlyContinue)){
                    Start-Sleep -Milliseconds 100
                }
            }
            Clear-TestCrashMarkers $phaseStarted $phase
            Test-NativeRecipeHashes $phase
        }
    }
    if($CheckOrdinaryLaunch){
        Start-Sleep -Seconds 10
        $phaseStarted=[DateTime]::UtcNow
        [Environment]::SetEnvironmentVariable('DOTNET_STARTUP_HOOKS',$null,'Process')
        $env:SPEKTRAFILM_LAUNCH_ID=[Guid]::NewGuid().ToString('N')
        $status=Join-Path (Join-Path $folder 'Launch status') ($env:SPEKTRAFILM_LAUNCH_ID+'.json')
        $process=Start-Process -FilePath $CaptureOnePath -WorkingDirectory $captureOneDirectory -ArgumentList ('"'+$catalog+'"') -WindowStyle Normal -PassThru
        try {
            $deadline=[DateTime]::UtcNow.AddSeconds(90)
            do {
                $process.Refresh()
                if($process.HasExited){throw 'Ordinary Capture One exited before opening the test catalog.'}
                $title=$process.MainWindowTitle
                if($title -like '*Host Smoke*'){break}
                if([DateTime]::UtcNow -gt $deadline){throw "Ordinary Capture One did not open the disposable catalog. Last title: $title"}
                Start-Sleep -Milliseconds 300
            } while($true)
            $failure=$null
            try { Wait-SpektrafilmPanel $process $status $env:SPEKTRAFILM_LAUNCH_ID 2 | Out-Null } catch { $failure=$_.Exception.Message }
            if($failure -notlike '*did not report the Spektrafilm startup hook*' -or (Test-Path -LiteralPath $status)){
                throw "Ordinary launch unexpectedly reported startup state: $failure"
            }
            $message="PASS ordinary Capture One opened $title without a Spektrafilm startup report."
            [IO.File]::WriteAllText((Join-Path $folder 'ordinary.passed'),$message)
            Write-Output $message
        } finally {
            if(!$process.HasExited){Stop-Process -Id $process.Id; $process.WaitForExit()}
            $process.Dispose()
            Clear-TestCrashMarkers $phaseStarted 'ordinary'
        }
    }
    if((Get-FileHash -LiteralPath $source).Hash -ne $sourceHash){throw 'Source catalog changed unexpectedly.'}
    if($fixtureChart -and ((Get-FileHash -LiteralPath $fixtureChart).Hash -ne $chartHash -or
        (Get-FileHash -LiteralPath $chartSource.FullName).Hash -ne $chartHash)){throw 'Calibration DNG changed unexpectedly.'}
} catch {
    $testError=$_
    throw
} finally {
    try {
        if ($NativeSettingsConfig) { Restore-TestCatalogPreferences $profile $backup $fixtureCatalogs $folder ([bool]$ConcurrentCatalogs) ([bool]$NativeWorkspaceSwitch) }
        Test-NativeWorkspaceFiles
        if ($MetadataFixtureRoot) { Test-FixtureRawFiles $MetadataFixtureRoot $metadataMarker }
    } catch {
        if ($null -eq $testError) { throw }
        Write-Warning "The host test and its recent catalog cleanup both failed. Preferences backup retained. $($_.Exception.Message)"
    } finally {
        foreach($name in $names){[Environment]::SetEnvironmentVariable($name,$prior[$name],'Process')}
        Write-Output "Host evidence directory: $folder"
    }
}
Write-Output "Host test passed; source catalog unchanged. Evidence: $folder"
