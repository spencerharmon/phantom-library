#!/usr/bin/env bash
# Mandatory integration check: no dry-run, no optional skip, both real server flows.
set -euo pipefail
ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
python3 "$ROOT/scripts/tests/playback-adapter.test.py"
exec python3 "$ROOT/tools/rig-scenarios/isolated-playback-rig.py" --backend postgres
