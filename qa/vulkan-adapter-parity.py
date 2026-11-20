"""Compare the real worker on the system Vulkan device and an explicitly supplied test ICD."""

import argparse
from hashlib import sha256
import json
import os
from pathlib import Path
import subprocess
import sys
from time import perf_counter

import numpy as np

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "ofx-still-renderer"))
from smoke import frame_request, run_stream


CASES = {
    "baseline": "",
    "mallett2019": "--spectral-method mallett2019",
    "scanner": "--film-format 8 --scanner-enabled true --scanner-glare .08 --scanner-mtf 25 "
               "--scanner-unsharp-radius 50 --scanner-unsharp-amount .5",
    "diffusion": "--camera-diffusion-family pro_mist --camera-diffusion-strength .75 --halation-amount .3",
}
ICD_ENVIRONMENT = {"VK_DRIVER_FILES", "VK_ICD_FILENAMES", "VK_ADD_DRIVER_FILES",
                   "VK_LOADER_DRIVERS_SELECT", "VK_LOADER_DRIVERS_DISABLE"}


def device_summary(env, vulkaninfo):
    result = subprocess.run([vulkaninfo, "--summary"], env=env, capture_output=True, text=True, timeout=25, check=True)
    names = {"apiVersion", "deviceType", "deviceName", "driverID", "driverName"}
    return {key: [line.partition("=")[2].strip() for line in result.stdout.splitlines()
                  if line.partition("=")[0].strip() == key] for key in sorted(names)}


def render(worker, chart, variation, env):
    options = ("--stock kodak_portra_400 --paper kodak_portra_endura --input-color srgb "
               "--grain-model off --output-color srgb " + variation)
    request = b"FRAME8" + frame_request(chart, options)[len(b"FRAME"):]
    request += f"METRICS\nREUSE {options}\nMETRICS\nQUIT\n".encode("ascii")
    started = perf_counter()
    replies = run_stream(worker, request, env=env, mode="--native-preview-stream", timeout=45)
    elapsed_ms = (perf_counter() - started) * 1000
    if len(replies) != 5 or replies[-1] != (0, b"") or any(status for status, _ in replies):
        failures = [data[:250] for status, data in replies if status]
        raise AssertionError(f"Unexpected replies ({len(replies)}): {failures}")
    packed_bytes, packed_metrics, float_bytes, float_metrics = (data for _, data in replies[:4])
    pixels = np.frombuffer(float_bytes, dtype="<f4").reshape(chart.shape)
    packed = np.frombuffer(packed_bytes, dtype=np.uint8).reshape(chart.shape)
    assert np.isfinite(pixels).all()
    np.testing.assert_array_equal(pixels[..., 3], chart[..., 3])
    expect = np.empty_like(packed)
    expect[..., :3] = np.rint(np.clip(pixels[..., 2::-1], 0, 1).astype(np.float64) * 255).astype(np.uint8)
    expect[..., 3] = np.rint(chart[..., 3] * np.float32(255)).astype(np.uint8)
    np.testing.assert_array_equal(packed, expect)
    float_readback = json.loads(float_metrics)["readbackBytes"]
    metrics = json.loads(packed_metrics)
    assert float_readback == pixels.nbytes
    assert metrics["readbackBytes"] == packed.nbytes + 4, metrics
    return pixels, packed, {
        "floatSha256": sha256(float_bytes).hexdigest(),
        "packedSha256": sha256(packed_bytes).hexdigest(),
        "coldTwoRenderMs": elapsed_ms,
        "floatReadbackBytes": float_readback,
        "packedReadbackBytes": metrics["readbackBytes"],
        "gpuMemoryBudgetBytes": metrics["gpuMemoryBudgetBytes"],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("worker", type=Path)
    parser.add_argument("test_icd", type=Path, help="path to an installed, trusted test ICD JSON file")
    parser.add_argument("--vulkaninfo", default="vulkaninfo")
    args = parser.parse_args()
    worker, icd = (path.resolve(strict=True) for path in (args.worker, args.test_icd))
    base = {key: value for key, value in os.environ.items()
            if key.upper() not in ICD_ENVIRONMENT and not key.upper().startswith("SPEKTRAFILM_")}
    alternate = {**base, "VK_DRIVER_FILES": str(icd)}
    system_devices, alternate_devices = (device_summary(env, args.vulkaninfo) for env in (base, alternate))
    assert system_devices["deviceName"] and alternate_devices["deviceName"] and system_devices != alternate_devices
    assert any(kind != "PHYSICAL_DEVICE_TYPE_CPU" for kind in system_devices["deviceType"])

    height, width = 47, 65
    y, x = np.mgrid[0:1:height*1j, 0:1:width*1j].astype(np.float32)
    chart = np.stack((.08 + .85*x, .08 + .72*y, .04 + .65*x*y, .14 + .82*x), axis=-1).astype("<f4")
    receipt = {
        "runtimeSha256": sha256(worker.read_bytes()).hexdigest(),
        "testIcdManifestSha256": sha256(icd.read_bytes()).hexdigest(),
        "systemDevices": system_devices,
        "testIcdDevices": alternate_devices,
        "cases": {},
    }
    baseline = None
    for name, options in CASES.items():
        pixels, packed, hardware = render(worker, chart, options, base)
        comparison, alternate_packed, test_icd = render(worker, chart, options, alternate)
        float_error = float(np.max(np.abs(pixels[..., :3] - comparison[..., :3])))
        packed_error = int(np.max(np.abs(packed.astype(np.int16) - alternate_packed.astype(np.int16))))
        assert float_error <= 1 / 65535 and packed_error <= 1, (name, float_error, packed_error)
        if baseline is None:
            baseline = hardware["floatSha256"]
        else:
            assert hardware["floatSha256"] != baseline, f"{name} did not exercise distinct processing"
        receipt["cases"][name] = {
            "system": hardware, "testIcd": test_icd,
            "maxFloatRgbError": float_error, "maxPackedCodeError": packed_error,
        }
    print(json.dumps(receipt, indent=2))


if __name__ == "__main__":
    main()
