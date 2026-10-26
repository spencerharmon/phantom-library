#!/usr/bin/env bash
# Missing deployment configuration must fail before attempting registry access.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
TARGET="$ROOT/scripts/tests/loadtime-thresholds-chart-published.test.sh"
fail() { echo "FAIL: $*" >&2; exit 1; }

# Never contact a real registry, even when testing the old defaulting behavior.
helm() { echo "unexpected helm invocation" >&2; return 99; }
export -f helm

for state in unset empty; do
  if [[ "$state" == unset ]]; then
    unset REGISTRY
  else
    export REGISTRY=""
  fi
  if output="$(bash "$TARGET" 2>&1)"; then
    fail "$state REGISTRY was accepted"
  fi
  [[ "$output" == 'FAIL: REGISTRY is required (e.g. oci://registry.example.com/charts)' ]] \
    || fail "$state REGISTRY did not fail early with configuration guidance: $output"
  echo "ok: $state REGISTRY rejected before Helm access"
done
echo "ALL PASS"
