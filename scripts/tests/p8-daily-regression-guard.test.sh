#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/p8-daily-regression-guard.test.sh
#
# In-repo regression harness for the P8 daily dual-purpose improvement +
# regression guard (tools/perf/loadtime-guard.sh +
# tools/perf/loadtime-expo-to-measurements.py) — ROI Priority 8, item 5.
# Mirrors p8-loadtime-flows.test.sh: deterministic, sandbox-only (bash +
# python3 + dotnet, NO live Jellyfin, NO cluster, NO network, NO beehive CLI
# required — always run with --no-file so a breach never tries to file a
# real task from this harness).
#
# Asserts:
#   A. loadtime-guard.sh and loadtime-expo-to-measurements.py exist,
#      are executable, and are syntax-clean.
#   B. loadtime-expo-to-measurements.py converts a Prometheus load-time
#      exposition into the ratchet-guard MeasurementSet JSON contract
#      correctly (flow/item_type -> flow/backend, seconds -> ms, quantile
#      fixed at "single").
#   C. A DRY RUN of loadtime-guard.sh against a scratch copy of the six-flow
#      thresholds file: first run SEEDS every scenario (fixture is the same
#      every run, ceiling starts at 0) and exits 0.
#   D. A second identical dry run HOLDS (same fixture, now-seeded ceiling) —
#      exits 0, and does NOT tighten further (idempotent on stable input).
#   E. A forced materialise-flow measurement above the seeded ceiling
#      BREACHES (exit 3) and, in --no-file mode, never attempts to invoke
#      the beehive CLI.
#
# Exit 0 = all assertions passed; non-zero on the first failure.
# Skips with a NOTE (exit 0) if python3 or dotnet is unavailable.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
GUARD="$REPO_ROOT/tools/perf/loadtime-guard.sh"
CONVERTER="$REPO_ROOT/tools/perf/loadtime-expo-to-measurements.py"
THRESHOLDS="$REPO_ROOT/tools/perf/loadtime-thresholds.json"

pass_count=0
fail_count=0
ok()    { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()   { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

command -v python3 >/dev/null 2>&1 || { printf 'NOTE: python3 unavailable; skipping p8-daily-regression-guard harness.\n'; exit 0; }
command -v dotnet   >/dev/null 2>&1 || { printf 'NOTE: dotnet unavailable; skipping p8-daily-regression-guard harness.\n'; exit 0; }

WORK="$(mktemp -d)"
cleanup() { rm -rf "$WORK"; }
trap cleanup EXIT INT TERM

head_ "A. scripts exist, executable, syntax-clean"
[[ -f "$GUARD" ]] || fatal "loadtime-guard.sh not found: $GUARD"
[[ -f "$CONVERTER" ]] || fatal "loadtime-expo-to-measurements.py not found: $CONVERTER"
[[ -f "$THRESHOLDS" ]] || fatal "loadtime-thresholds.json not found: $THRESHOLDS"
if [[ -x "$GUARD" ]]; then ok "loadtime-guard.sh is executable"; else bad "loadtime-guard.sh is not executable (chmod +x)"; fi
if bash -n "$GUARD"; then ok "loadtime-guard.sh passes bash -n"; else bad "loadtime-guard.sh has a bash syntax error"; fi
if python3 -c "import ast; ast.parse(open('$CONVERTER').read())"; then
    ok "loadtime-expo-to-measurements.py parses as valid python3"
else
    bad "loadtime-expo-to-measurements.py has a syntax error"
fi

head_ "B. converter: exposition -> MeasurementSet JSON contract"
EXPO="$WORK/expo.txt"
cat > "$EXPO" <<'EOF'
# HELP phantom_loadtime_seconds Wall-clock duration of a phantom-library channel load-time flow, in seconds.
# TYPE phantom_loadtime_seconds gauge
phantom_loadtime_seconds{flow="materialise",item_type="movie",color="rig"} 4.500000
phantom_loadtime_runs_total{flow="materialise",item_type="movie",color="rig"} 1
phantom_loadtime_errors_total{flow="materialise",item_type="movie",color="rig"} 0
phantom_loadtime_seconds{flow="play_materialised",item_type="episode",color="rig"} 1.250000
phantom_loadtime_runs_total{flow="play_materialised",item_type="episode",color="rig"} 1
phantom_loadtime_errors_total{flow="play_materialised",item_type="episode",color="rig"} 0
EOF
MEAS="$WORK/measurements.json"
python3 "$CONVERTER" "$EXPO" "$MEAS" || fatal "converter exited non-zero"
if python3 - "$MEAS" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
ms = {(m["flow"], m["backend"]): m for m in d["measurements"]}
assert ("materialise", "movie") in ms, "missing materialise/movie"
assert ("play_materialised", "episode") in ms, "missing play_materialised/episode"
m1 = ms[("materialise", "movie")]
assert m1["quantile"] == "single", m1
assert abs(m1["value_ms"] - 4500.0) < 1e-6, m1
m2 = ms[("play_materialised", "episode")]
assert abs(m2["value_ms"] - 1250.0) < 1e-6, m2
assert len(d["measurements"]) == 2, d["measurements"]
PY
then
    ok "converter emits correct flow/backend/quantile/value_ms for both records"
else
    bad "converter output failed the contract assertions"
fi

head_ "C. dry-run seeds every scenario on first run (exit 0)"
SCRATCH_THRESHOLDS="$WORK/loadtime-thresholds.json"
cp "$THRESHOLDS" "$SCRATCH_THRESHOLDS"
set +e
OUT1="$(bash "$GUARD" --thresholds "$SCRATCH_THRESHOLDS" --apply --no-file 2>&1)"
rc1=$?
set -e
echo "$OUT1" | sed 's/^/    /'
if [ "$rc1" = 0 ]; then ok "first dry run exits 0 (seed, not breach)"; else bad "first dry run exited $rc1, expected 0"; fi
if python3 -c "
import json
d = json.load(open('$SCRATCH_THRESHOLDS'))
assert all(s['threshold_ms'] > 0 for s in d['scenarios']), 'a scenario was left unseeded'
"; then
    ok "every scenario in the thresholds file was seeded to a positive ceiling"
else
    bad "not every scenario was seeded"
fi

head_ "D. second identical dry run holds (idempotent, exit 0)"
set +e
OUT2="$(bash "$GUARD" --thresholds "$SCRATCH_THRESHOLDS" --apply --no-file 2>&1)"
rc2=$?
set -e
echo "$OUT2" | sed 's/^/    /'
if [ "$rc2" = 0 ]; then ok "second dry run exits 0 (held, no regression)"; else bad "second dry run exited $rc2, expected 0"; fi
if echo "$OUT2" | grep -q 'summary:.*0 breach'; then
    ok "second run reports zero breaches"
else
    bad "second run report did not confirm zero breaches"
fi

head_ "E. a forced breach exits 3 and never shells out to beehive in --no-file mode"
# Seed a thresholds file with a deliberately low materialise/movie ceiling so the
# unmodified dry-run fixture (4500ms) breaches it.
BREACH_THRESHOLDS="$WORK/breach-thresholds.json"
python3 - "$SCRATCH_THRESHOLDS" "$BREACH_THRESHOLDS" <<'PY'
import json, sys
d = json.load(open(sys.argv[1]))
for s in d["scenarios"]:
    if s["flow"] == "materialise" and s["backend"] == "movie":
        s["threshold_ms"] = 10.0  # far below the fixture's 4500ms
json.dump(d, open(sys.argv[2], "w"), indent=2)
PY
set +e
OUT3="$(bash "$GUARD" --thresholds "$BREACH_THRESHOLDS" --no-file 2>&1)"
rc3=$?
set -e
echo "$OUT3" | sed 's/^/    /'
if [ "$rc3" = 3 ]; then ok "forced breach exits 3"; else bad "forced breach exited $rc3, expected 3"; fi
if echo "$OUT3" | grep -qi 'breach'; then ok "breach reported in output"; else bad "no breach reported in output"; fi
if echo "$OUT3" | grep -qi 'not filing beehive tasks'; then
    ok "--no-file mode reports it is skipping beehive task filing"
else
    bad "--no-file mode did not report skipping task filing"
fi

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
