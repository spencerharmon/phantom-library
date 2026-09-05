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

Exit: 0 = converted (possibly zero measurements), 2 = malformed exposition line.
"""
import json
import re
import sys

LINE_RE = re.compile(r'^phantom_loadtime_seconds\{([^}]*)\}\s+(\S+)\s*$')


def parse_labels(raw: str) -> dict:
    labels = {}
    for part in raw.split(','):
        part = part.strip()
        if not part:
            continue
        key, _, value = part.partition('=')
        labels[key.strip()] = value.strip().strip('"')
    return labels


def convert(text: str) -> dict:
    measurements = []
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith('#'):
            continue
        match = LINE_RE.match(line)
        if not match:
            continue  # runs_total / errors_total / other series: not a duration measurement.
        labels = parse_labels(match.group(1))
        for required in ('flow', 'item_type'):
            if required not in labels:
                raise ValueError(f"phantom_loadtime_seconds line missing '{required}' label: {line!r}")
        try:
            seconds = float(match.group(2))
        except ValueError as exc:
            raise ValueError(f"non-numeric phantom_loadtime_seconds value in: {line!r}") from exc
        if seconds < 0:
            raise ValueError(f"negative phantom_loadtime_seconds value in: {line!r}")
        measurements.append({
            "flow": labels["flow"],
            "backend": labels["item_type"],
            "quantile": "single",
            "value_ms": round(seconds * 1000.0, 3),
        })
    return {"measurements": measurements}


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
