"""Measure the physical Gaussian support of the current Vulkan filters."""

import argparse
import json
from pathlib import Path
import struct
import subprocess

import numpy as np


# The physical film widths and shader caps come from SpektraVulkanRenderer.cpp
# and the Gaussian loops in SpektraDir, SpektraGrain and SpektraScannerPost.
FILM_EDGE_MM = {"8": 4.8, "35": 35.0, "70": 70.41}
FILTERS = {
    "scanner unsharp": ("scanner-unsharp-radius", 1.0, 256),
    "DIR core": ("dir-diffusion", 1.0, 256),
    "DIR longest tail lobe": ("dir-tail-size", 2.7684, 256),
    "grain final blur": ("grain-final-blur", 1.0, 64),
}


def gaussian(sigma, radius):
    x = np.arange(-radius, radius + 1, dtype=np.float64)
    weights = np.exp(-.5 * (x / sigma) ** 2)
    return x, weights


def kernel_report(parameters, long_edges):
    cases = []
    for name, (key, multiplier, cap) in FILTERS.items():
        for film, width_mm in FILM_EDGE_MM.items():
            for pixels in long_edges:
                pitch = width_mm * 1000 / pixels
                physical = parameters[key]["maximum"] * multiplier
                if key == "grain-final-blur":
                    physical *= (width_mm / 35.0) ** .62
                sigma = physical / pitch
                requested = int(np.ceil(3 * sigma))
                radius = min(requested, cap)
                x, weights = gaussian(sigma, radius)
                reference_x, reference_weights = gaussian(sigma, requested)
                mass_fraction = weights.sum() / reference_weights.sum()
                cases.append({
                    "filter": name, "filmFormat": film, "longEdge": pixels, "desiredSigmaUm": physical,
                    "desiredSigmaPixels": sigma, "desiredRadiusPixels": requested, "shaderRadiusPixels": radius,
                    "unscaledWeightFractionKept": float(mass_fraction),
                    "normalizedKernelL1Error": float(2 * (1 - mass_fraction)),
                    "effectiveSigmaUm": float(np.sqrt(np.dot(x ** 2, weights) / weights.sum()) * pitch),
                    "referenceEffectiveSigmaUm": float(
                        np.sqrt(np.dot(reference_x ** 2, reference_weights) / reference_weights.sum()) * pitch),
                })
    return cases


def render_scanner(exe, frame, film, unsharp):
    height, width = frame.shape[:2]
    header = (f"FRAME {width} {height} --stock kodak_portra_400 --film-format {film}"
              " --grain-model off --halation-amount 0 --scatter-amount 0 --output-color linear_rec709"
              " --scanner-enabled true --scanner-glare 0 --scanner-mtf 0 --scanner-unsharp-radius 100"
              f" --scanner-unsharp-amount {unsharp}\n").encode("ascii")
    result = subprocess.run([str(exe), "--native-preview-stream"], input=header + frame.tobytes(),
                            capture_output=True, timeout=90)
    if result.returncode or len(result.stdout) < 8:
        raise RuntimeError(result.stderr.decode("utf-8", errors="replace"))
    status, length = struct.unpack_from("<II", result.stdout)
    if status or length != frame.nbytes or len(result.stdout) != 8 + length:
        raise RuntimeError(result.stdout[8:4096].decode("utf-8", errors="replace"))
    return np.frombuffer(result.stdout, dtype="<f4", offset=8).reshape(frame.shape)


def measure_scanner(exe, long_edges):
    cases = []
    for film, width_mm in FILM_EDGE_MM.items():
        for width in long_edges:
            frame = np.full((32, width, 4), .25, dtype="<f4")
            frame[:, :, 3] = 1
            frame[:, width // 2, :3] = .5  # A vertical impulse isolates the horizontal blur.
            plain = render_scanner(exe, frame, film, 0)
            sharpened = render_scanner(exe, frame, film, 1)
            if not np.isfinite(sharpened).all() or not np.array_equal(plain[..., 3], sharpened[..., 3]):
                raise ValueError("Scanner impulse produced invalid RGB or altered alpha.")
            offset = np.arange(width) - width // 2
            response = np.abs(sharpened[16, :, 1].astype(np.float64) - plain[16, :, 1])
            response[width // 2] = 0
            nonzero = response > max(3e-8, response.max() * 1e-3)
            if not nonzero.any():
                raise ValueError(f"No measurable scanner response for film {film} at edge {width}.")
            active = np.where(nonzero, response, 0)
            effective_sigma = np.sqrt(np.dot(offset ** 2, active) / active.sum()) * width_mm * 1000 / width
            radius = int(np.max(np.abs(offset[nonzero])))
            if radius > 256:
                raise ValueError(f"Scanner impulse extends beyond the checked shader cap by {radius - 256} pixels.")
            cases.append({
                "filmFormat": film, "longEdge": width, "measurableRadiusPixels": radius,
                "measuredSigmaUm": float(effective_sigma), "peakOffCentreResponse": float(response.max()),
            })
    return cases


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--renderer", required=True, type=Path)
    parser.add_argument("--edge", action="append", type=int)
    parser.add_argument("--measure-scanner", action="store_true")
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    edges = args.edge or [1200, 4096, 6000]
    if any(edge < 512 or edge > 16384 for edge in edges):
        parser.error("Use long edges from 512 to 16,384 pixels.")
    exe = args.renderer.resolve(strict=True)
    description = json.loads(subprocess.check_output([str(exe), "--describe"], timeout=20))
    parameters = {item["key"]: item for item in description["parameters"]}
    result = {"parameterContract": description["parameterContract"], "kernels": kernel_report(parameters, edges)}
    if args.measure_scanner:
        result["scannerImpulse"] = measure_scanner(exe, edges)
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(result, output, indent=2, allow_nan=False)
        output.write("\n")


if __name__ == "__main__":
    main()
