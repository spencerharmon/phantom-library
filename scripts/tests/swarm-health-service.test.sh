#!/usr/bin/env bash
# Offline regression test for the swarm-health DHT measurement service
# (tools/swarm-health/swarm_health_service.py): request validation, de-duplication
# and the Prometheus exposition. The plugin side is covered by
# tests/.../SwarmHealthTests.cs (+ SwarmHealthPostgresTests.cs on Postgres).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
svc="$here/../../tools/swarm-health/swarm_health_service.py"
python3 -m py_compile "$svc"
python3 "$svc" --selftest
echo "PASS swarm-health-service"
