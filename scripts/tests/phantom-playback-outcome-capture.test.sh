#!/usr/bin/env bash
set -euo pipefail
exec python3 - "${BASH_SOURCE[0]}" <<'PY'
import copy
from decimal import Decimal
import hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import itertools
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import threading
import unittest
from urllib.parse import parse_qs, urlsplit

ROOT = Path(sys.argv.pop(1)).resolve().parents[2]
CAPTURE = ROOT / "scripts/phantom-playback-outcome-capture.sh"
ENGINE = ROOT / "scripts/phantom-playback-outcome-coverage.sh"
TIME = "1000.2500"
EARLIER = "600.1250"
METRIC = "phantom_playback_outcome_total"
QUERIES = {"instant": METRIC, "window": f"max_over_time({METRIC}[30d])",
           "timestamps": f"timestamp({METRIC})"}
FLOWS = ("materialise_then_play", "play_already_materialised")
ITEMS = ("movie", "episode")
CAUSES = ("success", "availability_abstain", "no_candidate", "magnet_dead_stale",
          "gostream_register_fail", "gostream_cannot_fetch", "first_byte_timeout", "plugin_host_error")
SERVING = {"instance": "serving.example.com", "job": "playback", "replica": "one"}
PEER = dict(SERVING, replica="two")
ENV = dict(os.environ, NO_PROXY="127.0.0.1,localhost", no_proxy="127.0.0.1,localhost")


def raw(value):
    # Deliberately noncanonical JSON: parsing and reserializing loses evidence.
    return (" \n" + json.dumps(value, indent=3, ensure_ascii=True) + "\n\t").encode()


def digest(value):
    return hashlib.sha256(value).hexdigest()


def inventory(*identities):
    return [{"labels": copy.deepcopy(labels), "role": role} for labels, role in identities]


def rows(labels=SERVING, evaluation=TIME, count=10):
    return [{"metric": dict(labels, __name__=METRIC, flow=flow, item_type=item, cause=cause),
             "value": [float(evaluation), str(count + index)]}
            for index, (flow, item, cause) in enumerate(itertools.product(FLOWS, ITEMS, CAUSES))]


def fixture(current=None, window=None, evaluation=TIME, source="990.25"):
    current = rows(evaluation=evaluation) if current is None else copy.deepcopy(current)
    window = copy.deepcopy(current if window is None else window)
    stamps = copy.deepcopy(current)
    for row in window:
        row["metric"].pop("__name__", None)
    for row in stamps:
        row["metric"].pop("__name__", None)
        row["value"][1] = source
    return {kind: raw({"status": "success", "data": {"resultType": "vector", "result": records}})
            for kind, records in (("instant", current), ("window", window), ("timestamps", stamps))}


def receipt(scope="cumulative", **changes):
    value = {"labels": copy.deepcopy(SERVING), "scope": scope,
             "attribution": "organic-serving", "source_end": "990.25"}
    if scope == "interval":
        value["source_start"] = "590.125"
    value.update(changes)
    return value


def snapshot(directory):
    return {str(path.relative_to(directory)): path.read_bytes()
            for path in directory.rglob("*") if path.is_file()}


class Prometheus:
    def __init__(self, payloads, fault=None, target="timestamps"):
        self.payloads = payloads
        self.fault = fault
        self.target = target
        self.requests = []
        self.stalled = threading.Event()
        self.release = threading.Event()
        owner = self

        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass

            def do_GET(self):
                parsed = urlsplit(self.path)
                params = parse_qs(parsed.query, keep_blank_values=True)
                owner.requests.append((parsed.path, params, self.path))
                kind = next((kind for kind, query in QUERIES.items()
                             if params.get("query") == [query]), None)
                if parsed.path != "/prometheus/api/v1/query" or kind is None:
                    self.send_error(404)
                    return
                body = owner.payloads[kind]
                status = 200
                truncated = False
                if kind == owner.target:
                    if owner.fault == "stall":
                        owner.stalled.set()
                        owner.release.wait(15)
                    elif owner.fault == "http":
                        status, body = 503, b"upstream unavailable\n"
                    elif owner.fault == "api":
                        body = raw({"status": "error", "errorType": "execution", "error": "failed"})
                    elif owner.fault in ("warnings", "infos"):
                        value = json.loads(body)
                        value[owner.fault] = ["partial data"]
                        body = raw(value)
                    elif owner.fault == "malformed":
                        body = b'{"status":'
                    elif owner.fault == "duplicate":
                        body = body.replace(b'"status": "success"',
                                            b'"status": "success", "status": "success"', 1)
                    elif owner.fault == "duplicate-label":
                        body = body.replace(b'"job": "playback"',
                                            b'"job": "playback", "job": "playback"', 1)
                    elif owner.fault == "truncated":
                        # A complete JSON value is still an invalid HTTP body when
                        # Content-Length promises bytes the peer never sends.
                        truncated = True
                self.send_response(status)
                self.send_header("Content-Type", "application/json")
                self.send_header("Content-Length", str(len(body) + (100 if truncated else 0)))
                self.end_headers()
                try:
                    self.wfile.write(body)
                    self.wfile.flush()
                except (BrokenPipeError, ConnectionResetError):
                    pass  # Expected when our timeout/SIGTERM test closes its client.
                self.close_connection = True

        self.server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.server.daemon_threads = True
        self.endpoint = f"http://127.0.0.1:{self.server.server_port}/prometheus"
        self.thread = threading.Thread(target=self.server.serve_forever,
                                       kwargs={"poll_interval": 0.01}, daemon=True)

    def __enter__(self):
        self.thread.start()
        return self

    def __exit__(self, *args):
        self.release.set()
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)


class CaptureTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        for script in (ENGINE, CAPTURE):
            if not os.access(script, os.X_OK):
                raise AssertionError(f"missing executable: {script}")
            subprocess.run(["bash", "-n", str(script)], check=True)

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="outcome-capture-")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.sequence = 0

    def path(self, name):
        self.sequence += 1
        return self.directory / f"{self.sequence}-{name}"

    def invoke(self, args, cwd=None, timeout=10):
        return subprocess.run([str(CAPTURE), *map(str, args)], input=b"", capture_output=True,
                              cwd=cwd or self.directory, env=ENV, timeout=timeout)

    def inputs(self, payloads, roles, attribution=None):
        directory = self.path("inputs")
        directory.mkdir()
        for kind, body in payloads.items():
            (directory / f"{kind}.json").write_bytes(body)
        (directory / "roles.json").write_bytes(roles if isinstance(roles, bytes) else raw(roles))
        if attribution is not None:
            (directory / "attribution.json").write_bytes(
                attribution if isinstance(attribution, bytes) else raw(attribution))
        return directory

    def arguments(self, server, inputs, output, evaluation=TIME, previous=None, options=()):
        args = ["capture", "--endpoint", server.endpoint, "--time", evaluation,
                "--roles", str(inputs / "roles.json"), "--output", str(output)]
        if previous is not None:
            args += ["--previous", str(previous)]
        if (inputs / "attribution.json").exists():
            args += ["--attribution", str(inputs / "attribution.json")]
        return args + list(options)

    def analyse(self, inputs, evaluation=TIME, previous=None, max_age="120", minimum="100"):
        args = [str(ENGINE), "--now", evaluation, "--max-age", max_age, "--min-attempts", minimum,
                "--expected", str(inputs / "roles.json")]
        for kind in QUERIES:
            args += [f"--{kind}", str(inputs / f"{kind}.json")]
        if previous is not None:
            args += ["--previous-instant", str(previous / "instant.json"),
                     "--previous-timestamps", str(previous / "timestamps.json")]
        if (inputs / "attribution.json").exists():
            args += ["--attribution", str(inputs / "attribution.json")]
        return subprocess.run(args, input=b"", capture_output=True, env=ENV, timeout=10)

    def same_result(self, actual, expected):
        self.assertEqual((actual.returncode, actual.stdout, actual.stderr),
                         (expected.returncode, expected.stdout, expected.stderr))

    def verify_bundle(self, bundle, inputs, args, result, server, previous=None,
                      max_age="120", minimum="100"):
        originals = snapshot(inputs)
        expected_names = set(originals) | {"report.txt", "stderr.txt", "manifest.json", "manifest.sha256"}
        self.assertEqual({p.name for p in bundle.iterdir()},
                         expected_names | ({"previous"} if previous is not None else set()))
        for name, body in originals.items():
            self.assertEqual((bundle / name).read_bytes(), body, name)
        self.assertEqual((bundle / "report.txt").read_bytes(), result.stdout)
        self.assertEqual((bundle / "stderr.txt").read_bytes(), result.stderr)
        manifest_bytes = (bundle / "manifest.json").read_bytes()
        manifest = json.loads(manifest_bytes)
        self.assertEqual(manifest["version"], 1)
        self.assertEqual(manifest["arguments"], args)
        self.assertEqual(manifest["evaluation_time"], args[args.index("--time") + 1])
        self.assertEqual(manifest["analyser_sha256"], digest(ENGINE.read_bytes()))
        self.assertEqual(Decimal(str(manifest["max_age"])), Decimal(max_age))
        self.assertEqual(Decimal(str(manifest["min_attempts"])), Decimal(minimum))
        self.assertEqual(manifest["files"], {name: digest((bundle / name).read_bytes())
                         for name in expected_names - {"manifest.json", "manifest.sha256"}})
        # The contract does not prescribe bare-digest vs sha256sum line syntax.
        checksum = (bundle / "manifest.sha256").read_text().split()
        self.assertIn(checksum, ([digest(manifest_bytes)], [digest(manifest_bytes), "manifest.json"],
                                 [digest(manifest_bytes), "*manifest.json"]))
        self.assertEqual(manifest["previous_manifest_sha256"],
                         digest((previous / "manifest.json").read_bytes()) if previous else None)
        if previous is not None:
            self.assertEqual(snapshot(bundle / "previous"), snapshot(previous))
        self.assertEqual(len(server.requests), 3)
        self.assertCountEqual([p[1]["query"][0] for p in server.requests], list(QUERIES.values()))
        self.assertEqual(len(manifest["requests"]), 3)
        self.assertCountEqual([r["query"] for r in manifest["requests"]], list(QUERIES.values()))
        for path, params, target in server.requests:
            self.assertEqual(path, "/prometheus/api/v1/query")
            self.assertEqual(params["time"], [manifest["evaluation_time"]])
            record = next(r for r in manifest["requests"] if r["query"] == params["query"][0])
            self.assertEqual(record["time"], manifest["evaluation_time"])
            self.assertEqual(record["url"], server.endpoint.removesuffix("/prometheus") + target)
            self.assertIs(type(record["started_ns"]), int)
            self.assertIs(type(record["finished_ns"]), int)
            self.assertGreater(record["started_ns"], 0)
            self.assertGreaterEqual(record["finished_ns"], record["started_ns"])

    def capture(self, payloads=None, roles=None, code=0, evaluation=TIME, previous=None,
                attribution=None, max_age="120", minimum="100", explicit=False):
        payloads = fixture() if payloads is None else payloads
        roles = inventory((SERVING, "serving")) if roles is None else roles
        inputs = self.inputs(payloads, roles, attribution)
        original_inputs = snapshot(inputs)
        old = snapshot(previous) if previous else None
        oracle = self.analyse(inputs, evaluation, previous, max_age, minimum)
        self.assertEqual(oracle.returncode, code, oracle.stderr + oracle.stdout)
        output = self.path("bundle with spaces")
        options = ("--max-age", max_age, "--min-attempts", minimum, "--timeout", "30") if explicit else ()
        with Prometheus(payloads) as server:
            args = self.arguments(server, inputs, output, evaluation, previous, options)
            result = self.invoke(args)
        self.same_result(result, oracle)
        self.verify_bundle(output, inputs, args, result, server, previous, max_age, minimum)
        self.assertEqual(snapshot(inputs), original_inputs)
        if previous:
            self.assertEqual(snapshot(previous), old, "capture changed prior evidence")
        before = snapshot(output)
        # This is another process, with the original endpoint already shut down.
        self.same_result(self.invoke(["replay", output]), oracle)
        self.assertEqual(snapshot(output), before, "replay changed evidence")
        return output, result

    def invalid(self, payloads=None, roles=None, previous=None, attribution=None,
                fault=None, target="timestamps", options=(), engine_invalid=False):
        payloads = fixture() if payloads is None else payloads
        roles = inventory((SERVING, "serving")) if roles is None else roles
        inputs = self.inputs(payloads, roles, attribution)
        old_inputs = snapshot(inputs)
        old = snapshot(previous) if previous else None
        output = self.path("invalid-output")
        if engine_invalid:
            oracle = self.analyse(inputs, previous=previous)
            self.assertEqual(oracle.returncode, 1, oracle.stdout + oracle.stderr)
        with Prometheus(payloads, fault, target) as server:
            result = self.invoke(self.arguments(server, inputs, output, previous=previous, options=options))
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertTrue(result.stderr, "invalid capture needs a diagnostic")
        self.assertFalse(os.path.lexists(output), "invalid capture published a destination")
        self.assertEqual(snapshot(inputs), old_inputs)
        if previous:
            self.assertEqual(snapshot(previous), old, "invalid capture changed prior evidence")
        return result

    def test_all_32_tuples_full_identity_and_no_invented_attribution(self):
        bundle, result = self.capture(fixture(rows() + rows(PEER, count=100)),
                                      inventory((SERVING, "serving"), (PEER, "serving")))
        self.assertEqual(result.stdout.count(b"coverage=32/32"), 2)
        for temperature, item in itertools.product(("cold", "warm"), ITEMS):
            self.assertEqual(result.stdout.count(f"  slice[{temperature}-{item}]".encode()), 2)
        self.assertEqual(result.stdout.count(b"cumulative attribution=unattributed"), 2)
        self.assertNotIn(b"attribution=organic-serving", result.stdout)
        self.assertNotIn(b"no organic traffic", result.stdout)
        self.assertNotIn("attribution.json", snapshot(bundle))

    def test_empty_vectors_retain_uninstrumented_serving_inventory(self):
        for current in ([], rows(PEER)):
            with self.subTest(inactive_instrumented=bool(current)):
                bundle, result = self.capture(fixture(current, []),
                    inventory((SERVING, "serving"), (PEER, "inactive")), code=2)
                self.assertIn(b"role=serving status=UNINSTRUMENTED coverage=0/32", result.stdout)
                self.assertEqual(len(json.loads((bundle / "roles.json").read_bytes())), 2)

    def test_unhealthy_coverage_is_published_and_replayed(self):
        for name, payloads, diagnostic in (
                ("incomplete", fixture(rows()[:-1]), b"status=INCOMPLETE"),
                ("stale", fixture(source="800"), b"status=STALE"),
                ("counter-reset", fixture(window=rows(count=1000)), b"COUNTER_DISCONTINUITY")):
            with self.subTest(name=name):
                _, result = self.capture(payloads, code=2)
                self.assertIn(diagnostic, result.stdout)

    def test_previous_and_eight_rolled_window_only_identities(self):
        rolled = [dict(SERVING, replica=f"rolled-{index}") for index in range(8)]
        gone = dict(SERVING, replica="previous-only")
        prior_window = dict(SERVING, replica="prior-window-only")
        absent = dict(SERVING, replica="prior-inventory-only")
        roles = inventory((SERVING, "serving"), (gone, "inactive"), (prior_window, "inactive"),
                          (absent, "inactive"), *((labels, "inactive") for labels in rolled))
        previous, _ = self.capture(
            fixture(rows(evaluation=EARLIER) + rows(gone, EARLIER),
                    rows(prior_window, EARLIER), source="590.125"), roles, evaluation=EARLIER)
        payloads = fixture(rows(count=100), list(itertools.chain.from_iterable(rows(who) for who in rolled)))
        bundle, result = self.capture(payloads, roles, code=2, previous=previous)
        self.assertIn(b"INTERVAL_DISCONTINUITY", result.stdout)
        self.assertIn(b"interval aggregate(", result.stdout)
        for labels in rolled:
            self.assertIn(labels["replica"].encode(), result.stdout)
        self.assertIn(b"aged-out/stale", result.stdout)
        self.assertEqual(snapshot(bundle / "previous"), snapshot(previous))
        # Require the current inventory, not a silently merged previous role.
        for omitted in (SERVING, rolled[0], gone, prior_window, absent):
            with self.subTest(missing_role=omitted["replica"]):
                self.invalid(payloads, [r for r in roles if r["labels"] != omitted], previous=previous)

    def test_missing_current_and_window_roles_and_extra_label(self):
        self.invalid(roles=inventory((PEER, "serving")), engine_invalid=True)
        self.invalid(fixture(window=rows(PEER)), engine_invalid=True)
        partial = dict(SERVING)
        del partial["replica"]
        self.invalid(roles=inventory((partial, "serving")), engine_invalid=True)

    def test_nested_previous_and_relocated_offline_replay(self):
        first, _ = self.capture(fixture(rows(evaluation="200.125"), source="190.125"),
                                evaluation="200.125")
        second, _ = self.capture(fixture(rows(evaluation=EARLIER, count=100), source="590.125"),
                                 evaluation=EARLIER, previous=first)
        third, result = self.capture(fixture(rows(count=200)), previous=second)
        self.assertEqual(snapshot(third / "previous" / "previous"), snapshot(first))
        detached = self.path("offline")
        detached.mkdir()
        moved = detached / "evidence"
        shutil.move(str(third), moved)
        for path in list(self.directory.iterdir()):
            if path != detached:
                shutil.rmtree(path)
        before = snapshot(moved)
        self.same_result(self.invoke(["replay", "evidence"], cwd=detached), result)
        self.assertEqual(snapshot(moved), before)

    def test_explicit_thresholds_and_exact_caller_receipts(self):
        previous, _ = self.capture(fixture(rows(evaluation=EARLIER), source="590.125"),
                                   evaluation=EARLIER)
        receipts = raw([receipt(), receipt("interval")])
        bundle, result = self.capture(fixture(rows(count=100)), previous=previous,
                                      attribution=receipts, max_age="60", minimum="25", explicit=True)
        self.assertEqual((bundle / "attribution.json").read_bytes(), receipts)
        self.assertIn(b"cumulative attribution=organic-serving", result.stdout)
        self.assertIn(b"interval attribution=organic-serving", result.stdout)
        _, suppressed = self.capture(fixture(rows(count=100)), attribution=[receipt()],
                                     max_age="60", minimum="100000", explicit=True)
        self.assertIn(b"insufficient attributed volume", suppressed.stdout)
        for receipts in ([], [receipt(source_end="980")], [receipt(attribution="mixed")]):
            with self.subTest(receipts=receipts):
                _, result = self.capture(attribution=receipts)
                self.assertIn(b"cumulative attribution=unattributed", result.stdout)
                self.assertNotIn(b"attribution=organic-serving", result.stdout)

    def test_invalid_caller_receipts_fail_without_publication(self):
        for name, receipts in (
                ("not-array", {}), ("unknown-scope", [receipt(scope="window")]),
                ("duplicate", [receipt(), receipt()]),
                ("partial-identity", [receipt(labels={"instance": SERVING["instance"]})]),
                ("endpoint-role-inference", [receipt("interval", start_role="serving", end_role="serving")]),
                ("reversed-span", [receipt("interval", source_start="999")]),
                ("contradiction", [receipt(), receipt("interval", attribution="mixed")]),
                ("nonfinite", [receipt(source_end="NaN")]),
                ("duplicate-json-key", b'[{"scope":"cumulative","scope":"interval"}]')):
            with self.subTest(name=name):
                self.invalid(attribution=receipts, engine_invalid=True)

    def test_nondefault_max_age_changes_health_and_replay(self):
        _, result = self.capture(fixture(source="970.25"), code=2,
                                 max_age="20", minimum="100", explicit=True)
        self.assertIn(b"status=STALE", result.stdout)

    def test_transport_api_annotations_and_json_fail_closed_at_every_request(self):
        for target, fault in itertools.product(QUERIES, (
                "http", "api", "warnings", "infos", "malformed", "duplicate", "duplicate-label", "truncated")):
            with self.subTest(target=target, fault=fault):
                self.invalid(fault=fault, target=target)

    def test_evaluation_times_must_match_each_other_and_requested_decimal(self):
        for changed in (("instant",), ("window",), ("timestamps",), tuple(QUERIES)):
            with self.subTest(changed=changed):
                payloads = fixture()
                for kind in changed:
                    value = json.loads(payloads[kind])
                    for row in value["data"]["result"]:
                        row["value"][0] = 999.25
                    payloads[kind] = raw(value)
                # All three agreeing on the WRONG time passes the engine's
                # common-time check; the capture layer must still reject it.
                self.invalid(payloads)
        payloads = fixture()
        value = json.loads(payloads["instant"])
        value["data"]["result"][0]["value"][0] = 999.25
        payloads["instant"] = raw(value)
        self.invalid(payloads)

    def test_previous_and_replay_verify_every_hash_without_mutating_evidence(self):
        baseline, _ = self.capture(fixture(rows(evaluation=EARLIER), source="590.125"),
                                   evaluation=EARLIER, attribution=[receipt(source_end="590.125")])
        for name in snapshot(baseline):
            with self.subTest(tampered=name):
                corrupt = self.path("corrupt")
                shutil.copytree(baseline, corrupt)
                path = corrupt / name
                path.write_bytes(path.read_bytes() + b" ")
                # Whitespace is valid in JSON: only integrity verification can
                # detect these source changes before accepting a prior bundle.
                if name == "manifest.sha256":
                    path.write_bytes(b"0" * 64 + b"\n")
                before = snapshot(corrupt)
                result = self.invoke(["replay", corrupt])
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertTrue(result.stderr)
                self.assertEqual(snapshot(corrupt), before)
                self.invalid(previous=corrupt)
        missing = self.path("missing-source")
        shutil.copytree(baseline, missing)
        (missing / "timestamps.json").unlink()
        self.invalid(previous=missing)
        child, _ = self.capture(fixture(rows(count=100)), previous=baseline)
        (child / "previous" / "instant.json").write_bytes(b"{}")
        self.assertEqual(self.invoke(["replay", child]).returncode, 1)
        self.invalid(previous=child)

    def test_existing_empty_and_nonempty_destinations_are_untouched(self):
        for nonempty in (False, True):
            with self.subTest(nonempty=nonempty):
                output = self.path("existing")
                output.mkdir()
                if nonempty:
                    (output / "keep.txt").write_bytes(b"operator evidence\x00\n")
                before = snapshot(output)
                inputs = self.inputs(fixture(), inventory((SERVING, "serving")))
                with Prometheus(fixture()) as server:
                    result = self.invoke(self.arguments(server, inputs, output))
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertTrue(output.is_dir())
                self.assertEqual(snapshot(output), before)

    def test_timeout_does_not_publish_partial_capture(self):
        self.invalid(fault="stall", options=("--timeout", "1"))

    def test_clock_step_and_write_failure_do_not_publish(self):
        for fault in ("clock", "write"):
            with self.subTest(fault=fault):
                injection = self.path("python-startup")
                injection.mkdir()
                # Fault injection in this test's subprocess only; no shared files.
                source = ("import time\n"
                          "ticks = iter([200, 100, 300, 400, 500, 600])\n"
                          "time.time_ns = lambda: next(ticks)\n") if fault == "clock" else (
                          "from pathlib import Path\n"
                          "original = Path.write_bytes\n"
                          "def write(self, data):\n"
                          "    if self.name == 'window.json':\n"
                          "        original(self, data[:10])\n"
                          "        raise OSError('injected partial write')\n"
                          "    return original(self, data)\n"
                          "Path.write_bytes = write\n")
                (injection / "sitecustomize.py").write_text(source)
                output = self.path("failed")
                inputs = self.inputs(fixture(), inventory((SERVING, "serving")))
                with Prometheus(fixture()) as server:
                    result = subprocess.run([str(CAPTURE), *self.arguments(server, inputs, output)],
                        capture_output=True, env=dict(ENV, PYTHONPATH=str(injection)), timeout=10)
                self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
                self.assertFalse(os.path.lexists(output))

    def test_pinned_analyser_and_racing_destination(self):
        for race in ("analyser", "destination"):
            with self.subTest(race=race):
                tools = self.path("tools")
                tools.mkdir()
                script = tools / CAPTURE.name
                engine = tools / ENGINE.name
                shutil.copy2(CAPTURE, script)
                shutil.copy2(ENGINE, engine)
                output = self.path("racing")
                inputs = self.inputs(fixture(), inventory((SERVING, "serving")))
                oracle = self.analyse(inputs)
                with Prometheus(fixture(), "stall") as server:
                    process = subprocess.Popen([str(script), *self.arguments(server, inputs, output)],
                        stdout=subprocess.PIPE, stderr=subprocess.PIPE, env=ENV)
                    try:
                        self.assertTrue(server.stalled.wait(5))
                        if race == "analyser":
                            engine.write_bytes(b"#!/bin/bash\nexit 1\n")
                        else:
                            output.mkdir()
                        server.release.set()
                        stdout, stderr = process.communicate(timeout=10)
                        if race == "analyser":
                            self.assertEqual((process.returncode, stdout, stderr),
                                             (oracle.returncode, oracle.stdout, oracle.stderr))
                            manifest = json.loads((output / "manifest.json").read_bytes())
                            self.assertEqual(manifest["analyser_sha256"], digest(ENGINE.read_bytes()))
                            # The changed engine is refused; the recorded revision replays.
                            changed = subprocess.run([str(script), "replay", str(output)],
                                capture_output=True, env=ENV, timeout=10)
                            self.assertEqual(changed.returncode, 1)
                            self.same_result(self.invoke(["replay", output]), oracle)
                        else:
                            self.assertEqual(process.returncode, 1, stdout + stderr)
                            self.assertEqual(list(output.iterdir()), [])
                    finally:
                        if process.poll() is None:
                            process.kill()
                        server.release.set()
                        process.communicate(timeout=5)

    def test_sigterm_during_stalled_request_does_not_publish(self):
        output = self.path("interrupted")
        inputs = self.inputs(fixture(), inventory((SERVING, "serving")))
        with Prometheus(fixture(), "stall") as server:
            process = subprocess.Popen([str(CAPTURE), *self.arguments(server, inputs, output)],
                                       stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                                       stderr=subprocess.PIPE, env=ENV, start_new_session=True)
            try:
                self.assertTrue(server.stalled.wait(5), "capture never reached the stalled request")
                self.assertFalse(os.path.lexists(output), "capture published before requests completed")
                os.killpg(process.pid, signal.SIGTERM)
                stdout, stderr = process.communicate(timeout=5)
                self.assertIn(process.returncode, (1, -signal.SIGTERM, 128 + signal.SIGTERM),
                              (stdout, stderr))
                self.assertFalse(os.path.lexists(output), "SIGTERM left a published partial bundle")
            finally:
                # Signal only the test-owned process group, never shared services.
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                server.release.set()
                process.communicate(timeout=5)
        self.assertFalse(os.path.lexists(output))

    def test_invalid_cli_usage_never_publishes(self):
        inputs = self.inputs(fixture(), inventory((SERVING, "serving")))
        with Prometheus(fixture()) as server:
            for option, value in (("--time", "NaN"), ("--max-age", "-1"),
                                  ("--min-attempts", "NaN"), ("--timeout", "-1"),
                                  ("--unknown-option", "yes")):
                with self.subTest(option=option):
                    output = self.path("bad-cli")
                    args = self.arguments(server, inputs, output)
                    if option == "--time":
                        args[args.index(option) + 1] = value
                    else:
                        args += [option, value]
                    result = self.invoke(args)
                    self.assertIn(result.returncode, (1, 2), result.stdout + result.stderr)
                    self.assertFalse(os.path.lexists(output))
            output = self.path("missing-time")
            args = self.arguments(server, inputs, output)
            index = args.index("--time")
            del args[index:index + 2]
            result = self.invoke(args)
            self.assertIn(result.returncode, (1, 2))
            self.assertFalse(os.path.lexists(output))


unittest.main(verbosity=2)
PY
