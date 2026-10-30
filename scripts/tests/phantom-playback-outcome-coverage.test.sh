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


def case(name, data, code, contains, excludes=(), options=(), stdin=None, exact_slices=None):
    cases.append((name, data, code, contains, excludes, options, stdin, exact_slices))


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
raw_window = rows()
for row in raw_window:
    row["metric"].pop("__name__")
case("raw max_over_time window", fixture(window=raw_window), 0,
     ["coverage=32/32", "attempts=0 errors=0 error_rate=undefined (no observed attempts)"],
     ["aged-out/stale", "cause_ranking="])
raw_historical = copy.deepcopy(historical)
raw_historical[0]["metric"].pop("__name__")
case("raw window-only failures are not improvement", fixture(window=raw_historical), 0,
     ["aged-out/stale", "window_max=19; not improvement; excluded from rate/ranking", "attempts=0 errors=0"],
     ["cause_ranking=first_byte_timeout"])
case("raw window does not instrument serving", fixture(rows(1), raw_window), 2, ["UNINSTRUMENTED"])

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

slice_names = {("materialise_then_play", "movie"): "cold-movie", ("materialise_then_play", "episode"): "cold-episode",
               ("play_already_materialised", "movie"): "warm-movie", ("play_already_materialised", "episode"): "warm-episode"}


def slice_line(flow, item, attempts, errors, rate, decision):
    return (f"slice[{slice_names[flow, item]}] flow={flow} item_type={item} attempts={attempts} errors={errors}"
            f" error_rate={rate} slice_ranking={decision}")


undefined = "undefined (no observed attempts)"
for sparse in itertools.product(flows, items):
    dense = next(p for p in itertools.product(flows, items) if p[0] != sparse[0] and p[1] != sparse[1])
    mixed = rows()
    for row in mixed:
        m = row["metric"]
        if (m["flow"], m["item_type"], m["cause"]) == (*dense, "success"):
            row["value"][1] = "1000"
        if (m["flow"], m["item_type"], m["cause"]) == (*sparse, "magnet_dead_stale"):
            row["value"][1] = "2"
    lines = [slice_line(*dense, 1000, 0, "0.000000%", "none (no errors)"),
             slice_line(*sparse, 2, 2, "100.000000%", "suppressed (slice attempts <= 100)")]
    lines += [slice_line(f, i, 0, 0, undefined, "suppressed (slice attempts <= 100)")
              for f, i in itertools.product(flows, items) if (f, i) not in (dense, sparse)]
    case(f"mixed population sparse {slice_names[sparse]}", fixture(mixed), 0,
         ["aggregate(all flows/items) attempts=1002 errors=2 error_rate=0.199601%",
          "cause_ranking=magnet_dead_stale:2", *lines],
         ["slice_ranking=magnet_dead_stale", "error_rate=0.000000% slice_ranking=suppressed"],
         exact_slices=1)
    above = copy.deepcopy(mixed)
    for row in above:
        m = row["metric"]
        if (m["flow"], m["item_type"]) == sparse:
            row["value"][1] = {"magnet_dead_stale": "60", "no_candidate": "41"}.get(m["cause"], "0")
    case(f"slice strictly above threshold {slice_names[sparse]}", fixture(above), 0,
         [slice_line(*sparse, 101, 101, "100.000000%", "magnet_dead_stale:60, no_candidate:41")], exact_slices=1)
    at = copy.deepcopy(above)
    for row in at:
        m = row["metric"]
        if (m["flow"], m["item_type"], m["cause"]) == (*sparse, "no_candidate"):
            row["value"][1] = "40"
    case(f"slice equality suppressed {slice_names[sparse]}", fixture(at), 0,
         [slice_line(*sparse, 100, 100, "100.000000%", "suppressed (slice attempts <= 100)")],
         ["slice_ranking=magnet_dead_stale"], exact_slices=1)
    other = fixture(at + [dict(r, value=[1000, "5000" if r["metric"]["cause"] == "no_candidate" else "0"])
                          for r in rows(1)])
    other["expected"][1]["role"] = "serving"
    case(f"other identity volume cannot unlock slice {slice_names[sparse]}", other, 0,
         [slice_line(*sparse, 100, 100, "100.000000%", "suppressed (slice attempts <= 100)")],
         ["slice_ranking=magnet_dead_stale"], exact_slices=2)
inactive_mixed = rows(1)
for row in inactive_mixed:
    if row["metric"]["cause"] == "no_candidate":
        row["value"][1] = "500"
case("inactive slices measured but never ranked", fixture(rows() + inactive_mixed), 0,
     [slice_line("materialise_then_play", "movie", 500, 500, "100.000000%", "withheld (inactive)")],
     ["slice_ranking=no_candidate"], exact_slices=2)
stale_slices = fixture()
stale_slices["timestamps"]["data"]["result"][0]["value"][1] = "879"
case("stale instance withholds slices", stale_slices, 2, ["error_rate=unavailable"], ["slice["], exact_slices=0)
incomplete_slices = fixture(rows()[1:])
case("incomplete instance withholds slices", incomplete_slices, 2, ["status=INCOMPLETE"], ["slice["], exact_slices=0)

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
for row in boundary["timestamps"]["data"]["result"]:
    row["value"][1] = "880"
case("freshness boundary inclusive", boundary, 0, ["status=OK", "sample_age_seconds=120"], ["INCOHERENT"])


def day19(identity, flow, item, stamp_errors="990", stamp_success="880"):
    current = rows(identity)
    for row in current:
        m = row["metric"]
        if (m["flow"], m["item_type"]) == (flow, item):
            row["value"][1] = {"success": "1000", "first_byte_timeout": "101"}.get(m["cause"], "0")
    data = fixture(current)
    for row in data["timestamps"]["data"]["result"]:
        row["value"][1] = stamp_success if row["metric"]["cause"] == "success" else stamp_errors
    return data


for flow, item in itertools.product(flows, items):
    label = slice_names[flow, item]
    for identity, role in ((0, "serving"), (1, "inactive")):
        mixed_source = day19(identity, flow, item)
        healthy = rows(1 - identity)
        mixed_source["instant"]["data"]["result"] += healthy
        for row in copy.deepcopy(healthy):
            row["metric"].pop("__name__")
            row["value"][1] = "990"
            mixed_source["timestamps"]["data"]["result"].append(row)
        case(f"day19 mixed source {role} {label}", mixed_source, 2,
             [f"role={role} status=INCOHERENT coverage=32/32",
              "INCOHERENT source timestamps: min=880 max=990 skew_seconds=110",
              f"role={'inactive' if role == 'serving' else 'serving'} status=OK coverage=32/32",
              "attempts=0 errors=0 error_rate=undefined (no observed attempts)"],
             ["9.173479%", "first_byte_timeout:101", "attempts=1101"], exact_slices=1)
    case(f"coherent control {label}", day19(0, flow, item, "880", "880"), 0,
         ["status=OK", "error_rate=9.173479%", "cause_ranking=first_byte_timeout:101",
          slice_line(flow, item, 1101, 101, "9.173479%", "first_byte_timeout:101")],
         ["INCOHERENT"], exact_slices=1)
independent = fixture(rows() + rows(1))
for row in independent["timestamps"]["data"]["result"]:
    row["value"][1] = "880" if row["metric"]["instance"] == "serving.example.com" else "990"
case("independently timed identities stay coherent", independent, 0,
     ["role=serving status=OK", "role=inactive status=OK"], ["INCOHERENT"], exact_slices=2)

# Coherent source times do not establish continuity of the counter lifetime.
for flow, item, cause, identity in itertools.product(flows, items, causes, (0, 1)):
    current = rows(identity)
    for row in current:
        m = row["metric"]
        if (m["flow"], m["item_type"], m["cause"]) == (flow, item, "success"):
            row["value"][1] = "1000"
    history = copy.deepcopy(current)
    for row in history:
        row["metric"].pop("__name__")
        m = row["metric"]
        if (m["flow"], m["item_type"], m["cause"]) == (flow, item, cause):
            row["value"][1] = "1101" if cause == "success" else "101"
    data = fixture(current + (rows() if identity else []), history)
    role = expected[identity]["role"]
    diagnostic = (f"COUNTER_DISCONTINUITY tuple={(flow, item, cause)} "
                  f"current={1000 if cause == 'success' else 0} window_max={1101 if cause == 'success' else 101}")
    case(f"counter discontinuity {role} {slice_names[flow, item]} {cause}", data, 2,
         [f"role={role} status=COUNTER_DISCONTINUITY coverage=32/32", diagnostic,
          "counter continuity not established", "error_rate=unavailable"],
         ["error_rate=0.000000%", "none (no errors)", "aged-out/stale"], exact_slices=identity)

continuity_current = rows()
continuity_current[0]["value"][1] = "1000"
continuity_history = copy.deepcopy(continuity_current)
for row in continuity_history:
    row["metric"].pop("__name__")
continuity_history[2]["value"][1] = "101"
continuity_diagnostic = ("COUNTER_DISCONTINUITY tuple=('materialise_then_play', 'movie', 'no_candidate') "
                        "current=0 window_max=101")
case("day20 exact reproduction", fixture(continuity_current, continuity_history), 2,
     [continuity_diagnostic, "error_rate=unavailable"], ["aggregate(", "cause_ranking=", "slice["], exact_slices=0)
case("partial window detects discontinuity", fixture(continuity_current, [continuity_history[2]]), 2,
     [continuity_diagnostic], ["aggregate(", "cause_ranking="], exact_slices=0)
for maximum in ("1000", "999", "0"):
    control = copy.deepcopy(continuity_history[:1])
    control[0]["value"][1] = maximum
    case(f"equal or lower window maximum {maximum}", fixture(continuity_current, control), 0,
         ["status=OK", "aggregate(all flows/items) attempts=1000 errors=0 error_rate=0.000000%",
          "cause_ranking=none (no errors)"], ["COUNTER_DISCONTINUITY"], exact_slices=1)
case("empty window retains current totals", fixture(continuity_current), 0,
     ["status=OK", "attempts=1000 errors=0 error_rate=0.000000%"], ["COUNTER_DISCONTINUITY"], exact_slices=1)
healthy = rows(1)
healthy[0]["value"][1] = "900"
healthy[2]["value"][1] = "101"
isolated = fixture(continuity_current + healthy, continuity_history + copy.deepcopy(healthy))
isolated["expected"][1]["role"] = "serving"
case("healthy serving identity survives discontinuous peer", isolated, 2,
     [continuity_diagnostic, 'instance={"instance": "inactive.example.com", "job": "playback"} role=serving status=OK',
      "aggregate(all flows/items) attempts=1001 errors=101 error_rate=10.089910%",
      "cause_ranking=no_candidate:101"], ["attempts=1000 errors=0"], exact_slices=1)
same_instance = copy.deepcopy(isolated)
for kind in ("instant", "window", "timestamps"):
    for row in same_instance[kind]["data"]["result"]:
        if row["metric"]["instance"] == "inactive.example.com":
            row["metric"].update(instance="serving.example.com", job="other-playback")
same_instance["expected"][1]["labels"].update(instance="serving.example.com", job="other-playback")
case("full identity includes job for continuity", same_instance, 2,
     [continuity_diagnostic, '"job": "other-playback"} role=serving status=OK',
      "attempts=1001 errors=101 error_rate=10.089910%", "cause_ranking=no_candidate:101"], exact_slices=1)
combined = fixture(continuity_current[:-1], continuity_history)
combined["timestamps"]["data"]["result"][0]["value"][1] = "879"
combined["window"]["data"]["result"][-1]["value"][1] = "19"
case("continuity preserves other failure and aging diagnostics", combined, 2,
     ["status=INCOMPLETE", "missing_tuples=", "STALE samples", "INCOHERENT source timestamps",
      continuity_diagnostic, "aged-out/stale", "window_max=19; not improvement; excluded from rate/ranking"],
     ["aggregate(", "cause_ranking="], exact_slices=0)



# Interval (current minus previous) measurement from an earlier capture.
def interval_fixture(current, previous, evaluation=600, stamp="500"):
    data = fixture(current)
    previous = copy.deepcopy(previous)
    for row in previous:
        row["value"][0] = evaluation
    stamps = copy.deepcopy(previous)
    for row in stamps:
        row["metric"].pop("__name__", None)
        row["value"][1] = stamp
    data["previous-instant"] = vector(previous)
    data["previous-timestamps"] = vector(stamps)
    return data


def set_values(records, values):
    for row in records:
        m = row["metric"]
        row["value"][1] = values.get((m["flow"], m["item_type"], m["cause"]), row["value"][1])
    return records


cold = ("materialise_then_play", "movie")
now_rows = set_values(rows(), {(*cold, "success"): "900", (*cold, "first_byte_timeout"): "100"})
then_rows = set_values(rows(), {(*cold, "success"): "400", (*cold, "first_byte_timeout"): "50"})
interval_line = "interval aggregate(all flows/items) attempts=550 errors=50 error_rate=9.090909%"
case("interval green delta", interval_fixture(now_rows, then_rows), 0,
     ["aggregate(all flows/items) attempts=1000 errors=100 error_rate=10.000000%",
      interval_line + " interval_ranking=first_byte_timeout:50", "interval source_seconds=490",
      "interval slice[cold-movie] flow=materialise_then_play item_type=movie attempts=550 errors=50"
      " error_rate=9.090909% interval_slice_ranking=first_byte_timeout:50",
      "interval slice[warm-episode] flow=play_already_materialised item_type=episode attempts=0 errors=0"
      f" error_rate={undefined} interval_slice_ranking=suppressed (interval slice attempts <= 100)"],
     ["INTERVAL_DISCONTINUITY"], exact_slices=1)
case("interval slice independent threshold", interval_fixture(now_rows, then_rows), 0,
     ["interval_ranking=suppressed (interval attempts <= 600)",
      "interval_slice_ranking=suppressed (interval slice attempts <= 600)"],
     ["interval_ranking=first_byte_timeout"], options=("--min-attempts", "600"))
case("no previous flags leaves output unchanged", fixture(now_rows), 0,
     ["attempts=1000 errors=100 error_rate=10.000000%"], ["interval", "INTERVAL_DISCONTINUITY"])
for flow, item, cause in itertools.product(flows, items, causes):
    decreased = set_values(copy.deepcopy(then_rows), {(flow, item, cause): "1001"})
    peer_now = set_values(rows(1), {(*cold, "no_candidate"): "300"})
    peer_then = set_values(rows(1), {(*cold, "no_candidate"): "100"})
    data = interval_fixture(now_rows + peer_now, decreased + peer_then)
    data["expected"][1]["role"] = "serving"
    case(f"interval decrease {slice_names[flow, item]} {cause}", data, 2,
         [f"INTERVAL_DISCONTINUITY tuple={(flow, item, cause)}", "< previous=1001",
          "interval attempts=unavailable errors=unavailable error_rate=unavailable (interval discontinuity)",
          "interval aggregate(all flows/items) attempts=200 errors=200 error_rate=100.000000%"
          " interval_ranking=no_candidate:200"],
         [interval_line])
case("interval mismatch missing previous tuple", interval_fixture(now_rows, then_rows[1:]), 2,
     ["INTERVAL_DISCONTINUITY tuple-set mismatch previous=31/32 current=32/32"], ["interval aggregate("])
case("interval mismatch inactive vanished", interval_fixture(now_rows, then_rows + rows(1)), 2,
     ["INTERVAL_DISCONTINUITY tuple-set mismatch previous=32/32 current=0/32"], [])
case("interval equal source time", interval_fixture(now_rows, then_rows, 995, "990"), 2,
     ["INTERVAL_DISCONTINUITY tuple=", "not earlier than current 990"], ["interval aggregate("])
incoherent_previous = interval_fixture(now_rows, then_rows)
incoherent_previous["previous-timestamps"]["data"]["result"][3]["value"][1] = "499"
case("interval incoherent previous source", incoherent_previous, 2,
     ["INTERVAL_DISCONTINUITY incoherent previous source timestamps"], ["interval aggregate("])
stale_interval = interval_fixture(now_rows, then_rows)
stale_interval["timestamps"]["data"]["result"][0]["value"][1] = "879"
case("interval withheld when cumulative unhealthy", stale_interval, 2,
     ["status=STALE", "error_rate=unavailable (cumulative status not OK)"], ["interval aggregate("])
case("interval previous evaluation not earlier", interval_fixture(now_rows, then_rows, 1000, "500"), 1,
     ["previous evaluation time must be strictly earlier"])
lone = interval_fixture(now_rows, then_rows)
del lone["previous-timestamps"]
case("interval flags both or neither", lone, 1, ["must be given together"])
uneven = interval_fixture(now_rows, then_rows)
uneven["previous-timestamps"]["data"]["result"].pop()
case("interval previous timestamp coverage", uneven, 1, ["previous timestamp coverage must exactly match"])

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
missing_name = fixture()
missing_name["instant"]["data"]["result"][0]["metric"].pop("__name__")
case("instant requires metric name", missing_name, 1, ["instant: expected phantom_playback_outcome_total family"])
for kind in ("instant", "window", "timestamps"):
    for family in ("phantom_runtime_info", "phantom_loadtime_rig_outcome_total", "phantom_playback_outcome_total_extra", ""):
        invalid = fixture(window=copy.deepcopy(raw_window))
        invalid[kind]["data"]["result"][0]["metric"]["__name__"] = family
        case(f"wrong family {kind} {family!r}", invalid, 1, [f"{kind}: expected phantom_playback_outcome_total family"])
for kind in ("instant", "window", "timestamps"):
    extra = fixture(window=rows())
    extra[kind]["data"]["result"][0]["metric"]["other_job"] = "unexpected"
    case(f"extra identity label {kind}", extra, 1, ["unlisted identity/extra labels"])
missing_timestamp = fixture()
missing_timestamp["timestamps"]["data"]["result"].pop()
case("missing sample timestamp", missing_timestamp, 1, ["timestamp coverage must exactly match"])
for kind in ("window", "timestamps"):
    mismatched = fixture(window=copy.deepcopy(raw_window))
    mismatched[kind]["data"]["result"][0]["value"][0] = 999
    case(f"different evaluation times {kind}", mismatched, 1, ["one common Prometheus evaluation time"])
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
    for name, data, code, contains, excludes, options, stdin, exact_slices in cases:
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
        for block in result.stdout.split("instance=")[1:]:
            if "COUNTER_DISCONTINUITY" in block:
                assert "status=OK" not in block, (name, block)
                assert "error_rate=unavailable" in block, (name, block)
                assert not any(s in block for s in ("aggregate(", "slice[", "cause_ranking=")), (name, block)
            if "INTERVAL_DISCONTINUITY" in block:
                assert not any(s in block for s in ("interval aggregate(", "interval slice[")), (name, block)
        if exact_slices is not None:
            for label in slice_names.values():
                count = sum(line.startswith(f"  slice[{label}] ") for line in result.stdout.splitlines())
                assert count == exact_slices, (name, label, count, output)
        assert all(path.read_bytes() == original for path, original in originals.items()), "input mutated"
        fake = subprocess.run([str(stub), *args], text=True, capture_output=True, check=True)
        assert not (fake.returncode == code and all(s in fake.stdout for s in contains)), f"stub survived: {name}"
        print(f"PASS {name}")
print(f"PASS: {len(cases)} offline coverage cases; 0%-stub rejected by every case; inputs unchanged")
PY
