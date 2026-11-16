#!/usr/bin/env bash
set -euo pipefail
root="$(realpath "$(dirname "${BASH_SOURCE[0]}")/../..")"
python3 "$root/scripts/tests/p0-prepare-jellyfin.test.py"
