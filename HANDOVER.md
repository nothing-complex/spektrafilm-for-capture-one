# Spektrafilm for Capture One development handover

Updated 2026-11-20. The critical-fix batch is implemented, tested, installed locally and published in a draft pull request. The audit follow-ups below passed local validation. The full 70-item integration audit tracks 23 bounded primary fixes implemented and 47 partial items. It includes feasibility investigations and enhancements.

## Resume here

1. Read this file, `AGENTS.md` and `qa/AUDIT-FIX-STATUS.json` before changing code. The JSON is the authoritative task-progress ledger; its `remaining_work` and original acceptance tests matter more than a short status label.
2. Check `git status`, current branch and PR head. Do not discard existing edits or switch the installed build merely because this documentation has a newer commit.
3. The regular installed runtime and original delivery package identify `ffc481988eb2e6e7ef7a1379486c4a8b2656ac81`. The initial product changes are in `285f0aa`. The latest verified development ZIP is `0.2.0-audit.12` from clean `4e42336`. Its verification used an isolated installation. The normal app stayed on `ffc4819`.
4. Choose an unresolved task and an exact ownership boundary. Do not repeat the completed installation, crash test or old defect reproductions as if nothing had happened.
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
- [ ] Add opt-in multi-selection application, exact native clone lineage, and explicit per-image progress and partial completion handling. Per-movement writes still wait for successful disk publication or recovery.

Evidence is under `.test-results/host-selective-presets-ranges-20261119`, `.test-results/contracts-selective-presets-20261119` and the four `.test-results/durable-*-20261119/measurement.json` files. To reproduce 10,000 measured edits, run `dotnet run --project tests/state/StateContracts.csproj -c Release -- --measure-durable-edits .test-results/<new-dir>`.

## Native grain, colour delivery and concurrent backups checkpoint

- [x] Display actual Capture One Grain alongside staged controls. Apply and Restore work with the Spektrafilm switch off. The live tests verified native Undo and Redo, a changed outside value, a restart with a restorable receipt and staging preserved by Restore. Clone and pixel-level native/optical grain separation remain open.
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
- [ ] Measure a calibrated sensor chart before Capture One’s developed-image stage. External profile replacement, native exposure/white balance/highlights and same-stage TIFF parity have separate checkpoints below.

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
- [ ] Validate an appearance-versioned larger-support filter and tile margins. Compare energy, resolution, boundaries, full/tiled parity and performance before enabling it.

Evidence is in `.test-results/physical-blur-support-reference-20261120.json` and `.test-results/physical-blur-tent-verified-20261120.json`. Repeat the prototype with `qa/blur-support.py --prototype-tent`.

## Catalog identity and look isolation

- [x] Clone one test database byte for byte into two folders. The native catalog and variant UUIDs, the database basename and RAW source are identical. The plugin uses the full opted-in catalog path to keep distinct look files in one shared test data root.
- [x] Pass 97 assertions across four licensed launches. Both clones restored their own film exposure, drew their own film bitmap and exported a distinct full-resolution RGB48 TIFF. The source catalog and RAW hashes matched.
- [x] Back up the host preferences and remove only the two fixture recent catalog entries. Preserve other settings. Identify session interfaces in metadata for the supported version.
- [ ] Verify actual native sessions and tethered files. Concurrent catalog windows, repeated focus and document teardown have a separate checkpoint below.

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
- [ ] Inject physical Vulkan device loss and repeat on slower hardware. An already submitted full-frame GPU command must finish before graceful acknowledgment. The process watchdog bounds an unresponsive child. [Vulkan fence behavior](https://docs.vulkan.org/refpages/latest/refpages/source/vkWaitForFences.html)

Evidence is under `.test-results/cooperative-vulkan-runtime-20261120`, `.test-results/contracts-cooperative-progress-20261120` and `.test-results/host-cooperative-renderer-20261120`. Use `qa/renderer-cancel-probe.py` to repeat the independent tiled trials.

## Installer linked paths and failed rollback

- [x] Install `0.2.0-audit.11` from clean `d9a66f2` under a private destination. All 152 package and 19 critical runtime files verified. ZIP SHA-256 `97d8700bd0aa9c724023c2700a230d52fffa2b74b939f256380813598184a04b`. Exact-package tests passed 28 GPU, 265 first-launch, 269 restart, 115 RAW and 27 native metadata assertions. The regular installed revision remains `ffc4819`. [Windows Contracts](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44670497699)
- [x] Prevent required asset loads through a linked ancestor in the installer. Verify both staged manifests before promoting the copy. Directory cleanup leaves junction targets alone. [Windows directory deletion behavior](https://learn.microsoft.com/en-us/dotnet/api/system.io.directory.delete?view=netframework-4.8.1)
- [x] Pass 54 inert Windows installer contracts. Case-only collisions, Unicode paths, locked files, unavailable Desktop and real NTFS junctions preserve user files. When an external file occupies the destination after the first directory move, failed rollback retains both directories and reports both exact locations. The fixture manually restores the reported previous version and verifies its data.
- [x] Upgrade an actual isolated audit.11 install to audit.12. Verify both staged manifests, retained Unicode look and archived modified ICC bytes, shortcut and CheckOnly. The exact audit.12 package from clean `4e42336` has SHA-256 `7ef25e54b275250594986c6f7c4bbf150e6e9bc58d0cffc5ba2be2f6bad38d69`. It passed 28 GPU, 265 first-launch, 269 restart, 115 RAW and 27 native metadata assertions. [Windows Contracts](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44671168038)
- [x] Publish a synchronous per-destination recovery receipt before moving the previous install. The next installer verifies both owned paths, restores the previous directory and retains the interrupted candidate for retrieval. A per-destination process lock prevents simultaneous recovery or publication. Ninety-four inert installer assertions cover actual forced child death both before and after promoting the candidate, refused overwrites and unverifiable receipts, bounded rollback, and exact preservation of prior and interrupted user data. Full local Windows contracts and Vulkan smoke passed.
- [x] Keep the previous install and a generation-specific receipt after a successful update. The launcher waits for the exact new process to report Ready with native Export connected. It then rechecks the current generation, previous ownership receipt and every custom file before retiring the prior copy. An unconfirmed or degraded launch keeps it. Inert tests cover stale acknowledgment and a custom file changed after the upgrade.
- [ ] Verify the installed production launcher retires a prior packaged version only after a real licensed launch. Verify externally maintained presets and legacy in-runtime user files across this path.

Evidence is under `.test-results/audit11-package-verify-20261120`, `.test-results/audit12-package-verify-20261120`, `.test-results/contracts-installer-failed-rollback-20261120`, `.test-results/contracts-install-hard-exit-final-20261120` and `.test-results/contracts-installed-ready-final-20261120`.

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
- [ ] Add an equivalent native-only memory baseline, per-stage timings across more sizes and adapters, and controlled multi-context memory contention.

Repeat with `qa/HostSmoke/Run-HostSmoke.ps1 -ViewerChurn 100` using the disposable four-RAW catalog and `-NativeSettingsConfig`. Evidence is under `.test-results/host-viewer-100-visits-current-paint-20261120`.

## Tagged Windows package validation

- [x] Require the full Windows contracts and a physical Vulkan smoke before creating a tagged non-development ZIP. Recheck the source digest after validation. The summary checksum is recorded in the package without test machine paths.
- [x] Test acceptance of a complete result and refusal of missing, failed, skipped, repeated and falsely claimed licensed results. The full local suite passed with 26 manifest assertions.
- [x] Package the exact clean `366e25e` source from an isolated clone tagged locally as `v0.2.0-audit.gate`. Missing local tag and a first fixture run that hit legacy Windows path length both refused publication. The successful short-path clone produced a 152-entry ZIP, required ten contracts and Vulkan smoke, and kept licensed validation listed separately. Archive SHA-256 `9e27be753c4152e82d2c9cc70bacfa305894f6b7383824d38004f79bb82faeb6`.
- [x] Download and inspect both receipts published by Windows Server 2022 and 2025 on draft PR #1. Each receipt records nine passing contracts and explicitly marks GPU and licensed Capture One as separate, unrunnable tiers on those runners. [Public CI run and receipts](https://github.com/nothing-complex/spektrafilm-for-capture-one/actions/runs/44675783266)
- [ ] Observe a genuine outside-fork PR and verify a licensed 16.7 host independently.

Evidence is under `.test-results/contracts-release-required-checks-20261120` and `D:/Library AI testing/sf-tag-gate-20261120/.test-results/tagged-release-proof.json`.

## Concurrent licensed Capture One catalog windows

- [x] Open byte-identical disposable catalog copies in two real native windows. The host File Open helper creates the second form. Enable the native multiple-window preference only within this scoped fixture. Across three focus cycles, the unapproved catalog never displayed or exported the approved look, never received a plugin look directory, and never changed the approved saved file. On return, the same tool and exact exposure were restored. Closing the other catalog left the approved one active. The repeat passed 26 licensed assertions. [Capture One multiple-window setting](https://support.captureone.com/hc/en-us/articles/360002649858-How-do-I-open-more-than-one-Catalog-or-Session-at-a-time)
- [x] Restore only the exact test paths from recent and last-open document lists and the prior native multiple-window setting. The first attempt exposed that Capture One can record one path in both lists. Verified backups were used to restore both fields and preserve unrelated settings. The final repeat exercised both restore paths automatically.
- [x] Close the approved native form while the second stays active. Verify the old panel, film paint, Export bridge and running worker deactivate. Reopen in the same process and verify a new native form, new tool and Export bridge, original variant, identical saved bytes and the visible film look. Thirty-three licensed assertions passed. The first fixture attempt used the wrong internal field name for the new Viewer, which was corrected before the successful repeat.
- [ ] Test session creation and tether arrivals. A compatible installer for a second licensed host is still required to verify 16.7. Capture One distributes older versions through the account download page. [Capture One historical downloads](https://support.captureone.com/hc/en-us/articles/14996282988957-Download-an-Older-Version-of-Capture-One)

Evidence is under `.test-results/host-concurrent-exact-preference-restore-20261120`, `.test-results/host-concurrent-close-reopen-final-20261120`, `.test-results/concurrent-preferences-cleanup-20261120` and `.test-results/concurrent-window-default-recovery-20261120`.

## Live input ICC change handling

- [x] Validate the selected profile during live refresh and include its fingerprint in source reuse. Invalidate cached and in-flight work if it changes. A previously captured native development holds the verified profile read-only and fails if the underlying ICC no longer matches that snapshot.
- [x] Make a byte-verified backup of a disposable ProPhoto profile, corrupt it without changing the selected input space, and observe both the stale-frame invalidation and stale-snapshot rejection. Restore the exact ICC and verify a fresh 16-bit image paints on its own. Detection took about 745 ms on both licensed launches. The complete runs passed 287 first-launch, 291 restart and 115 four-RAW colour assertions.
- [ ] Measure a calibrated sensor chart at earlier native seams and cross-check all derived cache keys. The current source remains Capture One-developed RGB.

Evidence is under `.test-results/host-input-icc-guard-final-20261120` and `.test-results/host-color-matrix-profile-guard-20261120`.

## Status maintenance

`qa/AUDIT-FIX-STATUS.json` owns task IDs, status, implemented substeps, remaining acceptance work and evidence. This handover owns context, runtime identity and resume instructions. `.test-results/FINAL-RESULT.json` is the completed runtime delivery receipt; keep its runtime commit/package identity distinct from later documentation-only commits.

The sibling audit folder retains baseline findings and line references at `d56ebef`. Its `CURRENT-IMPLEMENTATION.json` and task status overlays mirror the current ledger. `sync-implementation-status.py` refreshes those overlays; `assemble-audit.py` must invoke it after regeneration so progress cannot revert to `not_started`. Refresh the audit ZIP/checksum after editing its documents. Preserve historical probes and their original scope.

At each meaningful checkpoint: update only affected task records; name the actual test evidence and skipped cases; mark a milestone complete only after its result is observed; keep unproven acceptance work partial; record the build, package, installed and remote states separately; commit documentation with the associated change. Before final handoff, validate task IDs/counts and JSON, inspect `git diff --check`, and verify the remote state if reporting it. Do not rebuild or reinstall a working package solely to make its runtime revision equal a documentation commit.
