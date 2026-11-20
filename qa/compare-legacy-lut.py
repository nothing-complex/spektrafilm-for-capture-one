"""Measure native Vulkan and optional legacy Python LUTs over the same sRGB grid."""

import argparse
from contextlib import redirect_stdout
from hashlib import file_digest
import json
import os
from pathlib import Path
import shlex
import sys
from time import perf_counter


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("native", type=Path, nargs="+", help="Full-strength sRGB cubes exported by the Capture One panel")
    parser.add_argument("--output", type=Path, required=True, help="New machine-readable comparison receipt")
    args = parser.parse_args()
    if args.output.exists():
        parser.error("Choose a new report filename.")
    root = Path(__file__).resolve().parents[1]
    args.output.parent.mkdir(parents=True, exist_ok=True)
    os.environ.setdefault("MPLCONFIGDIR", str(args.output.resolve().parent / "legacy-matplotlib"))
    os.environ.setdefault("NUMBA_CACHE_DIR", str(args.output.resolve().parent / "legacy-numba"))
    sys.path.insert(0, str(root))
    import numpy as np
    from lut_worker import LutRenderer, LIMITS, SPECTRAL_METHODS

    renderer = LutRenderer()
    aliases = {
        "film": "stock", "exposure": "film-exposure", "printer_exposure": "print-exposure",
        "preflash_exposure": "preflash", "print_shadows": "print-shadow-shape", "print_highlights": "print-highlight-shape",
    }

    def header(text, key):
        return next(line.removeprefix(key).strip() for line in text.splitlines() if line.startswith(key))

    def pixels(text, size):
        samples = np.loadtxt([line for line in text.splitlines() if line and line[0] in "0123456789.+-"], dtype=np.float64)
        if samples.shape != (size ** 3, 3) or not np.isfinite(samples).all() or (samples < 0).any() or (samples > 1).any():
            raise ValueError("Invalid normalized sRGB LUT samples.")
        return samples

    cases = []
    for native in args.native:
        text = native.read_text(encoding="utf-8-sig")
        tokens = shlex.split(header(text, "# Colour parameters:"))
        if len(tokens) % 2 or any(not token.startswith("--") for token in tokens[::2]):
            raise ValueError("The cube does not contain an exact native renderer option list.")
        options = {key.removeprefix("--"): value for key, value in zip(tokens[::2], tokens[1::2])}
        size = int(header(text, "LUT_3D_SIZE "))
        if float(header(text, "# Look strength:")) != 1 or options.get("input-color") != "srgb" or options.get("output-color") != "srgb":
            raise ValueError("Use a full-strength cube with matching encoded sRGB input and output.")
        if options.get("mode") not in ("print", "scan", "scan_inverted") or options.get("spectral-method") not in SPECTRAL_METHODS:
            raise ValueError("Choose a mode and spectral model available in both renderers.")
        request = {"command": "export_lut", "size": size, "strength": 1}
        forwarded = {"input-color", "output-color"}
        for key in (*LIMITS, "film", "paper", "mode", "spectral_method", "film_push_pull_mode"):
            option = aliases.get(key, key.replace("_", "-"))
            if option in options:
                request[key] = float(options[option]) if key in LIMITS else options[option]
                forwarded.add(option)
        if request["mode"] == "scan_inverted":
            request.update(mode="scan", scan_invert=True)
        started = perf_counter()
        with redirect_stdout(sys.stderr):
            legacy = renderer.handle(request)["cube"]
        elapsed = (perf_counter() - started) * 1000
        actual = pixels(text, size)
        expected = pixels(legacy, size)
        delta = np.abs(actual - expected)
        neutrals = np.arange(size) * (size * size + size + 1)
        with native.open("rb") as file:
            digest = file_digest(file, "sha256").hexdigest()
        cases.append({
            "cube": native.name, "nativeSha256": digest, "nativeParameterContract": header(text, "# Engine parameter contract:"),
            "request": request, "unforwardedNativeOptions": sorted(options.keys() - forwarded), "legacySampleMs": elapsed,
            "samples": size ** 3, "meanAbsoluteSrgbCode": float(delta.mean()), "p95AbsoluteSrgbCode": float(np.quantile(delta, .95)),
            "maxAbsoluteSrgbCode": float(delta.max()), "meanNeutralError": float(delta[neutrals].mean()),
        })
    with (root / "lut_worker.py").open("rb") as file:
        reference = file_digest(file, "sha256").hexdigest()
    with args.output.open("x", encoding="utf-8") as file:
        json.dump({"schema": 1, "legacyWorkerSha256": reference, "cases": cases}, file, indent=2, allow_nan=False)
    for case in cases:
        print(f"{case['cube']}: mean {case['meanAbsoluteSrgbCode']:.5f}, p95 {case['p95AbsoluteSrgbCode']:.5f} sRGB code difference")


if __name__ == "__main__":
    main()
