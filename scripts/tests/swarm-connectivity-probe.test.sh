#!/usr/bin/env bash
# Offline regression test for the swarm-connectivity A/B probe
# (tools/rig-scenarios/51-swarm-connectivity.py): aggregation, quantiles, and the
# Pushgateway exposition it emits. The live measurement runs in-cluster on a schedule.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
python3 "$here/../../tools/rig-scenarios/51-swarm-connectivity.py" --selftest
python3 -m py_compile "$here/../../tools/rig-scenarios/51-swarm-connectivity.py"
echo "PASS swarm-connectivity-probe"
