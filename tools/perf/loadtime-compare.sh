#!/usr/bin/env bash
# phantom load-time before/after comparison (P5 task p5-postgres-loadtime-comparison).
#
# Re-runs of the five instrumented browse flows produce two MeasurementSet JSON files:
#   * the SQLite baseline  (backend=sqlite)  — captured before the P4 Postgres cutover
#   * the Postgres "after"  (backend=postgres) — captured against the P4 Stage A deployment
#
# This wrapper runs the phantom-loadtime-compare tool over the two files, prints the
# measured per-flow delta (NEVER assuming Postgres is faster — a slower flow is reported
# as a regression), and optionally feeds the Postgres "after" numbers into the ratchet
# guard's threshold table (tools/perf/ratchet-thresholds.json) so
# p5-ratcheting-regression-guard guards the Postgres backend too.
#
# Usage:
#   tools/perf/loadtime-compare.sh <baseline-sqlite.json> <after-postgres.json> [--seed] [--apply]
#
#   --seed   seed the fed Postgres ceilings from the measured values (default: add unseeded)
#   --apply  persist the fed thresholds back into ratchet-thresholds.json
#
# Exit: 0 = ok, 3 = a flow regressed against the SQLite baseline, 2 = error.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
thresholds="$here/ratchet-thresholds.json"
proj="$here/loadtime-compare/PhantomLoadtimeCompare.csproj"

if [[ $# -lt 2 ]]; then
  echo "usage: $0 <baseline-sqlite.json> <after-postgres.json> [--seed] [--apply]" >&2
  exit 2
fi

baseline="$1"; shift
after="$1"; shift

extra=(--fail-on-regression --thresholds "$thresholds")
for arg in "$@"; do
  case "$arg" in
    --seed|--apply) extra+=("$arg") ;;
    *) echo "unknown flag: $arg" >&2; exit 2 ;;
  esac
done

cleanup_dotnet() { dotnet build-server shutdown >/dev/null 2>&1 || true; }
trap cleanup_dotnet EXIT INT TERM

MSBUILDDISABLENODEREUSE=1 exec dotnet run --project "$proj" -c Release -p:UseSharedCompilation=false -- \
  --baseline "$baseline" --after "$after" "${extra[@]}"
