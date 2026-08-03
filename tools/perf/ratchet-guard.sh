#!/usr/bin/env bash
# Ratcheting performance-regression guard runner + auto-filer.
#
# Wraps the phantom-ratchet-guard tool. Given a perf run's measurement JSON,
# it ratchets the recorded per-scenario ceilings (tighten-on-improvement, never
# loosen) and, on a breach, FILES a beehive performance-review task per breached
# scenario and BLOCKS the guard task on it — so a regression is escalated to the
# swarm, never silently accepted.
#
# Usage:
#   ratchet-guard.sh --measurements <file> [--thresholds <file>] [--apply]
#                    [--source-task <id>] [--submodule <name>] [--no-file]
#
#   --measurements  perf-run measurement JSON (required).
#   --thresholds    ratchet threshold JSON (default: tools/perf/ratchet-thresholds.json).
#   --apply         persist tightened/seeded ceilings back to the thresholds file.
#   --source-task   the guard task id to block on each filed regression task
#                   (default: p5-ratcheting-regression-guard).
#   --submodule     beehive submodule name (default: phantom-library).
#   --no-file       detect + report breaches but do NOT run beehive task add/block
#                   (for local dry runs / CI where beehive is unavailable).
#
# Exit: 0 = no breach, 3 = breach (tasks filed unless --no-file), 2 = error.
set -euo pipefail

ROOT=${PHANTOM_REPO_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
GUARD_DIR="$ROOT/tools/perf/ratchet-guard"
THRESHOLDS="$ROOT/tools/perf/ratchet-thresholds.json"
MEASUREMENTS=""
SUBMODULE="phantom-library"
SOURCE_TASK="p5-ratcheting-regression-guard"
APPLY=0
NO_FILE=0

die() { echo "ratchet-guard.sh: $*" >&2; exit 2; }

while [ $# -gt 0 ]; do
  case "$1" in
    --measurements) MEASUREMENTS=${2:?}; shift 2 ;;
    --thresholds)   THRESHOLDS=${2:?}; shift 2 ;;
    --source-task)  SOURCE_TASK=${2:?}; shift 2 ;;
    --submodule)    SUBMODULE=${2:?}; shift 2 ;;
    --apply)        APPLY=1; shift ;;
    --no-file)      NO_FILE=1; shift ;;
    -h|--help)      sed -n '2,30p' "$0"; exit 0 ;;
    *) die "unknown argument '$1'" ;;
  esac
done

[ -n "$MEASUREMENTS" ] || die "--measurements is required"
[ -f "$MEASUREMENTS" ] || die "measurements file not found: $MEASUREMENTS"
[ -f "$THRESHOLDS" ]  || die "thresholds file not found: $THRESHOLDS"

PLAN=$(mktemp -t ratchet-plan.XXXXXX.json)
cleanup() { rm -f "$PLAN"; }
trap cleanup EXIT INT TERM

guard_args=(--thresholds "$THRESHOLDS" --measurements "$MEASUREMENTS" --file-plan "$PLAN")
[ "$APPLY" = 1 ] && guard_args+=(--apply)

set +e
MSBUILDDISABLENODEREUSE=1 dotnet run --project "$GUARD_DIR" -c Release \
  -p:UseSharedCompilation=false --no-launch-profile -- "${guard_args[@]}"
rc=$?
set -e

if [ "$rc" = 0 ]; then
  echo "ratchet-guard: OK (no regression)"
  exit 0
fi

if [ "$rc" != 3 ]; then
  echo "ratchet-guard: tool error (exit $rc)" >&2
  exit 2
fi

echo "ratchet-guard: BREACH detected" >&2

if [ "$NO_FILE" = 1 ]; then
  echo "ratchet-guard: --no-file set; not filing beehive tasks" >&2
  exit 3
fi

if ! command -v beehive >/dev/null 2>&1; then
  echo "ratchet-guard: beehive CLI not on PATH; cannot auto-file. Plan:" >&2
  cat "$PLAN" >&2
  exit 3
fi

# File one performance-review task per breached scenario and block the guard task
# on it. `beehive task add` is idempotent by id, so a standing regression re-files
# to the same task id rather than duplicating each run.
count=$(python3 -c 'import json,sys; print(len(json.load(open(sys.argv[1]))["entries"]))' "$PLAN")
for i in $(seq 0 $((count - 1))); do
  task_id=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["entries"][int(sys.argv[2])]["task_id"])' "$PLAN" "$i")
  title=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["entries"][int(sys.argv[2])]["title"])' "$PLAN" "$i")
  body=$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["entries"][int(sys.argv[2])]["body"])' "$PLAN" "$i")

  body_file=$(mktemp -t ratchet-body.XXXXXX.md)
  { echo "# $title"; echo; echo "$body"; } > "$body_file"

  echo "ratchet-guard: filing $task_id"
  beehive task add "$SUBMODULE" "$task_id" --body-file "$body_file" \
    --check "dotnet test" || echo "ratchet-guard: task add for $task_id skipped (may already exist)" >&2
  beehive task block "$SUBMODULE" "$SOURCE_TASK" --on "$task_id" \
    || echo "ratchet-guard: task block $SOURCE_TASK on $task_id skipped (may already be set)" >&2
  rm -f "$body_file"
done

exit 3
