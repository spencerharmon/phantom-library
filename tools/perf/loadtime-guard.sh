#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# tools/perf/loadtime-guard.sh
#
# ROI Priority 8, item 5 — the daily dual-purpose improvement + regression
# guard for the six ROI-named load-time flows (list_load, sort_change,
# info_open, get_sources, materialise, play_materialised). Reuses the P5
# ratcheting-guard mechanism (tools/perf/ratchet-guard) verbatim — same
# RatchetEngine, same never-loosen semantics, same auto-filing wrapper shape
# as tools/perf/ratchet-guard.sh — rather than reinventing it:
#
#   1. Runs the P8 measurement engine (tools/rig-scenarios/47-loadtime-flows.sh)
#      to get one Prometheus-exposition record per (flow, item_type).
#   2. Converts it to the ratchet-guard MeasurementSet JSON contract via
#      loadtime-expo-to-measurements.py.
#   3. Runs phantom-ratchet-guard against tools/perf/loadtime-thresholds.json:
#      an EXCEEDED per-flow threshold is a FAILING test (never silently
#      accepted); an IMPROVEMENT tightens the threshold (never loosens),
#      ratcheting materialise + play_materialised hardest toward the
#      operator's 0.5s target (loadtime-thresholds.json gives those two
#      flows a tighter improvement-margin/headroom so they track improvement
#      more aggressively; target_ms=500 is recorded for visibility only).
#   4. On a breach, files the next performance-review/improvement task per
#      breached scenario via `beehive task add` + `beehive task block` (a
#      real follow-up on the responsible code, never a silent accept) —
#      task ids are prefixed p8-daily-perf-regression- so they never collide
#      with P5's p5-perf-regression- ids for the same flow name.
#
# Usage:
#   loadtime-guard.sh [--live] [--api <url>] [--token <tok>] [--color <c>]
#                      [--thresholds <file>] [--apply] [--source-task <id>]
#                      [--task-prefix <prefix>] [--submodule <name>] [--no-file]
#
#   --live          drive the real rig (PHANTOM_CI_DRYRUN=0); default is a
#                   deterministic dry run (no cluster/network access).
#   --api           rig Jellyfin base URL for --live (default :18096; never
#                   :8096 — the measurement engine itself refuses that port).
#   --token         X-Emby-Token for --live.
#   --color         color label for the measurement batch.
#   --thresholds    ratchet threshold JSON (default tools/perf/loadtime-thresholds.json).
#   --apply         persist tightened/seeded ceilings back to the thresholds file
#                   (the daily schedule job passes this; ad-hoc/CI runs may omit it).
#   --source-task   guard task id to block on each filed regression task
#                   (default p8-daily-regression-guard).
#   --task-prefix   task-id prefix for filed breach tasks (default p8-daily-perf-regression).
#   --submodule     beehive submodule name (default phantom-library).
#   --no-file       detect + report breaches but do NOT run beehive task add/block
#                   (for local dry runs / CI where beehive is unavailable).
#
# Exit: 0 = no breach, 3 = breach (tasks filed unless --no-file), 2 = error.
# ---------------------------------------------------------------------------
set -euo pipefail

ROOT=${PHANTOM_REPO_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
ENGINE="$ROOT/tools/rig-scenarios/47-loadtime-flows.sh"
GUARD_DIR="$ROOT/tools/perf/ratchet-guard"
CONVERTER="$ROOT/tools/perf/loadtime-expo-to-measurements.py"
THRESHOLDS="$ROOT/tools/perf/loadtime-thresholds.json"
SUBMODULE="phantom-library"
SOURCE_TASK="p8-daily-regression-guard"
TASK_PREFIX="p8-daily-perf-regression"
LIVE=0
APPLY=0
NO_FILE=0
API=""
TOKEN=""
COLOR=""

die() { echo "loadtime-guard.sh: $*" >&2; exit 2; }

while [ $# -gt 0 ]; do
  case "$1" in
    --live)         LIVE=1; shift ;;
    --api)          API=${2:?}; shift 2 ;;
    --token)        TOKEN=${2:?}; shift 2 ;;
    --color)        COLOR=${2:?}; shift 2 ;;
    --thresholds)   THRESHOLDS=${2:?}; shift 2 ;;
    --apply)        APPLY=1; shift ;;
    --source-task)  SOURCE_TASK=${2:?}; shift 2 ;;
    --task-prefix)  TASK_PREFIX=${2:?}; shift 2 ;;
    --submodule)    SUBMODULE=${2:?}; shift 2 ;;
    --no-file)      NO_FILE=1; shift ;;
    -h|--help)      sed -n '2,45p' "$0"; exit 0 ;;
    *) die "unknown argument '$1'" ;;
  esac
done

[ -f "$ENGINE" ]     || die "measurement engine not found: $ENGINE"
[ -f "$CONVERTER" ]  || die "converter not found: $CONVERTER"
[ -f "$THRESHOLDS" ] || die "thresholds file not found: $THRESHOLDS"
command -v python3 >/dev/null 2>&1 || die "python3 is required"

EXPO=$(mktemp -t loadtime-expo.XXXXXX.txt)
MEASUREMENTS=$(mktemp -t loadtime-measurements.XXXXXX.json)
PLAN=$(mktemp -t loadtime-plan.XXXXXX.json)
cleanup() { rm -f "$EXPO" "$MEASUREMENTS" "$PLAN"; }
trap cleanup EXIT INT TERM

# --- 1. measure ---------------------------------------------------------
engine_env=()
if [ "$LIVE" = 1 ]; then
  [ -n "$API" ] && engine_env+=("PHANTOM_LOADTIME_API=$API")
  [ -n "$TOKEN" ] && engine_env+=("PHANTOM_LOADTIME_TOKEN=$TOKEN")
else
  engine_env+=("PHANTOM_CI_DRYRUN=1")
fi
[ -n "$COLOR" ] && engine_env+=("PHANTOM_LOADTIME_COLOR=$COLOR")

env "${engine_env[@]}" bash "$ENGINE" > "$EXPO"

# --- 2. convert -----------------------------------------------------------
python3 "$CONVERTER" "$EXPO" "$MEASUREMENTS" || die "measurement conversion failed"

# --- 3. ratchet-guard (reuses the P5 tool + engine verbatim) --------------
guard_args=(--thresholds "$THRESHOLDS" --measurements "$MEASUREMENTS" --file-plan "$PLAN" --task-prefix "$TASK_PREFIX")
[ "$APPLY" = 1 ] && guard_args+=(--apply)

set +e
MSBUILDDISABLENODEREUSE=1 dotnet run --project "$GUARD_DIR" -c Release \
  -p:UseSharedCompilation=false --no-launch-profile -- "${guard_args[@]}"
rc=$?
set -e

if [ "$rc" = 0 ]; then
  echo "loadtime-guard: OK (no regression)"
  exit 0
fi

if [ "$rc" != 3 ]; then
  echo "loadtime-guard: tool error (exit $rc)" >&2
  exit 2
fi

echo "loadtime-guard: BREACH detected" >&2

if [ "$NO_FILE" = 1 ]; then
  echo "loadtime-guard: --no-file set; not filing beehive tasks" >&2
  exit 3
fi

if ! command -v beehive >/dev/null 2>&1; then
  echo "loadtime-guard: beehive CLI not on PATH; cannot auto-file. Plan:" >&2
  cat "$PLAN" >&2
  exit 3
fi

# --- 4. file one performance-review/improvement task per breached scenario,
# block the guard task on it. `beehive task add` is idempotent by id, so a
# standing regression re-files to the same task id rather than duplicating.
count=$(python3 -c 'import json,sys; print(len(json.load(open(sys.argv[1]))["entries"]))' "$PLAN")
for i in $(seq 0 $((count - 1))); do
  task_id=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["entries"][int(sys.argv[2])]["task_id"])' "$PLAN" "$i")
  title=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["entries"][int(sys.argv[2])]["title"])' "$PLAN" "$i")
  body=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["entries"][int(sys.argv[2])]["body"])' "$PLAN" "$i")

  body_file=$(mktemp -t loadtime-body.XXXXXX.md)
  { echo "# $title"; echo; echo "$body"; } > "$body_file"

  echo "loadtime-guard: filing $task_id"
  beehive task add "$SUBMODULE" "$task_id" --body-file "$body_file" \
    --check "dotnet test" || echo "loadtime-guard: task add for $task_id skipped (may already exist)" >&2
  beehive task block "$SUBMODULE" "$SOURCE_TASK" --on "$task_id" \
    || echo "loadtime-guard: task block $SOURCE_TASK on $task_id skipped (may already be set)" >&2
  rm -f "$body_file"
done

exit 3
