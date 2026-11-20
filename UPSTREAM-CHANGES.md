# Upstream source and changes

## Vulkan renderer

The public source subset in `upstream-ofx` comes from Aedan Diez’s spektrafilm OFX commit `86476afc5b077de77e2278e3658d1ba9309892a1`. On 2026-11-14 Spektrafilm for Capture One modified `src/SpektraParameters.h` to let a caller identify an immutable input frame and `src/SpektraVulkanRenderer.cpp` to reuse that input on the GPU across render requests. The Vulkan renderer invalidates the remembered upload when the input, layout, or backing buffer changes. The same patch covers full-frame and tiled uploads and avoids a redundant copy for auto-exposure metering when it can read the original float buffer directly.

The still-image bridge can also install an optional cancellation probe on the Vulkan renderer. The tiled path checks it between tiles and between halation-milestone tiles, after the previous GPU work is safe to leave. A submitted GPU operation remains owned by its worker until it completes or the Windows parent shuts down that worker.

The corresponding source includes those modified files, the public stock and spectral data, shaders, and the tools that generate the C++ curves and runtime lookup tables. The subset omits upstream OFX distribution branding and PDF documentation, research output, and obsolete archived source. To rebuild Spektrafilm for Capture One, run `Build-Windows.ps1`, which compiles the `SpektraVulkanCopyHarness` target. The upstream OFX packaging targets require the upstream release assets.

## Python reference

The source subset in `upstream-spektrafilm` comes from Andrea Volpato’s spektrafilm commit `3bb2c2d2801ff68b92019cf1dbcbb133d60832bc`. Spektrafilm for Capture One uses the source and film profiles from that commit. The copy omits repository examples, screenshots, tests outside the source packages, and the optional third-party ICC binaries used by the upstream image exporter. The source subset includes the original notices and citation records.

## Building Spektrafilm for Capture One

`Build-Windows.ps1` builds the shader and spectral resources, then links the local `ofx-still-renderer` wrapper against the vendored Vulkan renderer. Pass `-BuildPanel` to build the managed panel against the installed Capture One assemblies and `-StageRuntime` to copy the runtime files into the launcher layout. The optional `lut_worker.py` runs the Python reference directly from the vendored source.
