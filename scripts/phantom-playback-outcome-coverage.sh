#!/usr/bin/env bash
# Offline only: stdin is preserved on fd 3 while Python reads its program.
set -euo pipefail
exec python3 - "$@" 3<&0 <<'PY'
import argparse
import itertools
import json
import math
import os
import sys
import time
from decimal import Decimal, InvalidOperation

METRIC = "phantom_playback_outcome_total"
DIMENSIONS = ("flow", "item_type", "cause")
FLOWS = ("materialise_then_play", "play_already_materialised")
ITEMS = ("movie", "episode")
CAUSES = ("success", "availability_abstain", "no_candidate", "magnet_dead_stale",
          "gostream_register_fail", "gostream_cannot_fetch", "first_byte_timeout",
          "plugin_host_error")
TEMPERATURE = {"materialise_then_play": "cold", "play_already_materialised": "warm"}
CANONICAL = set(itertools.product(FLOWS, ITEMS, CAUSES))


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def load(path):
    with os.fdopen(os.dup(3)) if path == "-" else open(path) as stream:
        return json.load(stream, object_pairs_hook=unique_object)


def number(value):
    if isinstance(value, bool) or not isinstance(value, (str, int, float)):
        raise ValueError("expected finite nonnegative number")
    result = Decimal(str(value))
    if not result.is_finite() or result < 0 or not math.isfinite(float(result)):
        raise ValueError("expected finite nonnegative number")
    return result


def rate(errors, attempts):
    return f"{Decimal(errors) / Decimal(attempts):.6%}" if attempts else "undefined (no observed attempts)"


def ranking(values):
    counts = {cause: sum(int(v) for key, v in values.items() if key[2] == cause)
              for cause in CAUSES if cause != "success"}
    ordered = sorted(((c, n) for c, n in counts.items() if n), key=lambda p: (-p[1], p[0]))
    return ", ".join(f"{c}:{n}" for c, n in ordered) or "none (no errors)"


def main():
    parser = argparse.ArgumentParser(
        description="Offline playback outcome coverage and cumulative error ratio: labelled per-instance aggregate plus four flow/item slices (cold=materialise_then_play, warm=play_already_materialised; movie/episode), each ranked only on its own volume",
        epilog="Capture all three vectors with the same explicit Prometheus time= value; --now only controls age checks.")
    parser.add_argument("--instant", required=True, help="raw outcome instant-vector API JSON")
    parser.add_argument("--window", required=True, help="max_over_time outcome vector API JSON (context only)")
    parser.add_argument("--timestamps", required=True, help="timestamp(outcome selector) vector API JSON")
    parser.add_argument("--expected", required=True, help="JSON array of {labels: {identity labels}, role: serving|inactive}")
    parser.add_argument("--now", default=str(time.time()), help="Unix time; override for reproducible offline analysis")
    parser.add_argument("--max-age", default="120", help="maximum sample/evaluation age in seconds (default 120)")
    parser.add_argument("--min-attempts", default="100", help="rank serving causes only ABOVE this volume, applied independently to the aggregate and to each slice (default 100)")
    args = parser.parse_args()
    now, max_age, minimum = map(number, (args.now, args.max_age, args.min_attempts))
    if [args.instant, args.window, args.timestamps, args.expected].count("-") > 1:
        raise ValueError("only one input may use stdin (-)")
    expected = load(args.expected)
    if not isinstance(expected, list) or not expected:
        raise ValueError("expected list must be nonempty")
    roles = {}
    for entry in expected:
        if not isinstance(entry, dict) or set(entry) != {"labels", "role"}:
            raise ValueError("expected entry requires labels and role")
        labels = entry["labels"]
        if (not isinstance(labels, dict) or not labels
                or any(k in (*DIMENSIONS, "__name__") or not k
                       or not isinstance(v, str) or not v for k, v in labels.items())
                or entry["role"] not in ("serving", "inactive")):
            raise ValueError("invalid expected identity labels or role")
        identity = tuple(sorted(labels.items()))
        if identity in roles:
            raise ValueError("duplicate expected identity")
        roles[identity] = entry["role"]
    if "serving" not in roles.values():
        raise ValueError("expected list must name at least one serving instance")

    vectors = {}
    evaluation_times = set()
    for kind, path in (("instant", args.instant), ("window", args.window), ("timestamps", args.timestamps)):
        payload = load(path)
        if (not isinstance(payload, dict) or payload.get("status") != "success"
                or payload.get("warnings") or payload.get("infos")
                or not isinstance(payload.get("data"), dict)
                or payload["data"].get("resultType") != "vector"
                or not isinstance(payload["data"].get("result"), list)):
            raise ValueError(f"{kind}: expected successful, unannotated API vector")
        records = {}
        for row in payload["data"]["result"]:
            if not isinstance(row, dict) or set(row) != {"metric", "value"}:
                raise ValueError(f"{kind}: expected metric/value sample")
            labels, sample = row["metric"], row["value"]
            if (not isinstance(labels, dict) or any(not isinstance(v, str) for v in labels.values())
                    or not isinstance(sample, list) or len(sample) != 2):
                raise ValueError(f"{kind}: malformed labels/sample")
            # max_over_time() and timestamp() drop __name__; reject any explicit foreign family.
            if labels.get("__name__", METRIC if kind in ("window", "timestamps") else None) != METRIC:
                raise ValueError(f"{kind}: expected {METRIC} family")
            key = tuple(labels.get(d) for d in DIMENSIONS)
            if key not in CANONICAL:
                raise ValueError(f"{kind}: noncanonical tuple {key}")
            identity = tuple(sorted((k, v) for k, v in labels.items() if k not in (*DIMENSIONS, "__name__")))
            if identity not in roles:
                raise ValueError(f"{kind}: unlisted identity/extra labels {identity}")
            full_key = (identity, key)
            if full_key in records:
                raise ValueError(f"{kind}: duplicate tuple {full_key}")
            evaluation, value = map(number, sample)
            if evaluation > now or (kind == "timestamps" and value > evaluation):
                raise ValueError(f"{kind}: future timestamp")
            if kind != "timestamps" and value != value.to_integral_value():
                raise ValueError(f"{kind}: counter must be integral")
            evaluation_times.add(evaluation)
            records[full_key] = (evaluation, value)
        vectors[kind] = records
    if len(evaluation_times) > 1:
        raise ValueError("inputs must use one common Prometheus evaluation time")
    instant, window, timestamps = (vectors[k] for k in ("instant", "window", "timestamps"))
    if instant.keys() != timestamps.keys():
        raise ValueError("timestamp coverage must exactly match instant series")

    unhealthy = False
    print("Cumulative counter snapshot, NOT a window increase or improvement measurement.")
    for identity, role in sorted(roles.items()):
        name = json.dumps(dict(identity), sort_keys=True)
        current = {key: value for (who, key), (_, value) in instant.items() if who == identity}
        ages = [now - timestamps[identity, key][1] for key in current]
        stale = bool(ages) and max(ages) > max_age
        complete = set(current) == CANONICAL
        status = ("UNINSTRUMENTED" if not current and role == "serving" else
                  "ABSENT (inactive)" if not current else
                  "INCOMPLETE" if not complete else "STALE" if stale else "OK")
        unhealthy |= (role == "serving" and not current) or (bool(current) and (not complete or stale))
        print(f"instance={name} role={role} status={status} coverage={len(current)}/32")
        print(f"  sample_age_seconds={max(ages) if ages else 'unavailable'}")
        if current and not complete:
            print(f"  missing_tuples={sorted(CANONICAL - current.keys())}")
        if stale:
            print("  STALE samples: rate and ranking withheld")
        if complete and not stale:
            attempts = sum(int(v) for v in current.values())
            errors = sum(int(v) for key, v in current.items() if key[2] != "success")
            print(f"  aggregate(all flows/items) attempts={attempts} errors={errors} error_rate={rate(errors, attempts)}")
            if role == "serving" and attempts > minimum:
                print("  cause_ranking=" + ranking(current))
            elif role == "serving":
                reason = "no organic traffic yet observed" if not attempts else "insufficient attributed volume"
                print(f"  {reason} (ranking suppressed: attempts <= {minimum})")
            else:
                print("  cause_ranking=withheld (inactive; not attributed serving traffic)")
            for flow, item in itertools.product(FLOWS, ITEMS):
                part = {key: v for key, v in current.items() if key[:2] == (flow, item)}
                s_attempts = sum(int(v) for v in part.values())
                s_errors = sum(int(v) for key, v in part.items() if key[2] != "success")
                if role != "serving":
                    decision = "withheld (inactive)"
                elif s_attempts > minimum:
                    decision = ranking(part)
                else:
                    decision = f"suppressed (slice attempts <= {minimum})"
                print(f"  slice[{TEMPERATURE[flow]}-{item}] flow={flow} item_type={item} attempts={s_attempts}"
                      f" errors={s_errors} error_rate={rate(s_errors, s_attempts)} slice_ranking={decision}")
        else:
            print("  attempts=unavailable errors=unavailable error_rate=unavailable (coverage/freshness not established)")
        for (who, key), (_, value) in sorted(window.items()):
            if who == identity and (who, key) not in instant and value > 0:
                print(f"  aged-out/stale tuple={key} window_max={value}; not improvement; excluded from rate/ranking")
    if evaluation_times and now - next(iter(evaluation_times)) > max_age:
        print("STALE capture evaluation: report is not current")
        unhealthy = True
    return 2 if unhealthy else 0


try:
    sys.exit(main())
except (ValueError, InvalidOperation, OSError, OverflowError) as error:
    print(f"phantom-playback-outcome-coverage: {error}", file=sys.stderr)
    sys.exit(1)
PY
