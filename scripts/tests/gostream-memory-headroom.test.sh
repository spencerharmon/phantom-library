#!/bin/sh
set -eu
exec python3 "$(dirname "$0")/gostream-memory-headroom.py" "$@"
