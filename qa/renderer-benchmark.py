#!/usr/bin/env python3
"""Measure the Windows worker transport and renderer on deterministic synthetic pixels."""

import argparse
from concurrent.futures import ThreadPoolExecutor
import ctypes
from ctypes import wintypes
import hashlib
import json
import math
from pathlib import Path
import os
import statistics
import struct
import subprocess
from tempfile import NamedTemporaryFile
import threading
import time

BLOCK_BYTES = 1024 * 1024
SCENES = {
    "color": ("--grain-model", "off", "--halation-amount", "0", "--scatter-amount", "0"),
    "metering": ("--grain-model", "off", "--halation-amount", "0", "--scatter-amount", "0",
                 "--auto-exposure", "true", "--auto-exposure-meter", "center_weighted"),
    "enlarger": ("--grain-model", "off", "--halation-amount", "0", "--scatter-amount", "0",
                 "--enlarger-scale", "1.4", "--enlarger-x", "3", "--enlarger-y", "-5"),
    "optics": ("--grain-model", "off", "--film-format", "super8", "--halation-amount", "1", "--scatter-amount", "0.5"),
    "stacked-optics": ("--grain-model", "production", "--film-format", "super8", "--halation-amount", "1",
                       "--halation-boost", "1", "--scatter-amount", "0.5", "--camera-diffusion-family", "pro_mist",
                       "--camera-diffusion-strength", "1.5", "--print-diffusion-family", "black_pro_mist",
                       "--print-diffusion-strength", "1.5", "--dir-amount", "1.5", "--dir-diffusion", "80",
                       "--scanner-enabled", "true", "--scanner-glare", "0.08", "--scanner-glare-roughness", "1",
                       "--scanner-unsharp-radius", "100", "--scanner-unsharp-amount", "2"),
    "synthesis-draft": ("--grain-model", "synthesis", "--film-format", "35", "--grain-synthesis-samples", "2"),
    "synthesis-full": ("--grain-model", "synthesis", "--film-format", "35"),
}


class ProcessMemoryCounters(ctypes.Structure):
    _fields_ = [
        ("cb", wintypes.DWORD), ("page_faults", wintypes.DWORD),
        ("peak_working_set", ctypes.c_size_t), ("working_set", ctypes.c_size_t),
        ("peak_paged_pool", ctypes.c_size_t), ("paged_pool", ctypes.c_size_t),
        ("peak_nonpaged_pool", ctypes.c_size_t), ("nonpaged_pool", ctypes.c_size_t),
        ("pagefile", ctypes.c_size_t), ("peak_pagefile", ctypes.c_size_t),
        ("private_bytes", ctypes.c_size_t),
    ]


def process_memory(pid):
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
    kernel.OpenProcess.restype = wintypes.HANDLE
    kernel.CloseHandle.argtypes = (wintypes.HANDLE,)
    kernel.K32GetProcessMemoryInfo.argtypes = (
        wintypes.HANDLE, ctypes.POINTER(ProcessMemoryCounters), wintypes.DWORD,
    )
    kernel.K32GetProcessMemoryInfo.restype = wintypes.BOOL
    process = kernel.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
    if not process:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        memory = ProcessMemoryCounters()
        memory.cb = ctypes.sizeof(memory)
        if not kernel.K32GetProcessMemoryInfo(process, ctypes.byref(memory), memory.cb):
            raise ctypes.WinError(ctypes.get_last_error())
        return {
            "workingSetBytes": memory.working_set,
            "peakWorkingSetBytes": memory.peak_working_set,
            "privateBytes": memory.private_bytes,
            "peakPagefileBytes": memory.peak_pagefile,
        }
    finally:
        kernel.CloseHandle(process)


def read_exact(stream, size):
    value = stream.read(size)
    if len(value) != size:
        raise EOFError("The worker closed its output before completing a reply.")
    return value


def reply_size(child):
    status, size = struct.unpack("<ii", read_exact(child.stdout, 8))
    if status:
        if not 1 <= size <= 4096:
            raise ValueError("Invalid renderer error frame.")
        raise ValueError(read_exact(child.stdout, size).decode("utf-8", errors="replace"))
    return size


def command_json(child, command):
    child.stdin.write(command + b"\n")
    child.stdin.flush()
    size = reply_size(child)
    if not 1 <= size <= 65536:
        raise ValueError("Invalid renderer JSON reply size.")
    return json.loads(read_exact(child.stdout, size))


def run_case(executable, shape, scene, route, repetitions, timeout, simulated_cap_mib, hash_replies, tile_size,
             source_change_interval, edit, tile_mode=None, disable_halation_peak_cache=False, before_request=None):
    packed = route == "viewer"
    width, height = shape
    pixels = width * height
    args = "--native-preview-stream" if packed else "--export-stream"
    env = {key: value for key, value in os.environ.items()
           if not key.upper().startswith("SPEKTRAFILM_") and key.upper() != "DOTNET_STARTUP_HOOKS"}
    if simulated_cap_mib is not None:
        env["SPEKTRAFILM_VULKAN_TRANSIENT_BUDGET_MB"] = str(simulated_cap_mib)
    if tile_size is not None:
        env["SPEKTRAFILM_VULKAN_TILE_WIDTH"] = str(tile_size[0])
        env["SPEKTRAFILM_VULKAN_TILE_HEIGHT"] = str(tile_size[1])
    if tile_mode is not None:
        env["SPEKTRAFILM_VULKAN_TILE_MODE"] = tile_mode
    if disable_halation_peak_cache:
        env["SPEKTRAFILM_VULKAN_TILED_HALATION_PEAK_CACHE"] = "0"
    child = subprocess.Popen([str(executable), args], cwd=executable.parent, env=env,
                             stdin=subprocess.PIPE, stdout=subprocess.PIPE)
    # An unresponsive renderer cannot leave a benchmark child or blocked pipe behind.
    watchdog = threading.Timer(timeout, child.kill)
    watchdog.start()
    try:
        version = command_json(child, b"HELLO 1")
        if version["protocol"] != 1 or pixels > version["maxPixels"] or max(shape) > version["maxSide"]:
            raise ValueError(f"Frame {width}x{height} exceeds this worker's advertised limits.")
        measurements = []
        tiles = [struct.pack("<8f", *sample) * (BLOCK_BYTES // 32) for sample in (
            (.12, .2, .3, 1, .7, .6, .5, 1), (.18, .25, .32, 1, .64, .51, .44, 1),
        )]
        source_change = 0
        for iteration in range(repetitions):
            send_source = iteration == 0 or (source_change_interval > 0 and iteration % source_change_interval == 0)
            mode = ("FRAME8" if packed else "FRAME") if send_source else ("REUSE8" if packed else "REUSE")
            command = (mode, str(width), str(height)) if send_source else (mode,)
            options = SCENES[scene] + (f"--{edit}-exposure", f"{iteration % 5 / 10:.1f}")
            if before_request is not None:
                before_request()
            started = time.perf_counter_ns()
            child.stdin.write((" ".join(command + options) + "\n").encode("ascii"))
            if send_source:
                tile = tiles[source_change % len(tiles)]
                source_change += 1
                for offset in range(0, pixels * 16, BLOCK_BYTES):
                    child.stdin.write(tile[:min(BLOCK_BYTES, pixels * 16 - offset)])
            child.stdin.flush()
            size = reply_size(child)
            if size != pixels * (4 if packed else 16):
                raise ValueError("The actual worker payload length does not match its frame.")
            digest = hashlib.sha256() if hash_replies else None
            for offset in range(0, size, BLOCK_BYTES):
                block = read_exact(child.stdout, min(BLOCK_BYTES, size - offset))
                if digest is not None:
                    digest.update(block)
            finished = time.perf_counter_ns()
            milliseconds = (finished - started) / 1e6
            diagnostic = command_json(child, b"METRICS")
            if diagnostic["schema"] != 1 or diagnostic["renderSequence"] != iteration + 1:
                raise ValueError("The renderer diagnostic is stale or incompatible.")
            measurement = {
                "iteration": iteration + 1,
                "sentSource": send_source,
                "wallMs": milliseconds,
                "startedNs": started,
                "finishedNs": finished,
                "replyBytes": size,
                "workerMemory": process_memory(child.pid),
                "renderer": diagnostic,
            }
            if digest is not None:
                measurement["replySha256"] = digest.hexdigest()
            measurements.append(measurement)
        warm = sorted(entry["wallMs"] for entry in measurements[1:])
        return {
            "width": width, "height": height, "route": route, "scene": scene, "edit": edit,
            "rendererParameterContract": version["parameterContract"],
            "coldMs": measurements[0]["wallMs"],
            "warmMedianMs": statistics.median(warm) if warm else None,
            "warmP95Ms": warm[math.ceil(.95 * len(warm)) - 1] if warm else None,
            "measurements": measurements,
        }
    finally:
        watchdog.cancel()
        try:
            child.stdin.close()
        except BrokenPipeError:
            pass
        try:
            child.wait(timeout=3)
        except subprocess.TimeoutExpired:
            child.kill()
            child.wait(timeout=5)
        finally:
            child.stdout.close()


def run_overlap(executable, viewer_size, export_size, scene, args):
    sizes = {"viewer": viewer_size, "export": export_size}
    options = (args.repetitions, args.case_timeout, args.simulate_gpu_cap_mib,
               args.hash_replies, args.tile_size, args.source_change_interval, args.edit,
               args.tile_mode, args.disable_halation_peak_cache)
    serial = {route: run_case(executable, shape, scene, route, *options) for route, shape in sizes.items()}
    barrier = threading.Barrier(2, timeout=args.case_timeout)

    def paired(route, shape):
        try:
            return run_case(executable, shape, scene, route, *options, before_request=barrier.wait)
        except BaseException:
            barrier.abort()
            raise

    with ThreadPoolExecutor(max_workers=2) as workers:
        jobs = {route: workers.submit(paired, route, shape) for route, shape in sizes.items()}
        overlap = {route: job.result() for route, job in jobs.items()}

    windows_ms = []
    for viewer, export in zip(overlap["viewer"]["measurements"], overlap["export"]["measurements"], strict=True):
        start = max(viewer["startedNs"], export["startedNs"])
        finish = min(viewer["finishedNs"], export["finishedNs"])
        windows_ms.append(max(0, finish - start) / 1e6)
    if any(duration <= 0 for duration in windows_ms):
        raise ValueError("A paired request did not overlap the other worker's request window.")
    parity = None
    if args.hash_replies:
        parity = {}
        for route, case in serial.items():
            hashes = [sample["replySha256"] for sample in case["measurements"]]
            actual = [sample["replySha256"] for sample in overlap[route]["measurements"]]
            parity[route] = hashes == actual
    return {"serial": serial, "overlap": overlap, "overlapWindowMs": windows_ms, "replyHashesMatch": parity}


def dimensions(value):
    try:
        width, height = map(int, value.lower().split("x"))
        if width < 1 or height < 1:
            raise ValueError
        return width, height
    except ValueError as error:
        raise argparse.ArgumentTypeError("Use positive WxH dimensions, e.g. 1600x1066.") from error


def update_result(result, path):
    with NamedTemporaryFile(mode="w", encoding="utf-8", delete=False, dir=path.parent, suffix=".json.tmp") as output:
        temporary = Path(output.name)
        try:
            json.dump(result, output, indent=2, allow_nan=False)
            output.write("\n")
        except BaseException:
            output.close()
            temporary.unlink(missing_ok=True)
            raise
    try:
        temporary.replace(path)
    finally:
        temporary.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--renderer", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--size", action="append", type=dimensions)
    parser.add_argument("--route", action="append", choices=("viewer", "export"))
    parser.add_argument("--scene", action="append", choices=SCENES)
    parser.add_argument("--edit", choices=("film", "print"), default="film",
                        help="Choose the exposure slider to sweep between requests.")
    parser.add_argument("--repetitions", type=int, default=5)
    parser.add_argument("--case-timeout", type=int, default=180)
    parser.add_argument("--simulate-gpu-cap-mib", type=int, help="Test-only cap for the renderer allocator. Does not change physical VRAM.")
    parser.add_argument("--hash-replies", action="store_true", help="Include the SHA-256 of each frame. Wall time then includes hashing.")
    parser.add_argument("--tile-size", type=dimensions, help="Override tile size in this worker for cases that use tiling.")
    parser.add_argument("--tile-mode", choices=("legacy", "tiled"), help="Force the full-frame or tiled worker path.")
    parser.add_argument("--disable-halation-peak-cache", action="store_true",
                        help="Recompute the tiled halation peak on every request for comparison.")
    parser.add_argument("--source-change-interval", type=int, default=0,
                        help="Alternate two source charts every N requests. Zero uploads only the first source.")
    parser.add_argument("--overlap-preview", type=dimensions,
                        help="Compare serial and paired Viewer/export worker requests. Use --size for the export canvas.")
    args = parser.parse_args()
    if os.name != "nt" or args.repetitions < 1 or args.case_timeout < 1:
        parser.error("Use Windows and positive repetition and timeout values.")
    if args.simulate_gpu_cap_mib is not None and args.simulate_gpu_cap_mib < 1:
        parser.error("The simulated GPU allocator cap must be positive.")
    if args.tile_size is not None and not all(64 <= edge <= 8192 for edge in args.tile_size):
        parser.error("Both tile dimensions must be between 64 and 8192 pixels.")
    if args.source_change_interval < 0:
        parser.error("The source change interval must be nonnegative.")
    executable = args.renderer.resolve(strict=True)
    if args.output.exists():
        parser.error("Choose a new output JSON path.")
    result = {
        "schema": 1,
        "simulatedGpuAllocatorCapMiB": args.simulate_gpu_cap_mib,
        "hashedReplies": args.hash_replies,
        "tileSizeOverride": args.tile_size,
        "tileModeOverride": args.tile_mode,
        "halationPeakCacheDisabled": args.disable_halation_peak_cache,
        "sourceChangeInterval": args.source_change_interval,
        "timingScope": "Worker request and reply. Each measurement records whether it transferred input or used the resident source.",
        "memoryScope": "Worker process and renderer diagnostics. Driver workspace and Capture One host memory are outside this report.",
        "cases": [],
    }
    with executable.open("rb") as handle:
        result["rendererSha256"] = hashlib.file_digest(handle, "sha256").hexdigest()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(result, output)
    if args.overlap_preview:
        result["overlapCases"] = []
        for scene in args.scene or ("color",):
            for size in args.size or ((6000, 4000),):
                case = run_overlap(executable, args.overlap_preview, size, scene, args)
                result["overlapCases"].append(case)
                update_result(result, args.output)
                for route, paired in case["overlap"].items():
                    print(f"{route} {scene} {paired['width']}x{paired['height']} "
                          f"serial p95={case['serial'][route]['warmP95Ms']} ms "
                          f"overlap p95={paired['warmP95Ms']} ms", flush=True)
        return
    for route in args.route or ("viewer", "export"):
        for scene in args.scene or ("color",):
            for shape in args.size or ((1200, 800), (3000, 2000), (6000, 4000)):
                try:
                    case = run_case(executable, shape, scene, route, args.repetitions, args.case_timeout,
                                    args.simulate_gpu_cap_mib, args.hash_replies, args.tile_size, args.source_change_interval,
                                    args.edit, args.tile_mode, args.disable_halation_peak_cache)
                except Exception as error:
                    result["failure"] = {"route": route, "scene": scene, "size": shape, "reason": str(error)}
                    update_result(result, args.output)
                    raise
                result["cases"].append(case)
                update_result(result, args.output)
                print(f"{route} {scene} {shape[0]}x{shape[1]} cold={case['coldMs']:.1f} ms warm={case['warmMedianMs']}",
                      flush=True)


if __name__ == "__main__":
    main()
