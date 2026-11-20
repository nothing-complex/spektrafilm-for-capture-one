"""Compare Capture One's native working-profile renders in common sRGB primaries."""

import argparse
import json
from pathlib import Path

import colour
import numpy as np


SPACES = {
    "srgb": "sRGB",
    "adobe_rgb": "Adobe RGB (1998)",
    "prophoto": "ProPhoto RGB",
    "linear_rec2020": "ITU-R BT.2020",
}


def common_srgb(frame, encoding):
    codes = frame[..., :3].astype(np.float64)
    if encoding == "linear_rec2020":
        linear = codes
    else:
        linear = colour.RGB_COLOURSPACES[SPACES[encoding]].cctf_decoding(codes)
    converted = colour.RGB_to_RGB(linear, SPACES[encoding], "sRGB", chromatic_adaptation_transform="Bradford")
    return colour.models.eotf_inverse_sRGB(np.clip(converted, 0, 1))


def percentiles(error):
    return {f"p{p}": float(np.percentile(error, p)) for p in (50, 95, 99, 100)}


def read_frame(root, row, column):
    name = row[column]
    if Path(name).name != name:
        raise ValueError("The probe frame must be inside the matrix folder.")
    frame = np.fromfile(root / name, dtype="<f4").reshape(row["Height"], row["Width"], 4)
    if not np.isfinite(frame).all():
        raise ValueError(f"Non-finite pixel in {name}")
    return frame


def analyze(root):
    records = json.loads((root / "matrix.json").read_text(encoding="utf-8"))
    grouped = {}
    for row in records:
        profiles = grouped.setdefault(row["Source"], {})
        encoding = row["InputEncoding"]
        if encoding in profiles or encoding not in SPACES:
            raise ValueError("Unexpected or duplicate native input encoding.")
        profiles[encoding] = row
    if len(grouped) != 4 or any(set(profiles) != set(SPACES) for profiles in grouped.values()):
        raise ValueError("Expected four camera files and four native profiles per file.")

    reports = []
    for camera, profiles in sorted(grouped.items()):
        srgb = read_frame(root, profiles["srgb"], "NativePixels")
        reference = common_srgb(srgb, "srgb")
        # Compare common-gamut colours away from clipping and near-black
        # transfer quantization. Keep the unrestricted metrics as evidence.
        interior = (reference.min(axis=2) > .02) & (reference.max(axis=2) < .97) & (srgb[..., 3] == 1)
        if not interior.any():
            raise ValueError(f"No opaque within-gamut pixels for {camera}")
        film = read_frame(root, profiles["srgb"], "FilmPixels")
        for encoding, row in profiles.items():
            if encoding == "srgb":
                continue
            native = read_frame(root, row, "NativePixels")
            rendered = read_frame(root, row, "FilmPixels")
            if np.array_equal(native, srgb) or not np.array_equal(native[..., 3], rendered[..., 3]):
                raise ValueError(f"Unexpected identical native encoding or changed alpha for {camera} {encoding}")
            converted = common_srgb(native, encoding)
            display_error = np.max(np.abs(converted - reference), axis=2)
            unconverted_error = np.max(np.abs(native[..., :3] - reference), axis=2)
            common_error = percentiles(display_error[interior])
            wrongly_encoded = percentiles(unconverted_error[interior])
            if common_error["p95"] >= wrongly_encoded["p95"]:
                raise ValueError(f"The expected {encoding} transform did not improve common-sRGB parity for {camera}.")
            reports.append({
                "camera": camera,
                "profile": encoding,
                "interiorPixels": int(interior.sum()),
                "commonSrgbCodeError": common_error,
                "unconvertedCodeError": wrongly_encoded,
                "allSrgbCodeError": percentiles(display_error),
                "filmLinearRec709Difference": percentiles(np.max(np.abs(rendered[..., :3] - film[..., :3]), axis=2)),
            })
    return reports


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("matrix", type=Path)
    args = parser.parse_args()
    report = analyze(args.matrix)
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
