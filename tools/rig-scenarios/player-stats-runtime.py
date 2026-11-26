#!/usr/bin/env python3
"""Offline half of scenario 52; invoked ONLY by isolated-playback-rig.py via unshare."""
import importlib.util
import json
import os
from pathlib import Path
import signal
import subprocess
import sys


def main():
    if sys.flags.optimize:
        raise RuntimeError("Assertions must be enabled")
    root, server, candidate = map(Path, sys.argv[1:4])
    host_netns = sys.argv[4]
    own_netns = os.readlink("/proc/self/ns/net")
    if host_netns == own_netns:
        raise RuntimeError("player stats runtime must run in its own network namespace")
    subprocess.run(["ip", "link", "set", "lo", "up"], check=True)
    (root / "network-identity.json").write_text(json.dumps({"host": host_netns, "rig": own_netns,
        "endpoint": "http://127.0.0.1:18096", "offline": True}, indent=2))
    spec = importlib.util.spec_from_file_location("rig", Path(__file__).with_name("isolated-playback-rig.py"))
    rig = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(rig)
    def interrupted(signum, frame):
        raise KeyboardInterrupt(f"signal {signum}")
    signal.signal(signal.SIGTERM, interrupted)
    results = []
    try:
        for kind in ("movie", "episode"):
            results.append({"scenario": kind, "backend": "sqlite", "result": "failed"})
            results[-1] = rig.scenario(kind, root / kind, server, candidate, "sqlite", os.environ.copy())
        print("PLAYBACK_PARITY_OK backend=sqlite movie=passed episode=passed", flush=True)
    finally:
        (root / "results.json").write_text(json.dumps(results, indent=2) + "\n")


if __name__ == "__main__":
    main()
