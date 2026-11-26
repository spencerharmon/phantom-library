#!/bin/sh
# script-test: real locked image, real HTTP/native UDP, no external runtime network.
set -eu
exec python3 scripts/tests/swarm_health_image.py
