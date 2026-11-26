#!/usr/bin/env bash
# Mandatory PostgreSQL + actual candidate .NET worker + movie/episode server parity.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
exec python3 "$ROOT/tools/rig-scenarios/availability-claims-rig.py"
