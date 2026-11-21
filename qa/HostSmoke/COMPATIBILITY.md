# Capture One 16.7.8 compatibility QA

The source fixture targets Windows x64 Capture One **16.7.8.3529**. It builds a separate panel gated to the candidate’s exact version and assembly fingerprint. The builder and runner also verify the executable fingerprint. The production launcher and package use the production host manifest. Viewer and export results come from a running, licensed copy.

Use a machine already running the licensed candidate. Close Capture One before starting the suite. Use a disposable catalog with externally referenced DNG, CR3, RAF and NEF files. Upgrade that disposable donor in 16.7.8 first if needed, then close it cleanly. The runner copies the closed donor for each case and backs up its version-specific native settings before launching. Capture One documents how older catalogs are upgraded and which files are shared across side-by-side installations. [Catalog compatibility](https://support.captureone.com/hc/en-us/articles/36002887363741-Capture-One-16-7-8-release-notes) · [Side-by-side installations](https://support.captureone.com/hc/en-us/articles/360004180998-Can-I-run-two-Capture-One-versions-on-the-same-computer)

From the source root, build the renderer and the QA hook. Use the path to the installed candidate executable and its existing per-version `user.config`. The candidate build checks the vendor signatures. Set `$trial` to a new output directory.

```powershell
$exe = 'C:\Program Files\Capture One\Capture One\CaptureOne.exe'
$donor = 'D:\QA\Disposable four-RAW 16.7 catalog\Fixture.cocatalogdb'
$nativeSettings = 'C:\Users\YourName\AppData\Local\Capture_One\<application-profile>\16.7.8.3529\user.config'
$trial = '.test-results\candidate-16.7.8'

.\Build-Windows.ps1
dotnet build qa/HostSmoke/HostSmoke.csproj -c Release
.\qa\Build-HostCompatibilityFixture.ps1 -CaptureOnePath $exe -OutputDirectory "$trial\build"

$hostArgs = @{
    CaptureOnePath = $exe
    HostCompatibilityFixtureDirectory = "$trial\build\panel"
    SourceCatalog = $donor
    NativeSettingsConfig = $nativeSettings
}

.\qa\HostSmoke\Run-HostSmoke.ps1 @hostArgs -ViewerStress -ResultsDirectory "$trial\viewer"
.\qa\HostSmoke\Run-HostSmoke.ps1 @hostArgs -ColorMatrix -ResultsDirectory "$trial\source-colour"
.\qa\HostSmoke\Run-HostSmoke.ps1 @hostArgs -NativeEffectExportMatrix -ResultsDirectory "$trial\native-export"
.\qa\HostSmoke\Run-HostSmoke.ps1 @hostArgs -NativeEffectOutputProfiles -ResultsDirectory "$trial\recipe-profiles"
.\qa\HostSmoke\Run-HostSmoke.ps1 @hostArgs -NativeEffectFolders -ResultsDirectory "$trial\recipe-paths"
.\qa\HostSmoke\Run-HostSmoke.ps1 @hostArgs -FloatingWorkspaceLayout -ResultsDirectory "$trial\floating"
.\qa\HostSmoke\Run-HostSmoke.ps1 @hostArgs -DurableSliderEdits -ResultsDirectory "$trial\durability"
.\qa\HostSmoke\Run-HostSmoke.ps1 @hostArgs -ResultsDirectory "$trial\attach-undo-detach"
```

Capture One 16.7.8 uses `Recipes165`, `Workspaces130` and version-scoped stability markers. The runner verifies the recipe files after each recipe fixture and the source catalog at the end of each successful run. Record the exact host and candidate receipt, the first and restart outcomes, and independently reopened 16-bit TIFF and PNG results with their embedded ICCs. For each native Export command, record whether the dialog completes once and whether a disabled look delegates to Capture One. Move the Developer Exhaustion and Camera Diffusion controls and record whether Viewer updates continue and settings stay within their expected ranges. Exercise a native C2PA-enabled recipe separately and record its disclosed delivery behavior. Attach the check results in the audit PR with screenshots or pixel reports where useful. [Contributor PR](https://github.com/nothing-complex/spektrafilm-for-capture-one/pull/2)

The reference-only result and any rejected, skipped or unrun runtime cases belong in the audit separately. Admission to the production host manifest requires the observed Viewer, native Export, color, Undo, floating, persistence, fallback and teardown results on the exact licensed build.
