#!/usr/bin/env python3
"""Hold the daily registry lock through the existing guard's read/ratchet/save."""

import fcntl
import json
import math
import os
from pathlib import Path
import stat
import subprocess
import sys
import tempfile


def registry(path):
    data = json.loads(path.read_text())
    if not isinstance(data, dict) or not isinstance(data.get("scenarios"), list):
        raise ValueError("invalid thresholds: scenarios array required")
    keys = set()
    for entry in [data] + data["scenarios"]:
        if not isinstance(entry, dict):
            raise ValueError("invalid threshold entry")
        fields = ["improvement_margin_ratio", "ratchet_headroom_ratio"]
        if entry is not data:
            key = tuple(entry.get(k) for k in ("flow", "backend", "quantile"))
            if not all(isinstance(k, str) and k for k in key) or key in keys:
                raise ValueError("invalid or duplicate threshold scenario")
            keys.add(key)
            fields += ["threshold_ms", "target_ms"]
            if "threshold_ms" not in entry:
                raise ValueError("threshold_ms required")
        for field in fields:
            value = entry.get(field)
            if value is None and field != "threshold_ms":
                continue
            if (isinstance(value, bool) or not isinstance(value, (int, float))
                    or not math.isfinite(value) or value < 0
                    or (field == "improvement_margin_ratio" and value >= 1)):
                raise ValueError(f"invalid threshold {field}")
    if not keys:
        raise ValueError("threshold scenarios required")
    return keys


def main():
    root, dry, configured, *args = sys.argv[1:]
    root = Path(root).resolve()
    seed = root / "tools/perf/loadtime-thresholds.json"
    expected = registry(seed)
    if not configured:
        if dry != "1":
            raise ValueError("PHANTOM_LOADTIME_THRESHOLDS_PATH required for live daily state")
        path = seed
    else:
        path = Path(configured)
        if not path.is_absolute() or path.resolve().is_relative_to(root):
            raise ValueError("threshold state must be absolute and outside the source checkout")
        if path.is_symlink():
            raise ValueError("threshold state must not be a symlink")

    def guard():
        source = path if path.exists() else seed
        if registry(source) != expected:
            raise ValueError("invalid threshold state: scenario set differs from shipped registry")
        return subprocess.run(["bash", str(root / "tools/perf/loadtime-guard.sh"),
                               *args, "--thresholds", str(source)], check=False).returncode

    if dry == "1":
        return guard()

    # Check even on a hold/breach: a read-only mount must not look operational.
    for target in (path.parent, path) if path.exists() else (path.parent,):
        if not target.stat().st_mode & (stat.S_IWUSR | stat.S_IWGRP | stat.S_IWOTH) or not os.access(target, os.W_OK):
            raise ValueError(f"threshold state not writable: {target}")
    with open(str(path) + ".lock", "a") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        if path.exists() and registry(path) != expected:
            raise ValueError("invalid threshold state: scenario set differs from shipped registry")
        # Same-directory temporary creation also probes actual mount permissions.
        fd, temporary = tempfile.mkstemp(prefix=".loadtime-thresholds-", dir=path.parent)
        try:
            with os.fdopen(fd, "wb") as out:
                if not path.exists():
                    out.write(seed.read_bytes())
                    out.flush()
                    os.fsync(out.fileno())
            if not path.exists():
                os.replace(temporary, path)
            # Persist the rename itself, not only the file's bytes. Also flush
            # the guard's replacement before reporting the daily result.
            directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(directory)
                result = guard()
                os.fsync(directory)
                return result
            finally:
                os.close(directory)
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (OSError, ValueError, TypeError) as exc:
        print(f"loadtime daily threshold state error: {exc}", file=sys.stderr)
        sys.exit(2)
