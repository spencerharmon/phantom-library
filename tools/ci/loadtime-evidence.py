#!/usr/bin/env python3
"""Sanitized daily measurement provenance, separate from the ratchet registry."""

import fcntl
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import sys
import tempfile
import time
import uuid


def main():
    action, name, *args = sys.argv[1:]
    path = Path(name)
    if action == "init":
        root, started, keep = args
        if not 1 <= int(keep) <= 10000:
            raise ValueError("retention must be between 1 and 10000")
        if not path.is_absolute() or path.is_symlink():
            raise ValueError("evidence directory must be absolute and not a symlink")
        if path.resolve().is_relative_to(Path(root).resolve()):
            raise ValueError("evidence directory must be outside checkout")
        parent = path if path.exists() else path.parent
        if not parent.stat().st_mode & (stat.S_IWUSR | stat.S_IWGRP | stat.S_IWOTH):
            raise ValueError("evidence directory is not writable")
        path.mkdir(mode=0o700, exist_ok=True)
        sha = subprocess.run(["git", "-C", root, "rev-parse", "--verify", "HEAD"],
                             capture_output=True, text=True, check=False).stdout.strip()
        if not re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", sha):
            sha = "unavailable"
        attempt = uuid.uuid4().hex
        data = dict(version=1, attempt_id=attempt, run_start=int(started),
                    checkout_sha=sha, color="unknown", status="running",
                    targets={}, failures=[], stages={}, retention=int(keep))
        path = path / f".run-{attempt}.partial.json"
    else:
        data = json.loads(path.read_text())
        if action == "color":
            data["color"] = args[0] if args[0] in ("blue", "green") else "unknown"
        elif action == "target":
            kind, target = args
            if kind not in ("Movie", "Series", "Season", "Episode") or not re.fullmatch(r"[A-Za-z0-9-]{1,256}", target):
                raise ValueError("invalid evidence target")
            data["targets"][kind] = target
        elif action == "failure":
            flow, kind, sample, target, code, status, stage = args
            if (flow not in ("list_load", "sort_change", "info_open", "get_sources", "materialise", "play_materialised")
                    or kind not in ("movie", "episode")
                    or stage not in ("request", "page_fetch", "badge_states")
                    or not re.fullmatch(r"[A-Za-z0-9-]{1,256}", target)
                    or not re.fullmatch(r"[0-9]{3}", status)):
                raise ValueError("invalid failure evidence")
            # Bound each artifact as well as the number of completed attempts.
            if len(data["failures"]) < 4096:
                data["failures"].append(dict(flow=flow, item_type=kind, sample_index=int(sample),
                    target=target, curl_exit_code=int(code), http_status=status,
                    response="transport/no-response" if status == "000" else "http", stage=stage))
            else:
                data["failures_omitted"] = data.get("failures_omitted", 0) + 1
        elif action == "stage":
            stage, code, started, *duration = args
            if stage not in ("discovery", "measure", "push", "guard"):
                raise ValueError("invalid evidence stage")
            data["stages"][stage] = dict(exit_code=int(code), start=int(started),
                                         seconds=int(duration[0]) if duration else max(0, int(time.time()) - int(started)))
        elif action == "finish":
            data.update(exit_code=int(args[0]), status="complete" if args[0] == "0" else "failed")
        else:
            raise ValueError("unknown evidence action")

    # Only sanitized, explicitly selected fields enter this file. Never ingest
    # stdout/stderr, URLs, headers, response bodies or an environment dump.
    if not path.parent.stat().st_mode & (stat.S_IWUSR | stat.S_IWGRP | stat.S_IWOTH):
        raise ValueError("evidence directory is not writable")
    fd, temporary = tempfile.mkstemp(prefix=".evidence-", dir=path.parent)
    try:
        with os.fdopen(fd, "w") as out:
            json.dump(data, out, sort_keys=True)
            out.write("\n")
            out.flush()
            os.fsync(out.fileno())
        if action != "finish":
            os.replace(temporary, path)
        with open(path.parent / ".retention.lock", "a") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            if action == "finish":
                final = path.parent / f"run-{data['attempt_id']}.json"
                # Prune only finalized manifests of this format, never partial
                # files, symlinks, other writers' scratch, or threshold files.
                completed = sorted((p for p in path.parent.iterdir()
                    if re.fullmatch(r"run-[0-9a-f]{32}\.json", p.name)
                    and p.is_file() and not p.is_symlink()), key=lambda p: (p.stat().st_mtime_ns, p.name))
                for old in completed[:max(0, len(completed) - data["retention"] + 1)]:
                    old.unlink()
                os.replace(temporary, final)
            directory = os.open(path.parent, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(directory)
                if action == "finish":
                    path.unlink()
                    os.fsync(directory)
            except OSError:
                if action == "finish":
                    final.unlink(missing_ok=True)
                raise
            finally:
                os.close(directory)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    if action == "init":
        print(path)


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, KeyError, TypeError):
        # Do not echo arbitrary configured strings or subprocess output.
        print("loadtime evidence persistence failed", file=sys.stderr)
        sys.exit(2)
