#!/usr/bin/env python3
"""
tools/perf/loadtime-expo-to-measurements.py

Converts the Prometheus text-exposition emitted by
tools/rig-scenarios/47-loadtime-flows.sh (phantom_loadtime_seconds{flow=...,
item_type=...,color=...} <secs>) into the phantom-ratchet-guard MeasurementSet
JSON contract ({"measurements":[{"flow","backend","quantile","value_ms"},...]}).

The ratchet-guard tool is generic over (flow, backend, quantile); here
`backend` carries the load-time flow's item_type (movie/episode) and
`quantile` is the constant "single" (a load-time flow records ONE wall-clock
duration per run, not a distribution/quantile like the P5 browse-flow guard).

Usage:
    loadtime-expo-to-measurements.py <exposition-file> [<measurements-out-file>]
    (reads stdin, writes stdout, when file args are omitted / "-")

Each duration tuple must carry matching phantom_loadtime_runs_total and
phantom_loadtime_errors_total counters (validated fail-closed). Tuples with
errors_total > 0 are listed under "ineligible" and NEVER under "measurements",
so they can neither seed, tighten, reset nor loosen a durable ceiling.

Exit: 0 = converted (possibly zero eligible measurements), 2 = malformed or
inconsistent exposition.
"""
import json
import re
import sys

SERIES_RE = re.compile(
    r'^phantom_loadtime_(seconds|runs_total|errors_total)\{([^}]*)\}\s+(\S+)\s*$')
INT_RE = re.compile(r'^[0-9]+(\.0+)?$')

# Eligibility contract (ttfb-loadtime-error-aware-ratchet): a (flow, item_type)
# tuple may mutate a durable ceiling ONLY when every one of its runs succeeded.
# The engine aggregates N samples into ONE median and records failed or
# unmeasured samples (timeouts, engine-budget skips) as `sample_add 0 1`, so an
# erroring tuple's median is not a successful-run statistic: a timeout plus two
# skips yields median 0 (which would tighten a positive ceiling to 0, after
# which the ratchet treats it as unseeded) and positive-duration errors can
# falsely tighten too. Such tuples are therefore reported as `ineligible`
# (still observable; the batch is published unchanged upstream) and never
# reach the ratchet. Counters are validated fail-closed: every duration tuple
# needs exactly one integer runs_total >= 1 and errors_total in [0, runs].


def parse_labels(raw: str) -> dict:
    labels = {}
    for part in raw.split(','):
        part = part.strip()
        if not part:
            continue
        key, _, value = part.partition('=')
        labels[key.strip()] = value.strip().strip('"')
    return labels


def _count(raw: str, line: str) -> int:
    if not INT_RE.match(raw):
        raise ValueError(f"non-integer load-time counter in: {line!r}")
    return int(float(raw))


def convert(text: str) -> dict:
    series = {'seconds': {}, 'runs_total': {}, 'errors_total': {}}
    order = []
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith('#'):
            continue
        match = SERIES_RE.match(line)
        if not match:
            continue  # list_errors_total / rig outcomes / other series are not ratchet inputs.
        kind, raw_labels, raw_value = match.groups()
        labels = parse_labels(raw_labels)
        for required in ('flow', 'item_type'):
            if not labels.get(required):
                raise ValueError(f"phantom_loadtime_{kind} line missing '{required}' label: {line!r}")
        key = (labels['flow'], labels['item_type'])
        if key in series[kind]:
            raise ValueError(f"duplicate phantom_loadtime_{kind} series for {key[0]}/{key[1]}")
        if kind == 'seconds':
            try:
                seconds = float(raw_value)
            except ValueError as exc:
                raise ValueError(f"non-numeric phantom_loadtime_seconds value in: {line!r}") from exc
            if seconds != seconds or seconds in (float('inf'), float('-inf')):
                raise ValueError(f"non-finite phantom_loadtime_seconds value in: {line!r}")
            if seconds < 0:
                raise ValueError(f"negative phantom_loadtime_seconds value in: {line!r}")
            series[kind][key] = seconds
            order.append(key)
        else:
            series[kind][key] = _count(raw_value, line)

    for kind in ('runs_total', 'errors_total'):
        orphans = set(series[kind]) - set(series['seconds'])
        if orphans:
            flow, item_type = sorted(orphans)[0]
            raise ValueError(f"phantom_loadtime_{kind} for {flow}/{item_type} has no duration series")

    measurements, ineligible = [], []
    for key in order:
        flow, item_type = key
        if key not in series['runs_total'] or key not in series['errors_total']:
            raise ValueError(f"{flow}/{item_type} lacks matching runs_total/errors_total counters")
        runs, errors = series['runs_total'][key], series['errors_total'][key]
        if runs < 1:
            raise ValueError(f"{flow}/{item_type} has runs_total {runs}; at least one run required")
        if errors > runs:
            raise ValueError(f"{flow}/{item_type} has errors_total {errors} > runs_total {runs}")
        record = {
            "flow": flow,
            "backend": item_type,
            "quantile": "single",
            "value_ms": round(series['seconds'][key] * 1000.0, 3),
        }
        if errors:
            ineligible.append({**record, "runs": runs, "errors": errors})
        else:
            measurements.append(record)
    return {"measurements": measurements, "ineligible": ineligible}


def main(argv):
    in_path = argv[1] if len(argv) > 1 and argv[1] != '-' else None
    out_path = argv[2] if len(argv) > 2 and argv[2] != '-' else None

    text = open(in_path).read() if in_path else sys.stdin.read()
    try:
        result = convert(text)
    except ValueError as exc:
        print(f"loadtime-expo-to-measurements: {exc}", file=sys.stderr)
        return 2

    out = json.dumps(result, indent=2) + "\n"
    if out_path:
        with open(out_path, "w") as f:
            f.write(out)
    else:
        sys.stdout.write(out)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
