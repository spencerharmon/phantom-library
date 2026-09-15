#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/gitea-mirror-sync.test.sh
#
# Regression harness for tools/ci/gitea-mirror-sync.sh (task
# ttfb-daily-rig-gitea-mirror-wiring). Proves the sync script:
#
#   1. Fails CLEARLY (non-zero exit, actionable message) when
#      PHANTOM_GITEA_REMOTE_URL is unset.
#   2. Pushes the tracked branch's current tip to a synthetic (throwaway,
#      local bare-repo) mirror correctly.
#   3. Is IDEMPOTENT: running it again with no new commits is a clean no-op
#      that still exits 0 and leaves the mirror at the same tip.
#
# Uses only bash + git against throwaway repos under a mktemp dir - no
# network access, no real Gitea instance, no dependency on this repo's own
# git state (never mutates it).
# ---------------------------------------------------------------------------

set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
SCRIPT="$REPO_ROOT/tools/ci/gitea-mirror-sync.sh"

pass_count=0
fail_count=0

ok()   { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()  { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_(){ printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal(){ printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

[ -x "$SCRIPT" ] || fatal "missing or non-executable $SCRIPT"

WORK="$(mktemp -d)"
cleanup() { rm -rf "$WORK"; }
trap cleanup EXIT

# --- fixture: a synthetic "source" repo (stand-in for this checkout) -------
SRC="$WORK/src-repo"
mkdir -p "$SRC"
git -C "$SRC" init -q -b main
git -C "$SRC" config user.email "test@example.com"
git -C "$SRC" config user.name "test"
echo "hello" > "$SRC/README.md"
git -C "$SRC" add -A
git -C "$SRC" commit -q -m "initial commit"

# --- fixture: a synthetic "Gitea mirror" as a local bare repo ---------------
MIRROR="$WORK/mirror.git"
git init -q --bare "$MIRROR"

head_ "1. fails clearly with no PHANTOM_GITEA_REMOTE_URL"
set +e
out="$(cd "$SRC" && env -u PHANTOM_GITEA_REMOTE_URL -u PHANTOM_GITEA_MIRROR_NAME -u PHANTOM_GITEA_SYNC_BRANCH bash "$SCRIPT" 2>&1)"
rc=$?
set -e
if [ "$rc" -ne 0 ]; then
  ok "non-zero exit ($rc) with no remote URL configured"
else
  bad "expected non-zero exit with no PHANTOM_GITEA_REMOTE_URL set, got 0"
fi
if printf '%s' "$out" | grep -qi "PHANTOM_GITEA_REMOTE_URL"; then
  ok "error message names the missing env var"
else
  bad "error message did not mention PHANTOM_GITEA_REMOTE_URL: $out"
fi

head_ "2. pushes the tracked branch tip to a synthetic mirror correctly"
set +e
out="$(cd "$SRC" && PHANTOM_GITEA_REMOTE_URL="$MIRROR" PHANTOM_GITEA_SYNC_BRANCH=main bash "$SCRIPT" 2>&1)"
rc=$?
set -e
if [ "$rc" -eq 0 ]; then
  ok "script exits 0 on a normal push"
else
  bad "script exited $rc on a normal push: $out"
fi

local_sha="$(git -C "$SRC" rev-parse HEAD)"
mirror_sha="$(git -C "$MIRROR" rev-parse refs/heads/main 2>/dev/null || echo "MISSING")"
if [ "$mirror_sha" = "$local_sha" ]; then
  ok "mirror's main is now at the source tip ($local_sha)"
else
  bad "mirror tip ($mirror_sha) != source tip ($local_sha)"
fi

head_ "3. idempotent on a second no-op run"
set +e
out2="$(cd "$SRC" && PHANTOM_GITEA_REMOTE_URL="$MIRROR" PHANTOM_GITEA_SYNC_BRANCH=main bash "$SCRIPT" 2>&1)"
rc2=$?
set -e
if [ "$rc2" -eq 0 ]; then
  ok "second run (no new commits) exits 0"
else
  bad "second run exited $rc2: $out2"
fi
if printf '%s' "$out2" | grep -qi "no-op"; then
  ok "second run reports a no-op instead of re-pushing"
else
  bad "second run did not report a no-op: $out2"
fi
mirror_sha2="$(git -C "$MIRROR" rev-parse refs/heads/main 2>/dev/null || echo "MISSING")"
if [ "$mirror_sha2" = "$local_sha" ]; then
  ok "mirror tip unchanged after the idempotent run"
else
  bad "mirror tip drifted after idempotent run: $mirror_sha2 != $local_sha"
fi

head_ "4. picks up a NEW commit on a subsequent run"
echo "more" >> "$SRC/README.md"
git -C "$SRC" add -A
git -C "$SRC" commit -q -m "second commit"
new_sha="$(git -C "$SRC" rev-parse HEAD)"
set +e
out3="$(cd "$SRC" && PHANTOM_GITEA_REMOTE_URL="$MIRROR" PHANTOM_GITEA_SYNC_BRANCH=main bash "$SCRIPT" 2>&1)"
rc3=$?
set -e
if [ "$rc3" -eq 0 ]; then
  ok "run after a new commit exits 0"
else
  bad "run after a new commit exited $rc3: $out3"
fi
mirror_sha3="$(git -C "$MIRROR" rev-parse refs/heads/main 2>/dev/null || echo "MISSING")"
if [ "$mirror_sha3" = "$new_sha" ]; then
  ok "mirror advanced to the new tip ($new_sha)"
else
  bad "mirror ($mirror_sha3) did not advance to the new tip ($new_sha)"
fi

head_ "summary"
echo "  pass=$pass_count fail=$fail_count"
if [ "$fail_count" -ne 0 ]; then
  exit 1
fi
exit 0
