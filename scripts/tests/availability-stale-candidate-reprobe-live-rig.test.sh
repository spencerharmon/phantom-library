#!/usr/bin/env bash
# No skip or structural-only success: real PostgreSQL, candidate DLL and REST.
set -euo pipefail
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
python3 "$ROOT/scripts/tests/stale-candidate-harness.test.py"
exec python3 "$ROOT/tools/rig-scenarios/isolated-playback-rig.py" --backend postgres --suite stale-candidate
