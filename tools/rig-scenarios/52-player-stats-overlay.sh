#!/usr/bin/env bash
# Mandatory isolated :18096 movie AND episode/browser proof, no production seed.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
export PYTHONDONTWRITEBYTECODE=1
npm ci --prefix "$ROOT/tools/rig-scenarios/browser" --ignore-scripts --no-audit --no-fund
if [[ -z "${PHANTOM_P14_CHROMIUM:-}" ]]; then
    if command -v chromium >/dev/null; then
        export PHANTOM_P14_CHROMIUM
        PHANTOM_P14_CHROMIUM="$(command -v chromium)"
    else
        "$ROOT/tools/rig-scenarios/browser/node_modules/.bin/playwright" install chromium
    fi
fi
python3 "$ROOT/tools/rig-scenarios/isolated-playback-rig.py" --backend sqlite --player-stats
printf 'PLAYER_STATS_OVERLAY_OK movie=passed episode=passed browser=passed\n'
