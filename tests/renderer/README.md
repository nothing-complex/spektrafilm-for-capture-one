# Renderer contracts

Run without Capture One, Python, the film data or a GPU:

```powershell
dotnet run --project tests/renderer/RendererContracts.csproj -c Release
```

This links the production managed renderer client and LUT exporter into a fake-worker test executable. Each case gets a disposable fixture below its ignored build-output folder. It checks malformed and truncated responses, incompatible capabilities, process replacement, valid error recovery, source reuse and changed shape, cancellation before/during transport, serialization of simultaneous requests, child-only environment isolation, malformed/mismatched release manifests, LUT options/exclusions, finite sample validation, grid order, and linear-light strength blending. It neither launches nor edits Capture One.

For a built source/runtime folder whose `renderer` subfolder contains the current native executable, shader folder and `.f32` resources:

```powershell
dotnet run --project tests/renderer/RendererContracts.csproj -c Release -- --gpu C:\path\to\Spektrafilm
python ofx-still-renderer/smoke.py --exe C:\path\to\Spektrafilm\renderer\SpektraStillRenderer.exe
```

The GPU contracts call the production managed client. They check negotiated diagnostics, unchanged-request cache reuse, byte-identical production output with an inherited `SPEKTRAFILM_VULKAN_COPY_PASS=1`, and 33/65-point LUT samples against a separate rendering of the same colour-only grid. The smoke suite covers persistent streams, invalid requests, export limits, output transfer functions and spatial/development effects. These synthetic probes do not establish camera/input-profile fidelity or performance on another GPU.

The compiled renderer also exposes `--describe` without rendering a frame. `Build-Windows.ps1` saves that JSON with the runtime and checks its parameter header hash. Both package and build manifests verify the description file. The panel uses it to verify its stock, mode and colour choices and to check Hanatos 2026 calibration.

```powershell
python ofx-still-renderer/smoke.py --parameter-matrix --exe build/still-renderer/Release/SpektraStillRenderer.exe
python ofx-still-renderer/smoke.py --spectral-stock-matrix --exe build/still-renderer/Release/SpektraStillRenderer.exe
```

The parameter matrix covers advertised defaults, both numeric limits, supported choices, invalid input and the Academy Printer Density calibration gate. The spectral matrix checks each bundled film in Print and Scan with the three spectral methods and four input encodings.

The panel negotiates protocol 2 with `HELLO 2`. Its little-endian eight-byte bootstrap header carries status and payload length. The bounded UTF-8 capability payload declares the reply header size, RGBA32F and BGRA8 layouts, byte order, frame limits, packed display availability and the SHA-256 of the parameter header.

Workers that advertise `gpuPlanSchema` support a numbered `PLAN width height [options]` request before image upload. It queries the selected Vulkan device and heap, reports actual driver budget and usage where available, and separates retained bytes from the conservative next-graph estimate. Planning preserves the frame cache and does not advance the render sequence. The managed client reserves that estimate while the frame is in flight. Older workers omit the capability and retain the existing request path.

On RTX 3090, use `--gpu-admission <runtime-root> <result-json> room` or `--gpu-admission <runtime-root> <result-json> pressure` with the managed contract project. The first workload pairs a 6 MP packed Viewer and 24 MP float export. The second uses a 60.22 MP export to exercise waiting under the same heap's budget. Each compares the full packed and float replies against serial renders. The fixture holds export reply delivery briefly to verify overlapping lease ownership.

After negotiation, `REQUEST <number> <verb>` uses positive, increasing unsigned 64-bit request numbers. Both `FRAME` and `REUSE` include width and height. Every reply includes its request number after the status and payload length, for a total of 16 header bytes. Status 1 is a request error with a bounded UTF-8 body. Status 2 is a framing error that closes the worker. A malformed header, mismatched number, truncated body or canceled transfer causes the client to discard the process and its cached source. `METRICS` returns diagnostics schema 1. Both negotiation and metrics can run before Vulkan starts.

Status 5 reports a Vulkan device-loss error with the same bounded UTF-8 body as status 1. The worker clears its render caches and recreates the backend for the next request. The client invalidates its source registration. The Viewer replaces the worker and may retry once while the selected variant and settings still match. Legacy protocol 1 reports the error as status 1. `qa/Build-DeviceLossFixture.ps1` and `qa/device-loss-probe.py` test recovery using a disposable Vulkan build that substitutes the status after an actual fence completes.

The worker continues to accept legacy protocol 1 for standalone tools. The panel and staged build manifest require protocol 2. Packaged launchers verify resource hashes in addition to the parameter-header fingerprint.

The managed worker verifies its installed binary, shaders, data tables, working profiles and stock list before it sends an image. Its SHA-256 appears in the diagnostic log, packaged preset provenance and native LUT comments. The still renderer only loads assets next to its executable. The Vulkan smoke deliberately supplies an alternate resource directory and removes one adjacent shader to exercise that boundary.
