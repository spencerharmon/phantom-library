#!/bin/sh
# Compatibility entrypoint for existing CI: the old fake/offline selftest is gone.
set -eu
exec ./scripts/tests/p05-swarm-health-service-contract.test.sh "$@"
