"""Measure the physical Gaussian support of the current Vulkan filters."""

import argparse
from hashlib import sha256
import json
import math
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

OPTICAL_CASES = {
    "cameraDiffusion": "--camera-diffusion-family pro_mist --camera-diffusion-strength .75",
    "printDiffusion": "--print-diffusion-family black_pro_mist --print-diffusion-strength .75",
    "halation": "--halation-amount 1 --halation-scale 2 --halation-boost 1",
    "scatter": "--scatter-amount .5",
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
    return render(exe, frame, film, f"--halation-amount 0 --scatter-amount 0 --scanner-enabled true "
                  f"--scanner-glare 0 --scanner-mtf 0 --scanner-unsharp-radius 100 --scanner-unsharp-amount {unsharp}")


def render(exe, frame, film, options):
    height, width = frame.shape[:2]
    header = (f"FRAME {width} {height} --stock kodak_portra_400 --film-format {film}"
              " --grain-model off --output-color linear_rec709 "
              f"{options}\n").encode("ascii")
    result = subprocess.run([str(exe), "--native-preview-stream"], input=header + frame.tobytes(),
                            capture_output=True, timeout=90)
    if result.returncode or len(result.stdout) < 8:
        raise RuntimeError(result.stderr.decode("utf-8", errors="replace"))
    status, length = struct.unpack_from("<II", result.stdout)
    if status or length != frame.nbytes or len(result.stdout) != 8 + length:
        raise RuntimeError(result.stdout[8:4096].decode("utf-8", errors="replace"))
    return np.frombuffer(result.stdout, dtype="<f4", offset=8).reshape(frame.shape)


def measure_optical_scales(exe, long_edges):
    """Measure actual linear output against controls from the same normalized chart."""
    cases = []
    baseline = "--paper kodak_portra_endura --input-color linear_rec2020 "
    for width in sorted(set(long_edges)):
        height = width // 16
        pixels = np.arange(width, dtype=np.float64)
        bar_left, bar_right = .485 * width, .515 * width
        coverage = np.clip(np.minimum(pixels + 1, bar_right) - np.maximum(pixels, bar_left), 0, 1)
        frame = np.ones((height, width, 4), dtype="<f4")
        frame[:, :, :3] = (.08 + .84 * coverage)[None, :, None]
        bar_distance = np.abs(pixels + .5 - width / 2) / width - .015
        outside_bar = coverage == 0
        for film in ("8", "35", "70"):
            control = render(exe, frame, film, baseline + "--halation-amount 0 --scatter-amount 0")
            for name, effect in OPTICAL_CASES.items():
                options = ("--halation-amount 0 " if name != "halation" else "")
                options += ("--scatter-amount 0 " if name != "scatter" else "") + effect
                result = render(exe, frame, film, baseline + options)
                if not np.isfinite(result).all() or not np.array_equal(result[..., 3], frame[..., 3]):
                    raise ValueError(f"Invalid RGB or altered alpha for {name}, {film} mm at edge {width}.")
                row = slice(height // 2 - 2, height // 2 + 2)
                delta = result[row, :, :3].astype(np.float64) - control[row, :, :3]
                response = np.mean(np.abs(delta), axis=(0, 2))
                off_bar = response[outside_bar]
                distances_um = bar_distance[outside_bar] * FILM_EDGE_MM[film] * 1000
                mass = float(off_bar.sum())
                if mass <= 1e-7:
                    raise ValueError(f"No measurable off-bar response for {name}, {film} mm at edge {width}.")
                cases.append({
                    "effect": name, "filmFormat": film, "longEdge": width, "rows": height,
                    "meanAbsRgbDelta": float(response.mean()),
                    "peakAbsRgbDelta": float(response.max()),
                    "offBarAbsRgbIntegrated": mass / width,
                    "offBarAbsRgbRmsMicrometers": float(np.sqrt(np.dot(distances_um ** 2, off_bar) / mass)),
                })
    return cases


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


def prototype_tent_kernels(kernels):
    """Compare phase-aligned tent reduction and interpolation with the exact 1D kernel."""
    cases = []
    for case in kernels:
        sigma = case["desiredSigmaPixels"]
        cap = case["shaderRadiusPixels"]
        if case["desiredRadiusPixels"] <= cap:
            continue
        scale = 2 ** math.ceil(math.log2(3 * sigma / cap))
        safe_width = math.ceil(4.5 * sigma) + 2 * scale
        axis = np.arange(-safe_width, safe_width + 1, dtype=np.float64)
        coarse = np.arange(math.floor(axis[0] / scale) - 1, math.ceil(axis[-1] / scale) + 1) * scale + (scale - 1) * .5
        coarse_sigma = sigma / scale
        radius = math.ceil(3 * coarse_sigma)
        weights = gaussian(coarse_sigma, radius)[1]
        weights /= weights.sum()
        errors = {"massError": 0., "centroidErrorPixels": 0., "sigmaRelativeError": 0., "l1Error": 0.}
        for phase in sorted({0, scale // 4, scale // 2, scale - 1}):
            impulse = np.maximum(0, 1 - np.abs(coarse - phase) / scale) / scale
            restored = np.interp(axis, coarse, np.convolve(impulse, weights, mode="same"))
            offset = axis - phase
            reference = np.exp(-.5 * (offset / sigma) ** 2) * (np.abs(offset) <= math.ceil(3 * sigma))
            reference /= reference.sum()
            mass = restored.sum()
            restored /= mass
            measured_sigma = np.sqrt(np.dot(offset ** 2, restored))
            reference_sigma = np.sqrt(np.dot(offset ** 2, reference))
            phase_errors = {
                "massError": abs(float(mass) - 1),
                "centroidErrorPixels": abs(float(np.dot(offset, restored))),
                "sigmaRelativeError": abs(float(measured_sigma / reference_sigma) - 1),
                "l1Error": float(np.abs(restored - reference).sum()),
            }
            for key, value in phase_errors.items():
                errors[key] = max(errors[key], value)
        if (errors["massError"] > 1e-10 or errors["centroidErrorPixels"] > 1e-9
                or errors["sigmaRelativeError"] > .004 or errors["l1Error"] > .0025):
            raise ValueError(f"Phase-aligned blur prototype exceeds its centered impulse tolerance: {case}, {errors}")
        cases.append({
            "filter": case["filter"], "filmFormat": case["filmFormat"], "longEdge": case["longEdge"],
            "scale": scale, "coarseRadiusPixels": radius, **errors,
        })
    return cases


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--renderer", required=True, type=Path)
    parser.add_argument("--edge", action="append", type=int)
    parser.add_argument("--measure-scanner", action="store_true")
    parser.add_argument("--measure-optical-scales", action="store_true")
    parser.add_argument("--prototype-tent", action="store_true", help="Check an isolated CPU multiscale reference.")
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
    if args.measure_optical_scales:
        result["rendererSha256"] = sha256(exe.read_bytes()).hexdigest()
        result["opticalScales"] = measure_optical_scales(exe, edges)
    if args.prototype_tent:
        result["prototypeTent"] = prototype_tent_kernels(result["kernels"])
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(result, output, indent=2, allow_nan=False)
        output.write("\n")


if __name__ == "__main__":
    main()
