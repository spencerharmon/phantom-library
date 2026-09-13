#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/latest-media-home-load.test.sh
#
# In-repo regression harness for restore-latest-row-and-drop-folders and its
# live-rig proof (tools/rig-scenarios/50-latest-media-home-load.sh). The live
# rig itself (a real Jellyfin + plugin on :18096, driven end to end for movie
# AND TV) can only run on a host with the seeded rig (gitea-live-rig-job /
# in-cluster-acceptance-rig), exactly like every other tools/rig-scenarios/*.sh
# entry — see that script's own header and docs/agents/testing.md. This
# harness is the in-sandbox, no-cluster gate:
#
#   A. Scenario 50 exists, is executable, and is `bash -n` syntax-clean.
#   B. It refuses the production port :8096, brings the rig up via
#      rig-up.sh --reset, and drives BOTH channels (Phantom Movies + Phantom
#      Shows) — movie/TV parity is structurally present, never just claimed.
#   C. It actually exercises all three proof points the task requires: no
#      folder tiles at either channel root, the real Home "Latest" surface
#      (GET /Channels/Items/Latest) populates for both channels, and that
#      call is asserted against a bounded timeout (the O(catalogue)
#      regression guard).
#   D. The channel source no longer emits ChannelItemType.Folder from the
#      curated-row code path (both channels), confirming (1) of the task's
#      DO list independent of the live rig.
#   E. Both channels declare ISupportsLatestMedia and implement
#      GetLatestMedia, confirming (2) of the task's DO list independent of
#      the live rig.
#   F. The underlying unit-level coverage for the same behaviours the rig
#      asserts live — the O(recent) materialised-only fast path, the
#      Media-only (never Folder) shape of its output, and the
#      ISupportsLatestMedia/GetLatestMedia wiring — EXISTS in both channels'
#      test files and is exercised for real via `dotnet test` filtered to
#      exactly those cases (a full unfiltered `dotnet test` is the separate
#      C# build/test gate's job, per scripts/tests/recently-played.test.sh's
#      convention).
#
# Exit 0 = all assertions passed; non-zero on the first failure.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
SCENARIO="$REPO_ROOT/tools/rig-scenarios/50-latest-media-home-load.sh"
RIG_UP="$REPO_ROOT/tools/rig-scenarios/rig-up.sh"
MOVIES_CHANNEL="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Channels/PhantomMoviesChannel.cs"
SHOWS_CHANNEL="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Channels/PhantomShowsChannel.cs"
TESTS_CSPROJ="$REPO_ROOT/tests/Jellyfin.Plugin.PhantomLibrary.Tests/Jellyfin.Plugin.PhantomLibrary.Tests.csproj"

pass_count=0
fail_count=0
ok()    { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()   { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

[[ -f "$SCENARIO" ]] || fatal "scenario not found: $SCENARIO"
[[ -f "$RIG_UP" ]]   || fatal "rig-up.sh not found: $RIG_UP"
[[ -f "$MOVIES_CHANNEL" ]] || fatal "PhantomMoviesChannel.cs not found: $MOVIES_CHANNEL"
[[ -f "$SHOWS_CHANNEL" ]]  || fatal "PhantomShowsChannel.cs not found: $SHOWS_CHANNEL"

# =====================================================================
head_ "A. Scenario 50 exists, executable, syntax-clean"
if [[ -x "$SCENARIO" ]]; then ok "scenario is executable"; else bad "scenario not executable (chmod +x): $SCENARIO"; fi
if bash -n "$SCENARIO" 2>/tmp/latest-rig-syntax.err; then
    ok "scenario is bash -n clean"
else
    bad "scenario has a syntax error: $(cat /tmp/latest-rig-syntax.err)"
fi
rm -f /tmp/latest-rig-syntax.err

# =====================================================================
head_ "B. production-port refusal + rig-up + movie/TV parity structurally present"
if grep -qE ':8096' "$SCENARIO" && grep -qi 'refus' "$SCENARIO"; then
    ok "scenario explicitly refuses the production port :8096"
else
    bad "scenario does not explicitly refuse the production port :8096"
fi
if grep -q 'rig-up.sh' "$SCENARIO" && grep -q 'rig-down.sh' "$SCENARIO"; then
    ok "scenario brings the rig up and tears it down via rig-up.sh/rig-down.sh"
else
    bad "scenario does not use the standard rig-up.sh/rig-down.sh bring-up/teardown"
fi
if grep -q 'Phantom Movies' "$SCENARIO" && grep -q 'Phantom Shows' "$SCENARIO"; then
    ok "scenario resolves BOTH the movies and shows channels"
else
    bad "scenario does not resolve both channels — movie/TV parity violation"
fi

# =====================================================================
head_ "C. exercises all three proof points: no folders, Latest populates, bounded timeout"
if grep -q "IsFolder" "$SCENARIO" && grep -qE "assert not folders" "$SCENARIO"; then
    ok "scenario asserts no folder tiles at either channel root"
else
    bad "scenario does not assert the channel root is folder-free"
fi
if grep -q '/Channels/Items/Latest' "$SCENARIO"; then
    ok "scenario calls the real Home Latest surface (GET /Channels/Items/Latest)"
else
    bad "scenario does not call /Channels/Items/Latest"
fi
if grep -qE "LATEST_TIMEOUT_SECONDS" "$SCENARIO" && grep -qE "O\(catalogue\) regression" "$SCENARIO"; then
    ok "scenario bounds the Latest call to a timeout budget (O(catalogue) regression guard)"
else
    bad "scenario does not bound the Latest call to a timeout"
fi
if grep -q 'check_latest "\$CH_MOVIES"' "$SCENARIO" && grep -q 'check_latest "\$CH_SHOWS"' "$SCENARIO"; then
    ok "scenario checks the Latest surface for BOTH movie and TV channels"
else
    bad "scenario does not check Latest for both channels — movie/TV parity violation"
fi

# =====================================================================
head_ "D. no ChannelItemType.Folder emission left in the curated-row code path (both channels)"
for f in "$MOVIES_CHANNEL" "$SHOWS_CHANNEL"; do
    name=$(basename "$f")
    if grep -q 'CuratedRows.ToFolderItems' "$f"; then
        bad "$name still calls CuratedRows.ToFolderItems (category-folder emission not removed)"
    else
        ok "$name no longer calls CuratedRows.ToFolderItems"
    fi
done

# =====================================================================
head_ "E. ISupportsLatestMedia + GetLatestMedia re-added to both channels"
for f in "$MOVIES_CHANNEL" "$SHOWS_CHANNEL"; do
    name=$(basename "$f")
    if grep -qE '^\s*:\s*IChannel,.*ISupportsLatestMedia' "$f"; then
        ok "$name declares ISupportsLatestMedia"
    else
        bad "$name does not declare ISupportsLatestMedia"
    fi
    if grep -qE 'Task<IEnumerable<ChannelItemInfo>>\s+GetLatestMedia\(' "$f"; then
        ok "$name implements GetLatestMedia"
    else
        bad "$name does not implement GetLatestMedia"
    fi
done

# =====================================================================
head_ "F. real dotnet-test proof of the O(recent) fast path + ISupportsLatestMedia wiring"
if ! command -v dotnet >/dev/null 2>&1; then
    bad "'dotnet' not found in PATH — cannot run the regression test proof"
else
    FILTER='FullyQualifiedName~LatestRefreshRootQuery|FullyQualifiedName~GetLatestMedia|FullyQualifiedName~ImplementsISupportsLatestMedia'
    if MSBUILDDISABLENODEREUSE=1 dotnet test "$TESTS_CSPROJ" -c Release -p:UseSharedCompilation=false \
        --filter "$FILTER" > /tmp/latest-media-dotnet-test.log 2>&1; then
        if grep -qE 'Passed:\s*[1-9][0-9]*' /tmp/latest-media-dotnet-test.log; then
            ok "dotnet test (filtered to the new latest-media/fast-path cases) passed: $(grep -E 'Passed!' /tmp/latest-media-dotnet-test.log | tail -1)"
        else
            bad "dotnet test reported success but matched zero tests — filter is stale: $FILTER"
        fi
    else
        bad "dotnet test (filtered to the new latest-media/fast-path cases) FAILED — see /tmp/latest-media-dotnet-test.log"
        tail -n 40 /tmp/latest-media-dotnet-test.log >&2 || true
    fi
    dotnet build-server shutdown >/dev/null 2>&1 || true
fi

# =====================================================================
printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[[ "$fail_count" -eq 0 ]]
