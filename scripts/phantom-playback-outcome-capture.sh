#!/usr/bin/env bash
# Read-only Prometheus capture; analysis remains in the existing offline engine.
set -euo pipefail
exec python3 - "${BASH_SOURCE[0]}" "$@" <<'PY'
import argparse
import ctypes
import hashlib
import http.client
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request
from decimal import Decimal, InvalidOperation

ENGINE = Path(sys.argv.pop(1)).resolve().with_name("phantom-playback-outcome-coverage.sh")
ENGINE_BYTES = ENGINE.read_bytes()
QUERIES = {
    "instant": "phantom_playback_outcome_total",
    "window": "max_over_time(phantom_playback_outcome_total[30d])",
    "timestamps": "timestamp(phantom_playback_outcome_total)",
}


def digest(data):
    return hashlib.sha256(data).hexdigest()


def unique(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def decode(data):
    return json.loads(data, object_pairs_hook=unique, parse_float=Decimal,
                      parse_constant=lambda value: (_ for _ in ()).throw(ValueError(value)))


def number(value):
    if isinstance(value, bool) or not isinstance(value, (str, int, Decimal)):
        raise ValueError("expected finite nonnegative decimal")
    result = Decimal(value)
    if not result.is_finite() or result < 0:
        raise ValueError("expected finite nonnegative decimal")
    return result


def validate_times(files, evaluation):
    for kind in QUERIES:
        payload = decode(files[kind + ".json"])
        if (not isinstance(payload, dict) or payload.get("status") != "success"
                or payload.get("warnings") or payload.get("infos")
                or not isinstance(payload.get("data"), dict)
                or payload["data"].get("resultType") != "vector"
                or not isinstance(payload["data"].get("result"), list)):
            raise ValueError(f"{kind}: expected successful, unannotated API vector")
        for row in payload["data"]["result"]:
            if (not isinstance(row, dict) or not isinstance(row.get("value"), list)
                    or len(row["value"]) != 2 or number(row["value"][0]) != evaluation):
                raise ValueError(f"{kind}: sample must match requested evaluation time")


def analyse(directory, manifest):
    args = ["bash", str(ENGINE)]
    for kind in QUERIES:
        args += ["--" + kind, str(directory / (kind + ".json"))]
    args += ["--expected", str(directory / "roles.json"),
             "--now", manifest["evaluation_time"], "--max-age", manifest["max_age"],
             "--min-attempts", manifest["min_attempts"]]
    if manifest["previous_manifest_sha256"] is not None:
        for kind in ("instant", "timestamps"):
            args += ["--previous-" + kind, str(directory / "previous" / (kind + ".json"))]
    if (directory / "attribution.json").exists():
        args += ["--attribution", str(directory / "attribution.json")]
    # Bind the recorded version to the executed bytes, even during a checkout update.
    with tempfile.TemporaryDirectory(prefix="phantom-analyser-") as temporary:
        pinned = Path(temporary) / ENGINE.name
        pinned.write_bytes(ENGINE_BYTES)
        args[1] = str(pinned)
        result = subprocess.run(args, capture_output=True, timeout=120)
    # Engine usage failures also use 2, but are not coverage reports.
    if result.returncode not in (0, 2) or result.stderr or not result.stdout.startswith(
            b"Cumulative counter snapshot, NOT a window increase or improvement measurement.\n"):
        raise ValueError("analyser rejected capture: " + result.stderr.decode(errors="replace"))
    return result


def roles_cover_previous(files, previous):
    current = decode(files["roles.json"])
    old = decode(previous["roles.json"])
    if not isinstance(current, list) or not isinstance(old, list):
        raise ValueError("roles must be arrays")
    # Carry forward the inventory too, including identities absent from every vector.
    labels = [entry["labels"] for entry in current]
    if any(entry["labels"] not in labels for entry in old):
        raise ValueError("missing explicit role for previous inventory identity")


def read_bundle(directory):
    if directory.is_symlink() or not directory.is_dir():
        raise ValueError("bundle must be a real directory")
    files = {}
    for path in directory.iterdir():
        if path.is_symlink():
            raise ValueError("bundle symlinks are forbidden")
        if path.name != "previous":
            if not path.is_file():
                raise ValueError("unexpected bundle entry")
            files[path.name] = path.read_bytes()
    raw = files.pop("manifest.json")
    checksum = files.pop("manifest.sha256")
    if checksum != (digest(raw) + "\n").encode():
        raise ValueError("manifest hash mismatch")
    manifest = decode(raw)
    required = {"version", "arguments", "evaluation_time", "analyser_sha256", "max_age",
                "min_attempts", "requests", "files", "previous_manifest_sha256", "exit_code"}
    if not isinstance(manifest, dict) or set(manifest) != required or manifest["version"] != 1:
        raise ValueError("unsupported or incomplete manifest")
    expected = {kind + ".json" for kind in QUERIES} | {"roles.json", "report.txt", "stderr.txt"}
    if "attribution.json" in manifest["files"]:
        expected.add("attribution.json")
    if set(files) != expected or set(manifest["files"]) != expected:
        raise ValueError("incomplete or unexpected bundle files")
    if manifest["files"] != {name: digest(data) for name, data in files.items()}:
        raise ValueError("bundle file hash mismatch")
    if manifest["analyser_sha256"] != digest(ENGINE_BYTES):
        raise ValueError("analyser version mismatch; use the recorded analyser revision")
    evaluation = number(manifest["evaluation_time"])
    number(manifest["max_age"])
    number(manifest["min_attempts"])
    if (not isinstance(manifest["arguments"], list)
            or not all(isinstance(arg, str) for arg in manifest["arguments"])
            or type(manifest["exit_code"]) is not int or manifest["exit_code"] not in (0, 2)
            or not isinstance(manifest["requests"], list) or len(manifest["requests"]) != 3):
        raise ValueError("invalid manifest metadata")
    for request, query in zip(manifest["requests"], QUERIES.values()):
        if (set(request) != {"query", "time", "url", "started_ns", "finished_ns"}
                or request["query"] != query or request["time"] != manifest["evaluation_time"]
                or urllib.parse.parse_qs(urllib.parse.urlsplit(request["url"]).query)
                != {"query": [query], "time": [manifest["evaluation_time"]]}
                or type(request["started_ns"]) is not int or type(request["finished_ns"]) is not int
                or not 0 < request["started_ns"] <= request["finished_ns"]):
            raise ValueError("invalid query/time metadata")
    validate_times(files, evaluation)
    previous = None
    if manifest["previous_manifest_sha256"] is not None:
        previous = read_bundle(directory / "previous")
        if digest(previous[1]["manifest.json"]) != manifest["previous_manifest_sha256"]:
            raise ValueError("previous manifest hash mismatch")
        if number(previous[0]["evaluation_time"]) >= evaluation:
            raise ValueError("previous evaluation must be strictly earlier")
        roles_cover_previous(files, previous[1])
    elif (directory / "previous").exists():
        raise ValueError("unexpected previous bundle")
    files.update({"manifest.json": raw, "manifest.sha256": checksum})
    # Analyse an immutable byte snapshot, not paths that could change after hashing.
    with tempfile.TemporaryDirectory(prefix="phantom-replay-") as temporary:
        snapshot = Path(temporary)
        write_tree(snapshot, (manifest, files, previous))
        result = analyse(snapshot, manifest)
    if (result.stdout != files["report.txt"] or result.stderr != files["stderr.txt"]
            or result.returncode != manifest["exit_code"]):
        raise ValueError("bundle report/exit code does not reproduce")
    return manifest, files, previous


def write_tree(directory, bundle):
    _, files, previous = bundle
    for name, data in files.items():
        (directory / name).write_bytes(data)
    if previous is not None:
        (directory / "previous").mkdir()
        write_tree(directory / "previous", previous)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        raise ValueError("HTTP redirects are not accepted")


def publish(staging, destination):
    # rename() can replace an EMPTY existing directory. Linux RENAME_NOREPLACE
    # atomically refuses even a destination created after our initial check.
    libc = ctypes.CDLL(None, use_errno=True)
    rename = libc.renameat2
    rename.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint]
    rename.restype = ctypes.c_int
    if rename(-100, os.fsencode(staging), -100, os.fsencode(destination), 1):
        code = ctypes.get_errno()
        raise OSError(code, os.strerror(code), str(destination))


def main():
    parser = argparse.ArgumentParser(description="Read-only pinned playback capture and verified offline replay (Linux).")
    commands = parser.add_subparsers(dest="command", required=True)
    capture = commands.add_parser("capture")
    capture.add_argument("--endpoint", required=True, help="Prometheus base URL, without credentials/query/fragment")
    capture.add_argument("--time", required=True, help="explicit decimal Unix evaluation time")
    capture.add_argument("--roles", required=True, help="explicit full-label serving/inactive inventory JSON")
    capture.add_argument("--output", required=True, help="new bundle directory; parent must exist")
    capture.add_argument("--previous", help="verified previous capture bundle (omitted for first baseline)")
    capture.add_argument("--attribution", help="optional caller receipts; never inferred from roles")
    capture.add_argument("--max-age", default="120")
    capture.add_argument("--min-attempts", default="100")
    capture.add_argument("--timeout", default="30", help="HTTP timeout in seconds")
    commands.add_parser("replay").add_argument("bundle")
    args = parser.parse_args()
    if args.command == "replay":
        manifest, files, _ = read_bundle(Path(args.bundle))
        sys.stdout.buffer.write(files["report.txt"])
        sys.stderr.buffer.write(files["stderr.txt"])
        return manifest["exit_code"]
    evaluation = number(args.time)
    number(args.max_age)
    number(args.min_attempts)
    timeout = number(args.timeout)
    if not 0 < timeout <= 3600:
        raise ValueError("timeout must be > 0 and <= 3600 seconds")
    endpoint = urllib.parse.urlsplit(args.endpoint)
    if (endpoint.scheme not in ("http", "https") or not endpoint.hostname
            or endpoint.username is not None or endpoint.password is not None
            or endpoint.query or endpoint.fragment):
        raise ValueError("expected HTTP(S) base URL without credentials/query/fragment")
    destination = Path(args.output).absolute()
    if os.path.lexists(destination):
        raise ValueError("destination already exists")
    files = {"roles.json": Path(args.roles).read_bytes()}
    if args.attribution:
        files["attribution.json"] = Path(args.attribution).read_bytes()
    previous = read_bundle(Path(args.previous)) if args.previous else None
    if previous is not None:
        if number(previous[0]["evaluation_time"]) >= evaluation:
            raise ValueError("previous evaluation must be strictly earlier")
        roles_cover_previous(files, previous[1])
    manifest = {"version": 1, "arguments": sys.argv[1:], "evaluation_time": args.time,
                "analyser_sha256": digest(ENGINE_BYTES), "max_age": args.max_age,
                "min_attempts": args.min_attempts, "requests": [],
                "previous_manifest_sha256": digest(previous[1]["manifest.json"]) if previous else None}
    opener = urllib.request.build_opener(NoRedirect())
    with tempfile.TemporaryDirectory(prefix=".phantom-capture-", dir=destination.parent) as temporary:
        staging = Path(temporary)
        for kind, query in QUERIES.items():
            url = args.endpoint.rstrip("/") + "/api/v1/query?" + urllib.parse.urlencode(
                {"query": query, "time": args.time})
            started = time.time_ns()
            with opener.open(url, timeout=float(timeout)) as response:
                if response.status != 200:
                    raise ValueError(f"HTTP status {response.status}")
                files[kind + ".json"] = response.read()
            manifest["requests"].append({"query": query, "time": args.time, "url": url,
                                         "started_ns": started, "finished_ns": time.time_ns()})
        validate_times(files, evaluation)
        write_tree(staging, (manifest, files, previous))
        result = analyse(staging, manifest)
        files.update({"report.txt": result.stdout, "stderr.txt": result.stderr})
        manifest["exit_code"] = result.returncode
        manifest["files"] = {name: digest(data) for name, data in files.items()}
        files["manifest.json"] = (json.dumps(manifest, indent=2) + "\n").encode()
        files["manifest.sha256"] = (digest(files["manifest.json"]) + "\n").encode()
        for name, data in files.items():
            (staging / name).write_bytes(data)
        # Apply replay's whole-bundle validation before making anything visible.
        read_bundle(staging)
        for directory, _, names in os.walk(staging, topdown=False):
            for name in names:
                with (Path(directory) / name).open("rb") as stream:
                    os.fsync(stream.fileno())
            descriptor = os.open(directory, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(descriptor)
            finally:
                os.close(descriptor)
        publish(staging, destination)
        descriptor = os.open(destination.parent, os.O_RDONLY | os.O_DIRECTORY)
        try:
            os.fsync(descriptor)
        finally:
            os.close(descriptor)
    sys.stdout.buffer.write(result.stdout)
    sys.stderr.buffer.write(result.stderr)
    return result.returncode


def interrupted(signum, frame):
    raise InterruptedError("capture interrupted")


signal.signal(signal.SIGTERM, interrupted)
try:
    sys.exit(main())
except (ValueError, InvalidOperation, OSError, KeyError, TypeError, AttributeError,
        http.client.HTTPException, urllib.error.URLError, subprocess.SubprocessError,
        RecursionError, KeyboardInterrupt) as error:
    print(f"phantom-playback-outcome-capture: {error}", file=sys.stderr)
    sys.exit(1)
PY
