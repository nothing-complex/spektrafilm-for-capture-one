# Spektrafilm for Capture One

<img src="assets/Spektrafilm.png" width="96" alt="Film strip icon with an orange viewfinder" />

An independent Windows tool that brings the spektrafilm film and print simulation into Capture One. Develop your RAW in Capture One, adjust the film look in a docked or floating panel, and export the result as a 16-bit image.

**Supported host: Capture One 16.4.6.2414 on Windows x64.** You'll also need a GPU and driver that support Vulkan. The launcher and panel share the exact host compatibility manifest. A different Capture One build needs its own verified capabilities before the film tool can attach. This build works with catalogs (`.cocatalogdb`). Start with a test catalog when trying the integration.

Capture One provides older installers in your account when your license supports them. [Capture One download guide](https://support.captureone.com/hc/en-us/articles/14996282988957-Download-an-Older-Version-of-Capture-One)

## Install and open

1. Download **Spektrafilm-for-Capture-One-v0.1.1-windows-x64.zip** from [GitHub Releases](https://github.com/nothing-complex/spektrafilm-for-capture-one/releases/latest) and extract it.
2. Run **Install Spektrafilm for Capture One.cmd**. It installs under your Windows account and creates the **Spektrafilm for Capture One** desktop shortcut. A fresh install uses `%LOCALAPPDATA%\Programs\Spektrafilm for Capture One`.
3. Close Capture One. Use the new shortcut and choose the catalog database inside your catalog folder. The launcher backs up the closed database before opening it.
4. Select a photo and open the **Spektrafilm for Capture One** tab with the film-strip icon. Allow the Viewer to develop the first frame.

For portable use, run **Start Spektrafilm for Capture One.cmd** from the extracted folder and choose a catalog. Use that launcher or the desktop shortcut when you want to open Capture One with Spektrafilm.

To update, close Capture One and run the new release's installer. The installer updates the existing installation in place, replaces its desktop shortcut, and retains saved looks and the optional Python environment. To verify the download, use `Get-FileHash -Algorithm SHA256` on the ZIP and compare the result with the `.sha256` file on the release page.

Updates also preserve custom files inside bundled folders. If a new package conflicts with a customized file or folder, its previous contents are retained under **Recovered files** inside the installation. The installer prints that location. New packages carry build and file manifests; mismatched runtime files are rejected before installation or launch.

If an update is interrupted, run the installer again. It can restore the verified prior installation and reports where it kept the interrupted copy. A completed update also keeps the prior version until a launch confirms that the Spektrafilm panel and native Export are ready. If any custom file in the prior version differs from its migrated copy, the prior folder stays available at the path printed by the installer.

The preset dialogs open in `%LOCALAPPDATA%\FilmToolsForCaptureOne\Presets` by default. You can choose another location, including a preset kept in an earlier installation.

## Use the panel

- Turn **Enable Spektrafilm** on or off. The live look follows the selected photo and stays visible when you switch tool tabs. **Show source** temporarily displays the developed source for comparison.
- Select **Print Simulation** to pair a negative and print stock, or **Scan Film** to work from film alone. Use film exposure, print exposure and look strength to set the overall result. Double-click a slider to restore its default. The number fields accept direct entry.
- In a number field, use the arrow keys for small adjustments. Mouse-wheel adjustments follow Capture One’s Tool Tabs preference. With wheel adjustments enabled, Alt lets you scroll the tool instead. [Capture One slider controls](https://support.captureone.com/hc/en-us/articles/360002484318-Adjustments-controls-Sliders) · [Mouse-wheel preference](https://support.captureone.com/hc/en-us/articles/360004339077-Using-the-mouse-scroll-to-change-a-slider-s-value)
- Type the start of a stock name in its picker. Use the picker’s context menu to add or remove the selected stock from favourites. **Recent presets** lists the latest saved or applied preset files.
- Expand the development, print, spectral reconstruction and optics sections for push/pull, dye and filter adjustments, halation, diffusion, scanner effects and grain. Choose **Hanatos 2026** in **Spectral reconstruction and DIR** to use the newer reconstruction. Hanatos 2025 remains the default. The **Capture One Grain** section shows the photo’s current native grain above the staged settings. **Apply grain** sends the staged values to Capture One. **Restore grain** restores the earlier native values when the current values still match the application. This also works after restart and with Spektrafilm switched off.
- Use Capture One’s Undo and Redo for film changes. A slider drag is one history entry, with each movement saved as it happens.
- Choose **Float Tool** in the tool's three-dot menu. Spektrafilm remembers each workspace's floating position, size and scroll. Close the floating tool to dock it again.
- Use **Viewer resolution** to select native size, a manual 720–4096 px long-edge cap, or **Auto (up to 4096 px)** based on the current Viewer size, zoom and available system memory. **Drag preview resolution** sets the temporary long edge while adjusting sliders. 1200 px is the starting value. Set it to 1600 or 2048 px for more detail, or lower either setting to reduce GPU work. The Viewer returns to its selected quality when the drag settles.
- In the color controls, pick the input working space Capture One develops into. Use sRGB to start, or choose Linear Rec.2020, ProPhoto RGB or Adobe RGB (1998). Capture One applies RAW development, its camera profile and the selected film curve before Spektrafilm receives the image. Linear Rec.2020 sets the transfer and primaries of that developed image. The Viewer supports sRGB, Rec.709 gamma 2.4 and Linear Rec.709. Native recipe exports use the selected supported RGB ICC profile.
- Expand **Viewer histogram** to inspect the settled preview’s sRGB values. Move over the Viewer for a pixel reading, or select **Mark clipped channels** to highlight pixels with a channel at 255. **Show source** switches all three tools to the source preview.
- Use **Save preset** and **Load preset** in **Presets and LUT export** to retain the film look, optics, enable switch and staged grain values. Loading a preset leaves the actual Capture One Grain adjustment untouched. In **Select settings to copy or load**, choose the sections you want. To reuse them on another photo, click **Copy selected settings**, select the other photo, then click **Apply copied settings**.
- To paste those sections to several variants, select them in Capture One and choose **Apply to selected variants…**. The confirmation shows how many variants will be affected. **Stop applying copied settings** keeps the changes already saved. One Capture One Undo reverts the saved group. The command accepts up to 1,000 variants.
- If Capture One closes during a batch, reopen the panel to see how many looks were saved. Copy the source settings again before applying them to the outstanding variants.
- Capture One’s **Clone Variant** carries the saved Spektrafilm look when each selected source can be matched to its clone. **New Variant** starts with fresh defaults. When cloning several variants of the same RAW together, use the panel’s Copy and Apply commands to choose which look each clone receives.

Each Spektrafilm launch writes a status file under `%LOCALAPPDATA%/FilmToolsForCaptureOne/Launch status`. It records whether the panel is ready, whether native Export connected, and whether the Viewer is disabled, waiting, rendering, showing an image or reporting a processing error. For diagnosis from PowerShell, run `Start-Spektrafilm.ps1 -CatalogPath <database> -WaitForPanel`. A slow startup remains open if the readiness wait expires.

The PowerShell and portable launchers keep the workspace’s selected tab. Add `-ShowTool` to open the Spektrafilm tab immediately. The desktop shortcut opens that tab for you.

Move the Spektrafilm tab using Capture One’s tab controls. Spektrafilm remembers its position in the active workspace and restores it the next time you open that workspace through the launcher.

Capture One’s **Remove Tool Tab** command hides the Spektrafilm tab for that workspace. The live look and native Export still use your saved edits. To show the tab again, close Capture One and reopen it through the Spektrafilm desktop shortcut or with `-ShowTool`.

## Saved looks

Spektrafilm automatically saves every change to the selected Capture One variant, including the enable switch, stocks and sliders. Preview resolution, comparison and panel layout are separate from the look. Each change is committed immediately, so Capture One can reopen the last complete save after a crash. Choose the same catalog through the desktop shortcut to restore its saved looks. If Capture One cannot save a change to either location, stay open when prompted and restore access to storage so Spektrafilm can retry.

Saved looks live under `%LOCALAPPDATA%\FilmToolsForCaptureOne\Variant looks`. Back up that folder along with your catalog. Saved looks are associated with the catalog's path on this computer. Use **Save preset** to take a look to another catalog or computer.

## Export at 16 bits

With Spektrafilm enabled, select the photo and one enabled Export Recipe, then open Capture One's **Export** dialog and click **Export**. The exported file contains the film look and the enabled optical effects. **Export with Previous Settings** opens the same Spektrafilm save step using the selected recipe. The recipe supplies the size, crop, print resolution and local RGB ICC profile. The save dialog shows the destination profile and offers **16-bit TIFF**, **16-bit PNG** and a **flattened 16-bit PSD**. TIFF delivery from a TIFF recipe inherits its lossless compression setting (Uncompressed, LZW or ZIP). Spektrafilm converts its SDR film output to the destination profile at 16 bits and embeds the exact profile. Reopen Export if the profile file changes while you choose a destination. Choose an RGB profile for this workflow. A recipe with no profile path uses sRGB. [Capture One recipe formats](https://support.captureone.com/hc/en-us/articles/360002646678-Specifying-file-format-in-the-Process-Recipe-tool)

The recipe’s **Disable All** setting suppresses Capture One sharpening during RAW development before Spektrafilm applies the look. Recipes stored in Capture One’s lens-only mode retain lens sharpening and suppress the Sharpening tool’s adjustments. Screen and Print output sharpening are disclosed as omissions. [Capture One crop and sharpening options](https://support.captureone.com/hc/en-us/articles/360020904857-Adjustments-tool)

Text and image watermarks from the selected recipe are applied after the film look and delivery profile conversion. Capture One supplies the rendered mark, its position and its opacity. Spektrafilm blends it into the 16-bit output. [Capture One Watermark tool](https://support.captureone.com/hc/en-us/articles/360021063278-Watermark-tool)

If the recipe asks for JPEG, 8-bit TIFF or PSD, or another unsupported format, the Save title names the request and offers 16-bit alternatives. Export history records the actual file format when it differs from the recipe. **Crop to Path** preserves the full image and adds an editable **Crop** path to the PSD. Where Capture One enables advanced guides, its **Guides** recipe setting adds editable guides inside the delivered canvas. Save and export history disclose unavailable recipe watermarks, Screen or Print sharpening, recipe layers, and paths or guides omitted from alternate formats. [Capture One crop settings](https://support.captureone.com/hc/en-us/articles/360020904857-Adjustments-tool) · [Capture One guide export](https://support.captureone.com/hc/en-us/articles/360002676098-Exporting-Guides-in-Capture-One-Enterprise)

The Save dialog uses the selected recipe’s Capture One naming preview. If the recipe includes a counter, saving the suggested filename advances it by the increment you set in Capture One. Canceling, a failed write, or using a different filename leaves the counter alone. If the counter changes while the export is running, Spektrafilm keeps the approved file and reports that the naming changed. [Capture One Naming tool](https://support.captureone.com/hc/en-us/articles/360020905117-Naming-tool) · [Controlling the counter](https://support.captureone.com/hc/en-us/articles/360002533438-Controlling-the-counter)

Save opens in the recipe's resolved subfolder when that folder exists. For a missing subfolder, it opens at the existing recipe root and lets you choose the final destination. **Add Suffix** offers the first available numbered filename. **Skip** preserves an existing file when a 16-bit TIFF, PNG or PSD recipe resolves to that exact path. Spektrafilm records the skip before rendering and keeps the output counter. Recipes that need a format or bit-depth substitution open Save so you can choose where to write the alternative. The regular Save dialog handles other destinations and explicit overwrite confirmation. [Capture One Export Recipes](https://support.captureone.com/hc/en-us/articles/360021057158-Export-Recipes) · [Location tool](https://support.captureone.com/hc/en-us/articles/360020905077-Location-tool)

Native recipe exports include the permitted copyright, creator, title, headline, description, capture date and rating when available. The **All**, **None** and **From selected keyword libraries** settings control flat and hierarchical keywords. Supported fields are written as XMP. GPS is omitted. The **Export with Spektrafilm** button in the panel uses the selected image, sRGB and 96 DPI. **Save preview PNG** saves the on-screen comparison frame.

Once you choose a destination, the panel reports Capture One development, film rendering and saving. Closing the Export dialog or switching workspaces leaves the job running in the same open catalog. The rebuilt Spektrafilm tool shows progress and offers **Cancel export**. Closing the catalog cancels its active job. If a write fails while Capture One's Export dialog is still open, the dialog title reports the failure and you can choose a fresh destination. When both the dialog and tool are hidden, Spektrafilm reports the failure through Capture One's Windows notifications.

After a restart, **Check interrupted export** can confirm that the finished file was already saved. Spektrafilm verifies it against the recorded checksum and updates the original job. To render again, select the image and choose **Retry interrupted export** when available. Spektrafilm verifies the original, adjustments, camera profiles, film settings, recipe and rendering files, then opens Save at the previous destination in the same format. It checks the Capture One adjustments and camera profiles again after Save and during RAW development. Use a new export if you changed the look, recipe or build, or if the interruption happened before the retry information was saved.

To export several images, select them in Capture One and choose **Export selected images** in the Spektrafilm tool. Choose one destination folder for the batch. Each image uses its own saved look and recipe watermark. Images with Spektrafilm switched off use Capture One’s development as the source. The folder prompt lists recipe omissions across the selection. With **Skip** selected, an exact existing 16-bit TIFF, PNG or PSD recipe filename in the chosen folder is kept and its image is skipped. Shared output names and other collisions get numbered suffixes. **Stop batch** cancels the queue while Spektrafilm is checking the files. During an accepted export, **Cancel export** skips the current image and **Stop after this image** lets it finish before stopping the queue.

Capture One’s **Export** and **Export with Previous Settings** also use this queue when you export multiple selected images and at least one needs Spektrafilm. **Export** shows Capture One’s recipe confirmation before asking for a single destination folder. When all selected images have Spektrafilm switched off, Capture One handles the export normally.

If Capture One closes during a batch, reopen the same catalog and choose **Resume interrupted batch**. Confirm the original folder to continue verified jobs at their approved filenames. Spektrafilm recognizes an already saved file by its checksum and completes that job. Other existing files in the approved folder are preserved. Images with changed settings stay pending so you can restore the look or recipe and try again. **Discard interrupted batch** clears the pending jobs and keeps the files already exported.

Choose one enabled recipe for Spektrafilm export. Export supports a canvas up to 100 megapixels and 16,384 pixels per edge when system and GPU memory allow it. Grain synthesis supports up to 32 megapixels. Choose a smaller Export Recipe if a requested frame exceeds these limits. Turn off **Enable Spektrafilm** for the variants you want to export through Capture One alone.

### .cube export

Choose a 33- or 65-point LUT from **Presets and LUT export**. The LUT uses the same Vulkan film engine as image processing, with sRGB input and output and the selected look strength. Before export, the panel lists image-dependent effects outside the LUT, such as grain, halation, diffusion and sharpening. **Cancel LUT export** preserves any existing destination until the new file commits. Keep the attribution in the LUT header if you share it. [Profile and LUT license](upstream-spektrafilm/SPEKTRAFILM_LICENSE.txt)

The bundled renderer describes its supported settings in `renderer/parameter-capabilities.json`. The panel checks that description before loading its controls. Hanatos 2026 is offered when the renderer includes the calibrated film data. The Windows still workflow exposes Print Simulation and Scan Film, including scan inversion.

Each new Windows ZIP includes a generated `CAPABILITIES.md` with the verified Capture One hosts, worker settings, value ranges and defaults, and an inventory of the native panel controls. The package manifest records the document's checksum.

### Optional Python reference runtime

The Capture One panel and its LUT exporter use the bundled Vulkan renderer. For the separate scientific Python worker, install Windows x64 CPython 3.13 and run **Setup LUT Export.cmd**. Setup checks the exact wheel hashes, verifies dependencies and renders a test stock before selecting the new environment. A failed setup keeps the previous environment available.

For offline setup, download the locked wheels on a connected Windows x64 CPython 3.13 machine, then copy the wheelhouse to the offline system.

```powershell
py -3.13 -m pip download --only-binary=:all: --require-hashes -r requirements-lut.txt --dest .\wheelhouse
.\Setup-LutExport.ps1 -Wheelhouse .\wheelhouse
```

## Troubleshooting and uninstall

| Symptom | What to do |
| --- | --- |
| The Spektrafilm tab is missing after restart | Close every Capture One window and launch again through **Spektrafilm for Capture One**. Open the catalog selected at launch. |
| Launcher reports a version mismatch | Select the x64 Capture One 16.4.6.2414 installation. |
| Viewer is slow | Select a smaller Viewer and drag-preview resolution. Lower the grain synthesis sample count if you are using synthesis. |
| Viewer stops after a processing error | Choose **Retry Viewer** to render the current photo again with its saved look. |
| Launcher finds an open or busy catalog | Open it in Capture One normally, close it, and retry. |
| GPU or startup error | Run `.\Start-Spektrafilm.ps1 -CheckOnly` in PowerShell from the installed folder. It checks the release files, Capture One build and a Vulkan test frame. |

Diagnostic logs and up to five joint backups per catalog are under `%LOCALAPPDATA%\FilmToolsForCaptureOne`. A changed database copy passes SQLite integrity verification before publication and backup rotation. Each database backup has matching `.spektrafilm.zip` and `.manifest.json` files containing its saved looks, pending recovery records and checksums. Keep all three together. A look-only change creates a new backup even if the catalog database hash is the same. Original images and Capture One previews need their own backups.

Use **Copy support details** in the Spektrafilm tool menu to copy the build versions, Viewer status, native Export route and look save status. It also reports the Viewer encoding, Capture One Recipe Proofing state and the fingerprint of the display profile associated in Windows, when available. The copied report contains no file paths or image data. The local diagnostic log keeps the active file and one previous generation.

To restore a backup, open PowerShell in the Spektrafilm installation folder. Choose a new catalog filename and run the preview. Add `-Apply` to the same command when the preview shows the intended path. The script verifies the database copy and restores variant looks and pending edits for the new location.

```powershell
.\Restore-SpektrafilmCatalog.ps1 -BackupDatabase 'C:\Backups\Catalog.cocatalogdb' -DestinationDatabase 'D:\Restored\Catalog.cocatalogdb'
```

For a moved or copied catalog, open its new location through the Spektrafilm launcher. The launcher recovers looks when one verified source matches the catalog byte for byte. The new path gets its own saved looks. If the catalog database has changed, the shortcut offers to recover a matching backup after verifying the native catalog and saved variants. From PowerShell, use `-RecoverUpdatedCatalogLooks` to approve that verification and recovery, or `-SkipCatalogLookRecovery` to launch without discovery.

When a single source matches, the launcher can carry newer edits still saved under the old path, including when the archived look ZIP is empty. Spektrafilm records a variant’s catalog and RAW IDs when saving its look. Those IDs let recovery verify a variant created after the catalog backup. If the launcher cannot verify a variant or several source paths match, choose a backup explicitly.

Close Capture One, use `-SavedLooksOnly` with the existing destination and add `-AllowUpdatedCatalog` if the database has changed. To recover current local edits for the chosen source, add `-LatestLocalLooks`. The preview checks the variant identities. Add `-Apply` to recover the verified looks. The restore preserves any saved looks already at the destination.

If a restore stops partway through, rerun the original command with `-Apply`. For an existing catalog, the Spektrafilm launcher can also resume the restore before it opens Capture One. Keep the original backup files together until the restore finishes. A retry checks any files already published and preserves them if they changed.

To uninstall, close Capture One, delete the **Spektrafilm for Capture One** desktop shortcut, and remove the installation folder printed by the installer. Keep the saved looks and catalog backups under `%LOCALAPPDATA%\FilmToolsForCaptureOne` if you want to use them later. If you used portable mode, remove the extracted folder.

## Build from source

Building requires Windows x64, Visual Studio 2022 with Desktop development with C++, CMake 3.24 or newer, a Vulkan SDK, the .NET 8 SDK, 64-bit Python 3.13 and a local installation of Capture One 16.4.6.2414. The checked-in upstream snapshots contain the code and tables used in the build. From the repository in PowerShell, point `$python` to your Python 3.13 executable and run:

```powershell
& $python -m pip install -r .\requirements-build.txt
.\Build-Windows.ps1 -BuildPanel -StageRuntime -PythonPath $python
.\Start-Spektrafilm.ps1 -CheckOnly
.\Package-Windows.ps1 -Version 0.1.1 -Development
```

Tagged release packaging reruns the Windows contracts and Vulkan smoke test against the staged runtime. The package manifest records the result fingerprint. Licensed Capture One tests run separately on a controlled Windows system.

Use `-VulkanSdkPath` to specify a Vulkan SDK location or `-CaptureOneDirectory` for a custom Capture One installation. The build runs the renderer smoke test by default. Packaging writes the archive and SHA-256 checksum to `dist`. See the [upstream build and changes](UPSTREAM-CHANGES.md) and [third-party notices](THIRD-PARTY.md).

`-Development` explicitly labels local test packages, including builds containing uncommitted changes. Public release packaging omits that switch and requires clean committed source, a matching `v<Version>` tag, passed renderer smoke tests, and matching runtime/source manifests. A partial rebuild or changed shader/table is rejected. The manifest records source, host assembly and runtime hashes without redistributing Capture One components.

## Run the contract tests

Run `./Test-Windows.ps1` from a Windows checkout with the .NET 8 SDK or newer. This tests saved-state and renderer contracts, viewer geometry, exact 16-bit output, release manifests, and installation into disposable folders. It does not open Capture One or use the real Desktop. Logs and JSON summaries are written to `.test-results`.

Add `-Gpu -PythonPath $python` after staging a renderer to run the Vulkan smoke suite as well. GPU and real Capture One integration are reported separately from the public no-host suite; passing the latter does not certify another Capture One version. GitHub runs the no-host suite on hosted Windows runners for pull requests, with read-only repository permissions and no private credentials.

## Contribute a compatibility fix

For ongoing development, start with the [handover](HANDOVER.md) and [current audit task status](qa/AUDIT-FIX-STATUS.json). They distinguish completed delivery steps, remaining acceptance work and the tested/installed runtime revision.

Changes for newer Capture One builds can be submitted from a fork. Push your fix to your fork, then open a pull request against `experimental/unchecked-capture-one`. [Fork and pull request instructions](https://github.com/nothing-complex/spektrafilm-for-capture-one/tree/experimental/unchecked-capture-one#contribute-a-compatibility-fix)

A host compatibility patch should include the exact executable build, the `CaptureOne.dll` SHA-256 and disposable-catalog results for attachment, high-depth development, export, Undo and teardown. The supported entries live in `capture-one-hosts.json`. [GitHub fork pull requests](https://docs.github.com/en/pull-requests/how-tos/create-pull-requests/creating-a-pull-request-from-a-fork)


## Support development

- Capture One integration — [Luka Kauzlaric on Ko-fi](https://ko-fi.com/lukakauzlaric)
- Original spektrafilm research, simulation, and film profiles — [Andrea Volpato on Buy Me a Coffee](https://buymeacoffee.com/andreavolpato)
- spektrafilm OFX and Vulkan renderer — [Aedan Diez on Ko-fi](https://ko-fi.com/aedandiez)

## Credits

Andrea Volpato created the original spectral simulation and built the film profiles from real sensitometry and dye data. Aedan Diez built the Vulkan-powered OFX port that makes it practical to use the engine interactively. Both projects are worth exploring. [Andrea's spektrafilm](https://github.com/andreavolpato/spektrafilm) · [Aedan's spektrafilm OFX](https://spektrafilm.114c.de/)

Capture One integration by [Luka Kauzlaric](https://www.lukakauzlaric.com). I'm also building [Lensflare](https://www.lensflare.io).

Spektrafilm for Capture One is an unofficial integration developed independently of Capture One and the upstream authors. The integration is distributed under the GNU GPL v3. Film profiles and direct derivatives have separate CC BY-SA 4.0 terms. [GPL v3](LICENSE) · [Profile and LUT license](upstream-spektrafilm/SPEKTRAFILM_LICENSE.txt)
