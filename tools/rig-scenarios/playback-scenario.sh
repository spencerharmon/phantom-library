#!/usr/bin/env bash
# Shared entrypoint; RIG_NO_RESET retains migration scenario 44's caller-owned rig.
set -euo pipefail
ROOT=${PHANTOM_REPO_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
if [[ ${RIG_NO_RESET:-0} != 1 ]]; then
  exec python3 "$ROOT/tools/rig-scenarios/isolated-playback-rig.py" --backend "${PHANTOM_RIG_BACKEND:-sqlite}" --scenario "$1"
fi
export JF_RIG_ROOT=${JF_RIG_ROOT:-/tmp/jf-rig}
export PHANTOM_RIG_API=${PHANTOM_RIG_API:-http://localhost:18096}
export PHANTOM_RIG_TOKEN=${PHANTOM_RIG_TOKEN:-testtoken00000000000000000000000}
export PHANTOM_RIG_PHDB=${PHANTOM_RIG_PHDB:-/var/tmp/jf-test/data/plugins/configurations/PhantomLibrary/phantom.db}
export PHANTOM_RIG_JDB=${PHANTOM_RIG_JDB:-/var/tmp/jf-test/data/data/jellyfin.db}
export PHANTOM_RIG_OUTPUT=${PHANTOM_RIG_OUTPUT:-$(mktemp -d "$JF_RIG_ROOT/logs/playback-$1-XXXXXXXX")}
exec python3 "$ROOT/tools/rig-scenarios/playback-scenarios.py" "$1"
