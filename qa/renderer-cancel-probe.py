"""Exercise the numbered worker protocol on a real tiled Vulkan request."""
from argparse import ArgumentParser
import ctypes
from ctypes import wintypes
import hashlib
import json
import os
from pathlib import Path
import struct
import subprocess
import threading
import time
from uuid import uuid4


def kernel_events():
    api = ctypes.WinDLL("kernel32", use_last_error=True)
    api.CreateEventW.argtypes = (wintypes.LPVOID, wintypes.BOOL, wintypes.BOOL, wintypes.LPCWSTR)
    api.CreateEventW.restype = wintypes.HANDLE
    for name in ("SetEvent", "ResetEvent", "CloseHandle"):
        getattr(api, name).argtypes = (wintypes.HANDLE,)
        getattr(api, name).restype = wintypes.BOOL
    return api


def exact(pipe, length):
    data = pipe.read(length)
    if len(data) != length:
        raise EOFError("Truncated reply from the independent Vulkan worker.")
    return data


def run(executable, repetitions):
    api = kernel_events()
    event_name = "Local\\Spektrafilm.QA." + uuid4().hex
    handle = api.CreateEventW(None, True, False, event_name)
    if not handle:
        raise ctypes.WinError(ctypes.get_last_error())
    environment = {key: val for key, val in os.environ.items()
                   if not key.upper().startswith("SPEKTRAFILM_") and key.upper() != "DOTNET_STARTUP_HOOKS"}
    environment.update(SPEKTRAFILM_REQUEST_CANCEL_EVENT=event_name, SPEKTRAFILM_REQUEST_PROGRESS="1",
                       SPEKTRAFILM_VULKAN_TILE_MODE="tiled", SPEKTRAFILM_VULKAN_TILE_WIDTH="384",
                       SPEKTRAFILM_VULKAN_TILE_HEIGHT="384")
    try:
        with subprocess.Popen([str(executable), "--native-preview-stream"], cwd=executable.parent,
                              env=environment, stdin=subprocess.PIPE, stdout=subprocess.PIPE) as worker:
            watchdog = threading.Timer(180, worker.kill)
            watchdog.start()
            request = 0
            options = "--grain-model off --halation-amount 0.7 --halation-boost 2 --scatter-amount 0.5"
            width, height = 2048, 1536
            block_pixels = struct.pack("<8f", .12, .2, .3, 1, .7, .6, .5, 1) * ((1024 * 1024) // 32)

            def send(command):
                worker.stdin.write(command.encode("ascii") + b"\n")
                worker.stdin.flush()

            def frame(reuse=False, cancel=False):
                nonlocal request
                request += 1
                send(f"REQUEST {request} {'REUSE8' if reuse else 'FRAME8'} {width} {height} {options}")
                if not reuse:
                    for offset in range(0, width * height * 16, len(block_pixels)):
                        worker.stdin.write(block_pixels[:min(len(block_pixels), width * height * 16 - offset)])
                    worker.stdin.flush()
                steps, canceled_at = [], None
                while True:
                    status, size, echoed = struct.unpack("<iiQ", exact(worker.stdout, 16))
                    if echoed != request or not 0 <= size <= width * height * 4:
                        raise ValueError("Unexpected request identity or reply size.")
                    if status == 4 and size == 1:
                        stage = exact(worker.stdout, 1)[0]
                        if not 1 <= stage <= 5 or steps and stage <= steps[-1]:
                            raise ValueError("Unexpected worker progress ordering.")
                        steps.append(stage)
                        if cancel and stage == 3:
                            time.sleep(.018)
                            canceled_at = time.perf_counter_ns()
                            if not api.SetEvent(handle):
                                raise ctypes.WinError(ctypes.get_last_error())
                        continue
                    if cancel:
                        if status != 3 or size != 0 or canceled_at is None:
                            raise ValueError(f"The worker did not return an empty cancellation: {status}, {size}, {steps}")
                        if not api.ResetEvent(handle):
                            raise ctypes.WinError(ctypes.get_last_error())
                        return {"request": request, "stages": steps,
                                "signalToReplyMs": (time.perf_counter_ns() - canceled_at) / 1e6}
                    if status != 0 or size != width * height * 4:
                        raise ValueError("The uncanceled worker must deliver the exact complete packed frame.")
                    checksum = hashlib.sha256()
                    while size:
                        block = exact(worker.stdout, min(size, 1024 * 1024))
                        checksum.update(block)
                        size -= len(block)
                    return {"request": request, "stages": steps, "sha256": checksum.hexdigest()}

            try:
                send("HELLO 2")
                status, size = struct.unpack("<ii", exact(worker.stdout, 8))
                if status != 0 or not 2 <= size <= 16384:
                    raise ValueError("Worker did not negotiate the numbered protocol.")
                capability = json.loads(exact(worker.stdout, size))
                if not (capability["requestCancellation"] and capability["frameProgress"]):
                    raise ValueError("Worker did not open the fixture signal.")
                started = time.perf_counter_ns()
                baseline = frame()
                baseline_ms = (time.perf_counter_ns() - started) / 1e6
                # Vary exposure so each REUSE request enters Vulkan instead of serving cached pixels.
                canceled = []
                for _ in range(repetitions):
                    options += " --film-exposure 0.25"
                    canceled.append(frame(reuse=True, cancel=True))
                    options = options.removesuffix(" --film-exposure 0.25")
                    recovered = frame(reuse=True)
                    if recovered["sha256"] != baseline["sha256"]:
                        raise ValueError("A canceled tile changed the next complete frame in the same worker.")
                request += 1
                send(f"REQUEST {request} METRICS")
                status, size, echoed = struct.unpack("<iiQ", exact(worker.stdout, 16))
                if status != 0 or echoed != request or not 2 <= size <= 32768:
                    raise ValueError("Tiled metrics mismatch after cancellation.")
                metrics = json.loads(exact(worker.stdout, size))
                if metrics["tileCount"] <= 1:
                    raise ValueError("The actual Vulkan worker did not use multiple tiles.")
                request += 1
                send(f"REQUEST {request} QUIT")
                if struct.unpack("<iiQ", exact(worker.stdout, 16)) != (0, 0, request) or worker.wait(timeout=5) != 0:
                    raise ValueError("The tested worker failed to exit cleanly.")
                return {"pixelDimensions": [width, height], "workerPid": worker.pid, "baselineMs": baseline_ms,
                        "rendererSha256": hashlib.sha256(executable.read_bytes()).hexdigest(),
                        "tileCount": metrics["tileCount"], "baseline": baseline, "canceled": canceled, "lastRecovered": recovered}
            finally:
                watchdog.cancel()
                if worker.poll() is None:
                    worker.kill()
    finally:
        api.CloseHandle(handle)


def main():
    parser = ArgumentParser(description=__doc__)
    parser.add_argument("renderer", type=Path)
    parser.add_argument("receipt", type=Path)
    parser.add_argument("--repetitions", type=int, default=5)
    args = parser.parse_args()
    if not 1 <= args.repetitions <= 50:
        parser.error("Use between 1 and 50 repetitions.")
    report = json.dumps(run(args.renderer.resolve(strict=True), args.repetitions), indent=2)
    args.receipt.write_text(report + "\n", encoding="utf-8")
    print(report)


if __name__ == "__main__":
    main()
