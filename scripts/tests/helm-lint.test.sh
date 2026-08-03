#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/helm-lint.test.sh
#
# Definition-of-done check for the phantom-library Helm chart source
# (task phantom-library-helm-chart-source and any task touching
# deploy/helm/phantom-library). Wraps `helm lint` but first locates a
# working `helm` binary rather than trusting a bare `helm` on PATH:
#
#   1. `helm` on PATH (fastest path when the check runs unsandboxed or the
#      environment already has it).
#   2. Common per-user/system install locations, in case PATH itself is
#      stripped but the filesystem location is still reachable.
#   3. A vendored `helm` release, extracted (once, cached) from the tarball
#      committed at scripts/tests/vendor/helm-v3.16.3-linux-amd64.tar.gz.
#      This is the load-bearing fallback: the beehive DoD-check sandbox is
#      filesystem-confined (bubblewrap) to this submodule's own checkout (+
#      linked submodules + declared read paths), so it does NOT bind a
#      per-user path like `~/.local/bin` even when helm is genuinely
#      installed there on the host — "helm: command not found" persists
#      regardless of PATH. Vendoring the release inside the submodule's own
#      tracked tree makes the check hermetic: it always has a helm binary
#      available inside its own sandboxed filesystem, no host install or
#      network egress required.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CHART_PATH="${1:-deploy/helm/phantom-library}"
VENDOR_TARBALL="$SCRIPT_DIR/vendor/helm-v3.16.3-linux-amd64.tar.gz"
CACHE_DIR="${TMPDIR:-/tmp}/phantom-library-helm-lint-vendor-cache"

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

  if [ -f "$VENDOR_TARBALL" ]; then
    if [ ! -x "$CACHE_DIR/linux-amd64/helm" ]; then
      mkdir -p "$CACHE_DIR"
      tar -xzf "$VENDOR_TARBALL" -C "$CACHE_DIR"
    fi
    if [ -x "$CACHE_DIR/linux-amd64/helm" ]; then
      echo "$CACHE_DIR/linux-amd64/helm"
      return 0
    fi
  fi

  return 1
}

HELM_BIN="$(resolve_helm)" || {
  echo "helm-lint.test.sh: no working 'helm' binary found on PATH, in common install locations, or via the vendored release" >&2
  exit 127
}

exec "$HELM_BIN" lint "$CHART_PATH"
