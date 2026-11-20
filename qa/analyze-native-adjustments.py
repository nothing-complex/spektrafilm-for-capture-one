"""Measure transfer behavior after native Exposure, White Balance, and Highlight edits."""

import argparse
import json
from pathlib import Path

import numpy as np


def analyze(directory):
    probes = json.loads((directory / "native-adjustments.json").read_text(encoding="utf-8"))
    reports = []
    for fixture in probes:
        width, height = fixture["Width"], fixture["Height"]
        pixels = {}
        for name, file in fixture["Pixels"].items():
            source = np.fromfile(directory / file, dtype="<f4").reshape(height, width, 4)
            if not np.isfinite(source).all():
                raise ValueError(f"{fixture['Source']} {name} returned nonfinite samples")
            pixels[name] = source
        baseline = pixels["original"][..., :3].astype(np.float64)
        luminance = np.array([.2627, .6780, .0593])
        original_y = baseline @ luminance
        low_y = pixels["minus-ev"][..., :3] @ luminance
        high = pixels["plus-ev"][..., :3]
        high_y = high @ luminance
        interior = (original_y > .06) & (original_y < .42) & (high_y < .9) & (low_y > .01)
        if np.count_nonzero(interior) < 100:
            raise ValueError(f"{fixture['Source']} did not offer enough comparable midtones")
        gain_low = low_y[interior] / original_y[interior]
        gain_high = high_y[interior] / original_y[interior]
        if not np.median(gain_low) < .9 or not np.median(gain_high) > 1.1:
            raise ValueError(f"{fixture['Source']} did not respond to its native exposure changes")
        bright = np.max(high, axis=2) > .65
        highlight_delta = np.abs(pixels["highlight-recovery"][..., :3] - high)
        warm_delta = pixels["warmer-wb"][..., :3] - baseline
        if not np.any(bright) or not np.any(highlight_delta[bright] > 2 / 65535):
            raise ValueError(f"{fixture['Source']} did not record highlight recovery in the bright region")
        if not np.any(np.abs(warm_delta) > 2 / 65535):
            raise ValueError(f"{fixture['Source']} did not record a white balance change")
        reports.append({
            "camera": fixture["Source"],
            "cameraProfile": fixture["CameraProfile"],
            "cameraProfileSha256": fixture["CameraProfileSha256"],
            "interiorPixels": int(interior.sum()),
            "minusOneEvGainP10P50P90": np.percentile(gain_low, [10, 50, 90]).tolist(),
            "plusOneEvGainP10P50P90": np.percentile(gain_high, [10, 50, 90]).tolist(),
            "brightPixels": int(bright.sum()),
            "brightPixelsChangedByRecovery": int(np.count_nonzero(np.any(highlight_delta[bright] > 2 / 65535, axis=1))),
            "meanAbsoluteWhiteBalanceChange": np.mean(np.abs(warm_delta), axis=(0, 1)).tolist(),
        })
    return reports


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("matrix", type=Path)
    print(json.dumps(analyze(parser.parse_args().matrix), indent=2, allow_nan=False))
