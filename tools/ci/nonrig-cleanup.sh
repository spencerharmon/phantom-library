#!/usr/bin/env bash
# tools/ci/nonrig-cleanup.sh
#
# Zuul post-run cleanup hook (playbooks/phantom-library-nonrig-cleanup.yaml)
# and a manual "leave no dotnet build server behind" helper. Thin wrapper over
# the shared cleanup used by the build-test trap, so the guaranteed-cleanup and
# happy-path cleanup are identical code.
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

# shellcheck source=tools/ci/lib-cleanup.sh
. "$REPO_ROOT/tools/ci/lib-cleanup.sh"

# Post-run must not be the thing that fails a job on its own; the run script's
# trap is the authoritative leftover gate. Report only here.
PHANTOM_CI_STRICT_LEFTOVERS=0 phantom_ci_cleanup_dotnet 0
