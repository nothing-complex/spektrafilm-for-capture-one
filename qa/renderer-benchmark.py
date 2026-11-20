#!/usr/bin/env python3
"""Measure the Windows worker transport and renderer on deterministic synthetic pixels."""

import argparse
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


def run_case(executable, shape, scene, route, repetitions, timeout, simulated_cap_mib):
    packed = route == "viewer"
    width, height = shape
    pixels = width * height
    args = "--native-preview-stream" if packed else "--export-stream"
    env = {key: value for key, value in os.environ.items()
           if not key.upper().startswith("SPEKTRAFILM_") and key.upper() != "DOTNET_STARTUP_HOOKS"}
    if simulated_cap_mib is not None:
        env["SPEKTRAFILM_VULKAN_TRANSIENT_BUDGET_MB"] = str(simulated_cap_mib)
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
        tile = struct.pack("<8f", .12, .2, .3, 1, .7, .6, .5, 1) * (BLOCK_BYTES // 32)
        for iteration in range(repetitions):
            mode = ("FRAME8" if packed else "FRAME") if iteration == 0 else ("REUSE8" if packed else "REUSE")
            command = (mode, str(width), str(height)) if iteration == 0 else (mode,)
            options = SCENES[scene] + ("--film-exposure", f"{iteration % 5 / 10:.1f}")
            started = time.perf_counter_ns()
            child.stdin.write((" ".join(command + options) + "\n").encode("ascii"))
            if iteration == 0:
                for offset in range(0, pixels * 16, BLOCK_BYTES):
                    child.stdin.write(tile[:min(BLOCK_BYTES, pixels * 16 - offset)])
            child.stdin.flush()
            size = reply_size(child)
            if size != pixels * (4 if packed else 16):
                raise ValueError("The actual worker payload length does not match its frame.")
            for offset in range(0, size, BLOCK_BYTES):
                read_exact(child.stdout, min(BLOCK_BYTES, size - offset))
            milliseconds = (time.perf_counter_ns() - started) / 1e6
            diagnostic = command_json(child, b"METRICS")
            if diagnostic["schema"] != 1 or diagnostic["renderSequence"] != iteration + 1:
                raise ValueError("The renderer diagnostic is stale or incompatible.")
            measurements.append({
                "iteration": iteration + 1,
                "sentSource": iteration == 0,
                "wallMs": milliseconds,
                "replyBytes": size,
                "workerMemory": process_memory(child.pid),
                "renderer": diagnostic,
            })
        warm = sorted(entry["wallMs"] for entry in measurements[1:])
        return {
            "width": width, "height": height, "route": route, "scene": scene,
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
    parser.add_argument("--repetitions", type=int, default=5)
    parser.add_argument("--case-timeout", type=int, default=180)
    parser.add_argument("--simulate-gpu-cap-mib", type=int, help="Test-only cap for the renderer allocator. Does not change physical VRAM.")
    args = parser.parse_args()
    if os.name != "nt" or args.repetitions < 1 or args.case_timeout < 1:
        parser.error("Use Windows and positive repetition and timeout values.")
    if args.simulate_gpu_cap_mib is not None and args.simulate_gpu_cap_mib < 1:
        parser.error("The simulated GPU allocator cap must be positive.")
    executable = args.renderer.resolve(strict=True)
    if args.output.exists():
        parser.error("Choose a new output JSON path.")
    result = {
        "schema": 1,
        "simulatedGpuAllocatorCapMiB": args.simulate_gpu_cap_mib,
        "timingScope": "Worker request and reply. The first request transfers input; later requests re-render edited parameters.",
        "memoryScope": "Worker process and renderer diagnostics. Driver workspace and Capture One host memory are outside this report.",
        "cases": [],
    }
    with executable.open("rb") as handle:
        result["rendererSha256"] = hashlib.file_digest(handle, "sha256").hexdigest()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("x", encoding="utf-8") as output:
        json.dump(result, output)
    for route in args.route or ("viewer", "export"):
        for scene in args.scene or ("color",):
            for shape in args.size or ((1200, 800), (3000, 2000), (6000, 4000)):
                try:
                    case = run_case(executable, shape, scene, route, args.repetitions, args.case_timeout, args.simulate_gpu_cap_mib)
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
