#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/helm-lint.sh
#
# Definition-of-done check for the phantom-library Helm chart source
# (task phantom-library-helm-chart-source and any task touching
# deploy/helm/phantom-library). Wraps `helm lint` but first locates a
# working `helm` binary rather than trusting a bare `helm` on PATH: the
# beehive DoD-check sandbox does not always inherit a user's shell PATH
# (e.g. a helm installed under a per-user `~/.local/bin` is invisible to
# a bwrap-jailed check even though the same host has it installed), so a
# bare `helm lint ...` Check can fail with "command not found" despite
# helm being present on the machine. This resolves helm the same way a
# real operator/CI would: PATH first, then the common per-user and
# system install locations, in order.
set -euo pipefail

CHART_PATH="${1:-deploy/helm/phantom-library}"

resolve_helm() {
  if command -v helm >/dev/null 2>&1; then
    command -v helm
    return 0
  fi
  local candidate
  for candidate in \
    "$HOME/.local/bin/helm" \
    "/usr/local/bin/helm" \
    "/usr/bin/helm" \
    "/opt/homebrew/bin/helm"
  do
    if [ -x "$candidate" ]; then
      echo "$candidate"
      return 0
    fi
  done
  return 1
}

HELM_BIN="$(resolve_helm)" || {
  echo "helm-lint.sh: no working 'helm' binary found on PATH or in common install locations" >&2
  exit 127
}

exec "$HELM_BIN" lint "$CHART_PATH"
