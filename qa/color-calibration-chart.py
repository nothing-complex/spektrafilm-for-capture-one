"""Generate a synthetic Bayer chart and measure Capture One developed output.

This product includes DNG technology under license by Adobe.
The fixture uses the public Digital Negative specification and no camera data.
https://helpx.adobe.com/camera-raw/desktop/dng-and-file-formats/digital-negative.html
"""
from argparse import ArgumentParser
from hashlib import file_digest
import json
from pathlib import Path

import numpy as np


def sha256(path: Path) -> str:
    with path.open("rb") as stream:
        return file_digest(stream, "sha256").hexdigest()


def generate(destination: Path, *, flat_field: bool = False) -> None:
    import rawpy
    import tifffile

    destination.mkdir()
    tile = 512
    width, height = 6 * tile, 4 * tile
    black, white = 512, 16000
    patches = [(2.0**stop,) * 3 for stop in range(-12, 0)] + [
        (.87, .87, .87), (.98, .98, .98), (.19, .035, .021), (.035, .19, .021), (.021, .035, .19),
        (.43, .31, .19), (.25, .38, .42), (.14, .07, .03), (.03, .07, .14), (.12, .06, .11),
        (.02, .36, .36), (.36, .36, .02),
    ]
    mosaic = np.zeros((height, width), np.uint16)
    centres = []
    for index, patch in enumerate(patches):
        x, y = (index % 6) * tile, (index // 6) * tile
        block = mosaic[y:y + tile, x:x + tile]
        red, green, blue = np.rint(black + (white - black) * np.asarray(patch)).astype(np.uint16)
        block[::2, ::2], block[::2, 1::2], block[1::2, ::2], block[1::2, 1::2] = red, green, green, blue
        centres.append({"center": [x + tile // 2, y + tile // 2], "cameraChannelCodes": [int(red), int(green), int(blue)]})

    if flat_field:
        # A smooth Bayer reference with known falloff and a mild asymmetric cast.
        # Keep all samples away from both clipping and the sensor's black level.
        yy, xx = np.mgrid[-1:1:height * 1j, -1:1:width * 1j]
        falloff = .54 - .09 * (xx * xx + yy * yy)
        mosaic[::2, ::2] = np.rint(black + (white - black) * (falloff + .04 * xx)[::2, ::2])
        mosaic[::2, 1::2] = np.rint(black + (white - black) * falloff[::2, 1::2])
        mosaic[1::2, ::2] = np.rint(black + (white - black) * falloff[1::2, ::2])
        mosaic[1::2, 1::2] = np.rint(black + (white - black) * (falloff - .04 * xx)[1::2, 1::2])

    # The synthetic camera uses linear sRGB primaries and a D65 white point.
    camera_to_xyz = np.array([[.4123908, .35758434, .18048079], [.21263901, .71516868, .07219232],
                              [.01933082, .11919478, .95053215]])
    xyz_to_camera = np.linalg.inv(camera_to_xyz)
    rationals = tuple(item for number in xyz_to_camera.ravel() for item in (round(number * 100000), 100000))
    chart = destination / "Calibrated Bayer chart.dng"
    standard_tags = [
        (271, "s", 0, "Spektrafilm", False), (272, "s", 0, "Synthetic Sensor", False),
        (274, "H", 1, 1, False),  # Orientation
        (50706, "B", 4, (1, 2, 0, 0), False), (50707, "B", 4, (1, 2, 0, 0), False),
        (50708, "s", 0, "Spektrafilm Synthetic Sensor", False),
        (50721, "2i", 9, rationals, False),  # ColorMatrix1 maps XYZ to Bayer camera channels.
        (50728, "2I", 3, (1, 1, 1, 1, 1, 1), False), (50730, "2i", 1, (0, 1), False),
        (50778, "H", 1, 21, False),  # D65
    ]
    sensor_tags = [
        (33421, "H", 2, (2, 2), False), (33422, "B", 4, (0, 1, 1, 2), False),  # RGGB
        (50710, "B", 3, (0, 1, 2), False), (50711, "H", 1, 1, False),
        (50713, "H", 2, (1, 1), False), (50714, "I", 1, black, False), (50717, "I", 1, white, False),
        (50718, "2I", 2, (1, 1, 1, 1), False),
        (50719, "I", 2, (0, 0), False), (50720, "I", 2, (width, height), False),
        (50829, "I", 4, (0, 0, height, width), False),
    ]
    preview = np.rint(255 * np.asarray(patches).reshape(4, 6, 3) ** (1 / 2.2)).astype(np.uint8)
    preview = np.repeat(np.repeat(preview, 24, axis=0), 24, axis=1)
    if flat_field:
        plane = (mosaic[::16, ::16].astype(np.float32) - black) / (white - black)
        preview = np.repeat(np.rint(255 * plane ** (1 / 2.2)).astype(np.uint8)[..., None], 3, axis=2)
    with tifffile.TiffWriter(chart) as writer:
        writer.write(preview, photometric="rgb", subfiletype=1, subifds=1, metadata=None, extratags=standard_tags)
        writer.write(mosaic, photometric=32803, subfiletype=0, metadata=None, rowsperstrip=64, extratags=sensor_tags)
    with rawpy.imread(str(chart)) as decoded:
        np.testing.assert_array_equal(decoded.raw_image_visible, mosaic)
        if decoded.black_level_per_channel != [black] * 4 or decoded.white_level != white:
            raise ValueError("LibRaw did not decode the known black and white levels.")
    (destination / "reference.json").write_text(json.dumps({
        "width": width, "height": height, "black": black, "white": white, "rawSha256": sha256(chart),
        "xyz_to_camera": xyz_to_camera.tolist(), "patches": centres if not flat_field else [],
    }, indent=2) + "\n", encoding="utf-8")


def analyze(directory: Path) -> dict:
    reference = json.loads((directory / "reference.json").read_text(encoding="utf-8"))
    developed = json.loads((directory / "developed.json").read_text(encoding="utf-8"))
    chart = directory / "Calibrated Bayer chart.dng"
    digest = sha256(chart)
    if reference["rawSha256"] != digest:
        raise ValueError("The chart does not match the generated sensor reference.")
    levels = np.array([patch["cameraChannelCodes"] for patch in reference["patches"]])
    neutral = (levels[:, 0] == levels[:, 1]) & (levels[:, 1] == levels[:, 2])
    signal = (levels[:, 0] - reference["black"]) / (reference["white"] - reference["black"])
    luma_weights = np.array([.2627, .6780, .0593])
    samples = {}
    for frame in developed["frames"]:
        width, height = frame["width"], frame["height"]
        pixels = np.fromfile(directory / frame["file"], "<f4").reshape(height, width, 4)
        if not np.isfinite(pixels).all() or not np.all(pixels[..., 3] == 1):
            raise ValueError(f"Invalid pixels in {frame['name']}.")
        means = []
        for patch in reference["patches"]:
            x, y = patch["center"]
            x, y = x * width // reference["width"], y * height // reference["height"]
            means.append(pixels[y - 8:y + 8, x - 8:x + 8, :3].mean(axis=(0, 1), dtype=np.float64))
        values = np.asarray(means)
        samples[frame["name"]] = {"meanRgb": values.tolist(), "luminance": (values @ luma_weights).tolist()}

    def ramp(base_name: str, brighter_name: str) -> dict:
        base = np.array(samples[base_name]["luminance"])
        brighter = np.array(samples[brighter_name]["luminance"])
        valid = neutral & (base > 2 / 65535) & (brighter > 0) & (brighter < .9)
        if np.count_nonzero(valid) < 4 or np.any(np.diff(base[neutral]) < -1 / 65535):
            raise ValueError("The native chart produced an insufficient or inverted neutral ramp.")
        stops = np.log2(brighter[valid] / base[valid])
        slope = np.polyfit(np.log2(signal[valid]), np.log2(base[valid]), 1)[0]
        return {
            "validNeutralPatches": int(valid.sum()), "neutralLogSlope": float(slope),
            "measuredOneEvStops": stops.tolist(), "medianMeasuredOneEv": float(np.median(stops)),
            "maximumOneEvErrorStops": float(np.max(np.abs(stops - 1))),
        }

    return {
        "chartSha256": digest, "stage": developed["stage"], "workingSpace": developed["workingSpace"],
        "defaultCurve": ramp("native-default", "native-plus-one-ev"),
        "linearResponseCurve": ramp("native-linear-response", "native-linear-plus-one-ev"),
        "normalizedSensorCodes": signal.tolist(), "developed": developed, "patchMeasurements": samples,
    }


if __name__ == "__main__":
    parser = ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("generate", "analyze"))
    parser.add_argument("directory", type=Path)
    parser.add_argument("--flat-field", action="store_true", help="generate a lens-cast calibration reference")
    args = parser.parse_args()
    if args.operation == "generate":
        generate(args.directory, flat_field=args.flat_field)
    else:
        print(json.dumps(analyze(args.directory), indent=2))
