"""Check worker recovery in the isolated build that simulates loss after its second completed fence."""

import argparse
from hashlib import sha256
import json
import os
from pathlib import Path
import struct
import subprocess


def query(executable, protocol, requests):
    env = {key: value for key, value in os.environ.items() if not key.upper().startswith("SPEKTRAFILM_")}
    commands = f"HELLO {protocol}\n".encode("ascii")
    for number, request in enumerate(requests, 1):
        if protocol == 2:
            commands += f"REQUEST {number} ".encode("ascii")
        commands += request
    result = subprocess.run([str(executable), "--native-preview-stream"], input=commands,
                            env=env, capture_output=True, timeout=45, check=True)
    status, length = struct.unpack_from("<II", result.stdout)
    hello = json.loads(result.stdout[8:8 + length])
    assert status == 0 and hello["protocol"] == protocol
    offset = 8 + length
    envelope = struct.Struct("<IIQ" if protocol == 2 else "<II")
    replies = []
    while offset < len(result.stdout):
        fields = envelope.unpack_from(result.stdout, offset)
        offset += envelope.size
        status, length = fields[:2]
        if protocol == 2:
            assert fields[2] == len(replies) + 1
        assert offset + length <= len(result.stdout)
        replies.append((status, result.stdout[offset:offset + length]))
        offset += length
    assert len(replies) == len(requests)
    return replies


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("production_worker", type=Path)
    parser.add_argument("injected_worker", type=Path)
    args = parser.parse_args()
    production, injected = (path.resolve(strict=True) for path in (args.production_worker, args.injected_worker))
    assert production != injected
    width, height = 64, 48
    frame = b"".join(struct.pack("<4f", .05 + .85 * x / width, .08 + .8 * y / height, .21, .6)
                     for y in range(height) for x in range(width))
    settings = "--stock kodak_portra_400 --paper kodak_portra_endura --input-color srgb --grain-model off"
    upload = f"FRAME {width} {height} {settings}\n".encode("ascii") + frame
    baseline, quit_reply = query(production, 2, [upload, b"QUIT\n"])
    assert baseline[0] == 0 and len(baseline[1]) == len(frame) and quit_reply == (0, b"")
    observations = {}
    for protocol in (2, 1):
        reuse_size = f"{width} {height} " if protocol == 2 else ""
        request = f"REUSE {reuse_size}{settings} --film-exposure .35\n".encode("ascii")
        initial, loss, recovered, diagnostics, exit_reply = query(injected, protocol,
                                                                 [upload, request, upload, b"METRICS\n", b"QUIT\n"])
        assert initial == recovered == baseline and exit_reply == (0, b"")
        assert loss[0] == (5 if protocol == 2 else 1)
        assert b"VK_ERROR_DEVICE_LOST" in loss[1] and b"Recreated the shared Vulkan backend" in loss[1]
        assert diagnostics[0] == 0 and json.loads(diagnostics[1])["renderSequence"] == 2
        observations[str(protocol)] = {
            "lossStatus": loss[0], "diagnostic": loss[1].decode("utf-8"),
            "initialAndRecoveredPixelsSha256": sha256(recovered[1]).hexdigest(),
            "validRenders": 2,
        }
    print(json.dumps({
        "productionSha256": sha256(production.read_bytes()).hexdigest(),
        "injectedSha256": sha256(injected.read_bytes()).hexdigest(),
        "simulation": "second render fence completed successfully before return-value substitution",
        "protocols": observations,
    }, indent=2))


if __name__ == "__main__":
    main()
