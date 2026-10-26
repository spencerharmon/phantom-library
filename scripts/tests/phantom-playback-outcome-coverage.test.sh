#!/usr/bin/env bash
set -euo pipefail
exec python3 - "${BASH_SOURCE[0]}" <<'PY'
import copy
import itertools
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

root = Path(sys.argv[1]).resolve().parents[2]
engine = root / "scripts/phantom-playback-outcome-coverage.sh"
assert os.access(engine, os.X_OK), f"missing executable: {engine}"
subprocess.run(["bash", "-n", str(engine)], check=True)
flows = ("materialise_then_play", "play_already_materialised")
items = ("movie", "episode")
causes = ("success", "availability_abstain", "no_candidate", "magnet_dead_stale",
          "gostream_register_fail", "gostream_cannot_fetch", "first_byte_timeout", "plugin_host_error")
expected = [{"labels": {"instance": "serving.example.com", "job": "playback"}, "role": "serving"},
            {"labels": {"instance": "inactive.example.com", "job": "playback"}, "role": "inactive"}]


def rows(identity=0):
    return [{"metric": dict(expected[identity]["labels"], __name__="phantom_playback_outcome_total",
                            flow=flow, item_type=item, cause=cause), "value": [1000, "0"]}
            for flow, item, cause in itertools.product(flows, items, causes)]


def vector(records):
    return {"status": "success", "data": {"resultType": "vector", "result": records}}


def fixture(current=None, window=None):
    current = rows() if current is None else current
    stamps = copy.deepcopy(current)
    for row in stamps:
        row["metric"].pop("__name__", None)
        row["value"][1] = "990"
    return {"instant": vector(current), "window": vector(window or []),
            "timestamps": vector(stamps), "expected": copy.deepcopy(expected)}


cases = []


def case(name, data, code, contains, excludes=(), options=(), stdin=None):
    cases.append((name, data, code, contains, excludes, options, stdin))


case("fresh zeros", fixture(), 0,
     ["coverage=32/32", "sample_age_seconds=10", "attempts=0 errors=0 error_rate=undefined (no observed attempts)",
      "no organic traffic yet observed"], ["error_rate=0", "cause_ranking="])
case("serving absent, inactive zeros", fixture(rows(1)), 2,
     ["role=serving status=UNINSTRUMENTED coverage=0/32", "role=inactive status=OK coverage=32/32"],
     ["error_rate=0"])
case("no series anywhere", fixture([]), 2, ["UNINSTRUMENTED", "ABSENT (inactive)"])
historical = rows(1)[:1]
historical[0]["metric"]["cause"] = "first_byte_timeout"
historical[0]["value"][1] = "19"
case("window-only failures are not improvement", fixture(window=historical), 0,
     ["aged-out/stale", "window_max=19; not improvement; excluded from rate/ranking", "attempts=0 errors=0"],
     ["cause_ranking=first_byte_timeout"])
case("window does not instrument serving", fixture(rows(1), rows()), 2, ["UNINSTRUMENTED"])

for flow, item in itertools.product(flows, items):
    positive = rows()
    for row in positive:
        metric = row["metric"]
        if (metric["flow"], metric["item_type"]) == (flow, item):
            row["value"][1] = {"success": "90", "first_byte_timeout": "7", "no_candidate": "3"}.get(metric["cause"], "0")
    case(f"positive rate/parity {flow}/{item}", fixture(positive), 0,
         ["attempts=100 errors=10 error_rate=10.000000%", "cause_ranking=first_byte_timeout:7, no_candidate:3"],
         options=("--min-attempts", "99"))
    case(f"threshold boundary {flow}/{item}", fixture(positive), 0,
         ["error_rate=10.000000%", "insufficient attributed volume"], ["cause_ranking=first_byte_timeout"],
         options=("--min-attempts", "100"))
    case(f"below threshold {flow}/{item}", fixture(positive), 0,
         ["error_rate=10.000000%", "ranking suppressed: attempts <= 101"], ["cause_ranking=first_byte_timeout"],
         options=("--min-attempts", "101"))
    incomplete = [r for r in rows() if not (r["metric"]["flow"] == flow and r["metric"]["item_type"] == item)]
    case(f"incomplete parity {flow}/{item}", fixture(incomplete), 2,
         ["status=INCOMPLETE coverage=24/32", "missing_tuples=", "error_rate=unavailable"], ["cause_ranking="])

inactive = rows(1)
inactive[0]["value"][1] = "10000"
case("inactive volume cannot unlock serving ranking", fixture(rows() + inactive), 0,
     ["attempts=10000 errors=0 error_rate=0.000000%", "no organic traffic yet observed",
      "withheld (inactive; not attributed serving traffic)"], ["cause_ranking=none"])
two_serving = fixture(rows() + rows(1))
two_serving["expected"][1]["role"] = "serving"
two_serving["instant"]["data"]["result"][1]["value"][1] = "101"
two_serving["instant"]["data"]["result"][34]["value"][1] = "5"
case("two serving instances keep separate volume and ranking", two_serving, 0,
     ["attempts=101 errors=101 error_rate=100.000000%", "cause_ranking=availability_abstain:101",
      "attempts=5 errors=5 error_rate=100.000000%", "insufficient attributed volume"],
     ["no_candidate:5", "attempts=106", "no organic traffic yet observed"])
stale = fixture()
stale["timestamps"]["data"]["result"][0]["value"][1] = "879"
case("fresh evaluation cannot hide stale sample", stale, 2, ["status=STALE", "sample_age_seconds=121", "error_rate=unavailable"])
boundary = fixture()
boundary["timestamps"]["data"]["result"][0]["value"][1] = "880"
case("freshness boundary inclusive", boundary, 0, ["status=OK", "sample_age_seconds=120"])
case("old capture", fixture(), 2, ["STALE capture evaluation", "error_rate=unavailable"], options=("--now", "2000"))
for kind in ("instant", "window", "timestamps"):
    duplicate = fixture(window=rows())
    duplicate[kind]["data"]["result"].append(copy.deepcopy(duplicate[kind]["data"]["result"][0]))
    case(f"duplicate {kind}", duplicate, 1, [f"{kind}: duplicate tuple"])
    failed = fixture()
    failed[kind]["status"] = "error"
    case(f"API failure {kind}", failed, 1, ["expected successful, unannotated API vector"])

for label, value in (("cause", "unknown"), ("flow", "materialise_then_play_extra"), ("item_type", "film")):
    invalid = fixture()
    invalid["instant"]["data"]["result"][0]["metric"][label] = value
    case(f"exact vocabulary {label}", invalid, 1, ["noncanonical tuple"])
for value in ("NaN", "+Inf", "-1", "0.5", "garbage", True):
    invalid = fixture()
    invalid["instant"]["data"]["result"][0]["value"][1] = value
    case(f"invalid counter {value}", invalid, 1, ["phantom-playback-outcome-coverage:"])
for kind in ("instant", "timestamps"):
    future = fixture()
    future[kind]["data"]["result"][0]["value"][0 if kind == "instant" else 1] = 1001
    case(f"future {kind}", future, 1, ["future timestamp"])
for family in ("phantom_runtime_info", "phantom_loadtime_rig_outcome_total", "phantom_playback_outcome_total_extra"):
    invalid = fixture()
    invalid["instant"]["data"]["result"][0]["metric"]["__name__"] = family
    case(f"wrong family {family}", invalid, 1, ["expected phantom_playback_outcome_total family"])
for kind in ("instant", "window", "timestamps"):
    extra = fixture(window=rows())
    extra[kind]["data"]["result"][0]["metric"]["other_job"] = "unexpected"
    case(f"extra identity label {kind}", extra, 1, ["unlisted identity/extra labels"])
missing_timestamp = fixture()
missing_timestamp["timestamps"]["data"]["result"].pop()
case("missing sample timestamp", missing_timestamp, 1, ["timestamp coverage must exactly match"])
mismatched = fixture()
mismatched["timestamps"]["data"]["result"][0]["value"][0] = 999
case("different evaluation times", mismatched, 1, ["one common Prometheus evaluation time"])
warned = fixture()
warned["instant"]["warnings"] = ["partial data"]
case("partial API warning", warned, 1, ["expected successful, unannotated API vector"])
case("stdin instant", fixture(), 0, ["error_rate=undefined (no observed attempts)"], stdin="instant")
case("stdin expected", fixture(), 0, ["error_rate=undefined (no observed attempts)"], stdin="expected")
bad_expected = fixture()
bad_expected["expected"].append(copy.deepcopy(expected[0]))
case("duplicate expected", bad_expected, 1, ["duplicate expected identity"])
case("bad option", fixture(), 1, ["expected finite nonnegative number"], options=("--max-age", "-1"))
case("truncated JSON", dict(fixture(), instant='{"status":'), 1, ["phantom-playback-outcome-coverage:"])
case("duplicate JSON label", dict(fixture(), instant='{"status":"success","status":"success"}'), 1, ["duplicate JSON key"])

with tempfile.TemporaryDirectory(prefix="outcome-coverage-") as temp:
    directory = Path(temp)
    stub = directory / "zero-percent-stub.sh"
    stub.write_text("#!/bin/sh\nprintf '0%%\\n'\n")
    stub.chmod(0o755)
    for name, data, code, contains, excludes, options, stdin in cases:
        args = ["--now", "1000", "--max-age", "120", "--min-attempts", "100"]
        originals = {}
        for kind, payload in data.items():
            path = directory / f"{kind}.json"
            text = payload if isinstance(payload, str) else json.dumps(payload)
            path.write_text(text)
            originals[path] = path.read_bytes()
            args.extend([f"--{kind}", "-" if stdin == kind else str(path)])
        args.extend(options)
        result = subprocess.run([str(engine), *args], input=originals[directory / f"{stdin}.json"].decode() if stdin else "",
                                text=True, capture_output=True, timeout=10)
        output = result.stdout + result.stderr
        assert result.returncode == code, (name, result.returncode, code, output)
        assert all(s in output for s in contains), (name, contains, output)
        assert not any(s in output for s in excludes), (name, excludes, output)
        assert all(path.read_bytes() == original for path, original in originals.items()), "input mutated"
        fake = subprocess.run([str(stub), *args], text=True, capture_output=True, check=True)
        assert not (fake.returncode == code and all(s in fake.stdout for s in contains)), f"stub survived: {name}"
        print(f"PASS {name}")
print(f"PASS: {len(cases)} offline coverage cases; 0%-stub rejected by every case; inputs unchanged")
PY
