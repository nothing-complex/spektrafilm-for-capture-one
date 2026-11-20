"""Exercise the Vulkan binary frame bridge without opening Capture One."""

import argparse
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import time
from tempfile import TemporaryDirectory

import numpy as np


def source_frame(path):
    if path:
        from PIL import Image, ImageOps

        with Image.open(path) as image:
            image = ImageOps.exif_transpose(image).convert("RGB")
            image.thumbnail((720, 720))
            rgb = np.asarray(image, dtype=np.float32) / 255
    else:
        y, x = np.mgrid[0:1:256j, 0:1:384j].astype(np.float32)
        checker = (np.floor(x * 12) + np.floor(y * 8)) % 2
        rgb = np.stack((x, y, 0.2 + 0.6 * checker), axis=-1).astype(np.float32)
    return np.concatenate((rgb, np.ones((*rgb.shape[:2], 1), np.float32)), axis=-1)


def render(exe, frame, options):
    height, width = frame.shape[:2]
    start = time.monotonic()
    result = subprocess.run(
        [str(exe), str(width), str(height), *options.split()],
        input=frame.astype("<f4", copy=False).tobytes(),
        capture_output=True,
        timeout=20,
    )
    if result.returncode:
        raise RuntimeError(result.stderr.decode("utf-8", errors="replace"))
    if len(result.stdout) != frame.nbytes:
        raise AssertionError("Unexpected output byte count")
    pixels = np.frombuffer(result.stdout, dtype="<f4").reshape(frame.shape)
    if not np.isfinite(pixels).all():
        raise AssertionError("Non-finite output")
    np.testing.assert_array_equal(pixels[..., 3], frame[..., 3])
    return pixels, time.monotonic() - start


def reference_crop(frame, scale=1.2, x_shift=0.04, y_shift=-0.03):
    height, width = frame.shape[:2]
    x = (width / 2) + (np.arange(width) + 0.5 - width / 2 + width * x_shift) / scale - 0.5
    y = (height / 2) + (np.arange(height) + 0.5 - height / 2 + height * y_shift) / scale - 0.5
    x0, y0 = np.floor(x).astype(int), np.floor(y).astype(int)
    tx = (x - x0)[None, :, None]
    ty = (y - y0)[:, None, None]
    top = (1 - tx) * frame[y0[:, None], x0] + tx * frame[y0[:, None], x0 + 1]
    bottom = (1 - tx) * frame[y0[:, None] + 1, x0] + tx * frame[y0[:, None] + 1, x0 + 1]
    return ((1 - ty) * top + ty * bottom).astype(np.float32)


def frame_request(frame, options):
    height, width = frame.shape[:2]
    return f"FRAME {width} {height} {options}\n".encode("ascii") + frame.astype("<f4", copy=False).tobytes()


def run_stream(exe, request, exit_code=0, mode="--stream", env=None, timeout=20):
    result = subprocess.run([str(exe), mode], input=request, capture_output=True, timeout=timeout, env=env)
    if result.returncode != exit_code:
        raise AssertionError(f"Expected renderer exit {exit_code}, got {result.returncode}: {result.stderr!r}")
    offset = 0
    replies = []
    while offset < len(result.stdout):
        if len(result.stdout) - offset < 8:
            raise AssertionError("Truncated reply header")
        status, length = struct.unpack_from("<II", result.stdout, offset)
        offset += 8
        if status not in (0, 1) or length > len(result.stdout) - offset:
            raise AssertionError(f"Invalid reply header: status {status}, length {length}")
        if status == 1 and not 1 <= length <= 4096:
            raise AssertionError(f"Invalid error length: {length}")
        replies.append((status, result.stdout[offset:offset + length]))
        offset += length
    return replies


def check_stream(exe, frame, base):
    small = frame[::8, ::8].copy()
    small[..., 3] = np.linspace(0, 1, small.shape[1], dtype=np.float32)
    different = small * np.float32(0.65)
    nonfinite = different.copy()
    nonfinite.flat[1] = np.nan
    baseline, _ = render(exe, small, base)
    alternate, _ = render(exe, different, base)
    reuse = f"REUSE {base}\n".encode("ascii")
    max_length_line = reuse[:-1].ljust(16384, b" ") + b"\n"

    replies = run_stream(exe, b"".join((
        reuse,
        frame_request(small, base),
        max_length_line,
        b"REUSE --output-color\n",
        frame_request(different, "--unknown-option 1"),
        frame_request(nonfinite, base),
        reuse,
        frame_request(different, base),
        reuse,
        b"QUIT invalid\n",
        b"QUIT\n",
    )))
    expected_errors = {
        0: b"Send a FRAME",
        3: b"Each setting needs a value",
        4: b"Unknown setting",
        5: b"Invalid input pixel",
        9: b"QUIT does not take arguments",
    }
    expected_frames = {1: baseline, 2: baseline, 6: baseline, 7: alternate, 8: alternate}
    assert len(replies) == 11, replies
    for index, error in expected_errors.items():
        status, payload = replies[index]
        assert status == 1 and error in payload, (index, status, payload)
    for index, expected in expected_frames.items():
        status, payload = replies[index]
        assert status == 0 and len(payload) == small.nbytes, (index, status, len(payload))
        pixels = np.frombuffer(payload, dtype="<f4").reshape(small.shape)
        np.testing.assert_array_equal(pixels, expected)
    assert replies[-1] == (0, b"")
    assert run_stream(exe, b"") == []
    assert run_stream(exe, frame_request(small, base)) == [(0, replies[1][1])]

    for request, error in (
        (b"FRAME 2048 2048\n", b"up to 1,048,576 pixels"),
        (b"FRAME 1 1\nshort", b"wrong byte length"),
        (b"FRAME 1 1 --bad 1\nshort", b"wrong byte length"),
        (b"REUSE\0\n", b"ASCII text"),
        (max_length_line[:-1] + b" \n", b"exceeds 16 KB"),
        (b"REUSE", b"before its line break"),
    ):
        failure = run_stream(exe, request, exit_code=1)
        assert len(failure) == 1 and failure[0][0] == 1 and error in failure[0][1], failure
    print("Stream validated persistent rendering, bounded input, error recovery, QUIT, and EOF")


def check_numbered_stream(exe, frame, base):
    def numbered(commands, exit_code=0):
        result = subprocess.run([str(exe), "--native-preview-stream"], input=b"HELLO 2\n" + commands,
                                capture_output=True, timeout=20)
        assert result.returncode == exit_code, result.stderr
        status, length = struct.unpack_from("<II", result.stdout)
        capabilities = json.loads(result.stdout[8:8 + length])
        assert status == 0 and capabilities["protocol"] == 2
        assert capabilities["requestHeaderBytes"] == 16 and capabilities["floatLayout"] == "rgba32f"
        assert capabilities["packedDisplay"] and capabilities["packedLayout"] == "bgra8"
        replies = []
        offset = 8 + length
        while offset < len(result.stdout):
            status, length, request = struct.unpack_from("<IIQ", result.stdout, offset)
            offset += 16
            assert length <= len(result.stdout) - offset
            replies.append((request, status, result.stdout[offset:offset + length]))
            offset += length
        return replies

    small = frame[np.arange(12)[:, None] % frame.shape[0], np.arange(13) % frame.shape[1]]
    upload = b"REQUEST 7 " + frame_request(small, base)
    same_frame = f"REQUEST 10 REUSE 13 12 {base}\n".encode("ascii")
    replies = numbered(upload + b"REQUEST 8 REUSE 12 13\n" + same_frame
                       + f"REQUEST 11 REUSE8 13 12 {base}\n".encode("ascii")
                       + b"REQUEST 12 METRICS\nREQUEST 13 QUIT\n")
    assert [(request, status) for request, status, _ in replies] == [(7, 0), (8, 1), (10, 0), (11, 0), (12, 0), (13, 0)]
    assert b"dimensions do not match" in replies[1][2] and replies[0][2] == replies[2][2]
    assert len(replies[0][2]) == small.nbytes and len(replies[3][2]) == small.nbytes // 4
    metrics = json.loads(replies[4][2])
    assert metrics["renderSequence"] == 1 and (metrics["width"], metrics["height"]) == (13, 12)
    assert replies[5][2] == b""
    for command, request, error in (
        (b"REQUEST 7 METRICS\n", 0, b"repeated renderer request"),
        (b"METRICS\n", 0, b"Expected a numbered"),
        (b"REQUEST 18446744073709551616 METRICS\n", 0, b"Invalid or repeated"),
        (b"REQUEST 8 FRAME 1 1\nshort", 8, b"wrong byte length"),
        (b"REQUEST 8 FRAME 16385 1\n", 8, b"Invalid integer"),
        (b"REQUEST 8 " + b" " * 16384 + b"\n", 0, b"exceeds 16 KB"),
    ):
        frames = numbered(upload + command, exit_code=1)
        assert len(frames) == 2 and frames[0] == replies[0]
        assert frames[1][:2] == (request, 2) and error in frames[1][2], frames[1]
    print("Protocol 2 validated request identity, exact reuse layout, packed output and fatal framing errors")


def check_resource_root(exe, options):
    with TemporaryDirectory(prefix="spektrafilm-resource-probe-") as folder:
        root = Path(folder)
        renderer = root / "renderer"
        renderer.mkdir()
        source = exe.resolve().parent
        binary = renderer / exe.name
        shutil.copyfile(exe, binary)
        shutil.copytree(source / "shaders", renderer / "shaders")
        for filename in ("SpektraHanatos2025Spectra.f32", "SpektraOutputGamutCompression.f32"):
            shutil.copyfile(source / filename, renderer / filename)
        relative = Path("shaders/SpektraFilmExposure.comp.spv")
        fallback = root / "Resources" / relative
        fallback.parent.mkdir(parents=True)
        fallback.write_bytes(b"not a shader")
        args = [str(binary), "1", "1", *options.split()]
        pixel = struct.pack("<4f", .2, .4, .6, 1)
        env = {key: value for key, value in os.environ.items() if not key.upper().startswith("SPEKTRAFILM_")}
        def run(environment):
            return subprocess.run(args, input=pixel, capture_output=True, timeout=20, env=environment)
        expected = run(env)
        assert expected.returncode == 0 and len(expected.stdout) == len(pixel), expected.stderr
        env["SPEKTRAFILM_RESOURCE_DIR"] = str(root / "Resources")
        shadowed = run(env)
        assert shadowed.returncode == 0 and shadowed.stdout == expected.stdout, shadowed.stderr
        adjacent = renderer / relative
        adjacent.replace(fallback)
        missing = run(env)
        assert missing.returncode and not missing.stdout and b"SpektraFilmExposure" in missing.stderr, missing.stderr
    print("Standalone Vulkan uses adjacent assets and refuses a missing shader despite an alternate copy")


def check_export_stream(exe, frame, base):
    small_request = frame_request(frame[::8, ::8].copy(), base)
    assert run_stream(exe, small_request, mode="--export-stream") == run_stream(exe, small_request)

    large = np.tile(frame, (1 + 1025 // frame.shape[0], 1 + 1025 // frame.shape[1], 1))[:1025, :1025].copy()
    request = frame_request(large, base)
    failure = run_stream(exe, b"FRAME 1025 1025\n", exit_code=1)
    assert len(failure) == 1 and failure[0][0] == 1 and b"up to 1,048,576 pixels" in failure[0][1]
    replies = run_stream(exe, request, mode="--export-stream")
    assert len(replies) == 1 and replies[0][0] == 0 and len(replies[0][1]) == large.nbytes
    exported = np.frombuffer(replies[0][1], dtype="<f4").reshape(large.shape)
    assert np.isfinite(exported).all()
    assert np.mean(np.abs(exported[..., :3] - large[..., :3])) > 0.0001
    np.testing.assert_array_equal(exported[..., 3], large[..., 3])

    for request, error in (
        (b"FRAME 16385 1\n", b"Invalid integer"),
        (b"FRAME 10001 10000\n", b"up to 100,000,000 pixels"),
    ):
        failure = run_stream(exe, request, exit_code=1, mode="--export-stream")
        assert len(failure) == 1 and failure[0][0] == 1 and error in failure[0][1]
    (status, capabilities), = run_stream(exe, b"HELLO 1\n", mode="--export-stream")
    contract = json.loads(capabilities)
    assert status == 0 and contract["maxSide"] == 16384 and contract["maxPixels"] == 100_000_000
    print("Export stream accepted a frame above the preview limit and preserved preview color parity")


def check_enlarger_cache(exe, frame, base):
    height, width = 87, 131
    source = frame[np.arange(height)[:, None] % frame.shape[0], np.arange(width) % frame.shape[1]]
    source[..., 3] = np.linspace(.1, .9, width, dtype=np.float32)
    second = source * np.float32(.65)
    optics = f"{base} --enlarger-scale 1.25 --enlarger-x 3 --enlarger-y -5"
    brighter = f"{optics} --print-exposure 0.4"
    border = f"{base} --enlarger-scale 1.25 --enlarger-x 100"
    maximum = f"{base} --enlarger-scale 32 --enlarger-x 3 --enlarger-y -5"
    def reuse(options):
        return f"REUSE {options}\n".encode("ascii")

    requests = (
        (source, optics, frame_request(source, optics), False, True),
        (source, brighter, reuse(brighter), True, True),
        (source, border, reuse(border), False, True),
        (source, maximum, reuse(maximum), False, True),
        (source, base, reuse(base), False, True),
        (source, maximum, reuse(maximum), True, True),
        (source, optics, reuse(optics), False, True),
        (second, optics, frame_request(second, optics), False, True),
        (second, brighter, reuse(brighter), True, True),
        (second, base, reuse(base), False, True),
        (source, base, frame_request(source, base), False, False),
        (source, optics, reuse(optics), False, True),
    )
    replies = run_stream(exe, b"".join(c + b"METRICS\n" for _, _, c, _, _ in requests), mode="--native-preview-stream")
    assert len(replies) == 2 * len(requests) and all(status == 0 for status, _ in replies)
    previous = None
    previous_options = None
    for index, (original, options, _, hit, retained) in enumerate(requests):
        payload = replies[index * 2][1]
        fresh, = run_stream(exe, frame_request(original, options), mode="--export-stream")
        assert fresh == (0, payload), ("cached versus fresh bit parity", index)
        metrics = json.loads(replies[index * 2 + 1][1])
        assert metrics["enlargerCacheHit"] == hit
        assert metrics["enlargerCachedBytes"] == (source.nbytes if retained else 0)
        alternate = metrics["alternateSourceCachedBytes"]
        assert not alternate or alternate >= source.nbytes, metrics
        if not retained:
            assert alternate == 0, metrics
        if hit:
            expected_upload = 0
            if previous_options == base and not alternate:
                expected_upload = source.nbytes
            assert metrics["enlargerMs"] == 0 and metrics["uploadBytes"] == expected_upload and payload != previous, metrics
            if previous_options == base and metrics["dynamicGpuBudget"] and metrics["gpuMemoryBudgetBytes"] >= 256 * 1024 * 1024:
                assert alternate >= source.nbytes, metrics
        elif options != base:
            assert metrics["enlargerMs"] > 0 and metrics["uploadBytes"] >= source.nbytes
        if options == border:
            rgba = np.frombuffer(payload, dtype="<f4").reshape(source.shape)
            assert rgba[0, -1, 3] == 1 and 0 < rgba[height // 2, 0, 3] < 1
        previous = payload
        previous_options = options
    print("Enlarger cache matched twelve fresh renders, retained a small crop through identity and cleared it for a new source")


def check_output_transfers(exe, frame, base):
    small = frame[::8, ::8].copy()
    small[..., 3] = np.linspace(0, 1, small.shape[1], dtype=np.float32)
    transfers = ("srgb", "rec709_gamma24", "linear_rec709")
    commands = "".join(f"REUSE {base} --output-color {name}\n" for name in transfers)
    commands += f"REUSE {base} --output-color adobe_rgb\nREUSE {base}\n"
    request = frame_request(small, base) + commands.encode("ascii")
    replies = run_stream(exe, request)
    assert len(replies) == 6 and all(status == 0 for status, _ in replies[:4]), replies
    assert replies[4][0] == 1 and b"Unknown option value" in replies[4][1]
    assert replies[5] == replies[0]
    default, srgb, gamma24, linear = (
        np.frombuffer(payload, dtype="<f4").reshape(small.shape) for _, payload in replies[:4]
    )
    np.testing.assert_array_equal(default, srgb)
    for name, output in zip(transfers, (srgb, gamma24, linear)):
        standalone, _ = render(exe, small, f"{base} --output-color {name}")
        np.testing.assert_array_equal(output, standalone)
        np.testing.assert_array_equal(output[..., 3], small[..., 3])

    srgb_rgb, gamma24_rgb, linear_rgb = (output[..., :3] for output in (srgb, gamma24, linear))
    magnitude = np.abs(srgb_rgb)
    srgb_decoded = np.copysign(
        np.where(magnitude <= 0.04045, magnitude / 12.92, ((magnitude + 0.055) / 1.055) ** 2.4), srgb_rgb
    )
    gamma24_decoded = np.copysign(np.abs(gamma24_rgb) ** 2.4, gamma24_rgb)
    assert np.max(np.abs(srgb_rgb - gamma24_rgb)) > 0.005
    np.testing.assert_allclose(gamma24_decoded, linear_rgb, rtol=0, atol=3e-6)
    # Upstream generates separate, rounded sRGB and BT.709 matrices for nominally the same primaries.
    np.testing.assert_allclose(srgb_decoded, linear_rgb, rtol=0, atol=2e-4)
    print("Verified sRGB, Rec.709 gamma 2.4, and linear Rec.709 output against their decoded color tolerances")


def check_packed_readback(exe, base):
    clean = {key: value for key, value in os.environ.items() if not key.upper().startswith("SPEKTRAFILM_")}
    height, width = 533, 779  # Odd sides, multiple work groups, and four genuine tiles.
    rng = np.random.default_rng(12471)
    frame = rng.uniform(-0.05, 1.25, (height, width, 4)).astype("<f4")
    alpha_boundaries = ((np.arange(255) + .5) / 255).astype("<f4")
    alpha = np.concatenate(([-.2, 0, 1, 1.3], alpha_boundaries,
                            np.nextafter(alpha_boundaries, np.float32(0)),
                            np.nextafter(alpha_boundaries, np.float32(1)))).astype("<f4")
    frame[..., 3] = np.resize(alpha, (height, width))
    cases = (
        ("sRGB full", "legacy", "", False),
        ("Adobe full", "legacy", "--input-color adobe_rgb --auto-exposure true", False),
        ("ProPhoto tiled", "tiled", "--input-color prophoto", False),
        ("linear Rec.2020 tiled", "tiled", "--input-color linear_rec2020 --scanner-enabled true", False),
        ("enlarger preserves original alpha", "legacy", "--enlarger-scale 1.4 --enlarger-x -4", True),
    )
    for name, tiling, extra, expect_cpu in cases:
        env = {**clean, "SPEKTRAFILM_VULKAN_TILE_MODE": tiling, "SPEKTRAFILM_VULKAN_TILE_WIDTH": "512",
               "SPEKTRAFILM_VULKAN_TILE_HEIGHT": "512"}
        options = f"{base} --output-color srgb {extra}"
        request = b"FRAME8" + frame_request(frame, options)[len(b"FRAME"):]
        request += f"METRICS\nREUSE8 {options}\nREUSE {options}\nMETRICS\nREUSE8 {options}\n".encode("ascii")
        replies = run_stream(exe, request, mode="--native-preview-stream", env=env, timeout=35)
        assert len(replies) == 6 and all(status == 0 for status, _ in replies), name
        packed, packed_metrics, reused, float_bytes, float_metrics, packed_from_float = (data for _, data in replies)
        rgba = np.frombuffer(float_bytes, dtype="<f4").reshape(frame.shape)
        expected = np.empty_like(frame, dtype=np.uint8)
        expected[..., :3] = np.rint(np.clip(rgba[..., 2::-1], 0, 1).astype(np.float64) * 255).astype(np.uint8)
        expected[..., 3] = np.rint(np.clip(frame[..., 3], 0, 1) * np.float32(255)).astype(np.uint8)
        np.testing.assert_array_equal(np.frombuffer(packed, dtype=np.uint8).reshape(frame.shape), expected, err_msg=name)
        assert packed == reused == packed_from_float, name
        render_metrics, reference_metrics = json.loads(packed_metrics), json.loads(float_metrics)
        assert render_metrics["tiled"] == reference_metrics["tiled"] == (tiling == "tiled"), name
        if tiling == "tiled":
            assert render_metrics["tileCount"] > 1
        assert reference_metrics["readbackBytes"] == frame.nbytes, name
        expected_gpu_bytes = height * width * 4 + 4 * max(1, render_metrics["tileCount"])
        assert render_metrics["readbackBytes"] == (frame.nbytes if expect_cpu else expected_gpu_bytes), name
        assert render_metrics["renderSequence"] == 1 and reference_metrics["renderSequence"] == (1 if expect_cpu else 2), name
    print("GPU BGRA8 matched the scalar reference over five full/tiled/crop cases, with exact readback size, original alpha and float reuse")


def check_metering_cache(exe, frame, base):
    height, width = 333, 517
    image = frame[np.arange(height)[:, None] % frame.shape[0], np.arange(width) % frame.shape[1]]
    second = image.copy()
    second[..., :3] *= .7
    options = f"{base} --auto-exposure true --auto-exposure-meter center_weighted"
    adobe = f"{options} --input-color adobe_rgb"
    median = f"{adobe} --auto-exposure-meter median"
    for tiling in ("legacy", "tiled"):
        environment = {key: value for key, value in os.environ.items() if not key.upper().startswith("SPEKTRAFILM_")}
        environment["SPEKTRAFILM_VULKAN_TILE_MODE"] = tiling
        cases = (
            (image, options, True, False),
            (image, f"{options} --print-exposure .3", False, True),
            (image, adobe, False, False),
            (image, median, False, False),
            (image, f"{median} --stock fujifilm_provia_100f", False, True),
            (second, median, True, False),
            (second, f"{median} --auto-exposure false", False, False),
            (second, f"{median} --film-exposure .2", False, True),
        )
        request = b"".join(
            (frame_request(source, args) if replace else f"REUSE {args}\n".encode("ascii")) + b"METRICS\n"
            for source, args, replace, _ in cases
        )
        replied = run_stream(exe, request, mode="--native-preview-stream", env=environment)
        assert len(replied) == len(cases) * 2
        for index, (source, args, _, hit) in enumerate(cases):
            fresh, = run_stream(exe, frame_request(source, args), mode="--export-stream", env=environment)
            assert fresh[0] == 0 and fresh == replied[index * 2], (tiling, index, args)
            metrics = json.loads(replied[index * 2 + 1][1])
            assert metrics["tiled"] == (tiling == "tiled")
            assert metrics["meteringCacheHit"] == hit, (tiling, index, metrics)
            ran = not hit and not args.endswith("--auto-exposure false")
            assert (metrics["meteringMs"] > 0) == ran, (tiling, index, metrics)
    print("Metering cache matched sixteen fresh full-frame and tiled renders, including source, input colour and method changes")


def check_tiled_parity(exe, base):
    """Compare genuine multi-tile renders on a signal that crosses tile boundaries."""
    height, width = 769, 1027
    y, x = np.mgrid[0:1:height * 1j, 0:1:width * 1j].astype(np.float32)
    checker = (np.floor(x * 7) + np.floor(y * 9)) % 2
    chart = np.stack((.1 + .7 * x, .07 + .8 * y, .1 + .5 * checker, .15 + .85 * x), axis=-1)
    chart[240:285, 475:530, :3] = 1.5  # Highlight crosses both default tile boundaries.
    # A long, thin image makes both physical scanner kernels wide without
    # requiring a full-size image. The hard RGB and alpha edges cross tiles.
    y, x = np.mgrid[:33, :4097]
    scanner_chart = np.stack((
        np.where((x % 511) < 230, .18, .87),
        np.where(((x + 112) % 727) < 340, .12, .83),
        np.where(((x + y * 2) % 321) < 115, .07, .70),
        np.where((x % 329) < 198, .1, 1.0),
    ), axis=-1).astype(np.float32)
    # The long Super 8 edge exercises the composed scatter and bounce support
    # at large-image sampling while the narrow dimension keeps the probe small.
    y, x = np.mgrid[:31, :12501]
    halation_chart = np.stack((
        np.where(x % 719 < 290, .05, .85),
        np.where((x + y) % 611 < 260, .09, .91),
        np.where(x % 513 < 142, .06, .75),
        .15 + .82 * x / 12501,
    ), axis=-1).astype(np.float32)
    cases = {
        "color": "--grain-model off",
        "8 mm halation/scatter": "--grain-model off --film-format 8 --halation-amount 1 --scatter-amount 0.5",
        "35 mm diffusion": "--grain-model off --film-format 35 --camera-diffusion-family pro_mist "
                           "--camera-diffusion-strength 1.5 --print-diffusion-family black_pro_mist "
                           "--print-diffusion-strength 1.5",
        "70 mm DIR": "--grain-model off --film-format 70 --dir-amount 1.5 --dir-diffusion 80 --dir-tail-weight 0.3",
        "metering/grain/scanner": "--grain-model production --film-format 35 --auto-exposure true --scanner-enabled true",
        "stacked scanner kernels": "--grain-model off --film-format 8 --scanner-enabled true --scanner-mtf 1 "
                                   "--scanner-unsharp-radius 100 --scanner-unsharp-amount 2 --scanner-glare .08 "
                                   "--scanner-glare-roughness 1 --scanner-glare-blur 32",
        "enlarger": "--grain-model off --enlarger-scale 1.3 --enlarger-x 3 --enlarger-y -2",
    }
    clean = {key: value for key, value in os.environ.items()
             if not key.upper().startswith("SPEKTRAFILM_") and key.upper() != "DOTNET_STARTUP_HOOKS"}
    graphs = [(label, options, scanner_chart if label == "stacked scanner kernels" else chart)
              for label, options in cases.items()]
    graphs.append(("stacked scanner kernels, full-width tiles", cases["stacked scanner kernels"],
                   np.ascontiguousarray(scanner_chart.transpose(1, 0, 2))))
    graphs.append(("long Super 8 scatter and bounce", cases["8 mm halation/scatter"], halation_chart))
    for label, options, frame in graphs:
        pictures = []
        for mode in ("legacy", "tiled"):
            environment = {**clean, "SPEKTRAFILM_VULKAN_TILE_MODE": mode}
            request = frame_request(frame, f"{base} {options}") + b"METRICS\nQUIT\n"
            replies = run_stream(exe, request, mode="--export-stream", env=environment)
            assert len(replies) == 3 and all(status == 0 for status, _ in replies), label
            assert len(replies[0][1]) == frame.nbytes
            picture = np.frombuffer(replies[0][1], dtype="<f4").reshape(frame.shape)
            assert np.isfinite(picture).all(), label
            alpha = reference_crop(frame, 1.3, .03, -.02)[..., 3] if label == "enlarger" else frame[..., 3]
            np.testing.assert_allclose(picture[..., 3], alpha, rtol=0, atol=4e-7, err_msg=f"Alpha: {label}")
            if label.startswith("stacked scanner kernels"):
                np.testing.assert_array_equal(picture[..., 3], alpha)
            metrics = json.loads(replies[1][1])
            assert metrics["tiled"] == (mode == "tiled"), (label, mode, metrics)
            assert metrics["readbackBytes"] == frame.nbytes, (label, mode, metrics)
            if mode == "tiled":
                assert metrics["tileCount"] > 1, (label, metrics)
            pictures.append(picture)
        np.testing.assert_allclose(pictures[0], pictures[1], rtol=0, atol=2e-6, err_msg=f"Tile parity: {label}")
        print(f"Tile parity {label}: max code difference {np.max(np.abs(pictures[0] - pictures[1])):.8g}, "
              f"{frame.nbytes} readback bytes")


def check_optical_scratch_lifetime(exe, base):
    frame = source_frame(None)[::2, ::2].copy()
    base += " --grain-model off --film-format 35"
    halation = "--halation-amount 1 --halation-boost 1 --scatter-amount 0.5"
    no_halation = "--halation-amount 0 --scatter-amount 0"
    camera = "--camera-diffusion-family pro_mist --camera-diffusion-strength 1.5"
    print_diffusion = "--print-diffusion-family black_pro_mist --print-diffusion-strength 1.5"
    dir_blur = "--dir-amount 1.5 --dir-diffusion 80 --dir-tail-weight 0.3"
    scanner = "--scanner-enabled true --scanner-glare 0.08 --scanner-unsharp-radius 100 --scanner-unsharp-amount 2"
    graphs = [f"{base} {settings}" for settings in (
        f"{halation} {camera} {dir_blur} {print_diffusion} {scanner} --preflash 0.3",
        f"{no_halation} {camera}",
        f"{no_halation} {dir_blur} {scanner}",
        halation,
        no_halation,
        f"{no_halation} {print_diffusion}",
    )]
    env = {key: value for key, value in os.environ.items()
           if not key.upper().startswith("SPEKTRAFILM_") and key.upper() != "DOTNET_STARTUP_HOOKS"}
    env.update(SPEKTRAFILM_VULKAN_TILE_MODE="tiled", SPEKTRAFILM_VULKAN_TILE_WIDTH="128",
               SPEKTRAFILM_VULKAN_TILE_HEIGHT="64")
    fresh = [run_stream(exe, frame_request(frame, graph), mode="--export-stream", env=env)[0] for graph in graphs]
    assert all(status == 0 and len(output) == frame.nbytes for status, output in fresh)
    assert len({output for _, output in fresh}) == len(graphs), "Each optical fixture must change the image"
    sequence = (0, 1, 5, 3, 2, 4, 5, 1, 2, 0)
    replacement = frame.copy()
    replacement[..., :3] *= .7
    replacement_fresh, = run_stream(exe, frame_request(replacement, graphs[0]), mode="--export-stream", env=env)
    assert replacement_fresh[0] == 0 and replacement_fresh != fresh[0]
    edited = f"{graphs[0]} --film-exposure 0.2"
    edited_fresh, = run_stream(exe, frame_request(replacement, edited), mode="--export-stream", env=env)
    assert edited_fresh[0] == 0 and edited_fresh != replacement_fresh
    request = frame_request(frame, graphs[sequence[0]]) + b"METRICS\n"
    request += b"".join(f"REUSE {graphs[index]}\nMETRICS\n".encode("ascii") for index in sequence[1:])
    request += frame_request(replacement, graphs[0]) + f"METRICS\nREUSE {edited}\nMETRICS\n".encode("ascii")
    reused = run_stream(exe, request, mode="--native-preview-stream", env=env)
    assert reused[::2] == [fresh[index] for index in sequence] + [replacement_fresh, edited_fresh], \
        "Changing optical passes or replacing the source reused stale pixels"
    previous_transient = 0
    for index, (status, payload) in enumerate(reused[1::2]):
        metrics = json.loads(payload)
        assert status == 0 and metrics["tiled"] and metrics["renderSequence"] == index + 1, (index, metrics)
        reclaimable = metrics["reclaimableDeviceHeapBytes"]
        assert 0 <= reclaimable <= previous_transient and (metrics["dynamicGpuBudget"] or reclaimable == 0), metrics
        previous_transient = metrics["transientCachedBytes"]
        if index in (0, len(sequence)):
            assert metrics["uploadBytes"] >= frame.nbytes, metrics
        else:
            assert metrics["uploadBytes"] < frame.nbytes, metrics
    print("Warmed optical graphs match fresh images, with one upload per source even with tiled halation boost")


def check_gpu_budget(exe, base):
    environment = {key: value for key, value in os.environ.items()
                   if not key.upper().startswith("SPEKTRAFILM_") and key.upper() != "DOTNET_STARTUP_HOOKS"}
    pressure = {**environment, "SPEKTRAFILM_VULKAN_TRANSIENT_BUDGET_MB": "128"}
    frame = np.tile(source_frame(None), (4, 3, 1))[:769, :1027].copy()
    large = np.tile(source_frame(None), (5, 5, 1))[:1200, :1800].copy()
    options = f"{base} --grain-model off"
    request = frame_request(frame, options) + b"METRICS\n"
    reference = run_stream(exe, request, mode="--export-stream", env=environment)
    assert all(status == 0 for status, _ in reference)
    original = np.frombuffer(reference[0][1], dtype="<f4")

    request += (
        frame_request(frame, f"{base} --grain-model synthesis --grain-synthesis-samples 2")
        + frame_request(large, options)
        + f"REUSE {options}\nMETRICS\nQUIT\n".encode("ascii")
    )
    replies = run_stream(exe, request, mode="--export-stream", env=pressure)
    assert [status for status, _ in replies] == [0, 0, 1, 1, 0, 0, 0], [status for status, _ in replies]
    metrics = json.loads(replies[1][1])
    assert metrics["tiled"] and metrics["tileCount"] > 1 and metrics["budgetSelectedTiling"]
    assert metrics["estimatedFullFrameGpuBytes"] > metrics["gpuMemoryBudgetBytes"] > 0
    assert b"Grain Synthesis needs more GPU memory" in replies[2][1]
    assert b"more GPU memory for the source buffers" in replies[3][1]
    assert len(replies[0][1]) == frame.nbytes == len(replies[4][1])
    np.testing.assert_allclose(np.frombuffer(replies[0][1], dtype="<f4"), original, rtol=0, atol=2e-6)
    assert replies[0][1] == replies[4][1], "memory rejection must preserve the previously accepted source"
    assert json.loads(replies[5][1])["renderSequence"] == 2
    print(f"GPU memory guard: simulated 128 MiB cap selected {metrics['tileCount']} tiles, "
          "preserved requested size, and recovered after two refused renders.")

    paired = np.tile(source_frame(None), (5, 6, 1))[:1025, :2048].copy()
    scaled = f"{options} --enlarger-scale 1.25 --enlarger-x 3"
    request = frame_request(paired, scaled) + f"METRICS\nREUSE {options}\nMETRICS\nREUSE {scaled}\nMETRICS\n".encode("ascii")
    pressure = {**environment, "SPEKTRAFILM_VULKAN_TRANSIENT_BUDGET_MB": "160"}
    toggled = run_stream(exe, request, mode="--native-preview-stream", env=pressure)
    assert len(toggled) == 6 and all(status == 0 for status, _ in toggled)
    assert toggled[0] == toggled[4] and toggled[2] != toggled[4]
    for _, payload in toggled[3::2]:
        metrics = json.loads(payload)
        assert metrics["alternateSourceCachedBytes"] == 0 and metrics["uploadBytes"] >= paired.nbytes, metrics
    assert metrics["enlargerCacheHit"] and metrics["enlargerMs"] == 0, metrics
    print("GPU memory guard: a 160 MiB budget reuploads the same CPU-cached enlargement without retaining a second GPU source")


def check_adaptive_viewer(exe):
    # Trigger natural tiling above 32 MP with cuts, edges and varying alpha.
    width, height = 8191, 4103
    x = np.arange(width, dtype=np.int32)[None, :]
    y = np.arange(height, dtype=np.int32)[:, None]
    frame = np.empty((height, width, 4), dtype="<f4")
    frame[..., 0] = np.where(x % 719 < 290, .05, .85)
    frame[..., 1] = np.where((x + y) % 611 < 260, .09, .91)
    frame[..., 2] = np.where((x + 2 * y) % 513 < 142, .06, .75)
    frame[..., 3] = .15 + .82 * x / width
    source = frame.tobytes()
    del frame
    clean = {key: value for key, value in os.environ.items()
             if not key.upper().startswith("SPEKTRAFILM_") and key.upper() != "DOTNET_STARTUP_HOOKS"}
    fixed = {**clean, "SPEKTRAFILM_VULKAN_TILE_WIDTH": "512", "SPEKTRAFILM_VULKAN_TILE_HEIGHT": "256"}
    for input_color in ("srgb", "adobe_rgb", "linear_rec2020"):
        options = (f"--input-color {input_color} --output-color srgb --grain-model off "
                   "--film-format 8 --halation-amount 1 --scatter-amount .5")
        command = f"FRAME8 {width} {height} {options}\n".encode("ascii")
        request = b"".join((command, source, b"METRICS\n"))
        frames = []
        for environment in (fixed, clean):
            replies = run_stream(exe, request, mode="--native-preview-stream", env=environment, timeout=90)
            assert len(replies) == 2 and all(status == 0 for status, _ in replies), input_color
            picture = np.frombuffer(replies[0][1], dtype=np.uint8).reshape(height, width, 4)
            metrics = json.loads(replies[1][1])
            assert metrics["tiled"] and metrics["readbackBytes"] == width * height * 4 + 4 * metrics["tileCount"], input_color
            frames.append((picture, metrics))

        (reference, fixed_metrics), (adaptive, adaptive_metrics) = frames
        assert adaptive_metrics["tileCount"] <= fixed_metrics["tileCount"], input_color
        max_error = 0
        for row in range(0, height, 64):
            first = reference[row:row + 64]
            second = adaptive[row:row + 64]
            np.testing.assert_array_equal(first[..., 3], second[..., 3], err_msg=input_color)
            max_error = max(max_error, int(np.max(np.abs(first[..., :3].astype(np.int16) - second[..., :3]))))
        assert max_error <= 1, (input_color, max_error)
        print(f"Adaptive {input_color}: {fixed_metrics['tileCount']} to {adaptive_metrics['tileCount']} tiles, "
              f"max BGRA error {max_error}, exact alpha")


def check_spectral_stock_matrix(exe):
    root = Path(__file__).resolve().parents[1]
    options = json.loads((root / "stocks.json").read_text(encoding="utf-8"))
    profile_root = root / "upstream-spektrafilm/src/spektrafilm/data/profiles"
    frame = source_frame(None)[::17, ::21].copy()
    stocks = [film["Id"] for film in options["Films"]]
    methods = [method["Id"] for method in options["SpectralMethods"]]
    assert methods[0] == "hanatos2025" and "hanatos2026" in methods
    for stock in stocks:
        profile = json.loads((profile_root / f"{stock}.json").read_text(encoding="utf-8"))
        window = profile["data"].get("hanatos2025_adaptation_window_params")
        assert window and len(window) == 4 and np.isfinite(window).all() and all(value > 0 for value in window), stock

    count = 0
    for space in ("srgb", "adobe_rgb", "prophoto", "linear_rec2020"):
        cases = [(stock, mode, method) for stock in stocks for mode in ("print", "scan") for method in methods]
        request = b"".join(frame_request(frame, f"--stock {stock} --mode {mode} --spectral-method {method} --input-color {space}")
                           for stock, mode, method in cases)
        responses = run_stream(exe, request + b"QUIT\n", mode="--native-preview-stream", timeout=90)
        assert len(responses) == len(cases) + 1 and responses[-1] == (0, b"")
        rendered = {}
        for case, (status, payload) in zip(cases, responses):
            assert status == 0 and len(payload) == frame.nbytes, (space, case, status, payload[:100])
            pixels = np.frombuffer(payload, dtype="<f4").reshape(frame.shape)
            assert np.isfinite(pixels).all(), (space, case)
            np.testing.assert_array_equal(pixels[..., 3], frame[..., 3])
            rendered[case] = pixels
        for stock, mode, method in cases:
            if method != "hanatos2026":
                continue
            delta = np.max(np.abs(rendered[(stock, mode, "hanatos2025")] - rendered[(stock, mode, method)]))
            assert delta > 1e-6, (space, stock, mode, "Hanatos 2026 must produce a distinct result")
        count += len(cases)
        print(f"{space}: {len(cases)} finite stock/mode/method renders with intact alpha.")
    print(f"PASS: {count} Vulkan spectral renders and {len(stocks)} explicit Hanatos 2026 calibrations.")


def check_parameter_description(exe):
    raw = subprocess.check_output([str(exe), "--describe"], timeout=15)
    manifest = json.loads(raw)
    assert manifest["schemaVersion"] == 1 and len(manifest["parameterContract"]) == 64
    assert raw == subprocess.check_output([str(exe), "--describe"], timeout=15), "Manifest bytes changed between runs"
    parameters = manifest["parameters"]
    assert len({parameter["key"] for parameter in parameters}) == len(parameters)
    root = Path(__file__).resolve().parents[1]
    panel = json.loads((root / "stocks.json").read_text(encoding="utf-8"))
    by_key = {parameter["key"]: parameter for parameter in parameters}
    for panel_key, renderer_key in (("Films", "stock"), ("Papers", "paper"), ("SpectralMethods", "spectral-method"), ("Modes", "mode")):
        assert {value["Id"] for value in panel[panel_key]}.issubset(by_key[renderer_key]["choices"]), panel_key
    assert all(film["hanatos2025"] and film["hanatos2026"] for film in manifest["scientific"]["films"])

    frame = source_frame(None)[::64, ::96].copy()
    base = "--stock kodak_portra_400 --paper kodak_portra_endura --grain-model off --grain-synthesis-samples 2"
    cases = []
    for option in parameters:
        key, kind = option["key"], option["kind"]
        if kind in ("number", "integer"):
            minimum, maximum, initial = option["minimum"], option["maximum"], option["default"]
            assert minimum <= initial <= maximum, option
            valid = (minimum, initial, maximum)
            invalid = minimum - 1
        elif kind == "boolean":
            assert isinstance(option["default"], bool), option
            valid = ("true", "false")
            invalid = "unknown"
        else:
            assert kind == "choice" and option["default"] in option["choices"], option
            valid = option["choices"]
            invalid = "unknown"
        for value in dict.fromkeys(valid):
            unavailable = key == "mode" and value == "process_negative" and not manifest["scientific"]["academyPrinterDensity"]
            cases.append((key, value, not unavailable))
        cases.append((key, invalid, False))

    request = b"".join(frame_request(frame, f"{base} --{key} {value}") for key, value, _ in cases)
    replies = run_stream(exe, request + b"QUIT\n", mode="--native-preview-stream", timeout=120)
    assert len(replies) == len(cases) + 1 and replies[-1] == (0, b"")
    for case, (status, payload) in zip(cases, replies):
        key, value, valid = case
        assert status == (0 if valid else 1), (case, status, payload[:200])
        if valid:
            assert len(payload) == frame.nbytes and np.isfinite(np.frombuffer(payload, dtype="<f4")).all(), case
        if key == "mode" and value == "process_negative" and not valid:
            assert b"Academy Printer Density calibration" in payload
    print(f"PASS: {len(parameters)} compiled parameter descriptions and {len(cases)} renderer range/choice/availability probes.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", type=Path, help="Optional photograph. Defaults to a synthetic image.")
    parser.add_argument("--exe", type=Path, default=Path(__file__).resolve().parents[1]
                        / "build-ofx-still" / "Release" / "SpektraStillRenderer.exe")
    parser.add_argument("--spectral-stock-matrix", action="store_true", help="Check stock calibration and all spectral methods across input spaces.")
    parser.add_argument("--parameter-matrix", action="store_true", help="Check the compiled parameter descriptions against the Vulkan parser and renderer.")
    parser.add_argument("--adaptive-viewer", action="store_true", help="Compare packed tile layouts on a 33 MP chart using a physical GPU.")
    args = parser.parse_args()
    if args.parameter_matrix:
        check_parameter_description(args.exe)
        return
    if args.spectral_stock_matrix:
        check_spectral_stock_matrix(args.exe)
        return
    if args.adaptive_viewer:
        check_adaptive_viewer(args.exe)
        return
    frame = source_frame(args.image)
    base = "--stock kodak_portra_400 --paper kodak_portra_endura --input-color srgb"
    check_stream(args.exe, frame, base)
    check_numbered_stream(args.exe, frame, base)
    check_resource_root(args.exe, base)
    check_export_stream(args.exe, frame, base)
    check_enlarger_cache(args.exe, frame, base)
    check_output_transfers(args.exe, frame, base)
    check_packed_readback(args.exe, base)
    check_metering_cache(args.exe, frame, base)
    check_tiled_parity(args.exe, base)
    check_optical_scratch_lifetime(args.exe, base)
    check_gpu_budget(args.exe, base)
    baseline, seconds = render(args.exe, frame, base)
    print(f"Baseline {frame.shape[1]}×{frame.shape[0]} in {seconds * 1000:.0f} ms")

    cases = {
        "halation": "--halation-amount 3 --halation-scale 3 --halation-boost 5",
        "camera diffusion": "--camera-diffusion-family pro_mist --camera-diffusion-strength 1.5",
        "print diffusion": "--print-diffusion-family black_pro_mist --print-diffusion-strength 1.5",
        "scanner glare and MTF": "--scanner-enabled true --scanner-glare 0.1 --scanner-mtf 15",
        "scanner sharpening": "--scanner-enabled true --scanner-unsharp-radius 60 --scanner-unsharp-amount 3",
        "production grain": "--grain-model production --grain-amount 1.6",
        "synthesis grain": "--grain-model synthesis --grain-synthesis-samples 12 --grain-synthesis-cell-cap 8",
        "spatial DIR": "--dir-amount 1.5 --dir-diffusion 80 --dir-tail-weight 0.3",
        "enlarger crop": "--enlarger-scale 1.2 --enlarger-x 4 --enlarger-y 3",
        "standard film push": "--film-push-pull 1.7",
        "experimental film push": "--film-push-pull 1.7 --film-push-pull-mode experimental",
        "negative bleach bypass": "--negative-bleach-bypass 0.9",
        "scan negative": "--mode scan",
        "invert negative scan": "--mode scan_inverted",
    }
    for label, settings in cases.items():
        actual, seconds = render(args.exe, frame, f"{base} {settings}")
        delta = float(np.mean(np.abs(actual[..., :3] - baseline[..., :3]))) * 255
        if delta <= 0.01:
            raise AssertionError(f"{label} did not change the output ({delta:.3f}/255)")
        print(f"{label:24s} mean RGB change {delta:7.3f}/255 in {seconds * 1000:.0f} ms")
        if label == "enlarger crop":
            expected, _ = render(args.exe, reference_crop(frame), base)
            np.testing.assert_allclose(actual, expected, rtol=2e-4, atol=2e-4)
    for stock in ("kodak_portra_400", "fujifilm_provia_100f"):
        options = f"--stock {stock} --input-color srgb"
        forward, _ = render(args.exe, frame, f"{options} --mode scan")
        inverted, _ = render(args.exe, frame, f"{options} --mode scan_inverted")
        delta = np.mean(np.abs(forward[..., :3] - inverted[..., :3])) * 255
        if delta <= 0.01:
            raise AssertionError(f"Scan inversion did not change the scan output for {stock}")
        print(f"{stock:24s} mean RGB scan inversion change {delta:7.3f}/255")


if __name__ == "__main__":
    main()
