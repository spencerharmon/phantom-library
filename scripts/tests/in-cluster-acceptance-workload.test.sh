#!/usr/bin/env bash
# Exercise the real, non-dry-run entrypoint without cluster or network access.
# Optional arguments select case names; no arguments runs the entire matrix.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec python3 "$HERE/in-cluster-acceptance-workload.fixture.py" "$@"
