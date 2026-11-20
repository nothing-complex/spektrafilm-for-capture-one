# Spektrafilm for Capture One development handover

Updated 2026-11-20. The critical-fix batch is implemented, tested, installed locally and published in a draft pull request. The audit follow-ups below passed local validation. The 70-item integration audit tracks 39 bounded primary fixes implemented and 31 partial items. It includes feasibility investigations and enhancements.

## Resume here

1. Read this file, `AGENTS.md` and `qa/AUDIT-FIX-STATUS.json` before changing code. The JSON is the authoritative task-progress ledger; its `remaining_work` and original acceptance tests matter more than a short status label.
2. Check `git status`, current branch and PR head. Do not discard existing edits or switch the installed build merely because this documentation has a newer commit.
3. The regular installed runtime and original delivery package identify `ffc481988eb2e6e7ef7a1379486c4a8b2656ac81`. The initial product changes are in `285f0aa`. The latest verified development ZIP is `0.2.0-audit.35` from clean `df4a7d9`. It passed 457 native export assertions, physical Vulkan checks and a private upgrade from audit.34 on Capture One 16.4. Audit.32 retains the earlier four-RAW native export and stacked effects campaign. Audit.31 retains the 10,000-edit hard-restart and full licensed host campaigns. Audit.29 retains the updated-catalog, workspace and export interruption campaigns.
4. Group related work by the user workflow it improves. Run focused acceptance checks for each change and reserve broad host regressions for changes that affect several workflows or for a package candidate. Track external prerequisites alongside the affected tasks.
5. Update task evidence and the delivery ledger at each completed milestone. Never mark a broad parent task complete because one mitigation shipped.

Inside the standalone audit archive, use `CURRENT-IMPLEMENTATION.json`; repository-only `AGENTS.md` and `qa/` paths refer to the linked source checkout. The archive contains a copy of this handover, not the product or local test artifacts.

## Locations and revision identities

| Item | Value |
| --- | --- |
| Active worktree | `D:\Library AI testing\SpektraFilm Capture One\Film-Tools-Audit-Fixes` |
| Branch | `audit/fixes-2026-11-16` |
| Remote | `https://github.com/nothing-complex/spektrafilm-for-capture-one.git` |
| Public review | [PR 1](https://github.com/nothing-complex/spektrafilm-for-capture-one/pull/1), draft, open, unmerged when checked |
| Original main baseline | `d56ebefe023cd32ca6bf3a129ae7add02ff0b6b4` |
| Original checkout | `D:\Library AI testing\SpektraFilm Capture One\Film-Tools-Release`; main was not merged or replaced |
| Experimental checkout | `D:\Library AI testing\SpektraFilm Capture One\Film-Tools-Experimental`, inspected at `2576830a387243603dfc90e5ec40f350c5db04b8`; removing a version gate does not establish 16.7 compatibility |
| Original audit and detailed plan | `D:\Library AI testing\SpektraFilm Capture One\Audit-2026-11-16` |
| Installed application | `C:\Users\luka\AppData\Local\Programs\Film Tools for Capture One` — legacy folder name is intentional; product branding is Spektrafilm for Capture One |
| Desktop shortcut | `C:\Users\luka\Desktop\Spektrafilm for Capture One.lnk` |
| Saved user state | `%LOCALAPPDATA%\FilmToolsForCaptureOne` |
| Supported host | Windows Capture One `16.4.6.2414`, installed under `C:\Program Files\Capture One\Capture One` |

Supported `CaptureOne.dll` SHA-256: `4E17B2AB68CB60CCE18BB92CF0C829A93A62024B15354136FD30A3F9EED2E342`. Retain the exact gate until another installed host passes an explicit compatibility matrix. This run did not establish 16.7 support.

## Completed delivery ledger

- [x] Created isolated audit worktree and source backup before implementation.
- [x] Implemented saved-look validation, preservation of unreadable/future data, explicit recovery, durable fallback checkpoints, compact saves and verified no-op elimination. Saving still occurs on every movement; this is not debounce-only autosave.
- [x] Prevented nested custom-file loss on upgrades; added collision recovery, verified rollback fixtures and joint catalog/look backup generations.
- [x] Isolated renderer experiment environment flags; added protocol negotiation, parameter fingerprints, malformed-response recovery and native-engine LUT export.
- [x] Kept all settled preview resolutions on native high-depth source development; corrected draft resampling to linear light and stopped unfinished provisional work from holding up ready final development.
- [x] Separated accepted image export cancellation from selecting a disabled look, added an explicit cancel control, surfaced native Export hook failure and guarded original output paths.
- [x] Added native grain partial-setter rollback and verified normal apply/restore in the real host.
- [x] Added global preview preferences and remembered floating bounds. Later checkpoints add section and scroll persistence.
- [x] Ran local managed, installer, backup, GPU and live-host checks with their scope recorded below.
- [x] Fixed PowerShell 7 to Windows PowerShell 5.1 child-module-path contamination discovered on GitHub; added six regression assertions and observed green CI.
- [x] Built a clean development package, verified extracted files and renderer, backed up the installed application/user state/shortcut, installed it and verified it in a fresh PowerShell process.
- [x] Confirmed all six pre-update user-data files were unchanged and the source test catalog's hash was unchanged.
- [x] Published source in draft PR 1. No main-branch merge or public GitHub Release was performed.

These completed milestones do not close every acceptance condition of the corresponding 70 audit tasks.

## Backups and the tested package

Original source backup: `..\Audit-2026-11-16\source-before-fixes-d56ebef.zip`.

Verified pre-install backup: `.test-results\installed-before-audit-20261116`. It contains `Installed application`, `User data`, the original desktop shortcut, `application-hashes.json` and `installed-check-passed.txt`. The original installation had 145 files, approximately 33.6 MB. Keep this backup; do not replace it with a copy of the newer audit build. Saved user state was verified unchanged after both installation passes.

Application rollback and saved-state restoration are separate decisions. Restoring the old `User data` folder later would overwrite edits made since this backup; do not do so as an automatic application rollback step.

Package: `dist\Spektrafilm-for-Capture-One-v0.2.0-audit.1-windows-x64.zip`.

- SHA-256: `52ea6f71a6ffa60d9342406f1cfa05df7ac0abbce7357db96b9ae8db8a1bf299`.
- Source commit: `ffc481988eb2e6e7ef7a1379486c4a8b2656ac81`, clean when built.
- Source digest: `cc91c4c6ad494db981ef1a8ff112f1b7167e5884993a494163071ed13803f37b`.
- Verified 146 packaged files and 17 runtime components, supported host fingerprints and Vulkan pixel processing.
- Extracted installer: `.test-results\final-package-271dea93cb444e67b18043c87781bdec\Spektrafilm for Capture One`.

This is a development package, not a tagged public release. Documentation updates after the runtime commit intentionally do not regenerate or reinstall it. `Package-Windows.ps1` rejects a source tree that differs from the staged manifest; rebuild/stage from a clean intended revision before producing a new package. Never hand-edit a manifest to bypass that check.

## Validation and limits of the evidence

| Tier | Observed result | Evidence |
| --- | --- | --- |
| Managed renderer client | 81 assertions | `.test-results/ci-pwsh-fix/RendererContracts.log` |
| State and recovery | 133 assertions, including separate-process fallback recovery and injected write/rollback failures | `.test-results/ci-pwsh-fix/StateContracts.log` |
| Viewer, colour and encoder contracts | 9,789 local assertions; 9,790 on CI where the symbolic-link fixture could run | `.test-results/ci-pwsh-fix/ViewerContracts.log` |
| Test-child environment | 6 assertions; parent environment unchanged | `.test-results/ci-pwsh-fix/runner-environment.log` |
| Manifest / backup / installer | 9 / 17 / 26 assertions | `.test-results/ci-pwsh-fix/summary.json` |
| Real GPU contracts | 9 checks; sampled 33/65-point LUT error at most about `5.0e-10` | `tests/renderer/README.md`, renderer test artifacts |
| Fresh full build and smoke | Panel zero warnings/errors; persistent protocol, encoding and effect checks passed | `.test-results/final-build.log` |
| Live Capture One | 17 first-process plus 19 restart assertions | `.test-results/host-final/host-smoke.log`, `first.passed`, `restart.passed`, `panel.log` |
| Extracted and installed package | Both `-CheckOnly` checks passed | `.test-results/final-package-check.json`, installation backup's `installed-check-passed.txt` |
| GitHub CI for runtime revision | Success, run `43916882324` | [Windows contracts](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/43916882324) |

The CI total is 10,062 assertions, not 10,062 distinct end-to-end scenarios. The local symbolic-link fixture skipped unavailable privilege. GPU tests and licensed-host tests are separate from the public CI job.

`.test-results`, `build`, `dist` and the installation backup are ignored local artifacts, not files shipped in the GitHub source checkout. Their paths in this handover are relative to the active worktree above. A remote agent can reproduce the public tests but must not assume access to Luka's logs, installed host, images or backups.

Live-host scope: the tool and native Export binding attached; tested slider movements persisted; legacy preview preference keys were accepted; the second process restored exposure 1.2 and a disabled master without losing the tool. Capture One logged a native 6000 × 4000 DNG source at 16 bits, and explicit 720/1200 scaled source checks passed. The GPU-to-TIFF assertion used the **1200 px scaled frame**, float32 processing and RGB48 output with expected dimensions. It did **not** exercise a full 24 MP GPU export, `ExportAsync` through the native dialog, every recipe, export during image navigation, all cameras/profiles, power-loss durability or visual proofing parity. Native grain normal apply/restore passed; actual host setter-failure injection remains open.

The host tests ran against product commit `285f0aa`; the later runtime package commit changes only the CI test runner. The final package and installed runtime were checked again. The current initial draft also uses original high-depth development (`RenderDraft` / `RenderPurpose.Draft`), not the old 8-bit proxy route.

## Export follow-up verified 2026-11-19

- [x] Reserve the captured target, settings, recipe and cancellation before the modal Save dialog pumps UI events. A concurrent request cannot open a second picker. Changing the active enable state during destination selection leaves the captured export intact.
- [x] Keep native Export window registration scoped to its owner and attempt both native delegate restorations during cleanup. Reject a Spektrafilm dialog open while another Export window owns the slot.
- [x] Build `NativePanel` and `HostSmoke` with zero warnings and errors. Pass all seven local contract suites with 10,061 assertions. The symbolic-link fixture skipped unavailable local privilege.
- [x] Pass 27 assertions in a disposable Capture One catalog. The harness injected missing first and second native callbacks and verified delegate rollback, prepared and released the native confirmation binding, checked another dialog's ownership, and ran the recipe-sized production export with a deterministic test destination. The resulting 3000 × 2000 TIFF is RGB48. Explicit cancellation preserved its bytes. The source catalog hash stayed unchanged.

Logs live under `.test-results/host-export-20261119-pass` and `.test-results/contracts-export-20261119`. The first attempt diagnosed a reflection error in the test hook and is superseded by the passing run. This run simulated the enable transition in the export panel. The previous development ZIP and installed build identify `ffc4819`.

## Host and recipe validation checkpoint

- [x] `FIX-01` uses a typed native Export bridge state. Both missing-callback cases used the production startup binding path. The warning survived a slider edit, direct export remained available, and a successful rebind cleared the warning.
- [x] The real Capture One selection API switched to a different, disabled variant during destination selection. The captured original exported at 3000 × 2000 in RGB48. Explicit cancellation preserved it. This advances `FIX-03` and `VE-05`.
- [x] An exclusive lock on a real saved-look file paused the controls. After it was released, the host watchdog restored the saved look and preserved the original bytes without user input. This advances `HS-03`.
- [x] Recipe print resolution now reaches the 16-bit encoder. The host output reopened as 300 DPI. Contract tests verified inch, centimeter and millimeter conversions, invalid values and both format encoders with unequal horizontal and vertical densities.
- [x] Added an independent fractional area-sampling oracle with nonconstant colour and alpha across all four input encodings. The Windows contract suite passed 11,968 assertions. The symbolic-link check skipped unavailable local privilege.
- [x] The expanded host run passed 38 first-launch and 40 restart assertions. With recipe DPI added, the follow-up passed 39 first-launch assertions. Both used disposable copies of the original test database and verified its hash.

Evidence lives in `.test-results/host-audit-state-selection-20261119`, `.test-results/host-audit-resolution-20261119` and `.test-results/contracts-audit-resolution-20261119`. Actual native confirmation and OS picker interactions, device-independent display colour, metadata policy, navigation after background development starts and document closure during export are separate acceptance cases.

## Output protection, detach and backup integrity checkpoint

- [x] Record the original, destination and directory identities when the Save dialog returns. Recognize hard links and folder aliases. Recheck approval immediately before the sibling temporary file commits. New destinations also use a rename that refuses overwrite.
- [x] Pass the real hardlink and junction fixtures, approved replacement, concurrent destination creation and replacement, and explicit cancellation after encoding. The symlink fixture runs when Windows grants that privilege. Existing-destination replacement still has a small Windows path-rename race after its final recheck.
- [x] Guard image, viewer snapshot, LUT and preset output with the same approval policy. LUT jobs have their own visible cancellation action, and preset export captures effect settings together before opening Save.
- [x] A suppressed close retained the active export. Explicit detach cancelled it, aborted the queued WPF callback, and preserved the previous file. This used the real host message loop and production detach code. The fixture kept the host window open so it could observe the asynchronous result. First/restart passed 44 and 46 assertions.
- [x] Verify a changed catalog copy using the Windows SQLite library's read-only `integrity_check`. Reject a corrupted database with a valid header before publication or rotation. The original fixture catalog hash stayed unchanged, and the verifier produced no SQLite sidecar. There were 21 backup assertions.
- [x] Compare the two supported LUT spectral methods against GPU rendering at 1,024 held-out colors for each cube size. All 17 GPU checks passed. The 65-point LUT normalized sRGB code p95 stayed below 0.00025 on the tested look, and the max was below 0.020. These are results for the tested setup and look.
- [x] Fix renderer cancellation test timing by waiting until the fake worker receives the request and explicitly releasing the queued-request fixture. The full Windows suite passed 11,989 assertions before the usual local symbolic-file-link privilege skip.

Logs live under `.test-results/contracts-audit-integrity-20261119`, `.test-results/host-detach-20261119`, `.test-results/gpu-lut-heldout-20261119.log`, and `.test-results/real-catalog-integrity-20261119`. Next audit work includes actual form closure, native command confirmation, recipe metadata, workspace and multi-monitor tests, portable catalog identity and full renderer request envelopes.

## Restore, layout and native geometry checkpoint

- [x] The Windows CI run exercised a symbolic file link, hardlink and directory junction. The output protection tests passed. The late Windows rename limitation remains documented under VE-14. [Windows contracts](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44612520456)
- [x] Stable panel section IDs preserve expansion across renames and reopen. Nested expansion leaves ancestors untouched. Floating scroll and bounds are stored together. Workspace and monitor/DPI coverage remains open.
- [x] Run the backup/restore suite with 31 assertions. Preview verifies the archive without writing, and explicit restore refuses collisions. The database and film files are checked before publication. Restore rebases pending film edits to the new path.
- [x] Restore an actual closed Capture One catalog to another path with matching database SHA-256 and two looks from the production reader. Run 53 assertions against a disposable copy of the restored catalog. Native developed pixels had the expected quarter turn and mirrored pattern, and keystone changed the image. The source hash remained unchanged.
- [x] Run the full Windows contract suite with 12,006 local assertions. The local symlink fixture skipped because Windows denied creation privilege.

Evidence is under `.test-results/contracts-backup-layout-20261119`, `.test-results/restored-real-catalog-20261119`, and `.test-results/host-restore-geometry-20261119`. The installed runtime still identifies `ffc4819`.

## Native export commands and recipe metadata checkpoint

- [x] Invoke the real confirmation command in a visible Capture One Export dialog with a deterministic Save destination. Cancel keeps the dialog open; success writes a 16-bit TIFF and closes its owner. Export with Previous Settings writes a 16-bit PNG through the same bridge.
- [x] Surface confirmation-adapter failures in the persistent Export warning. The disposable host checked missing and wrong-type closure fields and a clean reattach after each fault.
- [x] Capture recipe-permitted copyright, creator, title, date, rating and all-keyword selections before Save. Use fresh TIFF or PNG XMP with normalized output orientation and actual dimensions. GPS, whitelist mode and non-allow-listed metadata stay excluded. Profile choice remains sRGB.
- [x] Check original 16-bit codes, ICC and non-square DPI in both codecs. Read Unicode field values and exact orientation/dimensions back. Check PNG chunk checksums independently. Reopen the temporary encoded image before commit and refuse invalid output.
- [x] Run 12,032 local contract assertions and 68 first-launch plus 70 restart assertions in a disposable catalog. Its source hash stayed unchanged. The host exposed a null keyword list for the untagged fixture; the passing run verifies the empty-list handling.

Logs are in `.test-results/contracts-export-metadata-20261119` and `.test-results/host-export-meta-adapter-20261119`. The installed runtime still identifies `ffc4819`.

## Native grain restart and backup ordering checkpoint

- [x] Persist a catalog- and variant-scoped native grain restore record before changing the host settings. Distinguish the original, requested and previously applied values. Refuse restore after a conflicting native change.
- [x] Pass 71 first-launch and 74 restart assertions in a disposable catalog. After deliberate process termination, the second process restored the exact original Capture One Grain values. A later native grain edit was preserved. The source database hash stayed unchanged.
- [x] Include the grain restore record in joint backup and restore. Version 2 fingerprints use ordinal sorting. Legacy archives verify the manifest embedded in the hash-checked ZIP and recheck all entries. A three-file PowerShell 5.1 archive restored on PowerShell 7. A mismatched pair was rejected.
- [x] Run 12,050 assertions across all seven Windows contract suites, including 150 state assertions and 36 joint backup assertions.

Evidence is under `.test-results/host-grain-receipt-20261119`, `.test-results/contracts-grain-backup-v2-20261119` and `.test-results/legacy-three-entry-state`. Comparing exact grain values cannot detect an unrelated edit that returns them to the same values. Persistent host revision and safe cleanup on variant deletion are open. The installed runtime still identifies `ffc4819`.

## Native look Undo and portable metadata verification checkpoint

- [x] Prove the exact Capture One build accepts named extension actions in its native history. Film drags save every movement and register one completed history entry. Undo and Redo persist the captured variant and refresh its controls.
- [x] Verify mixed native/film adjustments in actual Undo and Redo order. Test undo for one image while a different image remains selected. The fixture uses the host's NoUndoScope for that selection so the targeted edit is next in history. The production code registers only normal native undo entries.
- [x] Keep newer external look edits when an older undo entry conflicts. Data replacement checks the expected look, then uses the existing durable writer and failure reporting.
- [x] Verify TIFF XMP by locating TIFF tag 700. Verify PNG XMP bytes and the iTXt checksum. These checks replace the codec-dependent metadata query that failed on GitHub.
- [x] Pass 12,056 local contract assertions plus 87 first-launch and 90 restart assertions in a disposable Capture One catalog. The source hash stayed unchanged.

Evidence is under `.test-results/contracts-native-undo-metadata-20261119` and `.test-results/host-undo-raw-metadata-20261119`. Preset/reset histories, pointer and keyboard edge cases, selection during gestures, and broader host builds remain open. The installed runtime still identifies `ffc4819`.

## Full-look presets checkpoint

- [x] Preset version 2 captures film, optics, the enable switch and staged grain. Vintage presets retain their version-specific behavior. Save and Load remain accessible for disabled looks, with Spektrafilm export disabled. The host's actual grain stays as it was.
- [x] Verify one native Undo action for preset load and the exact imported state after Redo. Refuse applying to a different variant after navigation. Test older and unversioned files, optional sections and invalid states in the managed suite.
- [x] All seven local suites pass 12,062 assertions. Capture One passes 96 first-launch and 99 restart assertions in the disposable catalog. The source hash stayed unchanged.
- [x] The preceding GitHub Windows run passed 12,057 assertions and exercised the portable metadata verification plus the symbolic-link fixture. [Windows contracts](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44619139548)

Evidence is under `.test-results/contracts-full-presets-20261119` and `.test-results/host-full-presets-20261119`. Selective section apply, every-mode roundtrips and adjacent native clone/copy behavior remain open. The installed runtime still identifies `ffc4819`.

## Output worker scheduling and full RAW export checkpoint

- [x] Retire and await Viewer processes before image or LUT GPU output. Native exports and LUT baking share the same panel output slot. Block a second GPU output and queue the latest Viewer refresh when output completes. Waiting for process exit is asynchronous.
- [x] Assert process exit for an idle and a blocked renderer using process handles. Reopen a worker after shutdown. Also verify cancellation, queued-request survival and malformed stream recovery.
- [x] Exercise real full and draft Vulkan processes in Capture One, block overlapping outputs, and export a complete 6000 × 4000 DNG without a recipe size reduction. The RGB48 TIFF includes an embedded ICC profile and 16-bit codes between 8-bit steps. First/restart fixture timing was 4.20 s and 4.03 s on the RTX 3090, each including a small decoded sample.
- [x] Pass 12,066 local Windows assertions, 17 GPU checks and 105 first-launch plus 108 restart host assertions. The source catalog hash stayed unchanged.

Evidence is under `.test-results/contracts-renderer-drain-20261119`, `.test-results/host-full-raw-24mp-20261119` and `.test-results/gpu-renderer-drain-20261119.log`. Global admission across panels and processes, accurate VRAM reservations and a performance matrix across GPUs remain open. The installed runtime still identifies `ffc4819`.

## Repeatable worker and memory baseline

- [x] Run 70 real Vulkan requests across 14 cases. The color and 8 mm optics scenes cover packed Viewer and float export at 0.96, 6 and 24 MP. Grain synthesis compares two-sample draft with the worker default at 0.96 MP.
- [x] Collect working set and process commit, diagnostic allocation counters, renderer stage timings, and request wall time. Store raw samples and warm median/p95 with the executable SHA. The tool writes progress after each completed case.
- [x] On the RTX 3090, the 24 MP optics Viewer request reported about 4,033 MiB in Vulkan static/cached allocations and 1,545 MiB of worker peak working set. Warm Viewer median was 422 ms. At 0.96 MP, synthesis warm median was 70 ms at two samples and 4,218 ms at the worker default.
- [x] Add Windows Server 2025 to public CI alongside 2022. Licensed host and GPU coverage remain explicit local tiers. [GitHub-hosted runner labels](https://docs.github.com/en/actions/reference/runners/github-hosted-runners)

Evidence is under `.test-results/renderer-benchmark-color-optics-20261119.json` and `.test-results/renderer-benchmark-grain-quality-20261119.json`. Repeat with `python qa/renderer-benchmark.py --renderer renderer/SpektraStillRenderer.exe --output .test-results/next-benchmark.json --scene color --scene optics`. The benchmark covers the worker and pipe. Capture One development, source upload from managed code, host composition, driver residency and file encoding require their own probes.

## Scheduler cancellation, DPI and system memory checkpoint

- [x] Retain the Capture One scheduler slot for an executing native callback. Canceling the waiting caller releases the slot immediately only when work remains queued. Run 200 races plus explicit running, abandoned and faulted cases.
- [x] Exercise WPF clip and image placement at 100%, 150% and 200% DPI under the native painter's modeled transform. Verify closure tolerance, degenerate geometry, offscreen clipping and crop rounding at canvas edges.
- [x] Check physical and commit headroom before native development, worker transport and encoding. Use a process-wide reservation and refuse oversized demand explicitly. Simulate 4, 6 and 8 GiB of system memory headroom with overlapping and pressure cases. [Windows memory status](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/ns-sysinfoapi-memorystatusex)
- [x] Compare full and multi-tile GPU output in six scenes crossing tile edges. Maximum tested difference was 1.79e-7. Verify alpha independently. Automatically choosing tiling under VRAM pressure is still open.
- [x] Pass 12,296 local Windows assertions, the Vulkan smoke/parity suite, and 105 first-launch plus 108 restart assertions. Both live runs included unscaled 24 MP export. The source catalog stayed unchanged.
- [x] Fix the worker-shutdown EOF/cancellation race exposed by Windows Server 2025. The preceding matrix passed on Server 2022. Verify the updated matrix run on both images. [Initial matrix](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44622033511)

Evidence is under `.test-results/contracts-memory-admission-20261119`, `.test-results/host-memory-admission-20261119` and `.test-results/gpu-tiled-parity-20261119.log`. The installed runtime still identifies `ffc4819`.

## Vulkan memory admission checkpoint

- [x] Read device-local budget and usage on the selected Vulkan adapter. Use a conservative heap-derived budget where the extension is unavailable. Estimate graph memory and choose full-size tiling when needed. Keep the full-frame synthesis requirement explicit. [Vulkan memory budget](https://registry.khronos.org/VulkanSC/specs/1.0-extensions/man/html/VkPhysicalDeviceMemoryBudgetPropertiesEXT.html)
- [x] Test a 128 MiB simulated cap, correct tile selection and pixel parity, and recovery after refused synthesis and source-buffer requests. The previous accepted source survives both.
- [x] Run the 6000 × 4000 optics case at 2/3/4 GiB simulated allocator caps. It used 845 MiB reported Vulkan allocations compared with 4,033 MiB full-frame. Warm median was 1.79–1.85 s tiled and 0.51 s full-frame on this RTX 3090. Requested dimensions stayed the same.
- [x] Rebuild the helper and pass 106 first-launch and 109 restart assertions in Capture One with the new GPU metrics. The source catalog hash stayed unchanged.
- [x] Both public Windows CI images passed 12,297 assertions, including DPI and the symbolic-link fixture. [Windows contracts](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44623654332)

Evidence is under `.test-results/gpu-memory-adaptive-20261119.log`, `.test-results/renderer-gpu-budget-*-20261119.json` and `.test-results/host-adaptive-vram-20261119`. Physical low-VRAM devices, multiple concurrent host windows and larger exact exports remain open. The installed runtime still identifies `ffc4819`.

## Larger exact export checkpoint

- [x] Keep requested native or recipe scaling. Validate up to 100 MP and 16,384 pixels per side, then admit based on system and GPU memory. Grain synthesis retains its 32 MP limit.
- [x] Validate worker dimensions and packed-pixel capability before sending pixels. An older worker with a smaller declared limit can still render a supported image after refusing a larger one.
- [x] Render 45 and 61 MP synthetic inputs through actual Vulkan at exact size. Separately reopen a 45 MP RGBA64 PNG and 61 MP RGB48 TIFF with exact sampled codes, ICC and DPI. A simulated allocation failure leaves accepted output intact.
- [x] Pass 12,319 local contracts, 11 large-encoder checks, and 107 first-launch plus 110 restart assertions in Capture One. The disposable host checked recipe dimensions in the Save dialog and matching output, as well as an unscaled 24 MP DNG. The source catalog stayed unchanged.

Evidence is under `.test-results/contracts-large-export-final-20261119`, `.test-results/renderer-large-export-20261119.json`, `.test-results/large-encoder-20261119.log` and `.test-results/host-exact-export-capabilities-20261119`. The larger worker and encoder tests use synthetic inputs. The installed runtime still identifies `ffc4819`.

## Cached catalog and Viewer lookups checkpoint

- [x] Cache typed service getters and reuse the palette binding for each live form. Strict approval uses the active document's exact database path and accepts either that database or its parent directory from the manager. The recurring checks do no catalog directory enumeration.
- [x] Keep the watchdog for selection and pending writes and skip a flush when the write queue is empty. Startup catalog validation and backup retain their existing file checks.
- [x] Run 113 first-launch and 116 restart assertions. The real host matched the native database path, rejected mismatched and unrelated network paths, and returned the same document and palette. The source catalog hash stayed unchanged.
- [x] Compare 5,000 repeated lookups on the same UI thread in both launches. Repeated reflective discovery took 32.52 and 45.97 ms. Cached validated lookups took 2.18 and 1.90 ms. These measurements cover host lookups alone.
- [x] Both public Windows 2022 and 2025 jobs passed on the larger exact export checkpoint. [Windows contracts](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44626037770)

Evidence is under `.test-results/host-cached-identity-20261119`. An event-led form/workspace lifecycle and unavailable-network startup fixtures remain open. The installed runtime still identifies `ffc4819`.

## Exact host compatibility manifest checkpoint

- [x] Make one tracked manifest the source for launcher, staged build and the panel's embedded compatibility gate. Each entry binds version, architecture, assembly hash and declared capabilities. The verify command checks the executable's PE machine and release component fingerprints.
- [x] Verify the known host and reject an architecture, binary or version mismatch in shell and managed tests. Restore and installer fixtures include the manifest.
- [x] Run 12,330 local contracts, including 14 manifest assertions. Pass the workspace-staged launcher CheckOnly with the supported installation and actual Vulkan. Capture One passes 113 first-launch and 116 restart assertions. The source catalog hash stayed unchanged.
- [ ] Complete an independent 16.7 adapter and host matrix on a licensed installation. Per-surface readiness reporting is a separate follow-up to the manifest.

Evidence is under `.test-results/host-manifest-gate-20261119`, `.test-results/contracts-host-manifest-20261119` and `.test-results/host-compatibility-check-20261119`. The installed runtime still identifies `ffc4819`.

## Reproduced development package

- [x] Build and stage a clean `8bb999e23d0cd21e6d4a938c999dcd3583f2060b` checkout. Package `dist/Spektrafilm-for-Capture-One-v0.2.0-audit.2-windows-x64.zip`. SHA-256 is `ce194d1f13a0548cb2176915cc8bb5e0f5e3431f440fe7baad7540da52d0b1b2`. Its build manifest records 18 runtime components and its package manifest lists 149 payload files.
- [x] Pass CheckOnly from the extracted package and an isolated installation. Verify the test Desktop shortcut's launcher and film icon. The ZIP contains exactly its listed payload and manifest, and it links to the exact source commit.
- [x] Refuse a modified shader, either scientific table, stocks file, helper, panel or host manifest in an isolated tamper copy. Recheck the copied runtime after restoring its originals.
- [x] Rebuild the same commit in a fresh detached checkout. Both generated tables, profile curves/counts and all packaged shaders match. The raw tree digest differs due only to line endings across 19 files. The host JSON differs only by line endings. The panel DLL and native EXE have different PE timestamps and byte ranges. The report lists the differing sections.

Evidence is under `.test-results/audit-package-compatibility-20261119`, `.test-results/package-repro-20261119.json`, `.test-results/package-tamper-20261119` and `.test-results/build-clean-repro-8bb999e.log`. The normal launcher and user installation still identify `ffc4819`. The new ZIP is an isolated development artifact.

## Runtime map for the next agent

| Responsibility | Main files and contracts |
| --- | --- |
| Startup, catalog opt-in, native tab and Export binding | `Start-Spektrafilm.ps1`, `ApprovedCatalog.cs`, `StartupHook.cs`, `HostProfile.cs`, `ExportHook.cs`. Exact host gate; one approved catalog; attach warning persists if Export binding fails. |
| Native panel and saved controls | `SpektraFilmTool.cs`, `NativeSlider.cs`, `LookSettings.cs`, `LookStateValidation.cs`. Native sliders/reset behavior, enable switch, presets, recovery, floating native window. |
| Per-variant persistence | `VariantLookStore.cs`, `PanelFiles.cs`. Absolute catalog-path hash plus variant ID; primary state, shared pending session state and alternate `Look recovery` checkpoints. Unknown/corrupt/future state blocks writes until explicit recovery. Both storage paths failing cannot be made crash-safe; UI says the latest edit is not safely saved. |
| Machine preferences | `PanelPreferences.cs`. Viewer/drag quality, draft synthesis and floating rectangle are separate from creative looks; compare-source is transient. Stable section keys and floating scroll restore independently of creative settings. |
| Host pixels and geometry | `HostImageSource.cs`, `ViewerGeometry.cs`, `ViewerPreview.cs`. Capture One RAW development and native settings, 16-bit source to float, viewer overlay follows native geometry. This is developed bounded data, not proven pre-tone-map scene-linear RAW. |
| Preview scheduling and colour | `SpatialPreview.cs`, `ViewerDraftSource.cs`, `ColorTransfer.cs`, `RenderCoordination.cs`. Source/render identities, original-depth settled routes, linear-light downsampling, draft cancellation/final priority. No global memory admission or cooperative GPU kernel cancellation yet. |
| Worker and LUT | `OpticalRenderer.cs`, `WorkerEnvironment.cs`, `NativeLutExporter.cs`, `ofx-still-renderer/main.cpp`. Protocol 1 `HELLO` / `METRICS`, parameter SHA and layout validation. Worker child strips inherited `SPEKTRAFILM_*` and startup hooks. LUT uses the same Vulkan colour engine with explicit spatial exclusions. |
| Image output | `ExportOperation.cs`, `SpektraExport.cs`, `OutputPathGuard.cs`. Captured job ownership, explicit cancel, atomic commit, original-path checks, 16-bit sRGB TIFF/PNG. Recipe integration remains limited. |
| Native grain | `NativeGrain.cs`, `NativeAdjustmentTransaction.cs`. Undo-grouped setter sequence and rollback with aggregated errors. Catalog-scoped receipts restore matched native settings across restarts. |
| Build, installation and evidence | `ReleaseManifest.ps1`, `Build-Windows.ps1`, `Package-Windows.ps1`, `Install-Spektrafilm.ps1`, `Test-Windows.ps1`, `tests/`, `qa/HostSmoke/`. Manifested builds, preserved custom files, regression fixtures and explicit hardware/host test tiers. |

## Next work and known constraints

Consult the detailed JSON before implementation. Useful next bounded packets are:

1. Finish host fault and race coverage for `FIX-03`, `HS-03`, `HS-04`, `HS-19`, `VE-01`, `VE-05`, and `HS-15`. Check active document closure, adjustment revision races, delayed teardown callbacks and live-host state or grain failures. Build on the validated startup warning and real selection transitions described above.
2. Agree model/parameter/capability contracts (`HS-18`, `HS-11`, `HS-12`, `RC-07`) before native undo, copy/apply/clone (`HS-05`, `HS-06`) or broader host compatibility. One owner should edit each shared UI/scheduler region.
3. Complete export policy (`VE-02`, `VE-03`), including ICC intent, metadata, naming, crop and orientation edge cases, and explicit handling of unsupported recipes. Current output is 16-bit sRGB TIFF/PNG. Recipe exports use its print resolution, and direct exports use 96 DPI. The host has exercised confirmation and previous-settings commands. The real OS Save picker and broader recipe matrix are still open.
4. Add measured resource admission and generation-aware scheduling (`RC-11`, `VE-09`, `VE-07`, `VE-08`, `VE-17`, `RQ-09`) before increasing output limits. Export and native Viewer share a 100 MP / 16,384 px safety bound under current system and GPU admission. Grain synthesis uses a distinct 32 MP full-frame bound. Measure multi-context overlap and physical lower-VRAM adapters.
5. Solve portable catalog identity/restore (`HS-02`, `RQ-04`) and native grain revision and receipt pruning (`HS-08`). Verified joint restore rebases saved looks to the chosen path. Moving a catalog outside that flow still changes its path-based state key.
6. Treat histogram/proofing/native pixel hooks (`VE-10`, `VE-11`), multi-view and sessions as exact-build feasibility work. An overlay does not prove native analysis tools describe the processed image.

Other open work includes floating DPI, screen placement and workspace persistence, complete presets, native-grain control/model alignment, blur-scale behavior, global LUT/job progress, viewport-aware quality, cache/GPU-copy optimizations and broad camera/GPU performance matrices. Public source lacks licensed SMPTE printer-density tables, so the corresponding printer-density mode remains unavailable. Do not enable unsupported controls or claim measured speedups from code inspection.

## Commands and operating constraints

Use PowerShell in the active worktree. Prefer code, APIs and command-line fixtures; the user wants computer use only when strictly necessary. Do not write into a live user catalog. Back up before any fundamental installed/state change. Do not send messages to third parties without user authorization. Retain upstream attribution, the user's product name, personal website, Lensflare plug and donation links.

```powershell
git status --short
dotnet build NativePanel/NativePanel.csproj -c Release -p:TargetPlatformDisplayName=Windows
./Test-Windows.ps1 -ResultsDirectory .test-results/next-contracts
dotnet run --project tests/renderer/RendererContracts.csproj -c Release -- --gpu .
python ofx-still-renderer/smoke.py --exe renderer/SpektraStillRenderer.exe
```

GPU commands require a complete built/staged `renderer` directory. The public contract tier requires no licensed Capture One binaries or GPU. This machine used .NET SDK 9.0.300 targeting .NET 8 Windows, MSVC/Visual Studio 2022, Vulkan SDK 1.4.350.0 and 64-bit Python 3.13. Fresh scientific build dependencies were supplied via a **process-local** `PYTHONPATH` to `D:\Library AI testing\SpektraFilm Capture One\.deps313`; restore the environment afterwards and do not install packages globally just to reproduce this build.

For a new runtime package, finish/commit source first, run `Build-Windows.ps1 -BuildPanel -StageRuntime` with the explicit working Python/Vulkan environment, then `Package-Windows.ps1 -Version <version> -Development` for a development build. Public release mode requires the exact clean tag and smoke checks. Source changes during build intentionally invalidate the final manifest.

For installed verification, run the script in a **fresh** Windows PowerShell process and check that process's exit code. Do not inspect a stale caller `$LASTEXITCODE` after invoking a script directly; that produced one false wrapper failure even though the check had passed.

```powershell
& "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File 'C:\Users\luka\AppData\Local\Programs\Film Tools for Capture One\Start-Spektrafilm.ps1' -CheckOnly
```

The opt-in `qa/HostSmoke/Run-HostSmoke.ps1` launches and deliberately terminates its own Capture One processes; it is **not** a read-only test. It requires no existing Capture One process, a closed source test catalog without transaction sidecars, a new results directory and a built test hook. It copies the database, isolates look storage, tests native grain only in the disposable catalog, and keeps source originals protected. Use a known test catalog, not a user's production database. It backs up and removes only crash markers generated during its own phases; it refuses pre-existing markers. Do not broaden that cleanup or clear user crash state to force a pass.

Earlier hidden launches stalled, and an immediate deliberate-crash restart stopped in Capture One's own crash detection before the UI loop. The final harness uses a normal interactive window, a delay, a per-run test-hook copy and bounded cleanup of its own markers. Evidence from `host-integration-1` through `-4` contains abandoned attempts; `host-final` is the successful run. Do not count those earlier attempts as passing or change the product compatibility gate to work around test startup.

## Launch readiness checkpoint

- [x] Give every launch a unique status file tied to the Capture One PID and exact host build. The optional launcher wait distinguishes missing startup from slow or rejected startup. Panel readiness, Viewer health and native Export availability have separate states.
- [x] The production launcher reader consumed readiness from a first launch and restart. The two runs passed 120 and 123 live assertions, including an abrupt kill of the real Viewer worker inside the disposable runtime and failure of both native Export callbacks. A normal launch then opened the same disposable test catalog without a Spektrafilm report. The source catalog hash remained unchanged.
- [x] Pass 12,337 public assertions, including 11 launch reader contracts for wrong session/PID, delayed startup and invalid reports.

Evidence is under `.test-results/host-launch-full-handshake-20261119` and `.test-results/contracts-launch-readiness-20261119`. Use `Run-HostSmoke.ps1 -CheckOrdinaryLaunch` to repeat the ordinary-launch observation. The previously verified development ZIP and regular installation predate this change.

## Durable writes and selective presets checkpoint

- [x] Prepare and hash each saved snapshot once. Keep the per-movement flush, external-change check before file replacement, and independent fallback. Separate processes were killed immediately before and after replacement and recovered the expected exact bytes.
- [x] Compare two 10,000-edit runs per build across 64 synthetic variants. Preparation and flush measured 2.86–2.88 ms median before and 2.73–2.79 ms afterwards. Managed allocations fell by about 26%. They measure the save path without WPF dispatcher overhead. The process-kill tests did not emulate device power loss.
- [x] Let users choose which film, optics, staged Grain and master settings to load or copy. In the licensed host, Copy and Apply across two variants preserved unchecked controls and actual Capture One Grain. One native Undo restored the destination. Print and Scan each round-tripped all saved sliders at both limits.
- [x] Pass 12,360 public assertions and 139 first-launch plus 142 restart assertions on the copied catalog. Capture One did not change the source database hash.
- [x] Extend selective copying to confirmed multi-selection, per-variant progress and batch recovery. The later sections track native clone lineage and the remaining adjustment clipboard work. Per-movement writes still wait for successful disk publication or recovery.

Evidence is under `.test-results/host-selective-presets-ranges-20261119`, `.test-results/contracts-selective-presets-20261119` and the four `.test-results/durable-*-20261119/measurement.json` files. To reproduce 10,000 measured edits, run `dotnet run --project tests/state/StateContracts.csproj -c Release -- --measure-durable-edits .test-results/<new-dir>`.

## Native grain, colour delivery and concurrent backups checkpoint

- [x] Display actual Capture One Grain alongside staged controls. Apply and Restore work with the Spektrafilm switch off. The live tests verified native Undo and Redo, a changed outside value, a restart with a restorable receipt and staging preserved by Restore. The later native grain checkpoint records separate clone and pixel-level export checks.
- [x] Share the existing strength blend between Viewer, high-depth image delivery and LUT export. Test all input and output transfers, signed and saturated values, continuity and non-finite inputs. Nineteen GPU checks passed. The packed Viewer path matched managed sRGB BGRA byte for byte. The half-strength LUT differed from the independent formula by at most 0.000007125 in normalized code values.
- [x] Fix the same-tick backup filename collision observed on Windows 2022. Serialize per-catalog backup publication and rotate only successful complete generations. Verify 42 joint-backup assertions in both PowerShell versions, including a competing lock and a deliberately blocked manifest. A real closed catalog backed up twice at the same instant and restored the later generation with both look hashes intact.
- [x] Pass 12,404 local contract assertions with physical hard-link and junction fixtures, plus 146 first-launch and 150 restart checks. The source catalog hash remained unchanged. Local symlink privilege was unavailable. The earlier launch and selective preset checkpoints passed the full Windows 2022 and 2025 CI matrix.

Evidence is under `.test-results/host-native-grain-state-20261119`, `.test-results/real-same-tick-backup-20261119`, and `.test-results/contracts-grain-backup-policy-20261119`. [Launch CI](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44630864725) and [selective preset CI](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44632131352).

## Automatic Viewer quality checkpoint

- [x] Add an optional Auto resolution up to 4096 px. Use the image’s projected physical long edge and available system memory. Hysteresis prevents rapid size changes around a zoom boundary. Native and manual resolution modes remain available.
- [x] Test missing/offscreen geometry, stable and jittered zoom, pan translation and pressure recovery. The licensed host reported a 2934 px projection and used a 4096 px 16-bit RAW frame. Manual 2048 px then produced the exact requested 16-bit size. The fixture left the real stored preference unchanged.
- [x] Pass 12,513 ordinary contracts and 149 first-launch plus 153 restart assertions with the source catalog hash unchanged. Local alias fixtures requiring privilege were skipped in this ordinary tier and passed separately where noted above.
- [ ] Verify live pan/zoom/DPI transitions across physical displays and quantify P95 drag latency. Regional 1:1 refinement and quality presets need follow-up work.

Evidence is under `.test-results/host-auto-viewport-quality-20261120` and `.test-results/contracts-auto-viewport-quality-20261120`.

## Hanatos 2026 checkpoint

- [x] Offer Hanatos 2026 in **Spectral reconstruction and DIR**. New looks still start on Hanatos 2025. Existing 2025 looks keep their saved method.
- [x] Verify 20 explicit stock calibrations and pass 480 GPU cases across Print and Scan, both Hanatos methods plus Mallett, and all four input encodings. Each stock and mode produced a distinct 2026 reconstruction with finite pixels and unchanged alpha.
- [x] Pass 152 first-launch and 156 restart checks. The actual panel persisted 2026 when selected and rendered the selected photo from a 16-bit Capture One source. The source catalog hash remained unchanged.

GPU evidence is in `.test-results/spectral-stock-matrix-20261120.log`. Repeat with `python ofx-still-renderer/smoke.py --spectral-stock-matrix --exe build/still-renderer/Release/SpektraStillRenderer.exe`. The licensed-host log is under `.test-results/host-hanatos-2026-20261120`. Canonical control and backend capability documentation remains open.

## Compiled renderer capabilities checkpoint

- [x] Generate and fingerprint `renderer/parameter-capabilities.json` from the renderer’s option tables, defaults and compiled calibration. The panel checks it before exposing choices. A build without Academy Printer Density rejects Process Negative.
- [x] Test all 137 described parameters and 558 parser and GPU cases. Include defaults, numeric bounds, every enum choice, rejected values and the APD prerequisite. Also verify every actual Print and Scan argument assembled from Capture One’s slider boundary presets.
- [x] In the licensed host, load a legacy look with Developer exhaustion, keep the value and original preset, refuse the request before starting the worker, clear the saved value explicitly, and verify native Undo.
- [x] Pass 12,523 ordinary contracts and 161 first-launch plus 165 restart assertions. The original database hash remained unchanged.
- [ ] Generate the expanded control and release documentation from the schema. Units, dependencies and exact host colour acceptance remain open. Runtime package provenance for this checkpoint is separate from the earlier development ZIP.

Evidence is under `.test-results/contracts-compilercaps-20261120`, `.test-results/renderer-parameter-matrix-20261120.log` and `.test-results/host-compiled-controls-20261120`. Run `Build-Windows.ps1` before HostSmoke to generate the required description.

## Development package with compiled capabilities

- [x] Run the full Windows source build and Vulkan smoke from clean `bb0c19e672aee861c74a03c7bb970c31d85a9180`. The runtime contains the compiled 137-option description.
- [x] Verify 150 payload entries and 19 runtime entries after extraction. Reject a modified capabilities file with otherwise valid JSON, restore the original bytes, install into a disposable folder, and run CheckOnly through a fresh Windows PowerShell process.
- [x] Verify the test shortcut starts Windows PowerShell with the bundled launcher and `-Shortcut`, uses the installed working directory, and points to the bundled film icon. Confirm the regular installation still identifies `ffc4819`.

The development archive is `dist/Spektrafilm-for-Capture-One-v0.2.0-audit.3-windows-x64.zip`, SHA-256 `72daea1a947362d14ea759f97a633b7c225b962bf372ebd4c080b3ad819dd6cc`. Its source tree digest is `6a504b9caf2290b917e7839cb03b0d4867238686852fd78989284cbd572097b6`. Evidence is in `.test-results/audit3-package-verify-20261120/result.json`. The ZIP remains a local development artifact.

## Close with unavailable look storage

- [x] Retry pending saves when Capture One closes. Manual close stays open by default when neither save location can protect the changes. Windows shutdown attempts a save without prompting.
- [x] Use a real exclusive lock on the primary JSON and a directory obstructing its recovery target. The licensed-host fixture exercised keep-open, explicit proceed, prompt failure and shutdown decisions without opening a prompt. Removing the fallback obstruction protected the exact pending value. Removing the primary lock published it.
- [x] Pass 168 first-launch and 172 restart assertions with the source database hash unchanged. The worker-crash probe now waits for the previous worker to drain and verifies the new process path before terminating it.
- [ ] Exercise disk exhaustion and OS or device power loss as separate cases.

Evidence is in `.test-results/host-close-storage-retry-20261120`. This source checkpoint postdates the development `audit.3` ZIP.

## Optional Python reference setup

- [x] Pin all 26 wheels by published SHA-256 for Windows x64 CPython 3.13. Offline installs consume the exact same lock.
- [x] Build a short immutable candidate directory. Check dependencies and run a nonzero reference LUT before making the candidate active. Preserve the previous selection on failed setup and serialize competing runs. Installer upgrades keep the optional environment as user state.
- [x] Compare two independent offline installs. Portra 400 Print matched SHA-256 `e17e56d936153c62a7cf0deea061da11cad5569ba6ea4145753d6ccf52e9db5b`. Provia 100F Scan matched `70329e0ca5ca83dc427c12af335c132b1cc9408e8501f1e45b57fd6493d9007b`.
- [x] Pass missing wheel, unsuitable interpreter, concurrent setup, reuse and atomic replacement checks. Pass 12,534 ordinary contracts, including the legacy selection fallback and installer preservation. The regular app and the previous development ZIP predate this checkpoint.

Evidence is under `.test-results/reference-setup-A2-20261120`, `.test-results/reference-setup-B-20261120`, `.test-results/reference-transforms-20261120` and `.test-results/contracts-pinned-reference-20261120`. The fixture exposed a Windows DLL path-length limit at the longer candidate name. The shorter name passed the same nested-path fixture.

## Processed Viewer histogram

- [x] Count the RGB codes and maximum channels in the settled sRGB Viewer bitmap. Show source gives its own histogram. Cancel scans if the frame changes, the panel hides, or the section closes. Each scan uses only one row of additional pixel memory.
- [x] Verify the production bitmap identity, exact pixel count, all three histograms, source comparison and WPF graph layout in Capture One. The first-launch and restart fixtures each wrote a graph PNG.
- [x] Scope looks and panel preferences to the fixture data directory at process start. The tests passed 12,539 ordinary contracts and 174 first-launch plus 178 restart checks. The source catalog hash remained unchanged.
- [ ] Add pixel picking and the clipping overlay. Verify native analysis and edit-tool paint ordering and physical monitor colour behaviour separately.

Evidence is under `.test-results/host-viewer-histogram-20261120` and `.test-results/contracts-viewer-histogram-20261120`. Users can expand **Viewer histogram** below the resolution controls.

## Persistent export progress

- [x] Write one catalog-scoped receipt per accepted export. Preserve the chosen path and variant while reporting native development, film rendering, encoding and terminal outcomes. Preserve prior outputs under cancellation.
- [x] Observe both film rendering and encoding live during the full 6000 × 4000, 16-bit RAW export. The completed record matched its actual dimensions and destination. Detach saved a canceled status, which loaded on restart.
- [x] Pass 12,547 ordinary contracts and 177 first-launch plus 182 restart assertions. Validate stale completion, malformed history and unavailable progress storage separately. The source catalog hash remained unchanged.
- [ ] Add resumable job descriptors, batch scheduling and full interruption coverage.

Evidence is under `.test-results/host-export-progress-20261120` and `.test-results/contracts-export-progress-20261120`.

## Native RAW working-colour matrix

- [x] Use real CR3, RAF, NEF and DNG files in a copied catalog. Develop each in sRGB, Adobe RGB, ProPhoto RGB and Linear Rec.2020 at 16 bits, then render through Vulkan to linear Rec.709. All 16 cases preserve geometry and alpha within their camera fixture.
- [x] Independently convert native buffers to sRGB with Colour Science. P95 absolute per-pixel channel difference in the within-range regions was 0.000685–0.008187 for Adobe and ProPhoto and 0.006365–0.033348 for Linear Rec.2020. Interpreting those encoded values directly as sRGB produced larger errors in every case.
- [x] Pass 43 native assertions and match the four RAW checksums and original catalog hash. The matrix verifies Capture One-developed values and the film renderer seam. Monitor profiles, proofing and earlier sensor-domain data remain separate acceptance work.

Run `Run-HostSmoke.ps1 -ColorMatrix -SkipRestart` with the four-camera test catalog, then `python qa/analyze-native-colour.py <results-directory>/color-matrix`. The local evidence is under `.test-results/host-native-colour-matrix-20261120`.

## Developed frame identity

- [x] Carry the exact captured ICC fingerprint, working encoding, native adjustment revision, selected variant and format on developed and derived draft frames. Use the captured identity when reusing Viewer sources and validating export snapshots.
- [x] Pass 59 native colour checks across four RAW formats and four working profiles. All four camera fixtures reported the same exact ICC fingerprint for each encoding, and the four encodings had distinct fingerprints. The independent common-sRGB analysis still passed all twelve profile comparisons.
- [x] Pass 12,547 host-independent assertions and 177 new-launch plus 182 restart checks in a copied Capture One catalog.
- [x] Measure the supported native image stage against the calibrated RAW input, default and Linear Response curves, exposure, white balance, highlights, ICC replacement and same-stage TIFF. The earlier-stage feasibility probe and typed frame contract are recorded below.

Evidence is under `.test-results/host-frame-description-matrix-20261120` and `.test-results/host-frame-description-20261120`.

## Versioned renderer requests

- [x] Negotiate protocol 2 between the panel and worker. Verify exact layouts, dimensions, input-reuse geometry, response IDs and bounded payload lengths. Distinguish recoverable request errors from framing errors that close the worker.
- [x] Pass 12,587 host-independent assertions, including 134 renderer checks, 198 state assertions and injected cancellation during both upload and reply. The checked-in smoke also exercises mismatched layouts, repeated IDs, fatal frame lengths and legacy tools. Nineteen managed GPU checks passed.
- [x] Pass 177 new-launch and 182 restart checks in the licensed host, plus 59 native RAW colour checks across CR3, RAF, NEF and DNG. Both source catalogs retained their initial checksums.
- [ ] Add a separate cooperative worker cancellation and progress channel. Protocol 2 currently terminates a canceled worker and waits for exit before replacing it.

Evidence is under `.test-results/contracts-numbered-renderer-final-20261120`, `.test-results/numbered-renderer-runtime-20261120`, `.test-results/host-numbered-renderer-final-20261120` and `.test-results/host-numbered-renderer-raw-20261120`.

## Enlarger transform reuse

- [x] Keep one transformed Viewer frame up to 512 MiB and reuse its GPU source when other creative controls move. Keep the original CPU frame intact, release the cached transform at identity and decline to retain it above the bound. The export route still renders the full image at float precision.
- [x] Match eight fresh Vulkan renders byte for byte, including odd dimensions, alpha, borders, new sources, offset changes, identity and 32×. Repeated print changes measured zero transform time and zero source upload. A 36 MP probe reported no retained transform buffer.
- [x] At 6000 × 4000 on the RTX 3090, warm worker median fell from 610 to 216 ms and p95 from 719 to 231 ms. At 4095 × 2731 median fell from 296 to 101 ms. These timings include the worker and pipe. The licensed Capture One suite passed 177 first-launch and 182 restart checks.
- [ ] Validate cache interruption on the licensed host and additional hardware. Simultaneous original and transformed source residency on the GPU remains open.

Evidence is under `.test-results/enlarger-before-20261120.json`, `.test-results/enlarger-after-20261120.json`, `.test-results/enlarger-cache-smoke-20261120.log` and `.test-results/host-enlarger-cache-20261120`.

## High-depth input and export conversion

- [x] Read native RGB16, RGBA16 and RGB32F directly from validated row spans. Keep one owned float buffer, exact normalization and a shared cancelable CPU allocation for large conversions. A controlled blocked read stays valid through cancellation and drains before native memory is released.
- [x] At 6000 × 4000, final half-strength RGB16 conversion dropped from 318 to 52 ms warm median with all 72 million output codes equal on the same source and film inputs. Twenty-four more cases cover cropped RGB16 and RGBA16 in four input spaces and three strengths.
- [x] Retain the faster lookup implementation after measuring a slower SSE alternative. Direct source conversion allocated roughly 0.29–0.38 MB less at 24 MP with a warm median 0.9–1.8 ms above the previous parallel Viewer conversion. Export now shares the high-depth route. The full end-to-end export latency was not isolated by the conversion benchmark.
- [x] Pass 177 first-launch and 182 restart checks in licensed Capture One and 59 native RAW colour checks. Separate native TIFF acquisitions had identical 16-bit dimensions and ICC but showed small pixel variation between previous builds as well. Exact conversion comparison uses the same captured input on each path.

Evidence is under `.test-results/contracts-high-depth-final-20261120`, `.test-results/contracts-viewer-analysis-edges-20261120`, `.test-results/host-high-depth-final-20261120` and `.test-results/host-high-depth-matrix-20261120`. Use ViewerContracts with `--benchmark-rgb16 <report.json>` or `--benchmark-export-pixels <report.json>` for the CPU measurements.

## Viewer pixel inspection and clipping overlay

- [x] Sample the exact settled Viewer bitmap in sRGB using the inverse native paint transform and physical DPI. Bound the sample to its current image, clip and visible pixels.
- [x] Draw the optional one-bit clipping mask after the correct frame. A real Capture One WPF drawing-tree check verifies the order. Switching to Show source updates the histogram, picker and mask. Closing the analysis section clears the overlay and stops work.
- [x] Pass 12,659 host-independent assertions and 183 new-launch plus 188 restart checks. The original catalog hash was unchanged.
- [ ] Verify native crop, mask, focus and annotation modes independently. Monitor proofing and native analytic service integration remain open.

Evidence is under `.test-results/contracts-viewer-analysis-edges-20261120` and `.test-results/host-viewer-analysis-painter-20261120`.

## Development package with protocol 2 and Viewer analysis

- [x] Build `0.2.0-audit.4` from clean `b1766d0c954e59c6dfb4a72ed0ad659f94326d73`. The source digest is `8c15df03bdf5ee4878ead5478f9e92d24cc6e55292b9e3d002288966deaea6f2`. The ZIP SHA-256 is `dd615403a6fd584c024b4d91d3774aadfc4c219caf4e548c3aa3a8d69bb5423b`.
- [x] Verify all 151 payload and 19 essential runtime entries. Reject a changed compiled description. Install into a disposable folder and verify its film-icon shortcut, Windows PowerShell 5.1 launcher and Vulkan check. Pass 19 managed GPU checks, 558 parameter probes and 480 spectral renders.
- [x] Pass the licensed host suite using the installed package’s exact panel and renderer binaries. There were 183 new-launch, 188 restart and 95 RAW colour checks. The tested catalog files retained their original hashes. [Windows Contracts](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44647341973) passed for the packaged source.
- [x] Confirm the regular installation still identifies `ffc4819`. This ZIP is a local development package. The source changes remain on the draft PR branch.

Use `Run-HostSmoke.ps1 -PackagedRuntime <installed-or-extracted-package> -SourceCatalog <copied-catalog>` to copy the exact verified binaries into a new host test fixture. Evidence is under `.test-results/audit4-package-verify-20261120` and `.test-results/host-audit4-exact-package-20261120`.

## Native camera adjustments and ICC identity

- [x] Run the four RAW cameras through the installed development package. Record the actual Capture One camera ICC names and byte hashes. A separate analysis independently verifies the working-profile transforms.
- [x] Vary native exposure by one stop in each direction, then test white balance and highlight recovery. The 95-check suite verifies changed source revisions, unchanged canvas and alpha, and exact restoration of the native adjustment values and white balance multipliers on the copies.
- [x] Measure the Linear Rec.2020 developed data. Median +1 EV gain was 1.94–2.58 and −1 EV gain was 0.316–0.369, with gains varying within each image. Those values describe Capture One’s native developed response.
- [x] Compare same-stage native TIFF and bitmap destinations under identical settings. Four TIFF files reproduced all 2,004,657 RGB16 codes and the exact Linear Rec.2020 ICC bytes. The licensed matrix passed 111 assertions.
- [ ] Calibrated sensor-chart and earlier host seams remain open.

Evidence is under `.test-results/host-native-camera-icc-20261120` and `.test-results/host-native-adjustments-restored-20261120`. Run `python qa/analyze-native-adjustments.py <results>/color-matrix` to reproduce the exposure and white balance report.

## Verified renderer assets and portable preset provenance

- [x] Verify the installed renderer inputs before the first frame. Keep the still worker’s asset lookups in the renderer directory. The production managed worker rejected a same-length shader mutation and recovered the same asset fingerprint after restoring the bytes.
- [x] Verify that an alternate resource folder cannot supply a missing native shader. Two clean MSVC rebuilds produced the same `0228f69acd84fd387b4785ad3b5a74503cfcd6100cfa1322588a3e16164298d7` renderer hash.
- [x] Add optional verified source and renderer identities to version 3 presets and native LUT output. Retain the earlier version 2 file shape for source-only runs. Host tests exercised the real preset Save action in the source layout without changing the durable look.
- [x] Pass 12,672 host-independent assertions, the real Vulkan smoke, and 186 first-launch plus 191 restart assertions on the disposable catalog.
- [x] Verify the actual version 3 preset exported by the installed development ZIP. Its asset hash matched the real worker and its source identity matched the build manifest.
- [x] Resolve reproducibility across checkout paths with MSVC source-path normalization. Two independent roots with separately placed scientific build data produced the same `88fe9655cc789f2fb2e848e0b59972233f114d0778e5abf572cb84b497fbc111` binary as the ordinary audit build. Full Vulkan smoke passed. This proof uses the same toolchain.

Evidence is under `.test-results/contracts-runtime-provenance-final-20261120`, `.test-results/runtime-repro-link-20261120`, `.test-results/runtime-assets-real-20261120`, and `.test-results/host-runtime-provenance-dev-20261120`.

## Development package with verified renderer provenance

- [x] Build `0.2.0-audit.5` from clean `bbeae737277a6d7baf9b933b639ae703dce10fba`. The source digest is `b1ad8613618411e7228ba3e464684a5f1ceefb2ba52d87221da324e39dd37926`. The ZIP SHA-256 is `d3bb2c541ffb1e1f2bdf88f68fc055cccd923aa8529da983053da0da50e5cb78`.
- [x] Verify all 151 payload and 19 runtime records, the isolated shortcut and Windows PowerShell launcher. The 20 managed GPU checks report runtime asset hash `1bfde4ed7fad084de14a0359c51b0447c9fe7af57c0ba0b8664f723ca98da852`.
- [x] Pass 186 first-launch, 191 restart and 111 RAW colour checks in Capture One with the exact package. Its actual preset Save action writes version 3 with the same verified asset hash. [Windows Contracts](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44650162887) passed for the packaged source.
- [x] Confirm the regular installation still identifies `ffc4819`. The ZIP remains a local development package and the source is available in the draft PR.

Evidence is under `.test-results/audit5-package-verify-20261120`. The source-path fix was validated after packaging audit.5. Its evidence is under `.test-results/runtime-compiled-path-repro-20261120` and `.test-results/renderer-path-normalized-smoke-20261120.log`.

## Automatic exposure reuse

- [x] Reuse automatic exposure for one immutable source, input space and meter method. The full and tiled paths share the same key. Sixteen cases matched their respective fresh renders byte for byte, including invalidation after changing the source, input colour or meter method.
- [x] Record per-request meter stage time and hit state. The 100-edit sweep reused metering on 99 requests. At 1200 × 800, warm median changed from 10.26 to 9.69 ms. The 4095 × 2731 worker median was effectively unchanged.
- [ ] Add the later film-density, print and scanner cache stages and validate them under native motion and memory overlap.

Evidence is under `.test-results/metering-cache-smoke-20261120.log` and `.test-results/metering-*-20261120.json`.

## Native control labels and held comparison

- [x] Use the stock pickers’ native name search. Provide full film slider labels, displayed defaults and reset actions for automation and tooltips.
- [x] Add Hold to Compare through the WPF pressed state. A licensed panel test switches to the exact cached developed source and back to the original film bitmap. The master look and Show source state remain owned by their existing controls.
- [x] Pass 190 new-launch and 195 restart checks with the native controls in the disposable catalog.
- [x] Save stock favourites by stable ID and show them first after restart. Keep the existing selected stock and durable look. The recent preset menu lists up to six saved or applied presets. Verify future preference formats remain untouched.
- [x] Pass 12,678 host-independent and 193 new-launch plus 197 restart checks with the favourites and recent preset routes.
- [x] Route native label and slider double-clicks and commit decimal numeric input in Danish and U.S. English through the real WPF editor. Verify the saved values, reset and restoration in a 207-assertion licensed test.
- [ ] Verify cancel and invalid input, screen-reader navigation, focus travel and physical DPI.

Evidence is under `.test-results/host-native-controls-20261120`, `.test-results/contracts-stock-favorites-20261120`, `.test-results/host-stock-favorites-20261120` and `.test-results/host-native-slider-keyboard-20261120`.

## Workspace selection and tool placement

- [x] Preserve Capture One’s active tool tab on ordinary launch while loading Spektrafilm controls and bindings. An explicit `-ShowTool` argument or the desktop shortcut requests selection.
- [x] Keep one live tool instance and refuse occupied slots. The known slot selector passes a full-list exhaustion test without writing new workspace tabs.
- [x] Pass 197 ordinary first-launch and 201 restart checks. The explicit selection path passes 196 native checks.
- [ ] Reproduce native workspace serialization and actual reorder/removal/reopen in a dedicated fixture. Permanent factory registration remains unsupported pending proof.

Evidence is under `.test-results/host-workspace-selection-20261120` and `.test-results/host-workspace-explicit-20261120`.

## Native Recipe Proofing guard

- [x] Suppress the film drawing while native Recipe Proofing is active. Returning to the regular Viewer repaints the same film bitmap and retains the edit.
- [x] Verify the exact WPF drawing in 201 licensed-host assertions. The opt-in fixture backs up native preferences and removes its exact recent catalog entry. The final profile comparison retained the two Capture One run counters that advanced during launch.
- [ ] Verify color-managed paint, separate recipe profiles, wide-gamut displays, custom proof and HDR against independent numerical patches.

Evidence is under `.test-results/host-recipe-proofing-final-20261120`.

## Physical blur baseline

- [x] Quantify capped scanner, DIR and grain kernels against the uncapped finite kernels in 48 cases. Confirm scanner response in 12 real Vulkan renders across 8 mm, 35 mm and IMAX film at four resolutions.
- [x] Reproduce the resolution drift. Standard 8 scanner unsharp at 100 µm measured 99.53 µm at 1,200 px and 41.81 µm at 16,384 px. The capped DIR longest lobe has the largest analytical tail loss.
- [x] Validate a CPU prototype for centered impulses. Sixteen capped cases with globally aligned tent reduction, reduced Gaussian convolution and interpolation preserve mass and centroid within numerical noise. Worst sigma error is 0.316% and normalized L1 error is 0.00184.
- [x] Reproduce scanner seams and alpha drift with a 4097 × 33 Standard 8 chart and stacked glare, MTF and unsharp. Old full versus tiled RGB differed by 0.03222 with 274,479 codes outside the 2e-6 tolerance. MTF changed alpha by up to 0.60313. Use the sum of actual finite scanner supports in both the tiled planner and inner dispatch and preserve alpha during MTF passes. Actual Vulkan output now has zero full-versus-tiled difference and exact source alpha. All Windows contracts and Vulkan smoke passed.
- [ ] Validate an appearance-versioned larger-support filter and tile margins. Compare energy, resolution, boundaries, full/tiled parity and performance before enabling it.

Evidence is in `.test-results/physical-blur-support-reference-20261120.json`, `.test-results/physical-blur-tent-verified-20261120.json`, `.test-results/scanner-composed-halo-baseline-20261120/old.json` and `.test-results/contracts-scanner-composed-halo-20261120/renderer-smoke.log`. Repeat the prototype with `qa/blur-support.py --prototype-tent`.

## Catalog identity and look isolation

- [x] Clone one test database byte for byte into two folders. The native catalog and variant UUIDs, the database basename and RAW source are identical. The plugin uses the full opted-in catalog path to keep distinct look files in one shared test data root.
- [x] Pass 97 assertions across four licensed launches. Both clones restored their own film exposure, drew their own film bitmap and exported a distinct full-resolution RGB48 TIFF. The source catalog and RAW hashes matched.
- [x] Back up the host preferences and remove only the two fixture recent catalog entries. Preserve other settings. Identify session interfaces in metadata for the supported version.
- [ ] Verify a physical camera tether. Concurrent catalogs, window replacement and a real Capture folder arrival in a native session have a separate checkpoint below.

Evidence is under `.test-results/host-catalog-isolation-painted-20261120`.

## Native Viewer layouts

- [x] Paint the Spektrafilm preview on the unique visible view of the selected image while the native Viewer shows one, two or four images. Re-read the live native list and reject stale callbacks from earlier view attachments.
- [x] Inspect the actual drawing tree as the selection changes in two- and four-image layouts and after returning to one image. Pass 22 focused layout checks, 202 first-launch and 206 restart checks, and 12,678 host-independent contracts. Symlink and junction creation were skipped in the sandbox.
- [x] Turn on native Before/After in its FullScreen comparison mode. The before view exists and Spektrafilm stops painting. The regular Viewer restores the identical cached film bitmap. The licensed fixture passed 26 assertions and restored the starting setting.
- [ ] Verify split comparison, external viewers, OS fullscreen, virtualization and DPI before permitting additional simultaneous film previews.

Evidence is under `.test-results/host-viewer-layouts-selection-20261120`, `.test-results/host-viewer-reattachment-regression-20261120`, `.test-results/contracts-viewer-reattachment-20261120` and `.test-results/host-viewer-native-comparison-exact-20261120`.

## Native Viewer's presentation-only motion

- [x] At manual 2048, exercise the native zoom setter and view center in Capture One. The painted projection changed from 2934 to 6000 px. Panning translated it by -1466 and +1022 px without changing projected width. The exact 16-bit source, film bitmap and worker render sequence stayed the same.
- [x] Restore the initial native Fit view and verify the same film bitmap. The first launch and forced restart passed 214 and 218 assertions.
- [ ] Extend these measured transitions to automatic-quality thresholds, additional layouts and native lens, crop and grain mutations.

Evidence is under `.test-results/host-viewer-native-motion-final-20261120`.

## Packed Vulkan Viewer readback

- [x] Pack full-strength sRGB display output on the GPU and keep the established float paths for partial strength and 16-bit export. In the enlarger case, the CPU packer retains the original alpha.
- [x] Match five full-frame and tiled cases exactly to the scalar BGRA reference. Compare every byte of the 24 MP BGRA preview and 24 MP float export against the previous renderer. Both match.
- [x] Pass 23 managed GPU checks including cancellation, worker recovery, cache reuse and transition back to float. Pass the full Vulkan smoke suite.
- [x] Pass 202 first-launch and 206 forced-restart Capture One assertions with the new renderer, including 16-bit full-size RAW export and the native proofing guard.
- [x] Measure warm 24 MP worker p95 of 56.30 ms against 304.46 ms previously across 21 runs per size. GPU readback decreased from 384,000,000 to 96,000,004 bytes on the RTX 3090. This timing covers the worker request and reply.
- [ ] Evaluate optional GPU blending for all look strengths, enlarger alpha and shared memory or texture delivery. Verify more GPUs and physical device-loss recovery.

Evidence is under `.test-results/rc13-*-20261120.json`, `.test-results/gpu-packed-final-smoke-20261120.log` and `.test-results/host-gpu-packed-20261120`.

## Development package audit.6

- [x] Build `0.2.0-audit.6` from clean `fd7056d437b262c0c59371d3b179952e9d199387`. Verify the ZIP checksum, its 151 package and 19 critical runtime records, the test shortcut and the live `-CheckOnly` route.
- [x] Pass 24 managed GPU checks, 202 first-launch, 206 restart and 115 native RAW checks from the isolated installed package. The saved version 3 preset carries the exact source digest, parameter contract and runtime asset fingerprint. [Windows Contracts](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44658495640) passed at the same commit.
- [x] Verify the regular installation is still `ffc4819`. Keep this ZIP as a local development snapshot during the wider audit.

Evidence is in `.test-results/audit6-package-verify-20261120/result.json`.

## Native 10,000-edit durability fixture

- [x] Exercise the real WPF exposure, print exposure and look strength events at their native step sizes across four RAW variants and 100 image visits. Reread each committed value after its synchronous acknowledgment.
- [x] Force Capture One termination after the edits, then reopen the disposable catalog. Restore all final values, master switches and exact file hashes. The focused fixture passed 16 first-launch checks and 20 restart checks alongside 10,000 individual write checks.
- [x] Measure the WPF event and durable acknowledgement together at p50 3.10 ms, p95 3.76 ms and p99 6.42 ms. The maximum was 97.24 ms. Viewer work was paused for this latency measurement.
- [ ] Verify physical disk-full and OS crash boundaries. Evaluate journaling and compaction against this baseline while preserving per-movement acknowledgement.

Evidence is under `.test-results/host-10000-restart-20261120`.

## Package capability documentation

- [x] Generate the supported Capture One host and compiled Vulkan option tables from the exact packaged manifests. Include the Markdown report and its checksum in new Windows ZIPs.
- [x] Generate 137 current renderer options and pass 20 PowerShell 5.1 manifest checks. The documentation cases verify locale-independent numeric output and rejection of mismatched or malformed descriptors.
- [x] Find the installed-file omission in the audit.7 fixture and add the generated document to the installer. Verify installation and preservation of prior user notes in the 30-assertion installer suite.
- [x] Verify the full audit.8 extracted and installed manifests, including 152 package files and 19 critical runtime files. The installed guide matches the regenerated 137-option table. The manifest rejects a changed copy of the guide.
- [x] Pass 24 GPU, 208 first-launch, 212 restart and 115 native RAW tests from the installed audit.8 package. The same source passed [Windows Contracts](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44661185246).
- [ ] Map every visible panel control to its applicable worker or host contract.

Evidence is in `.test-results/audit-capabilities-generated-20261120.md` and `.test-results/audit8-package-verify-20261120/result.json`.

## Capture One Export Recipe ICC delivery

- [x] Capture and validate the local RGB ICC profile selected by the recipe before the destination dialog. Convert the existing SDR film result at 16 bits and verify the exact ICC bytes in the temporary file before publishing it.
- [x] Verify ProPhoto and Linear Rec.2020 in both RGB48 TIFF and RGBA64 PNG. Reopened color codes and PNG alpha match the WIC conversion exactly. Confirm the captured ICC remains stable if its backing path is locked or replaced. Scoped color contexts keep handles out of global state.
- [x] Pass 12,706 host-independent checks and 210 first-launch plus 214 forced-restart assertions. The licensed test verifies the exact Adobe RGB profile in the native recipe route and the complete 6000 × 4000 RGB48 direct export.
- [x] Compare SDR patch results against the actual three ICC matrix/TRC descriptions. P95 Delta E 2000 ranged from 0.031 to 0.159, with a maximum of 0.803. [Windows Imaging Component format contract](https://learn.microsoft.com/en-us/windows/win32/api/wincodec/nf-wincodec-iwiccolortransform-initialize)
- [x] Build `0.2.0-audit.9` from clean `c0505c3`. Verify 152 package and 19 runtime files, the installed capability guide, 24 managed GPU assertions and 210 first-launch, 214 restart and 115 RAW assertions on the exact package. [Windows Contracts](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44662945600)
- [ ] Compare additional native recipe profile references and implement format-quality and naming policies within the 16-bit export requirement.

Evidence is under `.test-results/host-recipe-icc-final-20261120`, `.test-results/contracts-recipe-icc-final-20261120`, `.test-results/wic-icc16-probe-20261120` and `.test-results/audit9-package-verify-20261120`.

## Support report, bounded logs and protected looks

- [x] Add Copy support details to the docked and floated tool menus and Retry look save while an edit is pending. The report exposes build and renderer identities, Viewer state, native Export route and look durability with an explicit allow-list.
- [x] Queue only 256 diagnostic records and write JSON on a background worker. Rotate at 1 MiB into one previous generation. Contracts cover long text, line breaks, locking, concurrent writes, rotation and reopening.
- [x] Exercise missing native Export callbacks, recovery, master-off Export and locked reads in the licensed host. Check that native error paths are absent from the copied report.
- [x] Test a future look format, missing stock, malformed UTF-8 and oversized look. Each is retained byte for byte and archived on explicit recovery. The fixture supplies the backed-up valid generation. The real host passed 240 first-launch and 244 restart checks.
- [x] Run the standalone file and queue stress test for 600 seconds. Each logger received 148,608 records. The writable logger dropped none, with 985,178 current and 1,048,274 previous bytes. The locked logger dropped every record. P99 enqueue time for the pair was 0.0036 ms and the worst measured pair was 0.4652 ms.
- [ ] Test more native capability faults and stress end-to-end telemetry during native interaction.

Evidence is under `.test-results/state-final-isolated-20261120` and `.test-results/host-protected-look-formats-20261120`. The standalone `--measure-diagnostic-log` probe wrote its receipt to `.test-results/panel-log-ten-minute-20261120/measurement.json`.

- [x] Show which export route applies to the current variant, including master off, 0% Look strength and an unavailable look. Retain separate status for failed Export binding, Viewer processing and saved-look durability. Their typed diagnostic events contain state or exception type fields.
- [x] Exercise actual missing command callbacks, an absent copied Viewer worker and a real file sharing violation on a slider save. Verify the read retry, independent recovery checkpoint, simultaneous Viewer and save warnings, native export reconnection and the exact edited look after recovery. Pass 47 checks in `.test-results/host-support-capability-faults-20261120`. The source catalog stays unchanged.
- [ ] Run the native Copy support details menu round trip with an empty or text clipboard. The fixture skipped that operation after detecting existing rich clipboard data. Windows denied creation of a named private clipboard station and returned `ERROR_BUSY` when the probe tried to attach to an existing noninteractive desktop from a fresh STA thread. Use `Run-HostSmoke.ps1 -NativeSupportDiagnostics` with the backed-up native preferences when a suitable interactive clipboard is available. [Windows station clipboard ownership](https://learn.microsoft.com/en-us/windows/win32/winstation/window-stations)

## Verified previous look recovery

- [x] Separate default, valid, recovered, unsupported, corrupt and retryable read outcomes. Normal saves retain the exact prior valid bytes with a variant identity and SHA-256. The new generation is included in joint catalog backup and restore.
- [x] On a corrupt or missing current look, show the previous verified values read-only and offer explicit restore. A future format or unavailable stock remains blocked. Explicit recovery keeps dated, hash-verified copies of the rejected bytes and refuses to overwrite a file that changed while confirmation was pending.
- [x] In a licensed disposable catalog, create the older generation through an actual WPF slider edit. Test a future format, missing stock, malformed development dictionary, broken UTF-8 and an oversized payload. Actual restore reinstates the original saved values. First launch passed 265 assertions and forced restart passed 269.
- [x] The Windows suite passed 243 state assertions, 43 joint backup/restore assertions and 12,245 Viewer assertions. The child-process tests check previous-generation recovery and the digest and variant checks reject corrupted backups.
- [x] Repeat 10,000 acknowledged writes and a forced restart across four RAW variants after adding the previous generation. P50 was 5.66 ms, p95 6.47 ms, p99 7.29 ms and the maximum was 24.54 ms with the Viewer paused. The separate earlier run had p95 3.76 ms.
- [x] Install and verify `0.2.0-audit.10` from clean `3702f35`. The ZIP SHA-256 is `496dc5465b8e3160c9e56f0e20b4d0db2544930762011901b1bd032cff9acfb5`. All 152 package files and 19 critical runtime files verified. The isolated install passed 24 GPU, 265 first-launch, 269 restart and 115 native RAW assertions. [Windows Contracts](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44667871590)
- [ ] Measure concurrent Viewer work and total latency, and exercise true disk-full and power-failure limits under `HS-01` and `HS-04`.

Evidence is under `.test-results/host-verified-previous-look-20261120`, `.test-results/contracts-previous-generation-20261120`, `.test-results/host-10000-edits-previous-generation-20261120` and `.test-results/audit10-package-verify-20261120`.

## Native floating window placement

- [x] Clamp native floating bounds to the active monitor work area. Save the solved placement in WPF Closing while the window bounds still exist. The original tool instance returns to its dock when closed.
- [x] In a disposable 16.4.6 catalog, open an intentionally invalid placement, close, reopen within two physical pixels and redock. Both licensed launches passed, with 249 and 253 assertions. The complete local Windows suite passed, including 12,245 Viewer contract assertions.
- [ ] Validate mixed-DPI displays, monitor removal, RDP and keyboard focus, and implement workspace-scoped floating mode restoration. The licensed fixture used one physical monitor.

Evidence is under `.test-results/host-floating-save-before-close-20261120` and `.test-results/contracts-floating-and-logs-20261120`. [Windows window bounds](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowrect), [Monitor work-area selection](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-monitorfromrect)

## Capture One export metadata and keyword privacy

- [x] Use Capture One's native keyword serialization to honor All, None and selected shared libraries. Extract only the approved flat and hierarchical values. Export recipe-permitted title, headline, description, copyright, creator, capture date and rating in XMP and identify the exporter. GPS stays out of the packet. [Capture One keyword library export settings](https://support.captureone.com/hc/en-us/articles/360002545677-Controlling-keyword-libraries-on-export)
- [x] Generate a closed private test catalog and verify all four copied RAW fingerprints before and after launch. Test a detached native recipe, a real shared library imported under the fixture and native catalog edits. The accepted metadata snapshot was stable after later edits. Both TIFF and PNG reopened with exact saved metadata, 16-bit samples, ICC, dimensions and non-square DPI. The dedicated licensed run passed 27 assertions. The full host-independent suite passed with 12,249 Viewer assertions.

To repeat, run `python qa/prepare-metadata-fixture.py <closed-catalog> <RAW-samples> <new-fixture-root>`, then launch `qa/HostSmoke/Run-HostSmoke.ps1` with the generated catalog, `-MetadataFixtureRoot` and the exact `-NativeSettingsConfig`. Evidence is under `.test-results/host-native-metadata-policy-20261120` and `.test-results/contracts-native-keyword-metadata-20261120`.

## Vulkan worker cancellation and Export progress

- [x] Negotiate a per-worker Windows event and a numbered cancellation reply. Samples can stop before or between GPU tiles and after full-frame work. Drain a complete late reply without publishing it. A broken or stalled connection falls back to bounded shutdown before retry. [Windows named event API](https://learn.microsoft.com/en-us/windows/win32/api/synchapi/nf-synchapi-openeventa)
- [x] Expose validated progress for source, preparation, Vulkan, verification and readback. The export label honors the current accepted job. Optional messages leave the ordinary live Viewer transport unchanged.
- [x] Pass 157 managed worker checks, including blocked upload, delayed output, stale-reply drainage, timeout and invalid stage order. On the RTX 3090, 27 checks passed and an in-flight full-frame cancellation returned in 26.65 ms while keeping the same worker. Ten independent 24-tile trials had median 0.56 ms and maximum 1.75 ms from signal to reply, with each recovered frame matching its baseline hash. Licensed Capture One passed 265 and 269 first-launch and restart assertions.
- [x] Substitute `VK_ERROR_DEVICE_LOST` after a real GPU fence completes in a separately built fixture. Protocol 2 returns typed status 5 and protocol 1 returns ordinary status 1. Both recover the same pixel hash as the production worker. An actual Capture One run with that worker repainted the exact durable look from the same high-depth source in 561 ms using a replacement process. Export stayed connected.
- [ ] Exercise loss caused by a device or driver and repeat cancellation on slower hardware. An already submitted full-frame GPU command must finish before graceful acknowledgment. The process watchdog bounds an unresponsive child. [Vulkan fence behavior](https://docs.vulkan.org/refpages/latest/refpages/source/vkWaitForFences.html)

Evidence is under `.test-results/cooperative-vulkan-runtime-20261120`, `.test-results/contracts-cooperative-progress-20261120`, `.test-results/host-cooperative-renderer-20261120` and `.test-results/host-native-device-loss-20261120`. Use `qa/renderer-cancel-probe.py` for tiled cancellation trials and `qa/Build-DeviceLossFixture.ps1` plus `qa/device-loss-probe.py` for the disposable Vulkan fault fixture. The licensed fixture uses `Run-HostSmoke.ps1 -DeviceLossFixtureDirectory` and the exact native settings backup.

## Cancellation inside file encoding

- [x] Detect the actual Windows TIFF and PNG codecs writing more than 1 MiB to a private staging file for 24 MP output. Previously the jobs kept encoding after cancellation for 1.81 s and 2.62 s. The guarded commit still preserved the earlier output.
- [x] Pass the encoder a stream that checks the accepted job's token before each write, flush or length change. Normalize errors from a canceled WIC callback to the existing cancellation state. The positive export tests reopen exact TIFF and PNG sample codes through a cancellable stream.
- [x] Repeat each portable cancellation four times. TIFF returned for cancellation in 2.56–2.86 ms and PNG in 0.76–0.96 ms in these runs. Every case preserved the prior destination and source bytes and removed the staging file.
- [x] Use the real Cancel export button while the licensed 24 MP TIFF writes staging data. Both launches preserved the previous TIFF's full SHA-256, removed the staging file and wrote a canceled job receipt. The two measured returns were 5.16 and 5.60 ms. The full first-launch and restart suites passed 321 and 325 assertions. The independent four-RAW native effects Export matrix passed 191 assertions on each launch.

Run `dotnet run --project tests/ViewerContracts/ViewerContracts.csproj -c Release -- --cancel-file-encoding <new-results-directory>` to repeat the actual codec test. Evidence is under `.test-results/native-encoder-cancel-before-20261120`, `.test-results/native-encoder-cancel-after-20261120`, `.test-results/native-encoder-cancel-repeat-1-20261120` through repeat 3, `.test-results/contracts-interruptible-encoder-20261120` and `.test-results/host-interruptible-encoder-full-20261120`.

## Native lifetime and export storage failures

- [x] Run two separate 100-cycle float, close and detach soaks using Capture One's actual floating tool windows and normal watchdog. The first cycle style redocked before detach. The second alternated redocking and detaching while floating. Every retiring HWND was destroyed and all 300 tracked window, panel and tool references became collectible in each run. The second run aborted every queued selection callback. Its GDI count held at 378, USER handles went from 279 to 266, and total process handles went from 2,376 to 2,232. Host private bytes rose by 114 MiB after warmup, and post-GC managed bytes rose by about 3.2 MiB. The two runs passed 447 and 717 checks. [Windows GUI resource counts](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getguiresources)
- [x] Close the actual approved catalog form while an accepted recipe export is in the native bitmap callback and a second catalog remains open. The closed form canceled the job. The callback held the scheduler slot after ImageCore returned until the fixture released it. The previous TIFF bytes and saved look were preserved, the job recorded Canceled, the retired Viewer process exited, and reopening attached a new native tool and painted the saved look. Thirty-nine licensed checks passed. Separate licensed tests covered a canceled close and late Viewer development.
- [x] Use a real Windows directory deny rule to stop staging creation and a real read-only file to stop final publication. The output transaction keeps the original and previous TIFF exact in both cases. A denied temporary-file cleanup preserves the original cancellation and records the secondary failure. The fixture removes only its own temporary ACL rule and test file. Actual disk-full and crash recovery remain under VE-13.
- [x] Recheck licensed native metadata after the TIFF encoder signature gained compression. All 27 assertions passed in a fresh catalog with private RAW copies. TIFF and PNG reopened with exact 16-bit samples, ICC, native selected keyword policy and geometry. Source RAW and catalog hashes matched.

Run `qa/HostSmoke/Run-HostSmoke.ps1 -NativeLifetimeCycles 100` with the exact settings backup for the soak, or `-ConcurrentCatalogs -DevelopmentLeaseFixtureDirectory <isolated-runtime>` for actual form closure during the controlled bitmap return. Full Windows and Vulkan contracts also passed, including 12,281 Viewer and 255 state checks. Evidence is under `.test-results/host-native-lifetime-baseline-20261120`, `.test-results/host-native-lifetime-mixed-20261120`, `.test-results/host-native-form-close-export-20261120`, `.test-results/contracts-atomic-storage-20261120` and `.test-results/host-native-metadata-atomic-20261120`.

## Installer linked paths and failed rollback

- [x] Install `0.2.0-audit.11` from clean `d9a66f2` under a private destination. All 152 package and 19 critical runtime files verified. ZIP SHA-256 `97d8700bd0aa9c724023c2700a230d52fffa2b74b939f256380813598184a04b`. Exact-package tests passed 28 GPU, 265 first-launch, 269 restart, 115 RAW and 27 native metadata assertions. The regular installed revision remains `ffc4819`. [Windows Contracts](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44670497699)
- [x] Prevent required asset loads through a linked ancestor in the installer. Verify both staged manifests before promoting the copy. Directory cleanup leaves junction targets alone. [Windows directory deletion behavior](https://learn.microsoft.com/en-us/dotnet/api/system.io.directory.delete?view=netframework-4.8.1)
- [x] Pass 54 inert Windows installer contracts. Case-only collisions, Unicode paths, locked files, unavailable Desktop and real NTFS junctions preserve user files. When an external file occupies the destination after the first directory move, failed rollback retains both directories and reports both exact locations. The fixture manually restores the reported previous version and verifies its data.
- [x] Upgrade an actual isolated audit.11 install to audit.12. Verify both staged manifests, retained Unicode look and archived modified ICC bytes, shortcut and CheckOnly. The exact audit.12 package from clean `4e42336` has SHA-256 `7ef25e54b275250594986c6f7c4bbf150e6e9bc58d0cffc5ba2be2f6bad38d69`. It passed 28 GPU, 265 first-launch, 269 restart, 115 RAW and 27 native metadata assertions. [Windows Contracts](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44671168038)
- [x] Publish a synchronous per-destination recovery receipt before moving the previous install. The next installer verifies both owned paths, restores the previous directory and retains the interrupted candidate for retrieval. A per-destination process lock prevents simultaneous recovery or publication. Ninety-four inert installer assertions cover actual forced child death both before and after promoting the candidate, refused overwrites and unverifiable receipts, bounded rollback, and exact preservation of prior and interrupted user data. Full local Windows contracts and Vulkan smoke passed.
- [x] Keep the previous install and a generation-specific receipt after a successful update. The launcher waits for the exact new process to report Ready with native Export connected. It then rechecks the current generation, previous ownership receipt and every custom file before retiring the prior copy. An unconfirmed or degraded launch keeps it. Inert tests cover stale acknowledgment and a custom file changed after the upgrade.
- [x] Upgrade audit.12 to the exact clean audit.13 package in an isolated install. CheckOnly retained the prior runtime and ordinary startup retired it only after the actual profile-matched Capture One process reported Ready with Export connected. A nested legacy look and an externally stored look kept their exact hashes. The first runner attempts exposed Windows PowerShell module inheritance and a log handle inherited by Capture One. The final fixture handled both and restored its exact recent-catalog entries. Receipt at `D:/Library AI testing/sf-native-ready-20261120/installed-upgrade-receipt.json`.
- [x] Default native preset dialogs to the separate user data directory. Existing portable files remain available through their original location and recent history, and the dialog still accepts another folder. The current-source licensed first/restart regression passed 288 and 292 assertions.

Audit.13 was packaged from exact clean `9c3c09b` with 153 files and SHA-256 `89f13b3860f373907aef432595c8ada0b4307d646969e36a45c809c7494005d9`. Its installed-launch receipt precedes the new preset-dialog default. The user's normal installation still identifies `ffc4819`.

Evidence is under `.test-results/audit11-package-verify-20261120`, `.test-results/audit12-package-verify-20261120`, `.test-results/contracts-installer-failed-rollback-20261120`, `.test-results/contracts-install-hard-exit-final-20261120`, `.test-results/contracts-installed-ready-final-20261120` and `.test-results/host-external-presets-20261120`.

## Native watchdog and accessible numeric editing

- [x] Subscribe to the actual document manager and dispatch lifecycle changes with one queued check. Back off to a 4-second defensive poll after steady attachment and return to a 1-second cadence after notification or a persistence error. A real sharing violation recovered without input. Two 1,000-tick measurements averaged 8.03 and 7.70 microseconds per stable UI tick on this host.
- [x] Verify the native embedded text editor's editable UI Automation Value pattern and the outer full label and reset instructions. Exact ±8 EV values commit. Escape, NaN, Infinity and malformed text leave the saved file byte-identical. The first and forced-restart suites passed 282 and 286 assertions.
- [ ] Exercise unavailable network paths, external screen readers, narrow layouts and physical mixed-DPI monitors. The coalescing fixture invokes the registered notification path under the licensed UI dispatcher. Actual concurrent-document and form replacement have a separate checkpoint below.

Evidence is under `.test-results/host-adaptive-watchdog-accessibility-20261120`.

## Viewer identity and 100-visit licensed benchmark

- [x] Reproduce the last image’s paint flag persisting through a subsequent selection. The Viewer now revalidates the attachment against the current native selection when reporting paint readiness. Real paint already checked availability, so this fixes the stale readiness claim.
- [x] Use 100 settled visits and 30 rapid intermediate switches across DNG, NEF, RAF and CR3, 2048 px high-depth native development and strengths 0%, 50% and 100%. Every settled frame painted the selected variant. Event to settled paint measured p50 395.68 ms and p95 865.12 ms, with maximum 1658.03 ms. WPF edit through durable save with live Viewer enabled measured p50 6.01 ms, p95 7.02 ms and p99 7.34 ms.
- [x] Peak host-private memory was 7.93 GB, including Capture One allocations. Peak worker-private memory was 700.86 MB and peak managed use was 958.64 MB. Explicit drain exited the worker. All 100 sampled source objects were collectible and managed use afterward was 160.01 MB.
- [x] Repeat the full Windows contracts and licensed proofing, layout, export, cancellation and restart regression after the identity fix. Both licensed phases passed with 282 and 286 assertions.
- [x] Run the same 100 settled and 30 interleaved selections without loading the plugin. The native-visible layer measured p50 17.30 ms and p95 50.64 ms. Capture One private memory peaked at 5.03 GB and managed memory at 0.168 GB. In the paired film run, settled 2048 px paint measured p50 359.52 ms and p95 771.35 ms. Capture One peaked at 7.95 GB, managed at 0.928 GB and the renderer separately at 0.701 GB. All sampled source objects were collectible after drain. The two timing endpoints use different work and the memory peaks are sampled independently.
- [ ] Add per-stage timings across more sizes and adapters and controlled multi-context memory contention.

Repeat with `qa/HostSmoke/Run-HostSmoke.ps1 -ViewerChurn 100` using the disposable four-RAW catalog and `-NativeSettingsConfig`. Add `-ViewerNativeBaseline` for the no-plugin run. Evidence is under `.test-results/host-viewer-100-visits-current-paint-20261120`, `.test-results/host-native-only-viewer-baseline-20261120` and `.test-results/host-viewer-paired-live-20261120`.

## Structured timings for actual Viewer paints

- [x] Enqueue one `viewer.performance.v1` record when the current settled bitmap first reaches the native WPF drawing context. Include an ephemeral frame ID, settings generation, actual source format and dimensions, exact Capture One build, and the associated renderer request and asset contract when available. Record native development, render and transport, display bitmap creation and paint timing. A cached stage has no duration. The event carries no source path, catalog identifier or variant UUID.
- [x] Match all 100 selected 2048 px painted frames to their exact settings generation, high-depth RAW source and renderer request in a licensed run. Capture One development measured p50 324 ms and p95 835 ms. Film render and transport measured p50 72 ms and p95 96 ms across 66 film requests. Display bitmap creation measured p50 14 ms and p95 100 ms. First WPF draw submission followed bitmap readiness in p95 0.80 ms. The interaction-to-settled-paint p50 and p95 were 494 and 1015 ms. These subtimings separate the native event, Viewer request and WPF draw endpoints.
- [x] Repeat across 40 selected frames at 4096 px. Native development measured p50 401 ms and p95 808 ms. Film render and transport measured p50 226 ms and p95 380 ms across 26 film requests. Three same-variant edits traced actual cache reuse. The packed exposure edit took 56 ms and the transition to float took 208 ms from request to draw, with no repeated native development or worker upload. A following strength-only blend took 43 ms and used the existing float pixels without a Vulkan request.
- [ ] Correlate discarded generations and measure first visible draft latency during a ten-minute licensed interaction and broader hardware matrix. Earlier ten-minute results apply to the background logger in isolation.

Repeat using `qa/HostSmoke/Run-HostSmoke.ps1 -ViewerChurn 100 -ViewerChurnResolution 2048` with the disposable four-RAW catalog and exact native settings backup. The fixture also supports 1200, 4096 and native resolution. The raw and summarized timings are in `.test-results/host-viewer-stage-timings-20261120` and `.test-results/host-viewer-stage-4096-and-warm-20261120`. Audit.19 predates `viewer.performance.v1`.

The current-source general host suite passed 321 and 325 assertions after the timing change, and the managed worker passed 167 transport checks. Evidence is under `.test-results/host-viewer-timing-full-20261120`.

## Live Viewer worker recovery

- [x] Retry a settled Viewer request once after transport interruption or a renderer deadline. The retry waits for the previous worker to exit, keeps the exact high-depth source, rechecks the variant and settings generation, and clears the stale preview. The failure stays local to the Viewer. Bad render settings and incompatible protocol replies still produce their normal errors.
- [x] Kill the actual disposable licensed worker during a 4096 px film request. The restarted worker painted the exact committed edit from the same source in 883 ms on first launch and 823 ms after restart. Killing both the original and retry children left the Viewer in a terminal error with no obsolete frame or further worker, while native Export stayed connected. The exact saved bytes were unchanged by recovery.
- [x] Pass 12,255 host-independent Viewer assertions, the full Windows and Vulkan contracts, 294 first-launch assertions, 298 restart assertions and 26 native layout assertions. Six deterministic retry tests exercise keyed delay, attempt exhaustion, source changes and reset.
- [x] Expose **Retry Viewer** after automatic recovery is exhausted. Driving the real WPF action restored an exact high-depth source and unchanged saved look with native Export connected. Disabling during a separate in-flight final render drained its children and discarded its result. Re-enabling painted the unchanged saved edit with a fresh process. Each licensed Viewer stress launch passed 43 assertions.
- [ ] Exercise a stall inside Capture One's development operation and delayed work across detached and export-owned contexts. The native admission deadline and disconnected original are covered by dedicated fixtures. Measure the effect of competing workloads.

Evidence is under `.test-results/contracts-viewer-worker-retry-20261120`, `.test-results/host-viewer-retry-native-final-20261120`, `.test-results/host-viewer-layout-regression-20261120` and `.test-results/host-viewer-manual-retry-20261120`.

## Verified audit.14 development package

- [x] Build clean `ffb5174`, extract the 153-file Windows ZIP and validate both package and 19-file runtime manifests. ZIP SHA-256 `df16f40db3a55c0cf1cae9fe4deeaa8e53261d009c9c013262108345c9b4be95`.
- [x] Upgrade an isolated audit.13 install, verify CheckOnly retains its previous version and observe retirement only after licensed panel and native Export readiness. The exact installed runtime kept both legacy and external preset bytes. The regular user runtime is still `ffc4819`.
- [x] Pass 294 first-launch, 298 restart, 115 RAW matrix, 27 native metadata and 28 managed physical Vulkan assertions on the exact extracted build. Public Windows 2022 and 2025 passed on its exact source. [Public CI](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44680701028)

The combined receipt is `.test-results/audit14-package-verify-20261120/result.json`. The verified development ZIP is under `dist/`; the package and source were checked independently.

## Tagged Windows package validation

- [x] Require the full Windows contracts and a physical Vulkan smoke before creating a tagged non-development ZIP. Recheck the source digest after validation. The summary checksum is recorded in the package without test machine paths.
- [x] Test acceptance of a complete result and refusal of missing, failed, skipped, repeated and falsely claimed licensed results. The full local suite passed with 26 manifest assertions.
- [x] Package the exact clean `366e25e` source from an isolated clone tagged locally as `v0.2.0-audit.gate`. Missing local tag and a first fixture run that hit legacy Windows path length both refused publication. The successful short-path clone produced a 152-entry ZIP, required ten contracts and Vulkan smoke, and kept licensed validation listed separately. Archive SHA-256 `9e27be753c4152e82d2c9cc70bacfa305894f6b7383824d38004f79bb82faeb6`.
- [x] Download and inspect both receipts published by Windows Server 2022 and 2025 on draft PR #1. Each receipt records nine passing contracts and explicitly marks GPU and licensed Capture One as separate, unrunnable tiers on those runners. [Public CI run and receipts](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44675783266)
- [x] Verify a genuine cross-repository PR targeting the public experimental branch. No upstream push access was needed. [Contributor PR 2](https://github.com/nothing-complex/spektrafilm-for-capture-one/pull/2)
- [ ] Verify Windows contract execution on a fork PR targeting a branch with the audited workflow, and verify a licensed 16.7 host independently. PR 2 currently has only a skipped third-party check.

Evidence is under `.test-results/contracts-release-required-checks-20261120` and `D:/Library AI testing/sf-tag-gate-20261120/.test-results/tagged-release-proof.json`.

## Concurrent licensed Capture One catalog windows

- [x] Open byte-identical disposable catalog copies in two real native windows. The host File Open helper creates the second form. Enable the native multiple-window preference only within this scoped fixture. Across three focus cycles, the unapproved catalog never displayed or exported the approved look, never received a plugin look directory, and never changed the approved saved file. On return, the same tool and exact exposure were restored. Closing the other catalog left the approved one active. The repeat passed 26 licensed assertions. [Capture One multiple-window setting](https://support.captureone.com/hc/en-us/articles/360002649858-How-do-I-open-more-than-one-Catalog-or-Session-at-a-time)
- [x] Restore only the exact test paths from recent and last-open document lists and the prior native multiple-window setting. The first attempt exposed that Capture One can record one path in both lists. Verified backups were used to restore both fields and preserve unrelated settings. The final repeat exercised both restore paths automatically.
- [x] Close the approved native form while the second stays active. Verify the old panel, film paint, Export bridge and running worker deactivate. Reopen in the same process and verify a new native form, new tool and Export bridge, original variant, identical saved bytes and the visible film look. Thirty-three licensed assertions passed. The first fixture attempt used the wrong internal field name for the new Viewer, which was corrected before the successful repeat.
- [x] Create a real disposable native session with its default folders and separate window. Select its Capture folder and wait for Capture One to register a copied RAW through the active collection. The arrival preserved both file hashes and could neither display nor export the approved catalog's look. Closing the session restored that catalog’s live Viewer and exact look. The final run passed 40 assertions with both native recent lists and the scoped window preference restored. Repeat with `-ConcurrentCatalogs -SessionIsolation`.
- [ ] Verify a physical camera tether, same-process opt-in for more than one document, and a licensed 16.7 host. Capture One distributes older versions through the account download page. [Capture One historical downloads](https://support.captureone.com/hc/en-us/articles/14996282988957-Download-an-Older-Version-of-Capture-One)

Evidence is under `.test-results/host-concurrent-exact-preference-restore-20261120`, `.test-results/host-concurrent-close-reopen-final-20261120`, `.test-results/host-session-active-capture-folder-20261120`, `.test-results/concurrent-preferences-cleanup-20261120` and `.test-results/concurrent-window-default-recovery-20261120`.

## Live input ICC change handling

- [x] Validate the selected profile during live refresh and include its fingerprint in source reuse. Invalidate cached and in-flight work if it changes. A previously captured native development holds the verified profile read-only and fails if the underlying ICC no longer matches that snapshot.
- [x] Make a byte-verified backup of a disposable ProPhoto profile, corrupt it without changing the selected input space, and observe both the stale-frame invalidation and stale-snapshot rejection. Restore the exact ICC and verify a fresh 16-bit image paints on its own. Detection took about 745 ms on both licensed launches. The complete runs passed 287 first-launch, 291 restart and 115 four-RAW colour assertions.
- [x] Create a calibrated synthetic Bayer chart, verify all mosaic samples and black/white levels with independent LibRaw, and import it in the licensed disposable catalog. The host accepted the preview/SubIFD layout and added the exact variant to All Images. Native 16-bit output captured the same Linear Rec.2020 profile for two curves at 0 and +1 EV. Across usable neutral patches, +1 EV measured 0.21–1.42 stops with the default DNG Standard curve and 0.08–1.61 stops with Linear Response. The selected curve and transfer function both influence the developed pixels. The original chart and catalog hashes were verified. The licensed chart fixture passed 23 assertions. [Adobe DNG specification](https://helpx.adobe.com/camera-raw/desktop/dng-and-file-formats/digital-negative.html)
- [x] Every copied frame declares the verified Capture One build and assembly hash, variant, native revision, ICC hash, working primaries and transfer, native bit depth, dimensions, alpha handling and sample range. Managed drafts retain the immutable source identity and describe the new dimensions and sampling. The Viewer checks source identity, revision and native layout before reusing its buffers. Export compares the exact captured identity and sample layout. Replacing the source drops its rendered/draft derivatives. The renderer reuses only the same immutable float buffer and dimensions, so a new source identity cannot reuse a previous GPU upload or its dependent caches.
- [x] Inspect the exact host wrapper’s bitmap and file processors, embedded preview, point sampler and raw geometry fields. The licensed tests verified the full developed route including Linear Response. The inspected APIs did not establish a calibrated full-image pre-development route. The production input label is Capture One-developed RGB.
- [x] Pass 12,266 host-independent Viewer assertions, the Windows and Vulkan suites, 131 four-RAW checks and 295 first-launch plus 299 restart checks. Both licensed launches verified a genuine RGB16 draft describes its resized pixels and retains exact source and ICC identity.

Evidence is under `.test-results/host-input-icc-guard-final-20261120`, `.test-results/host-color-matrix-profile-guard-20261120`, `.test-results/host-calibrated-all-images-20261120`, `.test-results/native-earlier-buffer-contract-20261120`, `.test-results/contracts-developed-frame-description-20261120`, `.test-results/host-developed-frame-four-raws-20261120` and `.test-results/host-developed-frame-full-20261120`. Run `qa/color-calibration-chart.py generate` with a new directory, NumPy, tifffile and rawpy, then pass the DNG to `Run-HostSmoke.ps1 -CalibrationChart`. Analyze the fixture’s `Calibration` directory with the same script.

## Native edit invalidation, alternate Vulkan devices and 16.7 review

- [x] Compare 16 actual native edits to independent RGB16 developments in the copied four-RAW catalog. Exposure, lens distortion and grain affected full-canvas samples. Asymmetric crop changed the geometry counter and preserved full-canvas pixels within native repeatability. Crop and lens correction change the same geometry counter. Keep conservative invalidation until the host exposes a verified way to distinguish them. The 96-check run restored the scoped native settings and verified the original catalog hash. Use `Run-HostSmoke.ps1 -NativeSourceMutations` with the four-RAW fixture and an exact preferences backup. Evidence is in `.test-results/host-native-source-mutations-final-20261120`.
- [x] Compare the packaged audit.14 worker across NVIDIA RTX 3090, Intel integrated graphics and SwiftShader. Four cases cover the baseline, Mallett 2019 reconstruction, scanner optics and camera diffusion. All preserve the original alpha and match the same-device scalar packer exactly. Each 65 by 47 frame read back 48,880 bytes in float or 12,224 packed. Maximum cross-device float differences were 9.47e-7 (Intel) and 6.70e-6 (SwiftShader) with a maximum one-code 8-bit difference. The fixture scopes the test ICD to its child processes and records detected device types. Use `qa/vulkan-adapter-parity.py <renderer> <ICD manifest>`. Evidence is under `.test-results/swiftshader-differential-20261120`. [Vulkan loader driver selection](https://github.com/KhronosGroup/Vulkan-Loader/blob/main/docs/LoaderDriverInterface.md#overriding-the-default-driver-discovery)
- [x] Review PR 2 at `1eaaf7e`. The proposed 16.7.8.3529 changes concern an asynchronous Export confirmation with a native callback, a recipe crop-scale enum, C2PA destination marshaling and the earlier primary-variant message. The contributor reports that the native Viewer still does not show film. Validate the Export callback lifecycle and single native delegation, verify crop-mode and C2PA recipe outcomes, and check message timing plus the Viewer drawing seam on the exact licensed binary before adding its fingerprint. [Contributor PR 2](https://github.com/nothing-complex/spektrafilm-for-capture-one/pull/2)
- [x] Verify Capture One A/S Authenticode for the exact 16.7.8.3529 installer and extract its reference assemblies in the ignored disposable fixture. The extracted x64 host DLL SHA-256 is `805CBE9C9B5884DB5F61B9DDFE6E7F83441DE54B321972BA88672BD1EF90EADA`. Both host versions use .NET 8. The signed candidate defines an async confirmation with the original completion callback, a six-argument geometry scale method with optional null process settings, all three C2PA fields, the pre-selection message and the existing Viewer Paint event. This establishes API shapes.
- [x] Build the shared source with zero warnings against each reference set. The installed 16.4 host passes 40 checks on launch and 40 on restart through both native export commands, including failure retry, matching effects at 16 bits and the recipe ICC. The recipe location and collision fixture passes 105 checks per launch. All nine recipes verify after every launch and both source catalogs stay unchanged. Run `dotnet run --project tests/state/StateContracts.csproj -c Release -- --host-signatures` for the 13 focused portable contracts. Evidence is in `.test-results/host67-contracts`, `.test-results/host-164-cross-version-seams-20261120` and `.test-results/host-164-shared-recipe-adapter-20261120`. The signed host files are local QA inputs.
- [x] Compare the emitted signatures of the two reference builds. The initial comparison found the recipe `FileType` getter under different native interfaces despite both builds succeeding. Bind the getter on the actual loaded interface. The resulting builds have 563 matching CLR host references, including generic specializations, and matching assembly names, cultures and public-key tokens. The checker reports the input build versions separately. Run `dotnet run --project qa/HostReferenceCheck/HostReferenceCheck.csproj -c Release -- <16.4-panel> <16.7-panel>`. The portable suite now passes 14 signature assertions. See `.test-results/host67-contracts/reference-drift-before.json` and `reference-drift-after.json`.
- [x] Pass 128 licensed assertions on launch and 128 after restart in Capture One 16.4. Switch the real selected recipe between TIFF and PNG in the disposable host. Both native export commands identify the recipe format and preserve the applied effect pixels. Independently parse all four recipe-selected PNG headers and ICC chunks and verify 3000 by 2000, 16-bit RGBA and the exact recipe profile. All nine original recipe files and the source catalog verify. Repeat with `Run-HostSmoke.ps1 -NativeEffectCompression`. Evidence is in `.test-results/host-164-recipe-format-adapter-20261120`.
- [x] Resolve nine Viewer attachment members independently in the signed metadata for both builds. The main form, grid, grid model, grid document and selected views, variant view, WPF painter, Paint event, and invalidation method match by declaring assembly, type and signature. The metadata comparison covers the private members resolved by name as well as the compiled Paint event. See `.test-results/host67-contracts/viewer-members`.
- [ ] Run a licensed 16.7.8.3529 matrix on a private copy of the four-RAW catalog before admitting the candidate fingerprint. Check whether the selected variant reaches the visible grid and whether invalidation triggers Paint, then verify geometry, native RGB16 source, both native 16-bit export commands with effects and the recipe ICC, C2PA, Undo, floating, restart and detach. The shared interface names match. The runtime firing order and on-screen image are still unverified for 16.7. The existing 16.4 installation remains the supported runtime. 16.7 upgrades older catalogs on open. [Capture One 16.7.8 release notes](https://support.captureone.com/hc/en-us/articles/36002887363741-Capture-One-16-7-8-release-notes)
- [x] Check the published v0.1.1 listing against the installer guide. The ZIP and SHA-256 file names match the actual GitHub release assets. [Published release](https://github.com/nothing-complex/spektrafilm-for-capture-one/releases/tag/v0.1.1)

## Viewer stress and native grain compensation

- [x] In the licensed native WPF viewport, resize the drawable area until the projection reaches 796 px and the high-depth source settles at 900 px. Restore the area, zoom to a 6000 px projection and observe Auto settle at 4096 px. Panning keeps the exact 4096 px source and bitmap. Restore the viewport and exercise manual 2048 independently. Run the separate fixture with `-ViewerStress` or the full licensed suite.
- [x] Submit 100 slider movements during a real 4096 px request. On both licensed launches, the surviving worker completed one final render on the original immutable RGB16 source, matching the last durable value. The measured whole operation was 1.34 and 2.97 seconds including the 300 ms quiet check. A later forced worker loss recovered in 829 and 850 ms. Two losses still stop the retry without disconnecting native Export. Fault injection verifies the exact disposable worker path before termination. The missing-runtime fixture now waits for the old workers to exit before substituting its disposable runtime root.
- [x] Bind native Grain changes and Undo grouping to the same approved document. Check that the captured primary variant still matches before each setter and verify accepted values before ending the group. If closing fails after the changes, attempt reverse-order compensation through the same native manager in a no-undo scope.
- [x] Inject failures at each genuine native setter, an exception after a real no-undo scope close, and a real mid-transaction selection change. The tests verify exact native values, unchanged alternative variant, unchanged receipt, and intact prior history in the no-undo fixture. Normal Apply, Restore, Undo and Redo still use Capture One's actual history. An actual KeepAll close exception after recording a history entry remains unverified.
- [x] Pass 254 state contracts, the full Windows and NVIDIA-capable contract suite, and 306 first-launch plus 310 restart checks. The packaged worker also passed full Intel Vulkan smoke with exact packed/scalar parity and seven tiled cases. Worst Intel tiled mismatch was 1.20e-7.

Evidence is in `.test-results/host-viewer-grain-conditional-20261120`, `.test-results/contracts-grain-transaction-20261120` and `.test-results/swiftshader-differential-20261120/intel-full-smoke.log`.

## Native control inventory and Viewer PNG

- [x] Inventory 196 actual native controls, sections and menu entries. Validate the 136 controls that emit renderer options against the exact compiled description. Keep managed controls and unavailable developer exhaustion explicit. The licensed repeat matched `qa/panel-controls.json` exactly. Use `Run-HostSmoke.ps1 -PanelInventory` with a disposable catalog and native settings backup after intentional control changes.
- [x] Generate the package's capability table from the recorded panel inventory and verified worker description. Fail generation on an unknown capability, unknown worker option, duplicate control or malformed Markdown cell. Thirty-one Windows PowerShell 5.1 manifest assertions and all Windows and Vulkan contracts passed.
- [x] Use the panel's window owner when saving the displayed Viewer PNG. Both licensed Viewer stress launches passed 37 checks. The output reopened with the displayed dimensions, one colour context and the identical sampled BGRA pixel. A fixture initially treated a dotless file extension as an error and entered the production error dialog. The exact disposable process was terminated, its test-only catalog entry was removed using the verified backup and the corrected fixture passed both launches.
- [x] Build `0.2.0-audit.15` from clean `cc24e45`. Verify all 153 package records, 19 critical records and byte-identical regeneration of the capability report. The archive SHA-256 is `3c3ad963f8c560cde990409ef4fdc7bff52e73ab1204208790dbed08a1a56eb4` and the report SHA-256 is `e5281940406abf421f6ca2c338d673c212a4b088c1a209cfd1158d6fb6d6b0e9`. The exact extraction passed 19 inventory, 310 first-launch, 314 restart, 131 four-RAW checks on each launch and 28 physical GPU checks. The regular installed manifest still identifies `ffc4819`. [Public Windows checks](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44687634647)
- [x] Upgrade audit.14 to audit.15 in a short-path disposable installation. CheckOnly retained the previous generation. Real panel readiness and connected native Export retired it only after saved user-file checks. The installed capability report matched the package SHA-256. Evidence is at `D:/Library AI testing/sf-audit15-ready-20261120/installed-upgrade-receipt.json`.
- [ ] Finish conditional command coverage on the licensed host.

Evidence is under `.test-results/host-panel-inventory-verified-20261120`, `.test-results/host-native-preview-png-final-20261120`, `.test-results/contracts-panel-control-caps-final-20261120` and `.test-results/audit15-package-verify-20261120`. The latest verified development ZIP is under `dist/`.

## Single high-depth development path

- [x] Remove the unused 8-bit Capture One proxy source, its retry branch and its display-code buffer. Native Viewer, scaled Viewer, drag draft and Export use the original Capture One image handle and the high-depth bitmap destination. RGB16, RGBA16 and RGB32F are accepted. Display conversion and the explicit Viewer PNG command retain their existing display precision.
- [x] Test the lowest-depth rejection at the actual native bitmap copy entry point, separately for each of the four RAWs. Both licensed colour runs passed 135 assertions, including identical native 16-bit TIFF samples, and the full first-launch and restart runs passed 310 and 314 checks. All Windows and Vulkan contracts passed.
- [x] Hold only the plugin's native development admission slot until the licensed 180-second deadline. Capture One's still-selected original repainted in 703 ms after admission returned, with the saved look and Native Export connection intact. The 17-check run used `-NativeDevelopmentDeadline`.
- [ ] Evaluate a stall inside native development independently of the admission fixture. Recovery from a disconnected original is verified in its own section below.

Source verification used `.test-results/host-high-depth-only-color-20261120`, `.test-results/host-high-depth-only-full-20261120`, `.test-results/contracts-high-depth-only-20261120`, `.test-results/host-native-development-deadline-20261120` and `.test-results/contracts-device-loss-and-deadline-20261120`. The audit.15 development ZIP predates this source simplification.

## ICC file lifetime during late native development

- [x] Pause a disposable licensed callback after actual high-depth ImageCore development, before copying or returning its bitmap. With a fixture-only three-second deadline, the former caller timed out while the callback still owned the scheduler slot. The private ProPhoto ICC became writable early.
- [x] Validate and retain the ICC read lease inside the scheduled callback. The corrected fixture held both resources after the timed-out caller returned. Releasing the callback unlocked the private file, left its bytes unchanged and let the next real request develop verified RGB16.
- [x] Verify the production source change through 135 four-RAW colour checks, 58 native effects Export checks on each launch, and 317 plus 321 general checks. The existing external ICC replacement and automatic Viewer recovery cases passed.
- [x] Hold the native Viewer callback after it develops a real bitmap. Accept a recipe export while the Viewer request is canceled. The export waited for the reserved slot, the late frame never replaced the previous Viewer image, and the saved output reopened as an ICC-tagged RGB48 TIFF. The Viewer then repainted the active view and released the same private profile. The fixture passed 24 licensed checks.
- [x] Detach the real tool with a late callback still paused. The disposed Viewer clears immediately and the callback retains its slot and ICC until release. The 26-check fixture excludes later publication into the old tool.
- [x] Switch from the approved catalog to a second unapproved catalog in the same licensed process with a native callback still paused. The inactive result never entered the source cache or painted the second window. The 35-check fixture also restored the exact first look, reopened the first document with an independent tool and restored the saved window preference.
- [ ] Qualify the same lifecycle on additional supported host revisions and with vendor-originated stalls if they can be reproduced safely. The licensed test pauses the plugin callback after actual ImageCore work.

Use `qa/Build-DevelopmentLeaseFixture.ps1` and `Run-HostSmoke.ps1 -DevelopmentLeaseFixtureDirectory` with an exact native preferences backup and disposable catalog. Add `-ConcurrentCatalogs` for the inactive-window case. The DLL receipt applies only to the isolated fault build. Evidence is under `.test-results/host-development-lease-before-20261120`, `.test-results/host-development-lease-after-20261120`, `.test-results/host-development-lease-colour-20261120`, `.test-results/host-development-lease-native-export-20261120`, `.test-results/host-development-lease-full-20261120`, `.test-results/host-development-lease-export-ownership-20261120`, `.test-results/host-development-lease-detach-20261120` and `.test-results/host-development-lease-inactive-20261120`. Audit.17 has the earlier source lifetime.

## Native Grain history commits after success

- [x] Execute all native Grain setters and compensation without recording intermediate history. Record one named Capture One Undo action only after the verified values and scope close succeed. Replaying it applies to the captured native variant and checks the exact expected grain tuple before writing.
- [x] Verify an actual Apply adds one native action, a single Undo restores every field and the previous history item, Redo restores the applied grain and Restore keeps the previous durable settings. The licensed setter, scope-close and selection-change faults preserve the prior history in the same native no-undo scope used by the product. The full suite passed 311 first-launch and 315 restart assertions.

Evidence is under `.test-results/host-grain-atomic-history-20261120`. The current package remains audit.15 and predates this change.

## Native Export applies the film and optical effects

- [x] Execute Capture One's real Export command and confirm from its visible native dialog. The fixture chooses a private save path and leaves the production command binding, recipe capture, renderer and encoder on the tested path. Repeat via **Export with Previous Settings**. Each invocation accepted exactly one job.
- [x] Reopen an effects-disabled reference, film-only TIFF, film-plus-camera-diffusion TIFF and matching PNG. The 3000 by 2000 outputs carry the exact Adobe RGB recipe profile at 16 bits. Across 41,238 sampled RGB codes per run, film changed a mean 11.66% of full-scale code and camera diffusion changed a further 4.76%. The two native effect outputs agreed within about one millionth of full-scale code on average.
- [x] Switch Spektrafilm off and observe Capture One's ordinary Export dialog. Simulate a changed callback name, verify each original callback after detach, repeat disposal and confirm a clean production reattach.
- [x] Pass 39 licensed checks on first launch and restart with the exact audit.15 extraction and the newer source runtime, for four runs. The source catalog hash was checked and temporary native settings were restored. Run `Run-HostSmoke.ps1 -NativeEffectExport` with the exact native settings backup and disposable test catalog.

Evidence and TIFF/PNG examples are under `.test-results/host-native-effect-export-final-20261120` and `.test-results/host-source-native-effect-export-20261120`. Native Export support remains tied to the verified Capture One build and one selected photo with one enabled recipe.

## Native and optical grain in Export and cloned variants

- [x] Use the panel's actual Apply grain and Restore grain buttons with the extracted audit.19 runtime. Staged native grain controls changed mean exported RGB by less than one millionth of full scale. Applied native grain changed it by 1.66%, and Spektrafilm production grain alone changed it by 1.81%. With both present, adding optical grain over native changed RGB by 2.13% on average, and adding native grain over optical changed it by 0.93%. Applying optical grain left the native values unchanged, and Restore returned the exact original settings.
- [x] Compare native Export TIFF and Export with Previous Settings PNG with both grains enabled alongside the film look, diffusion, halation and scatter. Mean RGB differences were at most 1.12e-6 of full scale. Both reopened at 3000 by 2000, 16-bit, using the exact Adobe RGB recipe profile. First launch and restart each passed 99 licensed checks across ten accepted exports.
- [x] Establish an earlier 104-check baseline on four private RAWs where Capture One cloned the native grain values and Spektrafilm used its default optical grain. The current source passes 119 assertions and inherits both native and optical grain on the clone. Only the source retains the native Restore receipt. The same run verifies both native export commands with their combined grain pixels and unchanged source catalog. Matching an outside edit that recreates the same native values remains under HS-08.

Run `qa/HostSmoke/Run-HostSmoke.ps1 -NativeEffectGrain` for the two-launch export comparison or `-NativeEffectClone` for the single-launch clone check. Evidence is under `.test-results/host-native-grain-effects-20261120`, `.test-results/host-native-clone-tuple-final-20261120` and `.test-results/host-native-clone-grain-effects-20261120`.

## Native export stage timings and lossless TIFF recipe settings

- [x] Record accepted-job timing for Viewer drain, high-depth Capture One development, effect render and transport, crop and blend, delivery ICC setup, and verified file commit. The structured `export.performance.v2` event shares the durable receipt ID and carries dimensions, format and input encoding. It omits source and destination paths. WIC can evaluate the ICC transform lazily during encoding, so the file stage includes that work. The field named `IccConversionMs` in audit.16's v1 event measures setup. The bounded background log drops invalid and oversized events without stalling later entries.
- [x] Two native launches timed all four 3000 by 2000 DNG exports per launch. The effect TIFF finished in 1.40–1.42 s after acceptance, with 487–498 ms in worker render and transport and 568–569 ms to encode, reopen and commit. The equivalent PNG finished in 1.68–1.71 s, with 888–936 ms in the file stage. These eight jobs used one RAW, one active profile and the local RTX 3090 workstation.
- [x] Read the selected TIFF recipe's Uncompressed, LZW or ZIP option. Reopen the TIFF and check its actual compression tag, high-depth format, exact profile and normalized orientation before committing the file. The host-independent test round-tripped all three lossless schemes and every 16-bit sample. The licensed Export command and previous settings regression exercised the active ZIP recipe with the film look and camera diffusion. [Capture One TIFF settings](https://support.captureone.com/hc/en-us/articles/360002646678-Specifying-file-format-in-the-Process-Recipe-tool)
- [x] Use both real native Export commands for DNG in sRGB, CR3 in Adobe RGB, RAF in ProPhoto RGB and NEF in Linear Rec.2020. Each run exported four files per RAW with the selected Adobe RGB destination profile, at 6.00–7.53 MP. Film changed mean RGB code by 6.23–11.66% of full scale and camera diffusion added 2.43–4.76%. Paired TIFF and PNG mean RGB differences stayed at or below 1.21e-6 of full scale. First launch and restart passed 159 assertions each, with 32 accepted outputs and v2 timings. TIFF with both effects finished in 1.27–1.68 s after acceptance and PNG in 1.55–2.26 s.
- [x] Repeat both native commands with film, camera diffusion, halation and scatter active together. The expanded fixture accepted 40 files across four RAWs and two launches. All reopened at the exact 16-bit geometry and Adobe RGB profile. Halation plus scatter changed mean RGB code by a further 0.095–0.892% of full scale. Matching TIFF and PNG effect files agreed within 8.81e-7 mean full-scale RGB. Both licensed launches passed 191 assertions.
- [x] On the exact audit.17 package, two full 6000 by 4000 DNG exports took 3.60 and 4.08 s after acceptance. The file stage took about 2.12 s in each run, film render and transport took 1.08–1.41 s, and native RGB16 development took 209–265 ms. These were independent host launches with the same photo and local storage. The per-job receipt is `.test-results/audit17-package-verify-20261120/native-export-24mp.json`.

Evidence is under `.test-results/host-tile-centre-native-export-20261120`, `.test-results/host-tile-centre-full-20261120`, `.test-results/host-recipe-compression-native-export-20261120`, `.test-results/contracts-structured-export-compression-20261120`, `.test-results/host-four-raw-native-effects-20261120` and `.test-results/host-composed-native-effects-20261120`. Repeat the four-camera disposable fixture with `Run-HostSmoke.ps1 -NativeEffectExportMatrix`.

## Development package audit.16

- [x] Package clean `8891983f4da0d3ac81326116939d3563f381a81a` and verify all 153 package and 19 critical runtime records, checksum and independent capability report. The ZIP SHA-256 is `cc76060976cb0c0856037bd16cd36b401a0f9477798c1df1702c71cc704bdb83`. This package includes the centre-only GPU transfers, device-loss recovery, the TIFF compression adapter and v1 export timings. [Windows 2022 and 2025 checks](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44694685692)
- [x] Run the exact extraction through 50 native effects Export assertions, 135 four-RAW colour assertions and 311 general licensed assertions on first launch. Restart passed the same focused counts and 315 general assertions. The timing correction and four-camera effects fixture use newer source. The normal local install still identifies `ffc4819`.
- [x] Upgrade a private audit.15 install to audit.16. CheckOnly retained the prior runtime. Actual native Ready and Export Connected retired it only after the installed user-file hashes matched. The installed capability report matched the ZIP byte for byte. The exact package passed 28 physical Vulkan checks. The isolated upgrade receipt is at `D:/Library AI testing/sf-audit16-ready-20261120/installed-upgrade-receipt.json`.

Evidence is under `.test-results/audit16-package-verify-20261120`, `.test-results/host-audit16-native-effect-export-20261120`, `.test-results/host-audit16-full-20261120` and `.test-results/host-audit16-four-raw-20261120`. The v1-compatible hook is preserved under the package verification directory.

## Development package audit.17

- [x] Package clean `9b08fe21e3b6e72821a727c6fbd968cc55a62dea` and verify its checksum, 153 package entries, 19 critical runtime entries and independently generated report. The ZIP SHA-256 is `45b8c78f21f0086099379e85ba14d7fd56673de97166b9ed03febe6b18883016`. The build includes corrected composed optical support, tile budgeting and native Viewer recovery. [Windows 2022 and 2025 checks](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44699747561)
- [x] Verify the exact extraction against 191 first-launch and 191 restart native Export checks with all tested effects across four RAWs. The general suite passed 317 and 321, native control inventory passed 19 checks across 197 controls, and managed physical Vulkan passed 28.
- [x] Upgrade a private audit.16 install to audit.17 with real Capture One Ready and Export Connected before retiring the previously verified package. Custom and external preset hashes were retained and the installed capability report matched the ZIP. The ordinary installation identifies `ffc4819`.

The combined receipt is `.test-results/audit17-package-verify-20261120/result.json`. See `.test-results/host-audit17-native-effects-20261120`, `.test-results/host-audit17-control-inventory-20261120`, `.test-results/host-audit17-full-20261120` and `D:/Library AI testing/sf-audit17-ready-20261120/installed-upgrade-receipt.json`.

## Development package audit.18

- [x] Build from clean `c03bfc76888a52ca9702da40be9c1ddb9b9d9a06`. Verify the ZIP checksum, all 153 package entries, all 19 critical runtime entries and independent regeneration of its capability report. The SHA-256 is `ebffd651c1507f253c438ab6080990edadab0e56abf4d5d05a446e3d8638f052`. The build includes native ICC leases, cancellation during WIC file writing and export storage-error preservation. [Windows 2022 and 2025 checks](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44705160261)
- [x] Pass 191 native effects Export assertions per launch from the exact package. The 40 accepted TIFF and PNG files cover both native Export commands, four RAW types, four input encodings, film, diffusion, halation and scatter. The full licensed suite passed 321 first-launch and 325 restart checks. Physical Vulkan passed 28 checks. The actual Cancel export action stopped both in-flight 24 MP TIFF encodes and preserved both prior destinations.
- [x] Verify a private audit.17 to audit.18 upgrade. CheckOnly retained the previous installation until the exact new process reported Ready with Export connected. Custom and external look hashes were preserved and the installed capability report matched the extracted bytes. The normal local install identifies `ffc4819`.

The combined receipt is `.test-results/audit18-package-verify-20261120/result.json`. Exact output and regression evidence is under `.test-results/host-audit18-native-effects-20261120`, `.test-results/host-audit18-full-20261120` and `D:/Library AI testing/sf-audit18-ready-20261120/installed-upgrade-receipt.json`.

## Development package audit.19

- [x] Build from clean `17a7b8039d7d1994aba47008e15776804384bee9`. The ZIP SHA-256 is `6ec50480e5c7b4455c12e31feedb224d2b9cbf1ceae700770bc3bb96f50a69e2`. Verify all 153 package and 19 critical runtime entries and regenerate the capability report byte for byte. The package contains the verified shared-scratch renderer and portable descriptor fix. [Windows 2022 and 2025 checks](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44707254358)
- [x] Pass both native Export commands on four RAW types with all tested effects. The exact package produced 40 ICC-tagged 16-bit files across first launch and restart. It passed 191 native effects assertions per launch, 321 and 325 full general assertions, and 28 managed Vulkan checks.
- [x] Verify an isolated audit.18 to audit.19 upgrade. The exact licensed process reported Ready and Export Connected before retirement of the previous copy. Retained custom and external looks and the installed capability report matched their recorded hashes. The regular installation identifies `ffc4819`.

The combined receipt is `.test-results/audit19-package-verify-20261120/result.json`. See `.test-results/host-audit19-native-effects-20261120`, `.test-results/host-audit19-full-20261120` and `D:/Library AI testing/sf-audit19-ready-20261120/installed-upgrade-receipt.json`.

## Development package audit.20

- [x] Build clean `9ed06283793372fd6761d7485b9a4045445897a0` and verify 153 package entries, 19 critical runtime entries and independently reproduced capability bytes. The ZIP SHA-256 is `deb1a053c47a1b38411f09dd12e2c22c5492296b7fe0792a49218b2b293584a5`. [Windows 2022 and 2025 checks](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44711843509)
- [x] Pass 191 native effects assertions on each launch with both native Export commands and four RAW types. The matrix produced 40 tagged 16-bit TIFF and PNG files with film, camera diffusion, halation and scatter. The separate native and optical grain suite passed 99 assertions per launch and produced 20 accepted outputs with the same high-depth policy.
- [x] Pass 327 first-launch and 331 restart general assertions, including the actual LUT Cancel button. The preliminary exact-package matrix reached Ready and Export Connected but the restart stopped responding before the test callback. Both launches then passed on a fresh disposable catalog. The cause of the initial stall remains unconfirmed.
- [x] Pass 32 physical Vulkan checks and 51 Viewer churn checks. The latter verified 24 actual draws at a 4096 px long edge across DNG, NEF, RAF and CR3. The recorded worker asset fingerprint matched the package.
- [x] Upgrade a private audit.19 installation to audit.20. CheckOnly retained the prior installation until the exact new licensed process reported the panel Ready and Export Connected. The installed capability report and retained custom and external preset hashes passed verification. The regular installation identifies `ffc4819`.

The combined receipt is `.test-results/audit20-package-verify-20261120/result.json`. The exact host output and regressions are under `.test-results/host-audit20-native-effects-repeat-20261120`, `.test-results/host-audit20-native-grain-20261120`, `.test-results/host-audit20-native-full-20261120`, and `.test-results/host-audit20-native-viewer-20261120`. The private upgrade receipt is at `D:/Library AI testing/sf-audit20-ready-20261120/installed-upgrade-receipt.json`.

## Development package audit.21

- [x] Build clean `ce24f862a901b00fca321d526d8ffff53a774ee9` and verify 153 package entries, 19 critical runtime entries and independently regenerated capability bytes. The ZIP SHA-256 is `68438df6f4433a1e60336c54f39474b10c45ef52f3916ca9546b13eb3b0595e1`. The extracted worker matches the recorded 45 and 100 MP benchmark binary. [Windows 2022 and 2025 checks](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44719732269)
- [x] Pass 191 native effects checks and 104 native/optical grain checks on both launch and restart. The exact package produced 40 effects-matrix files and 20 additional accepted grain-fixture files in 16-bit TIFF or PNG. Both native Export commands carry the recipe profile and geometry and apply the film look and tested optical effects. The read-only copies used to test failed replacements are excluded from these counts.
- [x] Pass 336 first-launch and 340 restart general host assertions, plus 32 managed physical Vulkan checks. The exact Viewer trace passed 19 checks across 18 navigation bursts and 144 edits over 25.98 seconds. Its actual draws identify the package's renderer asset fingerprint.
- [x] Upgrade privately from audit.20 to audit.21. The previous version remained available until the exact new licensed host reported Ready with Export Connected. The installed capability report, custom file and external look kept their verified bytes. The regular installation remains `ffc4819`.

The combined receipt is `.test-results/audit21-package-verify-20261120/result.json`. Use `.test-results/host-audit21-native-effects-20261120`, `.test-results/host-audit21-native-grain-20261120`, `.test-results/host-audit21-native-full-20261120` or `.test-results/host-audit21-native-viewer-20261120` for the individual runs. The upgrade receipt is at `D:/Library AI testing/sf-audit21-ready-20261120/installed-upgrade-receipt.json`.

## Development package audit.22

- [x] Build clean `6e6c6a9008b87da6112e65552592570f1d37023a` and verify 153 package entries, 19 critical runtime entries and regenerated capability bytes. The ZIP SHA-256 is `981f80b23206295fb5f15c60fd5dffd1ab6818adf40952963289c17a3d50acd5`. The worker is the exact binary verified on both physical adapters with the paired large Viewer sources. [Windows 2022 and 2025 checks](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44724568528)
- [x] Pass 191 native effects checks and 110 grain checks at both launch and restart. The exact package produced 40 effects-matrix files and 22 additional accepted files in the independent grain and failed-destination retry fixture. Both native commands apply effects and deliver 16-bit TIFF or PNG with the native recipe's selected ICC and dimensions.
- [x] Pass 341 first-launch and 345 restart general host assertions plus 32 managed checks on the physical GPU. The native Viewer suite exercises the 4096 px enlarger round trip and renderer recovery.
- [x] Upgrade privately from audit.21 to audit.22. The previous runtime remained available until Capture One 16.4.6.2414 reported Ready and native Export Connected. The private upgrade preserved its external preset and custom file.

The combined receipt is `.test-results/audit22-package-verify-20261120/result.json`. Individual runs are in `.test-results/host-audit22-native-effects-20261120`, `.test-results/host-audit22-native-grain-20261120` and `.test-results/host-audit22-native-full-20261120`. The upgrade receipt is at `D:/Library AI testing/sf-audit22-ready-20261120/installed-upgrade-receipt.json`.

## Development package audit.23

- [x] Build clean `042bdc050bb2ba47e272a1bb9070df4d3c27fec7` and verify 153 package entries, 19 critical runtime entries and regenerated capability bytes. The ZIP SHA-256 is `5f0487b483f1d8a4a16293765e1c01a34aa22b48b71825dc04edc84ef5f7930c`. [Windows 2022 and 2025 checks](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44728585175)
- [x] Pass 207 effects checks across DNG, CR3, RAF and NEF, 114 native and optical grain checks, and 112 native TIFF compression checks in each of two launches. These tests produced 84 accepted 16-bit effect and control files with verified ICC, geometry and stage receipts. They exercise the actual Capture One Export and Previous Settings commands, check native filename suggestions, and verify all nine existing recipe files in both compression launches.
- [x] Pass 344 first-launch and 348 restart general host assertions and 32 managed physical Vulkan checks. The package's Viewer presentation report matches the independent display profile fingerprint.
- [x] Rehearse the private upgrade from audit.22 to audit.23. The previous runtime remained available until Capture One 16.4.6.2414 reported Ready with native Export Connected. The installed test fixture retained its external preset and custom file.

The combined receipt is `.test-results/audit23-package-verify-20261120/result.json`. Individual runs are in `.test-results/host-audit23-native-effects-20261120`, `.test-results/host-audit23-native-grain-20261120`, `.test-results/host-audit23-tiff-compression-20261120` and `.test-results/host-audit23-native-full-20261120`. The upgrade receipt is at `D:/Library AI testing/sf-audit23-ready-20261120/installed-upgrade-receipt.json`.

## Development package audit.24

- [x] Build clean `e01a286f8b1055bff922e71708e34c35f756193d` and verify 153 package entries, 19 critical runtime entries and regenerated capability bytes. ZIP SHA-256 is `7f51503ceb8b8aaf8b14f48270dd2c79907a1bd7c46c90d3788fcbda2c89d7a4`. [Windows 2022 and 2025 checks](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44731662242)
- [x] Across launch and restart, pass 207 four-RAW effects checks, 114 grain checks, 112 TIFF compression checks, 87 recipe folder checks and 96 workspace checks. The five campaigns produce 118 accepted 16-bit effect and control files. The actual native commands preserve film and optics with the tab hidden or tool floating and use the approved nested recipe folders and exact RGB ICC.
- [x] Independently decode the four-camera TIFF and PNG pairs, including strength-zero, film-only and full optics. OpenCV measures eight cases with 156,601,728 uint16 RGB codes per comparison. The highest mean difference between native commands is 7.45e-7 of full scale. All eight processed TIFFs contain more than 256 distinct codes in every channel.
- [x] Pass 344 general assertions on launch and 348 on restart, 20 numeric inventory assertions, and 32 managed physical Vulkan checks. The actual 112 numeric controls match the worker and the 197-control inventory remains the same.
- [x] Privately upgrade audit.23 to audit.24 and preserve the test fixture's custom files and outside preset. The previous runtime remains available until the licensed host reports Ready with native Export Connected.

The combined receipt is `.test-results/audit24-package-verify-20261120/result.json`. The independent pixel evidence is `.test-results/audit24-package-verify-20261120/independent-rgb16.json`. Individual runs use the `.test-results/host-audit24-*-20261120` folders. The upgrade receipt is at `D:/Library AI testing/sf-audit24-ready-20261120/installed-upgrade-receipt.json`.

## Development package audit.25

- [x] Build clean `14f1d5b7953ef1bc1f1691cfe2983847c9025979` and verify 153 package entries, 19 critical runtime entries and regenerated capability bytes. The ZIP SHA-256 is `b8bc072d96d633471973f4e4ad455d11b0e007ab6a579c6261565a9897bafb9c`. [Windows 2022 and 2025 checks](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44735588072)
- [x] Pass 207 four-RAW effects checks, 114 grain checks, 112 TIFF compression checks, 87 recipe folder checks and 104 hidden/floating tool checks in each launch. Both native commands apply the film and optics. The layouts also exercise a real storage failure and its durable notification path. The five campaigns produce 118 accepted 16-bit effect and control files.
- [x] Independently decode every RGB16 channel code in eight DNG, CR3, RAF and NEF cases. Separate the measured effects of film, diffusion, and the additional halation and scatter. The largest mean difference between native commands is 7.26e-7 of full scale across 156,601,728 codes per comparison.
- [x] Pass 344 general assertions on launch and 348 on restart, 19 moved catalog assertions on each launch, 20 native numeric inventory assertions and 32 managed physical Vulkan checks. The moved catalog retains exact saved look and database bytes.
- [x] Privately upgrade audit.24 to audit.25. Preserve fixture custom files and the external preset. Retire the previous runtime after the licensed host reports Ready with native Export Connected.

The combined receipt is `.test-results/audit25-package-verify-20261120/result.json`. The independent decoder receipt is `.test-results/audit25-package-verify-20261120/independent-rgb16.json`. Individual runs use `.test-results/host-audit25-*-20261120`. The upgrade receipt is at `D:/Library AI testing/sf-audit25-ready-20261120/installed-upgrade-receipt.json`.

## Development package audit.26

- [x] Build clean `5720deaed43d3797691362720aaecf497e4a2267` and verify 153 package entries, 19 critical runtime entries and regenerated capability bytes. ZIP SHA-256 is `5705a3534bd5fc6706a52cd127fe6740b1e3886546e8413f7a3b8e90c65c7db4`. [Windows 2022 and 2025 checks](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44739459517)
- [x] Pass 213 four-RAW effects checks, 114 native and optical grain checks, 112 TIFF compression checks, 87 recipe folder checks and 104 hidden/floating tool checks in each launch. These campaigns produce 118 accepted 16-bit effect and control files. Mixed selection preflight preserves the enabled secondary look and allows native fallback when both masters are off.
- [x] Independently decode eight DNG, CR3, RAF and NEF cases. Separate film, diffusion, and additional halation and scatter from their controls. The largest mean difference between native commands is 6.99e-7 of full scale across 156,601,728 uint16 RGB codes per comparison.
- [x] Stop the licensed disposable process after 1,819,025 encoder bytes. Pass 17 checks before the stop and 22 after reopening. Preserve the exact previous 16-bit TIFF, unrelated temporary file and enabled 1.37 EV look. No owned stage remains. A new explicit retry writes a 24 MP RGB48 TIFF with its embedded ICC.
- [x] Pass 344 general assertions on launch and 348 on restart, 19 moved catalog assertions per launch, 20 numeric inventory assertions and 32 managed physical Vulkan checks. Privately upgrade audit.25 to audit.26 and preserve the fixture's custom file and outside preset until the licensed host reports Ready with native Export Connected.

The combined receipt is `.test-results/audit26-package-verify-20261120/result.json`. The independent decoder receipt is `.test-results/audit26-package-verify-20261120/independent-rgb16.json`. Individual runs use `.test-results/host-audit26-*-20261120`. The upgrade receipt is at `D:/Library AI testing/sf-audit26-ready-20261120/installed-upgrade-receipt.json`.

## Development package audit.27

- [x] Build clean `291bd7a3cb8f9094b39ff7b8e315b6a3d8139df3` and verify 153 package entries, 19 critical runtime entries and regenerated capability bytes. ZIP SHA-256 is `b05f66c05bdd5ab098ed392b5a1f7855e4b7047672bfd314b56695720b8a8990`. [Windows 2022 and 2025 checks](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44745627970)
- [x] Pass 213 four-RAW effects checks, 114 native and optical grain checks, 112 TIFF compression checks, 87 recipe folder checks and 104 hidden/floating tool checks in each launch. The five campaigns produce 118 accepted 16-bit effect and control files. The additional 68-check real workspace switch writes five verified files using both native commands with the floating tool restored.
- [x] Independently decode eight DNG, CR3, RAF and NEF cases. Compare film, diffusion, additional halation and scatter, and the two native commands across 156,601,728 uint16 codes per comparison. The maximum mean difference between native commands is 7.53e-7 of full scale.
- [x] Pass 22 and 19 checks for passive floating restore, then 717 checks across 100 native tool lifecycle cycles with no retired windows, controls or tools retained. Both original workspace files match their verified backups after the real switch fixture.
- [x] After 1,061,123 confirmed encoder bytes, stop the disposable process and verify the saved look, previous output and unrelated temporary file after restart. Both interruption phases pass 17 and 22 checks. No owned staging file remains and explicit 24 MP retry succeeds.
- [x] Pass 344 general assertions on launch and 348 on restart, 19 moved catalog assertions per launch, 20 numeric inventory assertions and 32 managed physical Vulkan checks. Privately upgrade audit.26 to audit.27 while keeping fixture user files and the previous runtime through native Export readiness.

The combined receipt is `.test-results/audit27-package-verify-20261120/result.json`. Its independent decoder receipt is `.test-results/audit27-package-verify-20261120/independent-rgb16.json`. Individual runs use `.test-results/host-audit27-*-20261120`. The upgrade receipt is at `D:/Library AI testing/sf-audit27-ready-20261120/installed-upgrade-receipt.json`.

## Development package audit.28

- [x] Build clean `f96eaddd64c8deaed1fa6a3cfafc23b64c5e00e3` and verify 153 package entries, 19 critical entries and regenerated capability bytes. ZIP SHA-256 is `98cc5b3c0371c5cb97fe5ec654b4a5aab775f367b98850acdc8399790e5271b1`. [Windows 2022 and 2025 checks](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44748776120)
- [x] Pass 213 four-RAW effects checks, 114 native and optical grain checks, 112 compression checks, 105 recipe location and suffix checks and 104 hidden/floating tool checks in each launch. These campaigns produce 122 accepted 16-bit effect and control files. The real workspace switch adds five matching TIFF and PNG outputs and passes 68 checks.
- [x] Independently decode 156,601,728 uint16 RGB codes per comparison in eight cases. Both native commands agree within 7.60e-7 mean full scale. The two numbered filename runs each compare 18 million codes. They agree within 7.35e-7 and preserve the occupied entries.
- [x] Verify both packaged restore scripts and run both workflows from the extracted package. The exact archive and the changed catalog each reopen twice with the verified look. All four launches pass 19 checks.
- [x] Pass 22 and 19 passive workspace restore assertions, 717 native lifetime checks and 17 and 22 interruption checks. The exact process interruption after 1,391,134 staged output bytes leaves no owned temporary file and preserves the previous TIFF and saved look.
- [x] Pass 344 general assertions on launch and 348 on restart, 20 native numeric inventory assertions and 32 managed physical Vulkan checks. Retain fixture user files and the prior runtime until the private upgrade from audit.27 reports native Export ready.

The combined receipt is `.test-results/audit28-package-verify-20261120/result.json`. Independent outputs are reported in `independent-rgb16.json` and `numbered-rgb16.json` in the same directory. The private upgrade receipt is `D:/Library AI testing/sf-audit28-ready-20261120/installed-upgrade-receipt.json`.

## Development package audit.29

- [x] Build clean `2d46117999aadc3fb0199f4bd5d6c87ca78dc180` and verify 153 package entries, 19 critical entries and regenerated capability bytes. ZIP SHA-256 is `6af96dc63cc41a25400ad07a3ff59b84a704df7d3a2e376d575aa6c9e40cf0fc`. [Windows 2022 and 2025 checks](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44753282154)
- [x] Both licensed launches pass 215 four-RAW effects checks, 114 native and optical grain checks, 112 TIFF compression checks, 105 native location and suffix checks, 104 hidden and floating tool checks and 108 output-profile checks. These campaigns produce 144 accepted 16-bit effect and control files. The 68-check native workspace switch adds five matching TIFF and PNG files.
- [x] Independently decode eight DNG, CR3, RAF and NEF cases and compare 156,601,728 RGB16 codes per comparison. The two native commands agree within 7.51e-7 mean full scale. Each RGB channel in the finished TIFFs uses at least 43,654 distinct codes. The numbered export pairs agree within 7.64e-7 and preserve the occupied entries.
- [x] Decode the selected sRGB, ProPhoto RGB and linear Rec.2020 profiles of both native command outputs. Across 12 files and 122,694 pixels per comparison, the largest sampled Delta E 2000 is 0.885 and the highest 95th percentile is 0.364.
- [x] Pass 19 catalog restore assertions per launch for both an exact snapshot and a legitimately updated database. Pass 22 and 19 passive floating checks and 717 lifecycle assertions over 100 cycles. Stopping the disposable process after 1,061,124 observed encoder bytes preserves the previous TIFF and saved 1.37 EV look. The interruption fixture passes 17 and 22 checks and leaves no owned staging bytes.
- [x] Pass 20 native numeric inventory and 32 managed physical Vulkan checks. The Windows host-independent suite passes 167 renderer client, 268 state, 12,406 Viewer and export, and 101 installer assertions. A local symbolic-link probe was unavailable.
- [x] Pass 347 general native assertions on launch and 351 on restart. The private upgrade from audit.28 preserves fixture user files and holds its prior runtime until the licensed panel and both native Export routes are ready.

The combined receipt is `.test-results/audit29-package-verify-20261120/result.json`. The licensed runs are in `.test-results/host-audit29-*-20261120`. The private upgrade receipt is `D:/Library AI testing/sf-audit29-ready-20261120/installed-upgrade-receipt.json`.

## Development package audit.30

- [x] Build clean `f68de282af2086578997abee84303be5a5037a02`. Verify 153 package files, 19 critical entries, regenerated capability bytes and the same worker SHA-256 as the physical comparison. ZIP SHA-256 is `0a5c47d40d376cabab0a1df07cdb61028718cef8614293db879beda096e9710c`. [Windows 2022 and 2025 checks](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44758626678)
- [x] Pass 215 four-RAW native effects and 129 output profile and stacked optics checks on both launches. These campaigns save 68 verified 16-bit effect and control files. Independently read 156,601,728 RGB16 codes per comparison across the RAW matrix. The maximum mean difference between the two native commands is 7.44e-7 of full scale.
- [x] Decode the selected ICC transforms in 12 sRGB, ProPhoto RGB and linear Rec.2020 files. The highest sampled Delta E 2000 is 0.885. Adding print diffusion with camera diffusion, highlight boost and scatter changes mean RGB by 1.244%. Both native command routes preserve the same combined look to within 4.62e-7 mean full scale across 18 million codes each launch.
- [x] Pass 348 and 352 full native assertions, including the production storage-failure close confirmation, and pass 32 managed physical Vulkan checks. The private upgrade from audit.29 holds its prior runtime until the licensed panel and native Export are ready.

The exact package receipt is `.test-results/audit30-package-verify-20261120/result.json`. The licensed fixture results are in `.test-results/host-audit30-*-20261120`. The installer receipt is `D:/Library AI testing/sf-audit30-ready-20261120/installed-upgrade-receipt.json`. Audit.29 retains the separate workspace, crash recovery and recipe naming receipts.

## Development package audit.31

- [x] Build clean `c48b8a845da7b4c62e37d279d0fbd77e89a7eb12`. Verify 153 package entries, 19 critical entries, and the exact renderer that passed the physical heap accounting comparison. ZIP SHA-256 is `4e404b9795917f4aca9efab6474716cec4a2db963eb50f0902d1e008f8ccb316`. [Windows 2022 and 2025 checks](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44765205234)
- [x] Pass 222 four-RAW native effects and isolated-save checks, plus 129 stacked-optics and three-profile checks on each launch. The campaigns write 68 verified 16-bit files. Both real native commands show the expected saved-look loading warning before accepting an output job.
- [x] The independent decoder compares 156,601,728 RGB16 codes per comparison and measures at most 7.09e-7 mean full-scale difference between the two native commands. Adding print diffusion changes mean RGB by 1.244% while both native routes agree within 4.47e-7. The largest sampled colour difference across three selected ICC profiles is 0.885 Delta E 2000.
- [x] Pass 348 and 352 general licensed checks and 32 managed physical Vulkan checks.
- [x] Read all 10,000 acknowledged slider writes over 100 visits across four RAW variants. A hard stop and restart restore the final values, master switches and exact saved bytes. The packaged run records a 5.47 ms p95 slider event and save time with the Viewer paused. The private audit.30 to audit.31 upgrade holds the prior runtime until Capture One reports that the panel and native Export are ready.

The verified receipt is `.test-results/audit31-package-verify-20261120/result.json`. Per-run evidence is in `.test-results/host-audit31-*-20261120`. The exact private upgrade receipt is `D:/Library AI testing/sf-audit31-ready-20261120/installed-upgrade-receipt.json`.

## Development package audit.32

- [x] Build clean `ca0b5cd552256a64194ef1fb9ec2d2dc08a0c4c0` and verify 153 package entries and 19 critical runtime files. The ZIP SHA-256 is `9e69802f784023a3fb2050ce50c42d37f06b3d3f325c0450813cfbd7f42aa5b6`. The extracted worker passes 32 managed GPU checks and the exact source passes Windows Server 2022 and 2025 CI. [GitHub CI](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44769430454)
- [x] On Capture One 16.4.6.2414, first launch and restart each pass 222 four-RAW export, 146 stacked optics and recipe, 114 grain and 52 Viewer stress checks. Independently reopen all 96 export files from both native commands, including native and optical grain together, TIFF RGB48, PNG RGBA64 and the exact recipe RGB profile. The four-RAW route comparisons use 156,601,728 codes per comparison and agree within 7.02e-7 mean full scale.
- [x] Adding print diffusion to camera diffusion, boosted halation and scatter changes mean RGB 1.244% of full scale. Both native commands preserve the stack and agree within 4.27e-7 mean full scale. In Scan Negative, remembered print-only settings and neutral values agree within 4.05e-7. The independent ICC matrix and tone-curve comparison across three output profiles has maximum Delta E 2000 of 0.8844. The combined native and optical grain route agrees within 1.04e-6 across both launches.
- [x] Privately upgrade from audit.31. Verify the desktop launcher is created, the package reaches Ready and native Export Connected, and the prior runtime stays available until the readiness check succeeds. Preserve the nested user look and separate user preset. The regular installed runtime remains at the earlier revision.

The receipt is `.test-results/audit32-package-verify-20261120/result.json`. Individual runs are in `.test-results/host-audit32-*-20261120`. The isolated installer receipt is `D:/Library AI testing/sf-audit32-ready-20261120/installed-upgrade-receipt.json`.

## Accepted export dialog and storage failure

- [x] On first launch and restart, close the actual native Export confirmation after a TIFF with native and optical grain is accepted. The binding is released immediately and the job completes at its approved path with the same 16-bit pixels and recipe ICC.
- [x] Through the actual Export with Previous Settings command, attempt to replace a read-only fixture TIFF. Windows rejects the replacement, the panel reports the failure without a second dialog, and its persistent receipt records Failed for the exact approved path. The previous bytes and the original RAW remain intact. The approved stage file is cleaned up.
- [x] Both launches pass 104 assertions, and the Windows contracts pass. Evidence is in `.test-results/host-export-nonmodal-close-20261120` and `.test-results/contracts-export-failure-viewer-20261120`. These results are from current source and extend the packaged audit.20 coverage.
- [x] Use `-NativeExportInterruption DevelopingSource`, `RenderingFilm` or `Encoding` on the single private 24 MP DNG. All three runs stopped only their own disposable process and passed 21 assertions after restart. The prior output and the exact exposure and strength persisted. A new explicitly approved TIFF at a different path reopened in RGB48 with its profile.
- [x] A first film-stage attempt recovered its preferences but missed the durable progress event because reading a Windows receipt blocked atomic replacement. Serialize journal readers and replacement across the in-process tool instances, then pass the licensed film-stage retry. The earlier encoding test left an 8.03 MB staging file after a hard stop. Explicit retry preserved the previous destination. Verified paths include `.test-results/host-export-hard-stop-DevelopingSource-repeat-20261120`, `.test-results/host-export-hard-stop-RenderingFilm-journal-fixed-20261120` and `.test-results/host-export-hard-stop-Encoding-repeat-20261120`. The newer guarded output fixture exercises Windows-owned staging below.
- [x] Exercise four concurrent in-process readers through independent journal instances while publishing 96 stage updates with another instance. All updates reopen exactly, with no lost or malformed receipts. Windows Viewer and export contracts pass 12,378 assertions and every other local suite passes.
- [x] With Capture One's native Export window still visible, confirm an export to a read-only destination. The title shows the failure and the original confirmation remains usable. The same window accepts a fresh destination, clears the indicator and completes the TIFF. Both source runs pass 109 native effects and output checks. Evidence is in `.test-results/host-native-export-dialog-retry-20261120`. This is a source checkpoint beyond the verified audit.21 ZIP.
- [x] A later grain regression caught an immediate Previous Settings command arriving after the accepted TIFF had finished and its dialog had closed. The old bridge continuation still held the busy flag. Track the real output task, keep nested destination picking guarded and prevent an older continuation from clearing later work. First launch and restart pass 110 grain checks and 191 four-RAW native Export checks. See `.test-results/host-resident-pair-effects-ready-20261120` and `.test-results/host-native-effects-ready-20261120`.

## Native keyboard and default history

- [x] Use the actual Capture One numeric control and its routed keyboard events to increment and decrement exposure. Move real keyboard focus from the editor to Defaults and verify the typed number has reached disk.
- [x] Click the actual Defaults action. The host registers one named native undo action. Native Undo restores the complete before-state and native Redo the complete default state. The full suite passed 336 first-launch and 340 restart assertions. Evidence is in `.test-results/host-native-slider-defaults-20261120`.

## Ten-minute licensed Viewer telemetry

- [x] Run `qa/HostSmoke/Run-HostSmoke.ps1 -ViewerTelemetrySeconds 600` with the private four-RAW fixture and exact preferences backup. The licensed workload passed 19 checks in 601.12 s. It navigated 455 times, saved 3,640 slider edits and interrupted 57 active final renders. DNG, NEF, RAF and CR3 settle at 4096 px after 1200 px provisional work.
- [x] Record first visible draw p50/p95 of 340/403 ms and settled draw from selection of 681/773 ms. The p50/p95 durable edit times were 5.29/22.39 ms. The eight edits during exclusive log-file locking completed with p95 25.35 ms. Sampled peak host private memory was 8.61 GiB.
- [x] Keep at most two one-MiB diagnostic files even through rotation. The retained files contain 210 initial, 1,472 drag, 447 settled and 26 canceled settled events. These event counts cover the retained log generations. Navigation, edit and cancellation totals cover the full run. Settled film and transport p95 was 78.84 ms on a resident source versus 244.30 ms on a request that uploaded the source.
- [x] Keep the source geometry, settings hash and generation for each visit, plus the tested panel, hook and renderer hashes and workstation adapter/driver inventory. The receipt is `.test-results/host-viewer-telemetry-ten-minute-20261120/viewer-telemetry.json` and the source and driver identity is in the adjacent `source-provenance.json`.

## Disconnected original recovery

- [x] Move only a private calibrated RAW copy and observe the native original availability change. The previous source kept its processed Viewer layer visible across the offline interval.
- [x] Use Capture One's current original availability alongside its revision. On disconnection, cancel obsolete work, release cached developed and rendered frames and explain how to reconnect the original. On return, automatically develop a new RGB16 source and paint the same film settings.
- [x] Verify the fixture bytes are restored exactly, native recipe export refuses the disconnected source and the persistent look is identical. The final 29-check run measured about 307 ms to suspend and 1.72 seconds to repaint. Full Windows and Vulkan contracts, 311 plus 315 full native checks and 39 plus 39 native effects export checks passed.
- [x] Recheck native availability together with variant and revision immediately before accepting developed pixels or publishing the initial, drag or settled Viewer frame. The exact same publication guard accepted the live calibrated RAW and refused the currently selected revision when its private original was offline. The 31-check licensed regression restored the file and painted a fresh high-depth frame.

Evidence is under `.test-results/host-original-availability-baseline-20261120`, `.test-results/host-original-availability-final-20261120`, `.test-results/host-original-availability-full-20261120`, `.test-results/host-original-availability-export-20261120` and `.test-results/host-original-available-at-publication-20261120`. Use `-CalibrationChart` and `-OriginalAvailabilityProbe` with the exact preferences backup.

## Native Viewer and float export worker scale

- [x] Run colour and optical cases at 24, 45, 61 and 100 MP on the exact packaged worker, using only the NVIDIA RTX 3090 ICD. All 16 cases and 80 requests completed. Each case includes one cold request and four warm requests.
- [x] At 100 MP, packed Viewer warm p95 was 0.43 s for colour and 5.30 s for optics. Float export took 2.20 s and 8.51 s respectively. The 100 MP optical export peaked at 6.33 GiB of worker commit. Its 800 tiles used 3.09 GiB of transient GPU cache and transferred 9.38 GB from the device for a 1.60 GB frame reply. The physical host had 35.68 GiB available before the benchmark.
- [x] Render each existing halo on-device, then read only the tile centre into the final frame. Keep the full-tile nonfinite validator for packed pixels. At 100 MP the before-and-after RTX 3090 run transferred 1.60 GB instead of 9.38 GB for float optics and 0.40 GB instead of 2.34 GB for packed optics. Warm medians fell from 8.40 s to 5.95 s and 5.27 s to 4.81 s respectively. These timings cover the renderer worker and its transport. Four full 100 MP reply hashes matched across the prior and optimized binaries, including two exposure levels in each format.
- [x] Verify exact transfer counts and full/tile parity for eight optical and colour graphs, including wide and tall partial tiles. Complete Windows contracts and standalone smoke passed on physical NVIDIA and Intel. The padded-row OFX fixture passed with identical float values and at most one half-float code in the Hanatos 2026 optical graph on both adapters.
- [x] Budget larger packed Viewer tiles only when both axes are at least 4096 px, the existing halo is at most 256 px, and Vulkan reports usable dynamic headroom. Reserve space for the full-frame source and the optical scratch estimate before growing tiles. Keep explicit tile sizes and retain the original float export plan. Record actual tile geometry in worker metrics.
- [x] At 100 MP on RTX 3090, the packed worker used 28 tiles at 2048 by 2048 instead of 800 at 512 by 256. One warm exposure change with reply hashing took 1.55 s versus 4.89 s. Simulated 6 GiB selected 104 tiles at 1024 by 1024. Simulated 4 GiB retained 800 tiles and reused the warm input without reuploading. All twelve complete payload hashes matched the earlier plan, including float export and the 45 MP stacked case.
- [x] Compare every packed pixel on a separate 33.6 MP high-contrast image through sRGB, Adobe RGB and Linear Rec.2020. The 2048 px tile plan reduced 272 tiles to 12. Maximum RGB difference was one 8-bit code and alpha was exact for each input. Both physical NVIDIA and Intel passed the standard Vulkan smoke. Reproduce the large test with `ofx-still-renderer/smoke.py --adaptive-viewer` on a suitable workstation.
- [ ] Measure wider optical graphs under real lower-memory and competing-renderer loads. The 45 MP stacked fixture used about 14.15 GiB of transient allocations despite the worker's reported 7 GiB admission target. The adaptive branch leaves its plan unchanged pending better accounting for large halos.

Evidence is under `.test-results/matrix-24-45-61-100mp-20261120`, `.test-results/matrix-tile-centres-20261120`, `.test-results/tile-centre-100mp-parity-20261120` and `.test-results/contracts-tile-centre-20261120`. Run `qa/renderer-benchmark.py` on the two binaries with the same dimensions, routes and scene flags. Add `--hash-replies` for a separate byte comparison, since hashing also contributes to its elapsed time.

Adaptive planning receipts are under `.test-results/adaptive-viewer-only-20261120` and `.test-results/optical-tile-size-20261120`. Use the optional `--tile-size` benchmark flag to establish the explicit-size reference without changing the worker's default plan.

## Joint scatter and halation support at tile boundaries

- [x] Isolate the disagreement between 512 px and 2048 px tile centres on a 12,501 by 31 high-contrast 8 mm chart. The audit.16 worker's scatter-only and halation-only cases both match the full-frame reference exactly. Their composition differs by up to 0.000126 float RGB at a 512 px tile boundary. Alpha is exact. The 100 MP float experiment independently found up to 0.0000341 and 15,264,000 changed 16-bit channel codes when the tile plan changed.
- [x] Compute the two filters' finite support from film pixel size, scatter scale and the effective stock's bounce sigmas. Include both radii in the planner and shrink the active area after each filter. The long Super 8 chart matches the full-frame float reference exactly on the NVIDIA and Intel adapters. The full Windows and standard Vulkan contracts passed.
- [x] Verify the 100 MP optical fixture with two exposure settings and multiple tile layouts. All twelve complete packed or float comparisons matched the corrected reference byte for byte. The measured combined support was 431 px. The packed default used 54 tiles at 1536 by 1536 and completed one warm hashed reply in 2.00 s. The simulated 6 and 4 GiB caps used 170 and 250 tiles, and kept source reuse. The 33.6 MP high-contrast comparison now matches every packed code and alpha sample in three working encodings with 12 tiles instead of 96.
- [ ] Verify arbitrary combinations and hardware before changing float tile selection. The finite-support fix addresses the seam. Physical blur beyond each shader's radius cap and wide-halo memory remain separate audit work.

Evidence is in `.test-results/rc04-halation-overlap-baseline-20261120.json`, `.test-results/adaptive-optical-tile-budget-20261120/float-parity-100mp.json`, `.test-results/composed-halation-exact-20261120`, `.test-results/composed-halation-final-20261120` and `.test-results/contracts-composed-halation-20261120`.

## Shared Vulkan optical scratch and descriptor compatibility

- [x] Reuse five tile-sized allocations between camera optics, DIR and scanner post-processing. DIR retains the camera log raw through its last read, and the existing compute barriers sequence writes and reads between stages. Separate inter-stage results keep their existing storage. Eight transitions between a stacked graph, individual effects and the baseline matched fresh render pixels on NVIDIA and Intel. [Vulkan synchronization examples](https://docs.vulkan.org/guide/latest/synchronization_examples.html)
- [x] Compare the audit.18 worker with the new worker on the same 45 MP stacked optical graph. Packed Viewer and float export matched all six complete frame hashes and retained nine tiles at 2930 by 2930 with 1465 px halo. Peak transient allocations changed from 14.84 to 11.67 GiB for the Viewer and 14.83 to 11.67 GiB for export. Peak worker commit changed from 15.86 to 12.86 and 16.55 to 13.36 GiB respectively. Two warm measurements per build gave medians of 2.67 and 2.70 s for Viewer, and 3.21 s for export in both builds. Scope includes worker transport and payload hashing. The 7 GiB planning target still requires a tighter plan for this graph.
- [x] Reproduce Vulkan validation VUID `03018` on SwiftShader. The print pipeline declared 31 storage slots against the device's limit of 30. Reuse unused print slot 4 for the preflash response and screen both the per-stage and per-set caps before choosing an adapter. The final SwiftShader stack reproduced all four earlier full-frame hashes with synchronization validation active and zero errors. The Khronos Profiles layer confirmed both automatic and explicit device selection return an actionable error at 29 slots and return identical pixels at 30. [Khronos Profiles layer](https://github.com/KhronosGroup/Vulkan-Profiles/blob/main/layer/TUTORIAL.md)
- [x] Make both CMake build targets refresh adjacent shaders and tables independently of executable relinking. Replacing each generated shader copy with the earlier valid file demonstrated that an incremental build restores the exact new SPIR-V SHA-256 while the executable timestamp stays unchanged. [CMake custom targets](https://cmake.org/cmake/help/latest/command/add_custom_target.html)
- [x] Run full Windows and NVIDIA Vulkan contracts, the full Intel Vulkan smoke suite and 480 NVIDIA stock, input and spectral combinations. Worst standard tiled RGB difference was 1.79e-7 on NVIDIA and 1.19e-7 on Intel.

Evidence is under `.test-results/optical-scratch-physical-20261120`, `.test-results/optical-scratch-sync-20261120`, `.test-results/optical-scratch-final-20261120` and `.test-results/portable-vulkan-bindings-20261120`. Audit.18 contains the earlier renderer, which serves as the before-build reference.

## Resident Vulkan source for tiled halation

- [x] Use the existing full-image shader addressing for the halation reduction after the fenced source upload. The effect reads the resident device image and keeps its revision across Viewer edits. Release tiled host staging of 256 MiB or more after its copy completes. The export worker still takes a fresh source for each job.
- [x] Compare the audit.20 worker with the new binary on eight 45 MP stacked-optics replies across packed Viewer and float export, and two 100 MP packed optics replies. All ten matched byte for byte. The 45 MP plan kept nine tiles at 2930 px with 1465 px of overlap. Reported transient cache dropped from 11.67 to 11.00 GiB. On the two resident Viewer edits, reported upload fell from 720 MB to 3,784 bytes and hashed response times changed from 2.71 and 2.72 s to 2.55 and 2.56 s. Replacing the source still uploads the full 720 MB.
- [x] At 100 MP the retained transient cache dropped from 3.66 to 2.17 GiB and peak worker commit from 5.80 to 4.90 GiB. The warm hashed response remained about 1.76 s. Repeated captures of new 45 MP sources had similar whole-process peak commit before and after. The 7 GiB planning target remains open for the largest optical halo.
- [x] Enable Vulkan synchronization validation and send four alternating-source requests along each output route across two real tiles of a 4097 by 129 chart. Both validation logs contained no errors. Reproduce with the local SDK layer and `qa/renderer-benchmark.py --source-change-interval 2`. [Vulkan validation log configuration](https://github.com/KhronosGroup/Vulkan-ValidationLayers/blob/main/docs/error_messages.md#logging-to-a-file)
- [x] Pass the full Windows and physical NVIDIA smoke suite plus full Intel GPU smoke. In a separate padded-row OpenFX harness, compare the exact previous revision on both adapters. Both revisions pass the smaller 643 by 317 case. The larger 1027 by 769 case reports the same two-code half-float difference and fails the same tolerance on both revisions. Its float output matches exactly in both. Evidence is in `padded-ofx-comparison.json` in the directory below.

Evidence is in `.test-results/renderer-resident-halation-20261120/comparison.json`, `viewer-tiled-sync.json` and `export-tiled-sync.json` in that directory. The two worker hashes and every source-transfer decision are recorded.

## Enlarger cache across identity and worker recovery

- [x] Keep one unchanged input's cached enlarger geometry when returning temporarily to identity, with a separate 192 MiB inactive limit. At 4096 by 2731, returning to the exact transform eliminates the measured 117.64 ms of CPU resampling. The single paired hashed response changes from 277.07 to 152.81 ms. A 4096 by 3073 transform exceeds the limit and releases its cache. All six complete payload hashes match audit.21.
- [x] Verify 12 fresh-versus-warm cases on both physical adapters, including replacement input, shifted borders, switch to identity and return to the last geometry. The existing graphics path uploads again when switching between original and transformed source buffers.
- [x] Pass 47 native Viewer stress assertions including the actual 4096 px identity round trip. Terminate only the verified disposable renderer. The replacement reuses the native high-depth source, rebuilds the committed enlargement and repaints. The measured recovery is 1.26 s and native Export stays connected.

Use `.test-results/enlarger-toggle-continuity-20261120/identity-threshold.json`, the adjacent GPU logs and `.test-results/host-enlarger-toggle-recovery-20261120/host-smoke.log`. This checkpoint is newer than the verified audit.21 ZIP.

## Paired native Viewer GPU sources

- [x] Pair the original developed RGB16 source's float pixels and one exact enlarger transform. When the adapter reports enough current headroom, retain the other buffer and switch by immutable revision. A source change releases the old companion and uploads the new original. The existing 192 MiB inactive limit and renderer disposal still bound retention.
- [x] Compare eight 4096 by 2731 packed replies against the CPU-cache-only worker. Every payload hash matches. Once both buffers are warm, four alternating requests upload zero bytes instead of 178,978,816 each. Their median hashed response time moved from 114.88 to 97.04 ms on the RTX 3090. The first use of each actual source still uploads it.
- [x] Pass fresh-versus-warm comparisons on both physical GPUs and a simulated 160 MiB cap. The constrained route keeps the CPU transform and reuploads it on the GPU without changing the result. Khronos synchronization validation passes the alternating source and replacement program.
- [x] Pass 48 native Viewer stress assertions on both first launch and restart, with actual high-depth pixels and an active GPU companion. The replacement worker rebuilds the durable enlargement after a forced termination and the native Export bridge remains connected.
- [x] Repeat the 48-check licensed stress workload on Intel across both launches, with the exact Vulkan loader adapter recorded. A canceled request can replace the child process while retaining its managed wrapper. Count final render sequence numbers by process ID and verify the queue drains before measuring quietness. Each Intel burst ends with one completed final render. The later forced transformed-worker interruption recovers in 1.99–2.07 s. Full Windows and NVIDIA Vulkan contracts and the complete Intel smoke suite also pass.

Evidence is under `.test-results/enlarger-resident-pair-20261120/gpu-pair.json`, its distinct loader and validation logs and `.test-results/host-enlarger-resident-pair-20261120`. The exact measured worker SHA-256 is `54e41c5a11c41db7b9bb194c0010eb7fa22fa42ed27a6d38fa7a4996c682f1ff`.

## LUT control parity, legacy comparison and native cancellation

- [x] Use the actual Cancel LUT export button after accepting a 65-point job and observing its private Vulkan worker. The two licensed runs returned in 2.24 and 2.36 ms after the click, cleared the output lease and preserved the prior destination with no staging residue. A follow-up 33-point LUT completed on the same path in each run. The native fixture injects only the destination choice. The full general suite passed 327 and 331 assertions.
- [x] Recheck cancellation between text chunks during guarded LUT writes. The shared output transaction continues to revalidate the destination and check its token immediately before committing.
- [x] Sample 50 pointwise variations of film, print, spectral, DIR, scanner and colour-adaptation settings, enabling conditional controls for each comparison. The fixture checks sample count, exact parity and a real control response. Both NVIDIA and Intel passed 150 checks and reproduced the direct Vulkan samples with maximum sRGB code difference of 5.01e-10. Public builds retain the existing calibration gate for Academy Printer Density.
- [x] Compare 33- and 65-point LUTs against 1,024 held-out colours for each of Hanatos 2025, Hanatos 2026 and Mallett 2019. On this package all three improved with the denser grid. Hanatos 2026 p95 dropped from 0.000955 to 0.000255 of full-scale sRGB code. The managed physical-GPU suite passed 32 checks.
- [x] Compare both native 33-point test exports with the optional scientific Python LUT using the available shared settings. Each 35,937-sample comparison measured mean absolute difference 0.02018, p95 0.06840 and max 0.29351 in full-scale sRGB code. The evidence records both implementations, the forwarded settings and options unavailable to the legacy path. Native LUT correctness is measured against the active Vulkan engine.

Repeat the pointwise comparison with `dotnet run --project tests/renderer/RendererContracts.csproj -c Release -- --lut-sweep <extracted-package> <report.json>`. Use `python qa/compare-legacy-lut.py <full-strength-native.cube> --output <report.json>` with the pinned optional Python environment for the difference report. Evidence is under `.test-results/host-native-lut-cancel-20261120`, `.test-results/lut-pointwise-strict-20261120`, `.test-results/lut-hanatos2026-parity-20261120` and `.test-results/native-legacy-lut-20261120`.

## Read-only Viewer color diagnostics

- [x] Add Viewer encoding, Recipe Proofing state and the optional Windows display ICC fingerprint to **Copy support details**. The query runs on demand, and the report contains no profile or display paths. Windows returned the standard sRGB profile for the single 3072 by 1728 display. The separate GDI probe and the exact licensed host reported the same SHA-256. [Microsoft GetICMProfileW](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/nf-wingdi-geticmprofilew)
- [x] Pass 344 native checks on launch and 348 on restart. Verify that the actual proofing switch changes the support value and still suspends and restores the live paint layer.
- [x] Draw 18 offscreen WPF patches with both bundled alternate ICC profiles. Adding the ICC tag alone preserves all input codes. Explicit conversion changes 16 patches per profile. LittleCMS agrees within one 8-bit code for ProPhoto. Linear Rec.2020 shows two out-of-gamut red channel departures of 18 and 3 codes. These measurements cover the WPF bitmap draw before OS presentation. [Microsoft WPF offscreen bitmap draw](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/how-to-encode-a-visual-to-an-image-file)

Repeat the patch probe using `dotnet run --project tests/ViewerContracts -c Release -- --display-profile-patches <alternate.icc> <report.json>`. Read the measured reports under `.test-results/display-profile-readonly-20261120` and `.test-results/host-display-profile-support-20261120`. A real nonidentity display and Advanced Color measurement remain open. The display report is a source change after the verified audit.22 ZIP.

## Native TIFF recipe compression with effects

- [x] Exercise the actual native Export confirmation and Export with Previous Settings with an explicitly selected TIFF recipe in ZIP, LZW and uncompressed modes. Both launches passed 108 assertions and wrote 22 accepted files in total. The compression subset has 12 RGB48 TIFFs with the exact selected profile at 3000 by 2000. Each TIFF tag and corresponding job timing agree with the selected option.
- [x] Test the same immutable film, diffusion, halation and scatter settings over all 18 million 16-bit channel codes in each file. The independent repeated ZIP renders show small native rerender variation. All 12 comparisons average below 0.05 of one 16-bit code, with a largest individual departure of 157 codes. The offline encoder round trip checks the exact input codes across all three compression options.
- [x] Verify all nine original native recipe files after both launches. A prior fixture revision let Capture One persist its temporary recipe format and compression. Restore the exact backed-up bytes and verify the whole recipe set. The final fixture suppresses notifications to keep the variations in memory, restores format, depth and compression, then checks the separate file hashes before restarting.

Evidence is in `.test-results/host-native-tiff-compression-final-20261120`. The original rollback receipt is `.test-results/host-native-tiff-compression-parity-20261120/native-recipe-restoration.json`. Repeat the isolated fixture with `Run-HostSmoke.ps1 -NativeEffectCompression` on the disposable RAW catalog and pass the exact host's `-NativeSettingsConfig`.

## Native recipe naming suggestion

- [x] Resolve the selected recipe's token text using Capture One's actual naming manager and an observation-only counter token. Capture the suggestion before the Save dialog opens. Keep the existing source-derived fallback for missing or invalid names. The final approved path still passes the source and destination identity guard.
- [x] On first launch and restart, compare the offered stem to Capture One's actual Naming tool in the Export dialog. Both native commands offer the same stem. The output counter stays unchanged and the accepted files retain the measured Spektrafilm effects at 16 bits. Both licensed launches passed 62 checks.
- [x] Pass the Windows contracts, including Unicode and dotted names and rejection of invalid paths in the filename field. The Viewer/export suite passed 12,386 checks. Its optional symlink case was skipped because the test process lacks that privilege.

Evidence is under `.test-results/host-native-recipe-naming-async-20261120` and `.test-results/contracts-native-naming-display-20261120`. Native occupied-name policies and counter advancement remain open. [Capture One Naming tool](https://support.captureone.com/hc/en-us/articles/360020905117-Naming-tool)

## Native recipe location suggestion

- [x] Use the host's root location and subfolder naming manager to offer the existing recipe location in Save. Keep the existing root when a resolved subfolder is absent or cannot be used. The suggestion does not create a directory or approve a destination.
- [x] Pass 87 licensed assertions each on launch and restart. The native Location tool and Spektrafilm agree on the actual Recipe Name token path. Previous Settings opens Save in the existing nested literal folder or the root when the subfolder is absent. Six explicitly approved RGB48 TIFFs reopened in those exact isolated folders with the selected recipe ICC and dimensions. The other ten effect and control files retest normal routing.
- [x] All nine original recipe files matched their verified backups after each launch. The Windows Viewer and export suite passed 12,390 assertions, including absolute and parent-folder rejection for subfolder hints and leaving missing directories uncreated. Its optional symlink case requires a privilege unavailable locally.

Evidence is in `.test-results/host-native-location-accepted-paths-20261120`. Repeat with `Run-HostSmoke.ps1 -NativeEffectFolders` and the exact host's `-NativeSettingsConfig`. This source update is newer than the verified audit.23 package.

## Native slider and compiled renderer compatibility

- [x] Validate each native renderer slider's actual bounds, reset value and step against the compiled option type at startup. Verify output and input choices, push/pull, scan inversion and worker switches before loading the tool.
- [x] Keep existing managed reset values. Some authored values serve a separate panel function, including the UV and IR off sentinels and starting values for custom color channels. The native inventory reports the slider and renderer defaults explicitly for each managed number.
- [x] Reject ambiguous option shapes, nonfinite float32 bounds and invalid integer ranges. Pass 268 state and recovery assertions, the complete Windows contract script and the native 344/348 assertion general runs.
- [x] Record the bounds, increments, units and defaults of 112 native renderer sliders. The licensed inventory passes 20 assertions and retains the same 197-control published inventory.

Evidence is in `.test-results/host-renderer-numeric-inventory-20261120/panel-numeric-contract.json`, `.test-results/host-renderer-range-full-20261120` and `.test-results/contracts-renderer-control-ranges-20261120`. The compatibility check preserves existing reset behavior. A generated managed layout and versioned appearance defaults remain open.

## Native effects export outside the tool tab

- [x] Switch the actual Capture One workspace to an ordinary tab and confirm the docked Spektrafilm controls are hidden while the native Viewer paints the settled look.
- [x] Open the real Capture One floating tool while the ordinary tab remains selected. The same Viewer paints the selected look with the native floating controls visible.
- [x] Use the actual Export dialog and Previous Settings in each layout. Eight 3000 by 2000 RGB48 output files keep the selected ICC and the docked reference's film, diffusion, halation and scatter. On an independent sample of 41,238 codes per comparison, maximum mean difference is 1.05e-6 of full-scale RGB and the largest sampled difference is 0.000855.
- [x] Pass 96 licensed assertions on each launch, confirm film output versus the strength-zero reference, and restore the previous tab and docked tool each time.

Evidence is in `.test-results/host-native-export-hidden-and-floating-20261120`. Repeat with `Run-HostSmoke.ps1 -NativeEffectPanelModes` and the exact host's `-NativeSettingsConfig`.

## Native export failure with the tool hidden

- [x] Keep the export failure and exact destination in the durable job receipt. When neither the native Export dialog nor the panel status is visible, ask Capture One's Windows notification presenter to show a short message. The notification directs the user to the tool for details. The native dialog title and visible panel remain the in-app ways to see a failure.
- [x] Fail an accepted Previous Settings job against a read-only TIFF with the real tab hidden, then with the real floater visible. Both versions preserve every byte of the previous TIFF and leave a Failed receipt. The hidden route requests a single notification. The visible floater shows the error without a second notification. Both launches pass 104 native assertions and continue to export the same effects.
- [x] Submit a clearly labelled disposable export test message through the same Capture One presenter. The licensed host accepted submission and reported it in the local trace. Two automated layout runs separately exercise a refused submission and retain the durable panel result.

Evidence is in `.test-results/host-native-export-background-notice-20261120` and `.test-results/host-native-export-notice-submitted-20261120/native-notice-submission.json`. The fixture uses `-NativeEffectPanelModes`; set `SPEKTRAFILM_QA_SUBMIT_EXPORT_TOAST=1` for one clearly labelled submission during the first launch. This source checkpoint is newer than audit.24.

## Restore looks onto an already moved catalog

- [x] Add `Restore-SpektrafilmCatalog.ps1 -SavedLooksOnly`. It verifies both archived payloads and checks that the existing destination catalog has the exact archived database bytes. Hold a read lease on that closed database while publishing the look state under its new catalog key.
- [x] Keep the existing preview and `-Apply` flow. Check SQLite integrity, protect existing destination looks and refuse stale journals, mismatched databases and locked files. Preserve the database bytes, previous looks and grain records. Flush rebased recovery checkpoints before staging becomes visible.
- [x] Pass 54 backup and restore assertions in Windows PowerShell 5.1 and PowerShell 7. Keep an existing read-only catalog attribute and recognize the same state key under a case-only path change. In the licensed host, the already copied closed DNG catalog loads its saved enabled 1.37 EV look and paints against real 16-bit development on launch and restart. Both runs pass 19 assertions with no rewrite of the archived look bytes.

Evidence is in `.test-results/contracts-catalog-restore-durable-20261120`, `.test-results/contracts-catalog-restore-durable-ps7-20261120` and `.test-results/host-real-moved-catalog-looks-only-20261120`. The native fixture takes `-SavedLooksBackup` and uses a separate copied catalog. Automated discovery after a later database change and crash recovery around multi-directory publication remain open.

## Cleanup after an abrupt export stop

- [x] Put newly created, approved output staging files under Windows delete disposition while the writer owns the open handle. Write, verify and flush the data and recheck the approved destination before releasing the flag for atomic publication. Retain normal cleanup when Windows cannot provide disposition. Startup needs no filename sweep. [Windows file disposition](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_disposition_info)
- [x] In the licensed disposable catalog, stop Capture One after 1,325,155 confirmed encoder bytes. The prior TIFF, the unrelated temporary file and the enabled 1.37 EV look survive. Restart finds zero owned staging files and a distinct explicit retry produces a new 6000 by 4000 RGB48 film TIFF with an ICC profile. The fixture passes 17 checks before the stop and 22 after reopening.
- [x] Pass 12,395 Windows Viewer and export assertions, including a separate-process staging stop which preserves the source, previous output and unrelated file. Cancel actual 24 MP WIC TIFF and PNG writes in 3.16 ms and 0.84 ms after signaling. Both leave the original and previous destination byte-identical.
- [x] Repeat the actual native commands with film, optics and both grain implementations. Pass 114 assertions on both launches, including protected output failures and same-dialog retry. The complete native suite passes 344 and 348 assertions and exercises the maintained Cancel export button while confirmed TIFF writes are in progress.

Use `dotnet build -c Release` for both `NativePanel` and `qa/HostSmoke` before a source fixture. The initial checks reused an older Release build. The runner now rejects stale default binaries before it copies a catalog or starts Capture One. Explicitly pinned package and fixture binaries remain usable. The fresh-build interruption fixture measures confirmed WIC writes because an exclusive Windows handle can leave directory metadata stale. Evidence is in `.test-results/host-native-crash-owned-{encoding-built,ordinary,full}-20261120`, `.test-results/contracts-crash-owned-export-encoder-counter-20261120` and `.test-results/encoder-crash-owned-20261120`. This source checkpoint follows audit.25. Interruption immediately before final rename, filesystem fallback and physical power loss remain open.

## Mixed selection and native Export

- [x] For Capture One's edit-all selection, check secondary variants when the primary master is off. If a secondary has an enabled saved look or its saved state is temporarily unavailable, require one selected variant. A primary-only export with its master off delegates normally. An all-disabled selection also delegates.
- [x] Select actual RAW variants in the licensed Browser and verify the production bridge's routing decisions. With the primary off and secondary on, native batch preflight recognizes the saved look and requires a single variant. Selecting the secondary on its own enables export. Disabling both permits the native selection, and the last export receipt remains exact.
- [x] The full four-RAW matrix still applies film, diffusion, halation and scatter through the real Export and Previous Settings commands. It passes 213 assertions per launch and saves 40 verified 16-bit controls and processed images. Evidence is in `.test-results/host-native-export-mixed-selection-20261120`. Applied batch processing remains a separate audit item.
- [x] Keep an enabled saved look on the Spektrafilm path while its controls are loading. Both native commands require one selected variant and a ready look before starting. An explicitly disabled current look still delegates when its saved file is temporarily locked. The licensed fixture exercises the production route decisions and exports 40 verified 16-bit effect and control files across four RAW families. It passes 215 checks on both launches. The test probes the route decision directly for unavailable state; the normal commands and native confirmation are exercised with a ready look. Evidence is in `.test-results/host-saved-look-export-guard-off-lock-20261120`.

## Floating tool layout by native workspace

- [x] Read the active workspace identity through the licensed host's workspace manager. Keep separate floating mode, bounds and vertical scroll for each workspace. A new workspace starts docked and can use the prior placement as a starting point. Explicit Dock Tool clears only that workspace's open state. Detach keeps the last floating mode.
- [x] Reopen a saved owned Capture One window passively on the UI dispatcher. Keep the explicit Float Tool action able to activate it. A native auto-sized first window ignored programmatic height during the initial fixture. Restoring an explicitly saved size now switches to manual sizing so the saved height and scroll are usable.
- [x] Use `-FloatingWorkspaceLayout` to compare a second simulated layout on the real window, then reopen the real Default workspace in a separate licensed process. The selected tool, owner, 366.4 by 300 DIP bounds and 96 DIP scroll survive. The passive window does not become foreground. Both process launches pass, with 22 and 19 assertions. The Windows Viewer and export suite passes 12,398 assertions.
- [x] Pass 717 licensed lifecycle assertions across 100 measured attach, float, dock and detach cycles. All tracked retired windows, controls and hosted tools become collectible. Both native effects export commands still pass 104 hidden/floating layout checks in each launch. The full native suite passes 344 checks and 348 after restart.
- [x] Use the native manager to switch between two actual custom workspaces. Capture One reports the same internal name for both, so the saved layout key includes the custom label. When a switch replaces the palette, refresh the binding before showing a floater. Returning to the first workspace restores one passive floating window. The real Export and Previous Settings commands then write the expected film, diffusion, halation and scatter. The licensed fixture passes 68 assertions and produces five verified 16-bit effect and control files.
- [x] Verify copies of both custom workspace files before the native test. Both originals match their backups after the run. The two native workspace preference entries stay exact, and cleanup removes the disposable recent catalog entry while preserving the application's run counters. A separate fixture restores existing and missing scoped settings on disposable XML profiles and checks that unrelated settings are retained.
- [x] Independently decode 18 million RGB16 codes per comparison from the workspace export fixture. Both native commands agree within 7.32e-7 mean full scale. Repeat passive restore with 22 and 19 checks, pass the 33-check native concurrent-catalog fixture, and pass 12,400 Viewer and export contracts.
- [x] Fit the currently open floating window after Windows delivers a display or DPI change. Run after WPF finishes its layout and keep the original activation, owner and control instance. The licensed fixture moves the real floater beyond the work area and sends a simulated display-change message to its own native handle. It returns to the work area on both launches without taking focus. The complete host suite passes 347 and 351 assertions. With the new native window hook active, the 100-cycle lifetime fixture again passes 717 assertions and all tracked retired objects become collectible. [Windows display-change message](https://learn.microsoft.com/en-us/windows/win32/gdi/wm-displaychange) · [Windows DPI changes](https://learn.microsoft.com/en-us/windows/win32/hidpi/wm-dpichanged)

Evidence includes `.test-results/host-floating-workspace-{reopen-manual,lifetime,export,general}-20261120`, `.test-results/host-real-workspace-stable-effects-20261120`, `.test-results/host-workspace-passive-restart-20261120`, `.test-results/host-workspace-preference-scope-20261120`, `.test-results/scoped-workspace-preferences-20261120` and `.test-results/host-floating-display-change-20261120`. Repeat the two real custom workspace transitions with `Run-HostSmoke.ps1 -NativeWorkspaceSwitch` and the verified `-NativeSettingsConfig`. The primary workspace persistence fix is implemented. The licensed host's built-in preset collection was empty during the fixture. Additional preset types, physical mixed-DPI displays and RDP remain open.

## Native recipe filename suffix

- [x] Read Capture One's typed Existing Files setting. When the recipe uses Add Suffix, offer the first free space-number suffix in its resolved existing folder. Check occupied files and folders. Keep the Save confirmation and original-path protection. [Capture One Location tool](https://support.captureone.com/hc/en-us/articles/360020905077-Location-tool)
- [x] Run both native commands against occupied paths on launch and restart. Save offers suffixes 2 and 4 in order with an existing base file, another file and an occupied directory. Both paths preserve the old entries and reopen at 3000 by 2000 in RGB48 with the selected ICC. The independent decoder compares 18 million channels per run and measures less than 7.35e-7 mean full-scale difference.
- [x] Pass 105 native assertions per launch and verify all nine recipe files against the original backups after each run. The four-RAW native effects and mixed-selection matrix still passes 213 assertions per launch. Windows Viewer and export contracts pass 12,406 assertions, including an injected disk-full write after staging begins. The injected failure retains the Windows error and preserves both files.

Evidence is in `.test-results/host-native-recipe-suffix-effects-20261120`, `.test-results/host-native-suffix-raw-matrix-20261120` and `.test-results/contracts-updated-identity-native-export-20261120`. Repeat with `-NativeEffectFolders` and the exact host's `-NativeSettingsConfig`. This source checkpoint follows audit.27. The rest of Capture One's automatic existing-file actions and physical disk exhaustion remain open.

## Saved looks after catalog edits

- [x] Add the explicit `Restore-SpektrafilmCatalog.ps1 -SavedLooksOnly -AllowUpdatedCatalog` option. With closed database read leases and verified backup hashes, match the document UUID and the variant-to-image UUID for every saved state entry. Require complete unambiguous identities and verify SQLite integrity before publication. The existing exact SHA workflow remains the default.
- [x] Pass 63 joint backup assertions each in Windows PowerShell 5.1 and PowerShell 7. The fixtures verify read-only preview, exact look and recovery bytes, and rejection of changed document identity, a remapped image, a missing variant and a duplicate variant. Failed checks publish no look state and preserve database bytes.
- [x] Modify the output counter on a disposable copy of a verified real catalog. The copy has a different database hash. Restore the enabled 1.37 EV look and reopen in Capture One on first launch and restart. Both pass 19 checks and paint on native high-depth development. The backup and updated source keep their exact hashes. The opened database retains the updated counter and passes SQLite integrity.

Evidence is in `.test-results/catalog-identity-contracts-{ps51,ps7}-20261120`, `.test-results/updated-real-catalog-identity-20261120/database-update.json`, and `.test-results/host-updated-catalog-identity-20261120`. Repeat using `-SavedLooksBackup` and `-SavedLooksFromUpdatedCatalog` with a disposable moved copy. Automatic catalog discovery and power-loss publication remain open.

## Native effect export in three output profiles

- [x] In a disposable native recipe, select sRGB, ProPhoto RGB and linear Rec.2020 in turn. Use both actual native export commands with film, diffusion, halation and scatter applied. Each file reopens at 3000 by 2000 with 16-bit channels and the exact selected ICC bytes. Restore the previous live profile and verify all nine recipe files against their backups after launch and restart.
- [x] Pass 108 licensed assertions per launch. The ProPhoto and linear Rec.2020 outputs have different stored RGB values from sRGB. Each TIFF and PNG pair preserves the same look. An independent decoder evaluates the embedded ICC matrices and tone curves in D50 Lab over 122,694 pixels per comparison. Across 12 files the highest 95th-percentile Delta E 2000 is 0.364 and the largest sampled value is 0.885.

Evidence is in `.test-results/host-native-output-profiles-20261120`, including `independent-icc-matrix-trc.json`. Repeat with `-NativeEffectOutputProfiles` and the exact host's `-NativeSettingsConfig`. This source validation uses the same recipe color pipeline as audit.28.

## Diffusion buffer lifetime and remaining tile parity

- [x] Retain one raw scratch buffer for both camera and print diffusion outputs. The existing compute barrier finishes all camera reads first. The wider experiment also shared the halation boost buffer with print input, which changed tiled pixels. The retained implementation keeps those inputs separate. [Vulkan compute synchronization](https://docs.vulkan.org/guide/latest/synchronization_examples.html)
- [x] Compare the exact audit.29 worker with the narrowed implementation on the RTX 3090. All 16 complete 24 and 45 MP replies match, covering packed Viewer, float export, exposure edits and a changed source. Reported transient allocations fall by 291,776,000 and 425,201,600 bytes respectively. Peak worker commit falls by about 0.27 and 0.38 GiB. The 45 MP warm Viewer remains close to 2.56 seconds in this renderer-only test. Full renderer smoke also passes on physical Intel graphics.
- [x] Independently compare both layouts in the exact audit.29 worker and the narrowed candidate on a 4,097 by 31 Super 8 chart. Within each layout the builds agree byte for byte. Across layouts the baseline agrees within 4.77e-7 on halation and boost alone, then differs up to 0.06184 when print diffusion is added. The combined camera and print case differs up to 0.01447.
- [x] Through both actual Capture One commands, save 16-bit TIFF and PNG with print diffusion, camera diffusion, highlight boost and scatter. An independent decoder confirms print diffusion changed mean RGB by 1.244% and both commands agree within 5.29e-7 mean full scale across 18 million codes per launch. The expanded output profile fixture passes 129 assertions on each launch, writes 28 verified effects and control files, and verifies all nine recipe files against their backups. Evidence is in `.test-results/host-candidate-native-print-diffusion-20261120`.

The baseline reproduction is `.test-results/optical-raw-stage-20261120/baseline-legacy-tiled.json`. The narrowed comparison and physical smoke logs are in `.test-results/optical-diffusion-reuse-final-20261120`. Continue RC-04 by calculating the needed margin and active rectangles from the actual diffusion components and the aligned reduced-grid footprint. Exercise the long stress chart and typical 24 and 45 MP photographs before changing deployed tile geometry. To test a changed worker in a source host run, use `Build-Windows.ps1 -BuildPanel -StageRuntime` and verify the worker SHA before launching the disposable catalog.

The local branch `audit/diffusion-tile-support-prototype-2026-11-20` retains prototype `26a9c90`. It computes actual camera and print diffusion margins and leaves full-frame pixels identical to audit.30. On Intel, 12 graphs now match exactly across layouts. On the RTX 3090, eleven meet the strict 2e-6 float check and the remaining graph differs on five channels by up to 2.76e-6. The largest baseline stress difference was 0.11572. The corrected 24 and 45 MP export graphs use 9.57 and 17.94 GiB of reported transient allocation, up from 7.21 and 10.60 GiB. The one-warm-sample wall times are about 1.16 and 1.85 seconds in the corrected graph. Keep the prototype separate while calibrating a smaller graph footprint and investigating the strict residual. Exact fixtures are `.test-results/diffusion-tile-support-{nvidia,intel}-20261120.json` and `.test-results/diffusion-support-{24,45}mp-nvidia-20261120.json`.

## Vulkan memory accounting by heap

- [x] Record the allocation heap reported by the Vulkan memory type. The device-local budget credits reusable bytes on that same heap. Reflect the preflight credit and the total cached allocation separately in worker diagnostics.
- [x] Pass NVIDIA and Intel smoke tests. On the RTX 3090, all 16 complete packed Viewer and float export replies at 24 and 45 MP match audit.30. The selected heap credit excludes up to 137,362,784 bytes from the prior total cache. See `.test-results/physical-heap-budget-20261120`.
- [x] Verify the same worker SHA after staging. The licensed stacked-effects TIFF and PNG and three-profile fixture passes 129 checks on launch and again after restart. Both runs verify the nine recipe files against their backups. See `.test-results/host-heap-budget-native-effects-20261120`. This is a source checkpoint following the verified audit.30 ZIP.

## Actual save-failure close confirmation

- [x] Lock the disposable variant's primary look file and block its recovery file with a directory. Invoke the actual close handler. The test recognizes the owned Windows confirmation, checks that the default is No and invokes that button by its native control ID. Capture One keeps the pending edit. Releasing both obstructions publishes the exact value.
- [x] Pass the complete native fixture with 348 checks on launch and 352 after restart. The fixture also checks explicit discard, a failed prompt, Windows shutdown and recovery-only durability through the corresponding close decisions. Physical storage exhaustion and power loss remain open.

Evidence is in `.test-results/host-real-unsaved-close-prompt-20261120`. This completes the bounded HS-04 close and retention guard.

## Independent saves for the edited variant

- [x] Commit the changed variant as the slider moves. Leave other pending variants available for the existing selection, background retry and close paths. Preserve the recovery receipt when the same value is queued twice, and keep the failure visible until remaining writes clear.
- [x] Pass 271 state and recovery tests. An exclusive lock blocks an earlier primary while the current image commits. The earlier checkpoint stays byte-identical with the same timestamp, and retry commits the exact value after access returns. The Windows contract suite passes with 12,406 Viewer and export checks and 101 installer checks. See `.test-results/contracts-scoped-variant-save-20261120`.
- [x] Repeat isolation with real slider controls on two RAW variants. A held primary lock yields a durable checkpoint. The selected image's edit reaches its own file while the earlier checkpoint retains its bytes and timestamp. After the lock releases, retry publishes the earlier edit and leaves the newer one exact. Both licensed four-RAW launches pass 222 assertions.
- [x] Use both actual native export commands while an enabled saved look is loading. Each presents the expected owned warning and accepts no job before readiness. The fixture verifies and dismisses the live Windows dialog. The first automated attempt stalled because Windows assigned the visible OK button the Cancel control ID while reporting OK as the default. The corrected fixture verifies the body, owner and default before dismissing the actual button. Ready effects exports still pass with 40 verified files. See `.test-results/host-scoped-variant-save-native-final-20261120`.
- [x] Pass 348 and 352 complete licensed assertions, including the actual close confirmation, then repeat 10,000 real slider movements over 100 visits across four RAW variants. Read every acknowledged write from disk. After the disposable process is terminated, restart restores every variant's final acknowledged settings and master switch byte for byte. The new run measures p50 4.70 ms, p95 5.45 ms and p99 6.09 ms with the Viewer paused. Evidence is in `.test-results/host-scoped-variant-save-general-final-20261120` and `.test-results/host-scoped-save-10000-edits-20261120`.

## Print optics by processing mode

- [x] Disable the print diffusion and paper glare groups in Scan Negative and show the mode that makes them available. Keep the user's values and the active camera diffusion and scanner MTF controls. Returning to Print Simulation restores access to the retained values.
- [x] Compare the two actual native Scan Negative outputs with different dormant print settings. Both reopen as 16-bit files with the exact recipe ICC and agree within 4.17e-7 mean full scale across 18 million codes per launch. Both source launches pass 146 checks and write 32 verified effect and control files in total. The native UI inventory passes 20 checks.

The initial UI probe evaluated individual controls inside collapsed sections. The successful fixture verifies the owning groups, active neighboring controls and actual native output. Evidence is in `.test-results/host-scan-mode-print-optics-retry-20261120` and `.test-results/host-scan-mode-optics-inventory-20261120`. This is source validation after audit.31.

## Reusable linear-light drag previews

- [x] Keep a 128 MiB least-recently-used cache of float drag source levels per active Viewer. Each level is computed directly from the same exact developed frame and its declared input encoding. Turning drag preview off or redeveloping the RAW clears all levels. A renderer already using a level retains its buffer.
- [x] Both licensed Viewer stress launches pass 52 checks. Switching 720 to 1600 px and back reuses the same source and at least 32,807,680 bytes of float buffers. The fixture verifies that rebuilding a level gives byte-identical pixels and uses the same native developed source. Windows Viewer contracts pass 12,416 checks. Evidence is in `.test-results/host-draft-pyramid-20261120` and `.test-results/contracts-draft-pyramid-20261120`.
- [x] Run `qa/blur-support.py --measure-optical-scales` at 720, 1600, 2560 and 4096 px with the exact audit.31 worker. The 48 effect cases compare the same normalized bar on Super 8, 35 mm and 70 mm. In 35 mm, the camera diffusion off-bar RMS width spans 1,041 to 1,066 µm and print diffusion spans 1,866 to 1,909 µm. Scatter weakens markedly below a pixel at coarse settings, especially for 70 mm. The receipt separates mean, peak and off-bar response so later work can identify gains against the same chart. See `.test-results/optical-preview-scales-nvidia-20261120.json`.
- [x] Recheck real Capture One Export and Export with Previous Settings with the new Viewer cache. Both licensed launches pass 222 assertions and write 40 confirmed RGB48 TIFF and RGBA64 PNG effect and control files across four RAW formats and input encodings. The independent decoder checks more than 156 million 16-bit codes per comparison, exact ICC and the separate film, diffusion, halation and scatter changes. The largest mean route difference is 6.72e-7 of full scale. Compared with audit.31 the largest mean change is 2.81e-6 on the DNG film-only control. Native no-film controls vary slightly between the runs too. See `.test-results/host-effects-after-draft-pyramid-20261120/independent-rgb16.json`.

The primary RC-03 work is complete. Cross-context memory ownership is tracked under VE-09. The low-resolution scatter response and full-frame versus tiled diffusion still need renderer work.

## High-resolution Viewer fixture sources

- [x] Download the two RAW fixture sources and verify their SHA-256 against the public archive. Capture One ImageCore reads both without opening a catalog and reports 8192 × 5464 for the Canon EOS R5 CR3 and 9504 × 6336 for the Sony a7R IV A ARW. The native metadata and source hashes are under `.test-results/high-resolution-raw-fixtures-20261120`. [RAW sample archive](https://raw.pixls.us/) · [Checksum index](https://raw.pixls.us/data/filelist.sha256)
- [x] Use SQLite backup on the read-only donor and build an independent disposable catalog with the two verified references, the 24 MP Ricoh DNG and the 24.34 MP Nikon NEF. The two new variants carry their camera-native ICC, curve, orientation and dimensions from ImageCore. The catalog and four RAWs verify against their receipts and both source catalogs stay exact after launch. Fixture and reproducible local builder are under `.test-results/high-resolution-raw-fixtures-20261120`.
- [x] Pass 40 checks in matching runs of audit.32 and the current source. Each paints all four RAWs at real resolutions up to 9504 × 6336, at 0%, 50% and 100% strength. Initial running memory timeline includes 364 and 363 samples at a target 50 ms interval. The first attempt had a real already-developed first selection and failed a cold-development assumption. The corrected fixture explicitly clears the Viewer before sampling.

## Viewer display buffer ownership

- [x] End the temporary BGRA display buffer's lifetime at WPF bitmap creation, which takes its own copy. Managed float source and film pixels remain at high depth. On the RTX 3090, sampled peak managed bytes decreased from 8,073,213,552 to 7,071,490,880 on the paired 12-visit workload. Combined host and worker private bytes decreased from 21,400,592,384 to 20,881,649,664. Settled p50/p95 were 1,132/4,371 ms and 1,195/4,357 ms, with broadly stable latency across the pair. Compare `.test-results/viewer-display-scratch-ab-20261120.json`. [WPF cached bitmap implementation](https://source.dot.net/presentationcore/System/Windows/Media/Imaging/CachedBitmap.cs.html) · [Windows Imaging Component bitmap factory](https://learn.microsoft.com/en-us/windows/win32/api/wincodec/nf-wincodec-iwicimagingfactory-createbitmapfrommemory)
- [x] Pass 12,506 Windows Viewer contracts. Pixel checks include cross-thread immutable bitmap reading, every input encoding and test strength, source alpha, a parallel 4.2 MP conversion and cancellation. Both licensed Viewer stress launches pass 52 checks. Both native export runs pass 146 with camera and print diffusion, halation and scatter at 16 bits. The independent TIFF/PNG comparison captures all 18 million codes per launch and finds less than 4.41e-7 average full-scale difference. See `.test-results/contracts-viewer-display-parallel-20261120`, `.test-results/host-viewer-scratch-stress-20261120` and `.test-results/host-viewer-scratch-native-effects-20261120`. These are source checks after audit.32.
- [x] Run 100 native-resolution visits and 30 interleaved selections before the callback fix. The fixture paints 100 frames and passes 129 assertions. Its 2,514 samples establish that dropping the panel fields and exiting both workers still leaves the last RAW alive after collection. The managed heap is 1,079,096,800 bytes after drain. Evidence is in `.test-results/host-native-resolution-100-visits-20261120`.

## Completed native callback results

- [x] Detach the result task from Capture One's callback delegate on completion or abandonment. The awaiting caller keeps ownership of its result, and a running callback retains the native scheduler slot until it returns. The regression keeps a callback delegate alive while verifying its returned image becomes collectible. Windows contracts pass 12,507 assertions including 200 delivery and cancel races.
- [x] Repeat the same 12 native-resolution visits after the ownership fix. Each actual 16-bit frame paints, all sampled source objects become collectible after disabling the master, and managed memory after drain falls to 111,372,360 bytes. The fixture passes 41 assertions. Evidence is in `.test-results/host-native-result-ownership-12-visits-20261120`.
- [x] Pass 26 licensed checks with an actual paused native callback. Timeout keeps the native scheduler slot and the private ICC file until the callback returns. An accepted export waits for that slot and delivers an ICC-tagged 16-bit TIFF, and detach prevents late frames from publishing. The first two tests reused a source snapshot captured before the stalled callback. Cancellation telemetry confirmed the deliberate timeout and showed a separate rejection on the stale snapshot. Retaking the current source snapshot before recovery verifies the intended behavior. See `.test-results/host-native-result-ownership-lease-fresh-20261120`.
- [x] Repeat both real native export commands with stacked effects in the final source. First launch and restart each pass 146 assertions and all nine native recipe files match their backups. Independently reopen 32 16-bit TIFF and PNG files and verify film and optics. Across 18 million codes per comparison, the commands agree within 4.33e-7 mean full scale. Evidence is in `.test-results/host-native-result-ownership-effects-20261120/independent-rgb16.json`.
- [x] Repeat the identical 100 native-resolution visits and 30 interleaved selections with a strict zero-retained-source assertion. All 129 licensed checks pass, and all sampled RAW frames are collectible after the master is disabled. Managed memory after worker drain and collection falls from 1,079,096,800 bytes to 116,033,448 bytes. Compare 2,514 and 2,362 memory samples across 24.00, 24.34, 44.76 and 60.22 MP sources with the same worker. The paired native test verifies all four RAW hashes and both input catalogs. Evidence is in `.test-results/completed-native-result-ownership-ab-20261120.json` and `.test-results/host-native-result-ownership-100-visits-20261120`.

The memory fixes are in source commit `f1e5c9a`. Audit.32 remains the latest exact verified ZIP. The sampled 100-visit peak includes Capture One and its workers at full resolution and is about 21 GiB of combined private memory in each build. Cross-context cache and GPU admission remain open.

## Shared GPU contention baseline

- [x] Run `qa/renderer-benchmark.py --overlap-preview 3000x2000 --size 6000x4000 --scene stacked-optics --repetitions 12 --hash-replies` against the exact audit.32 worker. Twelve paired request windows overlap for 185 to 485 ms. All 24 results match their serial reply hashes. Warm Viewer p95 moves from 118 to 198 ms and export from 1,590 to 1,682 ms. The report holds per-worker memory and tile diagnostics. Evidence is in `.test-results/renderer-overlap-stacked-optics-20261120.json`.
- [x] Query the actual Vulkan device UUID and heap before upload, with separate fields for driver-reported budget and usage, retained memory and the full-frame estimate. The worker retains its tile-planning ceiling and rechecks current budget at render time. The managed coordinator groups active work by adapter and heap and uses the driver's reported heap budget with headroom. Pending output has priority, followed by interactive preview and optional warming. A forced retirement holds its lease until the worker actually exits.
- [x] On RTX 3090, the 6 MP Viewer completed during a held 24 MP export with 1.08 ms to acquire the lease. With a 60.22 MP export under pressure, the Viewer waited 4.46 seconds in the controlled delivery test. All packed and float replies matched serial SHA-256. The first prototype shared the per-worker tile ceiling, which queued the 6 MP Viewer unnecessarily. The calibrated version uses the reported heap. A cold Viewer start initially missed the overlap window, so the test now warms the Viewer and briefly holds export delivery at the measured native render boundary. These are admission and byte-parity checks. See `.test-results/managed-shared-admission-{room,pressure}-20261120.json`.
- [x] Pass 180 client and scheduler checks, 12,507 Viewer contracts and 32 managed physical GPU checks. The raw protocol and Vulkan pixel smoke pass on RTX 3090 and Intel integrated graphics. The licensed two-catalog fixture passes 33 checks and the Viewer stress fixture passes 52 on launch and restart. See `.test-results/contracts-shared-heap-admission-final-20261120`, `.test-results/managed-shared-admission-gpu-20261120.log`, `.test-results/intel-shared-admission-smoke-20261120.log` and `.test-results/host-shared-admission-{catalogs,viewer}-20261120`.
- [x] Give stale native export snapshots a distinct failure and retry path. Only a signaled export token records a cancellation. An export source mismatch records the development phase and the captured and observed counters. The same native dialog can accept a fresh decision to export. The focused `-NativeEffectSourceRevision` run passed 40 assertions on launch and 40 after restart. A real Capture One exposure edit bumped only the low-resolution counter in both runs, so even a low-resolution-only revision change must invalidate the earlier snapshot. Both runs kept the existing TIFF byte exact and reopened a retried RGB48 TIFF and a Previous Settings RGBA64 PNG with the same applied effects and exact recipe ICC. All nine recipe files matched their backups and the source catalog remained unchanged. Evidence is in `.test-results/host-export-source-revision-20261120`.
- [x] The packaged audit.33 regression reproduced a spurious source revision change before export development. Audit.34 waits for the native Viewer development slot to return before taking the source snapshot. See Packaged export source timing below for the exact package tests.
- [ ] Track and reclaim idle GPU residency under pressure and measure actual incremental tile allocation. Extend coordination across separate host processes if needed by the remaining audit cases, then run an unheld before/after contention measurement.

Vulkan reports memory budget and usage for each process. The coordinator limits concurrent estimates inside Capture One and each worker retains its per-render checks. The driver can change available memory as other processes run. [Khronos adapter identity](https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceIDProperties.html) · [Khronos memory budget specification](https://docs.vulkan.org/refpages/latest/refpages/source/VkPhysicalDeviceMemoryBudgetPropertiesEXT.html)

## Copy Spektrafilm settings to selected variants

- [x] Add an explicit multi-variant command alongside single-variant Apply. Capture up to 1,000 native variant IDs before confirmation and intersect the chosen sections with what was copied. Preflight every changed look and recheck its baseline before writing. Stop leaves earlier atomic saves in place. Group Undo covers that saved subset and refuses newer conflicting edits. Undo restores the effective defaults for a previously unedited target.
- [x] Pass 281 state assertions including 1,000 durable target changes, complete group Undo and Redo, a newer-look conflict and refusal of unavailable or pending writes. In the licensed four-RAW host, 25 launch and 19 restart assertions verify selection changes during approval, exact untouched settings, real native Undo and Redo, the panel Stop command and the exact saved subset after restart. Run `qa/HostSmoke/Run-HostSmoke.ps1 -NativeLookBatch` with a disposable four-RAW catalog and the verified native preferences backup.
- [x] Persist a separate per-catalog batch history. Before replacing each look, flush its variant ID and exact before and after digests. After replacement, flush its completion. When Capture One restarts, the panel shows the saved count and flags an uncertain final variant if its saved digest matches neither expected value. The history remains on the machine under `Look batches`. Individual look backups still use the established paths. The active native Undo group belongs to its current Capture One process.
- [x] Pass 301 state assertions, including 1,000 per-variant receipts, live-writer exclusion, process termination before and after publication, preservation of an ambiguous later edit and compensating Undo after a Windows sharing violation. Pass 23 licensed checks before deliberately stopping Capture One after one of two writes and 20 after restart. The panel reports the interrupted batch and one saved variant on first attachment, including when Capture One starts on another tab. The four saved look states match the pre-crash records and the source catalog remains unchanged. Evidence is in `.test-results/host-native-look-batch-crash-final-20261120`.
- [ ] Measure batch history loading on slower storage and verify longer cross-catalog batch workflows.

Evidence is in `.test-results/host-native-look-batch-final-20261120` and the 281-assertion `tests/state` run. The disposable host inventory matches all 199 documented controls, including the new apply and stop buttons. See `.test-results/host-look-batch-inventory-20261120`. This is a source checkpoint after audit.32.

## Native Clone Variant look inheritance

- [x] Inspect and bind the exact native Clone callback on the verified host. The original native command still creates and selects the variants using its own Edit Selected policy. Inherit the saved Spektrafilm look only after matching one new UUID on the same RAW to one source and confirming that Capture One selected it. Match distinct RAWs independently. New Variant has fresh defaults, and native Reset preserves the separate Spektrafilm look on the same UUID.
- [x] Refuse inferred assignments when several source variants share a RAW and explain how to use the panel’s Copy and Apply actions. Preserve the source’s independent grain Restore receipt and reject any preexisting destination look. Detach restores the original native callback in its original position.
- [x] Pass 24 licensed assertions on the disposable four-RAW fixture and 22 after normal close and restart. The reopened Capture One RAW groups contain all eight expected original and new variant UUIDs with their exact saved looks or fresh defaults. The source catalog is unchanged. Run `Run-HostSmoke.ps1 -NativeVariantLineage` with the approved preferences backup. Evidence is in `.test-results/host-native-variant-lineage-final-20261120`.
- [ ] Find a verified native lineage signal for several siblings of one RAW and investigate native adjustment clipboard selection. The earlier forced-stop fixture showed that Capture One can lose newly allocated variant IDs before saving its catalog even though their Spektrafilm files have been published. Normal close lets Capture One persist its own identifiers.

## Reuse of developed negative density

- [x] Retain one full-frame negative immediately before grain. The key follows the immutable source and the exact upstream GPU inputs for exposure, film development, camera diffusion, halation and DIR. Downstream changes can reuse the density. The existing film and DIR buffers hold the result. The checkpoint is published after a successful fence and readback, and clears when its backing buffer or static film resources change.
- [x] Pass the physical Vulkan suite with 44 byte-exact cache/recompute comparisons across full and tiled requests. All 156 packed preview replies matched hashes from the previous worker, with the same GPU scratch allocations. The 100-request sweep reused 95 results and invalidated on all five source uploads. The concurrent Viewer and export workers each matched their serial image hashes. The new worker passed 36 licensed Capture One 16.4 Viewer checks, including eight settled paints using the selected RGB16 source across four RAW formats in a disposable catalog. The source catalog hash matched after close.
- [x] Measure print exposure with `qa/renderer-benchmark.py --edit print --scene stacked-optics --size 3000x2000 --route viewer --repetitions 26 --hash-replies`. On RTX 3090 the worker median/p95 moved from 118.33/119.02 ms to 49.24/49.54 ms. The warm graph went from 140 to 64 passes. Halation without the other effects moved from 42.43 to 18.23 ms and color-only stayed at about 19 ms. These timings cover the worker request and hashed reply.
- [ ] Extend the dependency map to the print and scanner stages and evaluate tiled reuse separately. Measure the complete print-slider-to-Viewer paint time on the licensed host. The export worker and tiled routes continue to compute the negative from scratch.

Evidence is in `.test-results/negative-density-full-smoke-20261120.log`, `.test-results/density-{baseline,candidate,sweep,overlap}-20261120.json` and `.test-results/host-density-viewer-20261120`. The source-build checkpoint has local evidence. The latest verified ZIP and installed build identities are listed above.

## Film slider definitions and typed optical values

- [x] Use `NativePanel/FilmControls.cs` for development control order, saved keys, wire options, ranges, increments, units and reset values. The existing preset keys and native labels remain the same. The legacy Developer exhaustion value can still be read and explicitly cleared. It has no worker option.
- [x] Bind slider numerics to the generated renderer parameter type. Round the four integer grain controls before publishing a native value change or applying older fractional values. Preserve fractions for continuous settings. Loading older values leaves the input data intact and only corrects the active control and render value.
- [x] Pass 446 state and recovery checks, including all 42 development endpoints, one-step-outside values, 64 truncated descriptions and the manifest size limit. Run `Run-HostSmoke.ps1 -PanelInventory -SkipRestart` to verify the actual native controls. The licensed fixture passed 38 assertions, including storage and wire values after fractional native input, legacy reload, reset values, and exact inventory parity.
- [x] Pass the real Capture One 16.4 general fixture, with 352 checks on launch and 356 after restart. The copied catalog fixture retains its original hash. Build against the signed 16.7.8.3529 references without warnings and verify all 563 compiled host references match the licensed 16.4 build.
- [x] Put 110 optical controls in `NativePanel/OpticalControls.cs`. The same definitions drive WPF groups, choices, defaults, range validation and wire ownership. The entire numeric inventory matches byte for byte. The real 16.4 panel retains all 199 entries.
- [x] Preserve 13 print-only settings in the look when switching to Scan Film. The panel disables their controls and omits them from scan requests. Switching back to Print sends the exact staged settings. The 42-check licensed inventory and six Vulkan comparisons verify the behavior. The scan and inverted scan bytes match with the print flags included or omitted, and Print Simulation changes.
- [x] The normal Windows panel build verifies all 112 numeric controls against the worker's compiled description at their minimum, initial value and maximum. State tests cover the tracked inventory and 32 seeded look mutations.
- [x] Pass 728 host-independent state and recovery checks, 12,507 Viewer and export contracts, and 222 licensed native effects-export checks on DNG, CR3, RAF and NEF. Both native Export commands deliver the 16-bit effect with the chosen ICC. The copied catalog hash is unchanged, and the rebuilt renderer matches the licensed fixture worker byte for byte.
- [ ] Finish versioned migrations and verify the remaining backend availability conditions. The public CI inventory checks are host independent. Add the compiled-worker matrix to public Windows CI separately.

Evidence is in `.test-results/film-control-contracts-20261120.log`, `.test-results/host-film-control-schema-20261120`, `.test-results/host-film-control-regression-20261120` and `.test-results/host67-contracts/reference-drift-film-schema.json`.

The optical definition evidence is in `.test-results/host-optical-schema-inventory-20261120`, `.test-results/optical-mode-gpu-parity-20261120.log`, `.test-results/contracts-optical-schema-native-acl-20261120` and `.test-results/host-optical-schema-effects-20261120`.

## Native export filename counters

- [x] Capture the native recipe naming preview and whether it contains a counter token. Advance the native counter after successful publication of the suggested filename. A canceled picker, custom name, or failed write leaves the counter as it was. A stale naming preview preserves the already approved output and avoids advancing the current counter.
- [x] Use the licensed fixture’s three-digit counter and increment of three. The actual Export command writes `Counter 041.tif` and advances to 44. Previous Settings writes `Counter 044.tif` and advances to 47. Both files reopen as effect-processed RGB16 with the requested ICC. An intervening counter change to 90 preserves both the approved file and the new counter. Existing recipe suffix behavior uses a separate literal name in the fixture.
- [x] Pass the 12,507 host-independent Viewer and export assertions. Compile against 16.4 and signed 16.7.8.3529 with no warnings, and compare all 566 emitted host references.
- [x] Pass 142 licensed export and naming assertions in one targeted launch. The source catalog and all nine original recipe files match their starting hashes. The supported host’s ordinary Export still opens without a Spektrafilm job when the master switch is off.
- [x] The separate `-NativeEffectRecipeRestart` fixture intentionally keeps the updated counter and increment on its disposable copy. Capture One reads both values after a clean restart and uses the next name. File commit and counter advancement remain separate operations.

Run `Run-HostSmoke.ps1 -NativeEffectFolders -SkipRestart` with the copied fixture and a verified preferences backup. Evidence is under `.test-results/host-native-output-counter-completed-20261120`, `.test-results/host67-contracts/reference-drift-native-counter.json` and `.test-results/native-counter-viewer-contracts-20261120.log`. [Capture One counter settings](https://support.captureone.com/hc/en-us/articles/360002533438-Controlling-the-counter)

## Native export Skip policy

- [x] Keep the file when a 16-bit TIFF or PNG recipe set to Skip resolves to a verified existing destination. Record a separate terminal receipt and keep the native counter. Check the approved recipe path again after Save so a recently created file is preserved. Missing or unresolved subfolders still provide a Save hint.
- [x] Pass 152 checks on licensed 16.4. The fixture explicitly selects TIFF and invokes both native commands. It also covers a file arriving during Save and an occupied root with a missing recipe subfolder. Three receipts report Skip with zero render or counter events. Existing bytes, all nine recipe files and the source catalog verify.
- [x] Pass 12,509 Viewer and export contracts and compare 566 emitted host references against signed 16.7.8.3529. Licensed 16.7 runtime coverage remains open.
- [x] Verify a native PNG Skip recipe through both Export and Previous Settings in the delivery policy fixture below.
- [x] Require Save approval when an 8-bit TIFF recipe set to Skip needs a 16-bit alternative. Its existing named file stays intact when the user chooses a new name. The 185-check licensed source run also verifies explicit TIFF16 and PNG skips. The 12,519 host-independent assertions passed. See `.test-results/host-native-recipe-skip-alternative-v3-20261120` and `.test-results/native-recipe-skip-alternative-contracts-20261120.log`. This source change follows the audit.34 package.
- [ ] Extend the Location matrix. Explicit overwrites still use Save approval.

Evidence is under `.test-results/host-native-tiff-recipe-skip-20261120`, `.test-results/recipe-skip-viewer-contracts-20261120.log` and `.test-results/host67-contracts/reference-drift-recipe-skip.json`. Run `Run-HostSmoke.ps1 -NativeEffectFolders -SkipRestart` on a copied catalog with the scoped preferences backup.

## Recipe delivery disclosures

- [x] State the requested recipe format or depth in Save when the user is choosing a 16-bit substitute. Include the actual format in completed export history. The policy also discloses the full-image fallback for crop paths. Cancel, failure and Skip have no completed-format note.
- [x] Pass 175 checks on licensed 16.4. The real Export command uses a JPEG recipe and Previous Settings uses an 8-bit TIFF recipe. Both disclose the 16-bit choice and persist the completed delivery note. Independently read both TIFF headers, ICC digests and ZIP samples to confirm RGB16 at 3000 by 2000 with retained low bits. The run also verifies both native PNG Skip commands, all nine recipe files and the unchanged source catalog. Pass 12,519 host-independent checks.
- [x] An actual PSD crop-path recipe on an asymmetrically cropped RAW offers a disclosed full-image alternative. Native Export publishes a 3000 × 2000 RGBA16 PNG with the exact selected ICC. Its effects pixels match the full-image Ignore Crop reference TIFF within the fixture tolerance. The Save title and completed receipt name the omitted path. The PSD writer and broader recipe and sidecar matrix remain open.
- [x] The exact audit.34 package passes 91 launch and 72 restart assertions on Capture One 16.4. The copied catalog retained its native output counter and increment across clean shutdown. Export wrote `Restart 041.tif`, then Previous Settings wrote `Restart 044.tif` after relaunch. Both contain RGB16 and the selected ICC. The counter finished at 47. All nine native recipe files and the source catalog verified. Repeat with `Run-HostSmoke.ps1 -NativeEffectRecipeRestart` and the exact native preferences backup. See `.test-results/host-native-recipe-restart-20261120` and its `independent-recipe-delivery.json`.
- [x] Capture an active recipe watermark, a sharpening mode other than the native variant default, and requested PSD annotation, overlay or guide layers. The Save title and completed history describe the omitted settings alongside the 16-bit delivery. A licensed source run on 16.4 passes 100 launch and 72 restart checks. It exercises a native text watermark and screen sharpening in a TIFF recipe, and an annotation layer plus crop path in a PSD recipe. Both alternatives reopen with the exact ICC and the expected high-depth film pixels. The original catalog and all nine recipes verify. The final completed-history wording passed 12,521 host-independent assertions, and all 574 compiled host references still match signed 16.7.8.3529 assemblies. Repeat with `-NativeEffectRecipeRestart`. Evidence is under `.test-results/host-native-recipe-omissions-20261120`, `.test-results/recipe-effects-contract-20261120` and `.test-results/native-recipe-omissions-final-contracts-20261120.log`.
- [ ] Implement watermark compositing, recipe sharpening, and actual PSD layer delivery. Licensed 16.7 runtime validation remains open.

Evidence is under `.test-results/host-native-delivery-policy-20261120` and `.test-results/native-delivery-policy-contracts-20261120.log`. Run `Run-HostSmoke.ps1 -NativeEffectFolders -SkipRestart` with the backed-up disposable catalog. [Capture One format settings](https://support.captureone.com/hc/en-us/articles/360021057478-Format-and-Size?page=1) · [Capture One crop paths](https://support.captureone.com/hc/en-us/articles/360020904857-Adjustments-tool)

## Packaged export source timing

- [x] Audit.33 from `9f32bef` reproduced the original unforced export failure on licensed 16.4. Before development, the expected high-resolution revision advanced from 33 to 34. Low-resolution and geometry revisions matched. The job refused publication and the original catalog and nine recipes verified. This candidate is rejected. Evidence is in `.test-results/host-audit33-delivery-20261120` and `.test-results/audit33-build-20261120.log`.
- [x] Reserve the selected export and drain the Viewer before capturing the native source and showing Save. Wait for the native development slot to return even when an earlier preview caller has already canceled. Keep the existing revision checks on the captured source during and after approval. Include the preparation interval in automated export timing and exclude the time spent in Save.
- [x] Build audit.34 from clean `4370ebe`. Verify all 153 package files and 19 critical runtime files, the renderer capabilities, and its physical Vulkan smoke. The extracted package on Capture One 16.4.6.2414 passed 175 native effects export checks on launch and 175 after restart, plus 40 separate checks for a real native edit after source capture and a successful same-dialog retry. A private upgrade preserved the custom file and external preset and retired the prior version only after native Export connected. The package is `dist/Spektrafilm-for-Capture-One-v0.2.0-audit.34-windows-x64.zip`, SHA-256 `80e176f5badd17a14512a10cf4b7f20654c576c4ba0b269d4f93907cca640095`. Evidence is in `.test-results/audit34-package-verify-20261120/static.json` and `.test-results/host-audit34-{delivery,source-change,upgrade}-20261120`.
- [x] Reproduce the later in-development mismatch on Capture One 16.4. The original variant, low-resolution and geometry revisions stayed steady while HighRes advanced from 11 to 12. See `.test-results/recipe-output-final/panel.log`. Matching two independently developed byte sequences failed the deterministic counter-only fixture. Retain that result as rejected evidence in `.test-results/sf-source-verify1`.
- [x] Compare the exact native model representation for variants without local adjustment layers or LCC and ICC sidecars. Read it under Capture One's verified model lock with stable counters before and after. Normalize only the numeric `highresProcessCounter` value and require an unchanged checksum. Capture One still develops the approved RGB16 source once. A missing model, ambiguous field, busy lock, layer or sidecar leaves the strict guard in place. The 50-check launch and restart cases exercised the original exposure edit and retry, a verified counter-only update, and a real exposure change forced to present the same high-resolution-only counter shape. It rejected the real edit and preserved the existing TIFF. The original test catalog and all nine recipes verified. See `.test-results/sf-revision-state` and `.test-results/source-revision-semantics-20261120/model-reference-comparison.json`. The 16.4 and signed 16.7.8.3529 builds match across all 583 compiled host references and have the same model reader-lock and representation field shapes.
- [x] Run the current source on a fresh copy of the disposable catalog that originally exposed the race. The same DNG passed the 100-check initial native export and disclosures fixture. The restart passed 72 checks, and all nine recipe files and the source copy verified. The spontaneous revision notification did not recur in this run. See `.test-results/sf-recipe-verified`.
- [ ] Verify a naturally occurring in-development update, cover local masks and sidecars, and measure the model-read overhead across representative catalogs. Keep the strict guard when the complete source model cannot be established.
- [ ] Verify the shared host adapter in an installed, licensed Capture One 16.7 runtime before extending the supported host manifest. Wider GPU contention measurements remain part of the performance audit.

## Audit.35 development package

- [x] Package clean `df4a7d9690cca3c4c8affdfcdd63e6bb8a654770`. Validate all 153 package files and 19 critical runtime files, compiled controls and capabilities, and the physical Vulkan renderer. The ZIP is `dist/Spektrafilm-for-Capture-One-v0.2.0-audit.35-windows-x64.zip`, SHA-256 `ad372b46210f34b42a924787e5c94733defdd51218b4edda62ab283cf69079b7`. The renderer byte hash matches audit.34.
- [x] Run the exact extracted panel on licensed 16.4.6.2414 in disposable copies of the catalog that previously exposed the export fault. The revision fixture passed 50 launch and 50 restart assertions. The recipe disclosure and naming fixture passed 100 launch and 72 restart assertions. The folder and collision-policy fixture passed 185 assertions. All nine native recipes and the closed source copy verified. An independent reader verified five 3000 × 2000 TIFF/PNG outputs at 16 bits per channel with the exact selected ICC. All 256 low-byte codes appear in each sampled TIFF.
- [x] Upgrade privately from audit.34, verify the migrated custom file and external preset, and retain the old installation until the launched Capture One panel reports native Export connected. The installed panel hash matches the extracted binary. The private Desktop contains `Spektrafilm for Capture One.lnk`. The regular installation still identifies `ffc4819`. Evidence is in `.test-results/audit35-package-verify-20261120/result.json`, `independent-file-check.json` beside it and `.test-results/sf35-upgrade/installed-upgrade-receipt.json`.
- [ ] Complete licensed Capture One 16.7 runtime validation and the remaining audit criteria before public delivery.

## Status maintenance

`qa/AUDIT-FIX-STATUS.json` owns task IDs, status, implemented substeps, remaining acceptance work and evidence. This handover owns context, runtime identity and resume instructions. `.test-results/FINAL-RESULT.json` is the completed runtime delivery receipt; keep its runtime commit/package identity distinct from later documentation-only commits.

The sibling audit folder retains baseline findings and line references at `d56ebef`. Its `CURRENT-IMPLEMENTATION.json` and task status overlays mirror the current ledger. `sync-implementation-status.py` refreshes those overlays; `assemble-audit.py` must invoke it after regeneration so progress cannot revert to `not_started`. Refresh the audit ZIP/checksum after editing its documents. Preserve historical probes and their original scope.

At each meaningful checkpoint: update only affected task records; name the actual test evidence and skipped cases; mark a milestone complete only after its result is observed; keep unproven acceptance work partial; record the build, package, installed and remote states separately; commit documentation with the associated change. Before final handoff, validate task IDs/counts and JSON, inspect `git diff --check`, and verify the remote state if reporting it. Do not rebuild or reinstall a working package solely to make its runtime revision equal a documentation commit.

Carry verified evidence forward for unaffected behavior. Repeat a test when a new change can affect its result, when the previous run lacked required coverage, or when investigation needs a specific comparison. Record the reason before starting another full fixture.
