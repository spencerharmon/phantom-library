#!/bin/sh
# Real process + maintained libtorrent + controlled UDP DHT peers. No mocks of LT.
set -eu
if ! pkg-config --exists libtorrent-rasterbar 2>/dev/null || ! command -v c++ >/dev/null; then
    podman build -q -t localhost/swarm-health-contract-test -f tools/swarm-health/Dockerfile.test .
    exec podman run --rm -v "$PWD:/src:ro" localhost/swarm-health-contract-test \
        ./scripts/tests/p05-swarm-health-service-contract.test.sh "$@"
fi
scratch=$(mktemp -d)
trap 'rm -rf "$scratch"' EXIT HUP INT TERM
OUTPUT="$scratch/dht-bridge" sh tools/swarm-health/build-bridge.sh
export DHT_BRIDGE="$scratch/dht-bridge" PYTHONDONTWRITEBYTECODE=1
python3 scripts/tests/swarm_health_integration.py "$@"
