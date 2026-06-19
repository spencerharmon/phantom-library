#!/usr/bin/env bash
set -euo pipefail
ROOT=${ROOT:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}
cd "$ROOT"
echo "profile-rig-materialise delegates to movie channel e2e playback scenario"
bash tools/rig-scenarios/35-channel-e2e-playback.sh
grep -R "Perf\|materialise\|Availability" /tmp/jf-test/log* /tmp/jf-rig/logs 2>/dev/null || true
