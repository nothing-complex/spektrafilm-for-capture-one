"""Exercise the Vulkan binary frame bridge without opening Capture One."""

import argparse
import json
import os
from pathlib import Path
import struct
import subprocess
import time

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
        (source, base, reuse(base), False, False),
        (source, optics, reuse(optics), False, True),
        (second, optics, frame_request(second, optics), False, True),
        (second, brighter, reuse(brighter), True, True),
    )
    replies = run_stream(exe, b"".join(c + b"METRICS\n" for _, _, c, _, _ in requests), mode="--native-preview-stream")
    assert len(replies) == 2 * len(requests) and all(status == 0 for status, _ in replies)
    previous = None
    for index, (original, options, _, hit, transformed) in enumerate(requests):
        payload = replies[index * 2][1]
        fresh, = run_stream(exe, frame_request(original, options), mode="--export-stream")
        assert fresh == (0, payload), ("cached versus fresh bit parity", index)
        metrics = json.loads(replies[index * 2 + 1][1])
        assert metrics["enlargerCacheHit"] == hit
        assert metrics["enlargerCachedBytes"] == (source.nbytes if transformed else 0)
        if hit:
            assert metrics["enlargerMs"] == 0 and metrics["uploadBytes"] == 0 and payload != previous, metrics
        elif transformed:
            assert metrics["enlargerMs"] > 0 and metrics["uploadBytes"] >= source.nbytes
        if options == border:
            rgba = np.frombuffer(payload, dtype="<f4").reshape(source.shape)
            assert rgba[0, -1, 3] == 1 and 0 < rgba[height // 2, 0, 3] < 1
        previous = payload
    print("Enlarger cache matched eight fresh renders byte for byte and skipped transform and upload on print edits")


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


def check_tiled_parity(exe, base):
    """Compare genuine multi-tile renders on a signal that crosses tile boundaries."""
    height, width = 769, 1027
    y, x = np.mgrid[0:1:height * 1j, 0:1:width * 1j].astype(np.float32)
    checker = (np.floor(x * 7) + np.floor(y * 9)) % 2
    frame = np.stack((.1 + .7 * x, .07 + .8 * y, .1 + .5 * checker, .15 + .85 * x), axis=-1)
    frame[240:285, 475:530, :3] = 1.5  # Highlight crosses both default tile boundaries.
    cases = {
        "color": "--grain-model off",
        "8 mm halation/scatter": "--grain-model off --film-format 8 --halation-amount 1 --scatter-amount 0.5",
        "35 mm diffusion": "--grain-model off --film-format 35 --camera-diffusion-family pro_mist "
                           "--camera-diffusion-strength 1.5 --print-diffusion-family black_pro_mist "
                           "--print-diffusion-strength 1.5",
        "70 mm DIR": "--grain-model off --film-format 70 --dir-amount 1.5 --dir-diffusion 80 --dir-tail-weight 0.3",
        "metering/grain/scanner": "--grain-model production --film-format 35 --auto-exposure true --scanner-enabled true",
        "enlarger": "--grain-model off --enlarger-scale 1.3 --enlarger-x 3 --enlarger-y -2",
    }
    clean = {key: value for key, value in os.environ.items()
             if not key.upper().startswith("SPEKTRAFILM_") and key.upper() != "DOTNET_STARTUP_HOOKS"}
    for label, options in cases.items():
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
            metrics = json.loads(replies[1][1])
            assert metrics["tiled"] == (mode == "tiled"), (label, mode, metrics)
            if mode == "tiled":
                assert metrics["tileCount"] > 1, (label, metrics)
            pictures.append(picture)
        np.testing.assert_allclose(pictures[0], pictures[1], rtol=0, atol=2e-6, err_msg=f"Tile parity: {label}")
        print(f"Tile parity {label}: max code difference {np.max(np.abs(pictures[0] - pictures[1])):.8g}")


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
    args = parser.parse_args()
    if args.parameter_matrix:
        check_parameter_description(args.exe)
        return
    if args.spectral_stock_matrix:
        check_spectral_stock_matrix(args.exe)
        return
    frame = source_frame(args.image)
    base = "--stock kodak_portra_400 --paper kodak_portra_endura --input-color srgb"
    check_stream(args.exe, frame, base)
    check_numbered_stream(args.exe, frame, base)
    check_export_stream(args.exe, frame, base)
    check_enlarger_cache(args.exe, frame, base)
    check_output_transfers(args.exe, frame, base)
    check_tiled_parity(args.exe, base)
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
