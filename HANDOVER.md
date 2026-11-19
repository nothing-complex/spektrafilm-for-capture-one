# Spektrafilm for Capture One development handover

Updated 2026-11-19. The critical-fix batch is implemented, tested, installed locally and published in a draft pull request. The audit follow-ups below passed local validation. The full 70-item integration audit tracks 16 bounded primary fixes implemented, 39 partial items and 15 pending items. It includes feasibility investigations and enhancements.

## Resume here

1. Read this file, `AGENTS.md` and `qa/AUDIT-FIX-STATUS.json` before changing code. The JSON is the authoritative task-progress ledger; its `remaining_work` and original acceptance tests matter more than a short status label.
2. Check `git status`, current branch and PR head. Do not discard existing edits or switch the installed build merely because this documentation has a newer commit.
3. The installed and packaged **runtime revision is `ffc481988eb2e6e7ef7a1379486c4a8b2656ac81`**. The initial product changes are in `285f0aa`. The current source also includes the tested 2026-11-19 export follow-up. Its validation used a disposable catalog and a separate panel build.
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

## Status maintenance

`qa/AUDIT-FIX-STATUS.json` owns task IDs, status, implemented substeps, remaining acceptance work and evidence. This handover owns context, runtime identity and resume instructions. `.test-results/FINAL-RESULT.json` is the completed runtime delivery receipt; keep its runtime commit/package identity distinct from later documentation-only commits.

The sibling audit folder retains baseline findings and line references at `d56ebef`. Its `CURRENT-IMPLEMENTATION.json` and task status overlays mirror the current ledger. `sync-implementation-status.py` refreshes those overlays; `assemble-audit.py` must invoke it after regeneration so progress cannot revert to `not_started`. Refresh the audit ZIP/checksum after editing its documents. Preserve historical probes and their original scope.

At each meaningful checkpoint: update only affected task records; name the actual test evidence and skipped cases; mark a milestone complete only after its result is observed; keep unproven acceptance work partial; record the build, package, installed and remote states separately; commit documentation with the associated change. Before final handoff, validate task IDs/counts and JSON, inspect `git diff --check`, and verify the remote state if reporting it. Do not rebuild or reinstall a working package solely to make its runtime revision equal a documentation commit.
