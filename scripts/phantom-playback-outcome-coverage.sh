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


def interval_reasons(identity, current, timestamps, previous, previous_stamps):
    before = {key: value for (who, key), (_, value) in previous.items() if who == identity}
    reasons = []
    if set(before) != set(current):
        reasons.append(f"tuple-set mismatch previous={len(before)}/32 current={len(current)}/32"
                       f" only_previous={sorted(set(before) - set(current))}"
                       f" only_current={sorted(set(current) - set(before))}")
    old = {previous_stamps[identity, key][1] for key in before}
    if len(old) > 1:
        reasons.append(f"incoherent previous source timestamps min={min(old)} max={max(old)}")
    for key in sorted(set(before) & set(current)):
        if not previous_stamps[identity, key][1] < timestamps[identity, key][1]:
            reasons.append(f"tuple={key} previous source time {previous_stamps[identity, key][1]}"
                           f" not earlier than current {timestamps[identity, key][1]}")
        if current[key] < before[key]:
            reasons.append(f"tuple={key} current={current[key]} < previous={before[key]}")
    return before, reasons


def previous_staleness(identity, previous_stamps, max_age):
    """Previous-endpoint freshness relative to ITS OWN evaluation time (never --now)."""
    ages = [evaluation - source for (who, _), (evaluation, source) in previous_stamps.items() if who == identity]
    return max(ages) if ages and max(ages) > max_age else None


def load_attribution(path, roles):
    receipts = load(path) if path is not None else []
    if not isinstance(receipts, list):
        raise ValueError("attribution must be an array of receipts")
    result = {}
    for receipt in receipts:
        if not isinstance(receipt, dict) or receipt.get("scope") not in ("cumulative", "interval"):
            raise ValueError("attribution receipt requires cumulative or interval scope")
        scope = receipt["scope"]
        fields = {"labels", "scope", "attribution", "source_end"}
        if scope == "interval":
            fields.add("source_start")
        if set(receipt) != fields:
            raise ValueError("attribution receipt has missing or unexpected fields")
        labels = receipt["labels"]
        if not isinstance(labels, dict) or any(not isinstance(v, str) for v in labels.values()):
            raise ValueError("attribution receipt requires exact identity labels")
        identity = tuple(sorted(labels.items()))
        if identity not in roles:
            raise ValueError("attribution receipt has unlisted identity/extra labels")
        if receipt["attribution"] not in ("organic-serving", "unknown", "non-serving", "mixed"):
            raise ValueError("invalid attribution (expected organic-serving, unknown, non-serving or mixed)")
        key = identity, scope
        if key in result:
            raise ValueError("duplicate attribution receipt for identity/scope")
        end = number(receipt["source_end"])
        start = number(receipt["source_start"]) if scope == "interval" else None
        if start is not None and start >= end:
            raise ValueError("attribution source_start must be earlier than source_end")
        result[key] = (receipt["attribution"], start, end)
    for identity in roles:
        cumulative = result.get((identity, "cumulative"))
        interval = result.get((identity, "interval"))
        if (cumulative and interval and interval[2] <= cumulative[2]
                and ((cumulative[0] == "organic-serving" and interval[0] in ("non-serving", "mixed"))
                     or (cumulative[0] == "non-serving" and interval[0] in ("organic-serving", "mixed")))):
            raise ValueError("contradictory cumulative and interval attribution receipts")
    return result


def attributed(receipts, identity, role, scope, end, start=None):
    receipt = receipts.get((identity, scope))
    if role != "serving":
        reason = "inactive"
    elif receipt is None:
        reason = "missing receipt"
    elif receipt[0] != "organic-serving":
        reason = receipt[0]
    elif receipt[1:] != (start, end):
        reason = "source span mismatch (expired or partial receipt)"
    else:
        print(f"  {scope} attribution=organic-serving (caller receipt covers entire population)")
        return True
    print(f"  {scope} attribution=unattributed ({reason}); counts/ratios are unattributed observations; rankings withheld")
    return False


def report_interval(identity, role, current, timestamps, previous, previous_stamps, healthy, minimum, max_age, receipts):
    """Print interval (current minus previous) rates; return True when interval is discontinuous or stale."""
    before, reasons = interval_reasons(identity, current, timestamps, previous, previous_stamps)
    for reason in reasons:
        print(f"  INTERVAL_DISCONTINUITY {reason}; interval rates and rankings withheld")
    stale_age = previous_staleness(identity, previous_stamps, max_age)
    if stale_age is not None:
        print(f"  INTERVAL_STALE_PREVIOUS previous_sample_age_seconds={stale_age} max_age={max_age}"
              " (age at previous evaluation); interval rates and rankings withheld")
    if reasons or stale_age is not None or not healthy:
        why = ("interval discontinuity" if reasons else "stale previous endpoint" if stale_age is not None
               else "cumulative status not OK")
        print(f"  interval attempts=unavailable errors=unavailable error_rate=unavailable ({why})")
        return bool(reasons) or stale_age is not None
    delta = {key: int(current[key]) - int(before[key]) for key in current}
    old = next(iter({previous_stamps[identity, key][1] for key in before}))
    new = next(iter({timestamps[identity, key][1] for key in current}))
    attempts = sum(delta.values())
    errors = sum(v for key, v in delta.items() if key[2] != "success")
    print(f"  interval source_seconds={new - old} (current minus previous; not cumulative)")
    authorized = attributed(receipts, identity, role, "interval", new, old)
    if role != "serving":
        decision = "withheld (inactive)"
    elif not authorized:
        decision = "withheld (unattributed)"
    elif attempts > minimum:
        decision = ranking(delta)
    else:
        decision = f"suppressed (interval attempts <= {minimum})"
    print(f"  interval aggregate(all flows/items) attempts={attempts} errors={errors}"
          f" error_rate={rate(errors, attempts)} interval_ranking={decision}")
    for flow, item in itertools.product(FLOWS, ITEMS):
        part = {key: v for key, v in delta.items() if key[:2] == (flow, item)}
        s_attempts = sum(part.values())
        s_errors = sum(v for key, v in part.items() if key[2] != "success")
        if role != "serving":
            decision = "withheld (inactive)"
        elif not authorized:
            decision = "withheld (unattributed)"
        elif s_attempts > minimum:
            decision = ranking(part)
        else:
            decision = f"suppressed (interval slice attempts <= {minimum})"
        print(f"  interval slice[{TEMPERATURE[flow]}-{item}] flow={flow} item_type={item} attempts={s_attempts}"
              f" errors={s_errors} error_rate={rate(s_errors, s_attempts)} interval_slice_ranking={decision}")
    return False


def main():
    parser = argparse.ArgumentParser(
        description="Offline playback outcome coverage and cumulative error ratio: labelled per-instance aggregate plus four flow/item slices (cold=materialise_then_play, warm=play_already_materialised; movie/episode), each ranked only on its own volume",
        epilog="Capture all three vectors with the same explicit Prometheus time= value; --now only controls age checks. A matched window maximum above current reports COUNTER_DISCONTINUITY (exit 2), withholding that identity's aggregate/slice rates and rankings. Partial/empty windows are valid but do not prove continuity; equal/lower maxima do not trigger this guard. No reset cause, time, count, or adjusted totals are inferred. With --previous-instant/--previous-timestamps (an earlier capture), interval rates (current minus previous) are reported per identity; any tuple decrease, tuple-set mismatch, incoherent previous source time, or previous source time not earlier than current reports INTERVAL_DISCONTINUITY (exit 2) and withholds that identity's interval rates. Previous-endpoint freshness is judged per identity against the previous capture's OWN evaluation time (not --now, so day-old baselines are valid): a previous sample older than --max-age at its evaluation (age <= max-age is fresh) reports INTERVAL_STALE_PREVIOUS (exit 2) and withholds that identity's interval aggregate and all four interval slice rates/rankings; current cumulative reports and healthy peers are unaffected.")
    parser.add_argument("--instant", required=True, help="raw outcome instant-vector API JSON")
    parser.add_argument("--window", required=True, help="max_over_time outcome vector API JSON (continuity/aging evidence only, never added to totals)")
    parser.add_argument("--timestamps", required=True, help="timestamp(outcome selector) vector API JSON")
    parser.add_argument("--expected", required=True, help="JSON array of {labels: {identity labels}, role: serving|inactive}")
    parser.add_argument("--attribution", help="optional JSON receipt array: exact labels, scope cumulative|interval, attribution organic-serving|unknown|non-serving|mixed, source_end; interval also requires source_start. Cumulative asserts the ENTIRE counter population; interval asserts continuous organic serving throughout the EXACT source-time span, not just endpoint roles. Caller must establish provenance; current role alone never authorizes rankings. Missing/mismatched evidence leaves numeric observations unattributed. See docs/tasks/playback-outcome-coverage-report-001.md")
    parser.add_argument("--now", default=str(time.time()), help="Unix time; override for reproducible offline analysis")
    parser.add_argument("--max-age", default="120", help="maximum sample/evaluation age in seconds (default 120); also bounds previous-endpoint sample age at its own evaluation")
    parser.add_argument("--min-attempts", default="100", help="rank serving causes only ABOVE this volume, applied independently to the aggregate and to each slice (default 100)")
    parser.add_argument("--previous-instant", help="optional earlier raw outcome instant-vector API JSON (requires --previous-timestamps); enables per-identity interval (current minus previous) rates")
    parser.add_argument("--previous-timestamps", help="optional timestamp(outcome selector) vector API JSON at the --previous-instant evaluation time; each identity's sample age at that evaluation must be <= --max-age")
    args = parser.parse_args()
    if (args.previous_instant is None) != (args.previous_timestamps is None):
        raise ValueError("--previous-instant and --previous-timestamps must be given together")
    now, max_age, minimum = map(number, (args.now, args.max_age, args.min_attempts))
    if [args.instant, args.window, args.timestamps, args.expected,
            args.previous_instant, args.previous_timestamps, args.attribution].count("-") > 1:
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
    receipts = load_attribution(args.attribution, roles)

    vectors = {}
    evaluation_times = set()
    previous_times = set()
    sources = [("instant", args.instant), ("window", args.window), ("timestamps", args.timestamps)]
    if args.previous_instant is not None:
        sources += [("previous-instant", args.previous_instant), ("previous-timestamps", args.previous_timestamps)]
    for kind, path in sources:
        base = kind.replace("previous-", "")
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
            if labels.get("__name__", METRIC if base in ("window", "timestamps") else None) != METRIC:
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
            if evaluation > now or (base == "timestamps" and value > evaluation):
                raise ValueError(f"{kind}: future timestamp")
            if base != "timestamps" and value != value.to_integral_value():
                raise ValueError(f"{kind}: counter must be integral")
            (previous_times if base != kind else evaluation_times).add(evaluation)
            records[full_key] = (evaluation, value)
        vectors[kind] = records
    if len(evaluation_times) > 1:
        raise ValueError("inputs must use one common Prometheus evaluation time")
    instant, window, timestamps = (vectors[k] for k in ("instant", "window", "timestamps"))
    if instant.keys() != timestamps.keys():
        raise ValueError("timestamp coverage must exactly match instant series")
    interval = args.previous_instant is not None
    if interval:
        previous, previous_stamps = vectors["previous-instant"], vectors["previous-timestamps"]
        if len(previous_times) > 1:
            raise ValueError("previous inputs must use one common Prometheus evaluation time")
        if previous.keys() != previous_stamps.keys():
            raise ValueError("previous timestamp coverage must exactly match previous instant series")
        if previous_times and evaluation_times and not max(previous_times) < min(evaluation_times):
            raise ValueError("previous evaluation time must be strictly earlier than current evaluation time")

    unhealthy = False
    print("Cumulative counter snapshot, NOT a window increase or improvement measurement.")
    for identity, role in sorted(roles.items()):
        name = json.dumps(dict(identity), sort_keys=True)
        current = {key: value for (who, key), (_, value) in instant.items() if who == identity}
        ages = [now - timestamps[identity, key][1] for key in current]
        stale = bool(ages) and max(ages) > max_age
        complete = set(current) == CANONICAL
        # Source coherence is per identity only: independent exporters may differ.
        stamps = [timestamps[identity, key][1] for key in current]
        incoherent = len(set(stamps)) > 1
        discontinuities = {key: window[identity, key][1] for key, value in current.items()
                           if (identity, key) in window and window[identity, key][1] > value}
        status = ("UNINSTRUMENTED" if not current and role == "serving" else
                  "ABSENT (inactive)" if not current else
                  "INCOMPLETE" if not complete else "STALE" if stale else
                  "INCOHERENT" if incoherent else
                  "COUNTER_DISCONTINUITY" if discontinuities else "OK")
        unhealthy |= (role == "serving" and not current) or (
            bool(current) and (not complete or stale or incoherent or bool(discontinuities)))
        print(f"instance={name} role={role} status={status} coverage={len(current)}/32")
        print(f"  sample_age_seconds={max(ages) if ages else 'unavailable'}")
        if current and not complete:
            print(f"  missing_tuples={sorted(CANONICAL - current.keys())}")
        if stale:
            print("  STALE samples: rate and ranking withheld")
        if incoherent:
            low, high = min(stamps), max(stamps)
            print(f"  INCOHERENT source timestamps: min={low} max={high} skew_seconds={high - low};"
                  " counters not from one source sample, rate and ranking withheld")
        for key, maximum in sorted(discontinuities.items()):
            print(f"  COUNTER_DISCONTINUITY tuple={key} current={current[key]} window_max={maximum};"
                  " counter continuity not established; aggregate/slice rates and rankings withheld")
        healthy = complete and not stale and not incoherent and not discontinuities
        if healthy:
            authorized = attributed(receipts, identity, role, "cumulative", stamps[0])
            attempts = sum(int(v) for v in current.values())
            errors = sum(int(v) for key, v in current.items() if key[2] != "success")
            print(f"  aggregate(all flows/items) attempts={attempts} errors={errors} error_rate={rate(errors, attempts)}")
            if role == "serving" and not authorized:
                print("  cause_ranking=withheld (unattributed)")
            elif role == "serving" and attempts > minimum:
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
                elif not authorized:
                    decision = "withheld (unattributed)"
                elif s_attempts > minimum:
                    decision = ranking(part)
                else:
                    decision = f"suppressed (slice attempts <= {minimum})"
                print(f"  slice[{TEMPERATURE[flow]}-{item}] flow={flow} item_type={item} attempts={s_attempts}"
                      f" errors={s_errors} error_rate={rate(s_errors, s_attempts)} slice_ranking={decision}")
        else:
            print("  attempts=unavailable errors=unavailable error_rate=unavailable (coverage/freshness/coherence/continuity not established)")
        if interval and (current or any(who == identity for who, _ in previous)):
            unhealthy |= report_interval(identity, role, current, timestamps, previous, previous_stamps,
                                         healthy, minimum, max_age, receipts)
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
