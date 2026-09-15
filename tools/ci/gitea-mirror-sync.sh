#!/usr/bin/env bash
# tools/ci/gitea-mirror-sync.sh
#
# ROI Priority 9 fix (task ttfb-daily-rig-gitea-mirror-wiring). GitHub never
# reads .gitea/workflows/*, so every .gitea/workflows/*.yaml trigger in this
# repo (including the daily phantom-loadtime-daily rig) has nowhere to run
# until phantom-library exists as a Gitea repo. This script pushes the
# tracked branch's current tip to that Gitea mirror so a Gitea Actions
# workflow can actually fire.
#
# This is a THIN sync helper only: it never bakes in a hostname/URL (the
# infra-identifier rule) — the mirror remote URL comes entirely from the
# env var below, which is operator-configured per install (see LOCALS.md /
# the beehive layer, never this tracked file).
#
# Env:
#   PHANTOM_GITEA_REMOTE_URL   (required) - the Gitea mirror's git remote
#                              URL, e.g. https://git.example.com/org/repo.git
#                              or an ssh URL. Never hardcode a real one here.
#   PHANTOM_GITEA_MIRROR_NAME  (optional) - local remote name to use/create,
#                              default "gitea-mirror".
#   PHANTOM_GITEA_SYNC_BRANCH  (optional) - branch to sync, default the repo's
#                              current branch (falls back to "main").
#
# Usage:
#   PHANTOM_GITEA_REMOTE_URL=<url> ./tools/ci/gitea-mirror-sync.sh
#
# Exit codes: 0 on success (including the idempotent no-op case where the
# mirror is already at the local tip); non-zero with a clear message if
# PHANTOM_GITEA_REMOTE_URL is unset, or if the push itself fails.

set -euo pipefail

# Operates on whatever git working tree the caller invoked it from (the CWD)
# — never hardcodes this script's own on-disk location as the repo root, so
# it works both as `./tools/ci/gitea-mirror-sync.sh` from a real checkout and
# under test against a throwaway synthetic repo.
REPO_ROOT="$(git rev-parse --show-toplevel 2>/dev/null || true)"
if [ -z "$REPO_ROOT" ]; then
  echo "gitea-mirror-sync: FATAL: not inside a git working tree (run this from a repo checkout)." >&2
  exit 1
fi

MIRROR_NAME="${PHANTOM_GITEA_MIRROR_NAME:-gitea-mirror}"

if [ -z "${PHANTOM_GITEA_REMOTE_URL:-}" ]; then
  echo "gitea-mirror-sync: FATAL: PHANTOM_GITEA_REMOTE_URL is not set." >&2
  echo "  This must be the Gitea mirror's git remote URL (operator-provided," >&2
  echo "  e.g. via a CI secret/var or a local shell export). Refusing to guess." >&2
  exit 1
fi

cd "$REPO_ROOT"

BRANCH="${PHANTOM_GITEA_SYNC_BRANCH:-}"
if [ -z "$BRANCH" ]; then
  BRANCH="$(git rev-parse --abbrev-ref HEAD 2>/dev/null || true)"
  if [ -z "$BRANCH" ] || [ "$BRANCH" = "HEAD" ]; then
    BRANCH="main"
  fi
fi

if git remote get-url "$MIRROR_NAME" >/dev/null 2>&1; then
  current_url="$(git remote get-url "$MIRROR_NAME")"
  if [ "$current_url" != "$PHANTOM_GITEA_REMOTE_URL" ]; then
    echo "gitea-mirror-sync: updating existing remote '$MIRROR_NAME' URL"
    git remote set-url "$MIRROR_NAME" "$PHANTOM_GITEA_REMOTE_URL"
  fi
else
  echo "gitea-mirror-sync: adding remote '$MIRROR_NAME' -> $PHANTOM_GITEA_REMOTE_URL"
  git remote add "$MIRROR_NAME" "$PHANTOM_GITEA_REMOTE_URL"
fi

local_sha="$(git rev-parse HEAD)"

# Idempotency check: if the mirror already has this exact sha on the target
# branch, skip the push entirely (a genuine no-op, not a failed/odd push).
remote_sha=""
if remote_refs="$(git ls-remote "$MIRROR_NAME" "refs/heads/$BRANCH" 2>/dev/null)"; then
  remote_sha="$(printf '%s' "$remote_refs" | awk '{print $1}' | head -n1)"
fi

if [ -n "$remote_sha" ] && [ "$remote_sha" = "$local_sha" ]; then
  echo "gitea-mirror-sync: mirror '$MIRROR_NAME' already at $local_sha on $BRANCH; no-op"
  exit 0
fi

echo "gitea-mirror-sync: pushing HEAD ($local_sha) -> $MIRROR_NAME $BRANCH"
git push "$MIRROR_NAME" "HEAD:refs/heads/$BRANCH"

echo "gitea-mirror-sync: done"
