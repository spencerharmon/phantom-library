#!/usr/bin/env bash
# Movie browse, native open, materialise, persisted metadata, stream and source replacement.
set -euo pipefail
ROOT=${PHANTOM_REPO_ROOT:-$(cd "$(dirname "$0")/../.." && pwd)}
exec bash "$ROOT/tools/rig-scenarios/playback-scenario.sh" movie
