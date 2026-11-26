#!/usr/bin/env bash
# Series/season/episode browse, badge scope, native open, persisted metadata and stream.
set -euo pipefail
ROOT=${PHANTOM_REPO_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
exec bash "$ROOT/tools/rig-scenarios/playback-scenario.sh" episode
