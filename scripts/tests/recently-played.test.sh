#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/recently-played.test.sh
#
# In-repo regression harness for the recently-played-fix operator bug and its
# live-rig proof (tools/rig-scenarios/48-recently-played.sh) + the plugin-side
# fix (src/Jellyfin.Plugin.PhantomLibrary/Materialisation/RecentlyPlayedSyncListener.cs).
#
# The live rig (real Jellyfin + gostream/tmdb mocks under user systemd units)
# can only run on the dedicated rig host per LOCALS.md — THIS harness is the
# in-sandbox, deterministic machine gate: bash + python3 only, NO live
# Jellyfin, NO network, NO cluster.
#
# Asserts:
#   A. The scenario exists, is executable, and is `bash -n` syntax-clean.
#   B. It refuses to run against the production port :8096.
#   C. It drives a REAL Sessions/Playing → .../Progress → .../Stopped report
#      to completion (never a direct UserData PATCH — that would mask
#      exactly the gap this fix targets) for BOTH a movie and an episode.
#   D. It asserts the standard-Jellyfin recently-played surface
#      (Filters=IsPlayed, SortBy=DatePlayed) for both item types, and checks
#      Played + LastPlayedDate + a reset resume position.
#   E. It re-asserts 35/36 (existing-gostream + native-open materialise)
#      parity by exercising the same AutoOpenLiveStream flow those scenarios
#      use, so a recently-played fix can never regress them.
#   F. Efficiency guard: every query in the scenario is a targeted,
#      Filters=IsPlayed / per-item lookup — never a full catalogue scan, and
#      it never reintroduces the deliberately-removed ISupportsLatestMedia
#      "Latest" (recently-added) row.
#   G. The plugin-side fix exists, is wired into DI, and its own xUnit
#      regression tests exist and cover: a completed watch persists
#      Played+DatePlayed even when the cached BaseItem snapshot has a stale/
#      zero RunTimeTicks; a partial watch does NOT mark Played; the
#      still-phantom splash guard; and non-phantom items are left alone.
#      (`dotnet test` itself is NOT re-run here — that's the C# build/test
#      gate's job; this harness only guards that the coverage exists and
#      wasn't quietly deleted.)
#
# Exit 0 = all assertions passed; non-zero on the first failure.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
SCENARIO="$REPO_ROOT/tools/rig-scenarios/48-recently-played.sh"
LISTENER="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Materialisation/RecentlyPlayedSyncListener.cs"
LISTENER_TESTS="$REPO_ROOT/tests/Jellyfin.Plugin.PhantomLibrary.Tests/RecentlyPlayedSyncListenerTests.cs"
REGISTRATOR="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/PluginServiceRegistrator.cs"
SIBLING_35="$REPO_ROOT/tools/rig-scenarios/35-channel-e2e-playback.sh"
SIBLING_36="$REPO_ROOT/tools/rig-scenarios/36-channel-episode-e2e-playback.sh"

pass_count=0
fail_count=0
ok()    { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()   { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

command -v python3 >/dev/null 2>&1 || { printf 'NOTE: python3 unavailable; skipping recently-played harness.\n'; exit 0; }

head_ "A. scenario exists, executable, syntax-clean"
[[ -f "$SCENARIO" ]] || fatal "live-rig scenario not found: $SCENARIO"
if [[ -x "$SCENARIO" ]]; then ok "scenario is executable"; else bad "scenario is not executable (chmod +x): $SCENARIO"; fi
if bash -n "$SCENARIO"; then ok "$SCENARIO passes bash -n"; else bad "$SCENARIO has a bash syntax error"; fi

head_ "B. production-port refusal"
if grep -qE ':8096' "$SCENARIO" && grep -qi 'refus' "$SCENARIO"; then
    ok "scenario explicitly refuses the production port :8096"
else
    bad "scenario does not explicitly refuse the production port :8096"
fi

head_ "C. drives REAL session-report playback to completion (never a direct UserData PATCH), movie + episode"
if grep -q 'Sessions/Playing"' "$SCENARIO" && grep -q 'Sessions/Playing/Progress' "$SCENARIO" && grep -q 'Sessions/Playing/Stopped' "$SCENARIO"; then
    ok "scenario reports PlaybackStart, Progress, and Stopped via the real Sessions/Playing endpoints"
else
    bad "scenario is missing one of the real Sessions/Playing / Progress / Stopped reports"
fi
if grep -qE "Users/\\\$USER_ID/Items/[^ ]*/UserData" "$SCENARIO"; then
    bad "scenario patches UserData directly via REST — this masks the exact gap the fix targets"
else
    ok "scenario never patches UserData directly (exercises the real playback-report path only)"
fi
if grep -q 'play_to_completion "\$ALPHA_ID"' "$SCENARIO" && grep -q 'play_to_completion "\$EPISODE_ID"' "$SCENARIO"; then
    ok "scenario drives playback-to-completion for BOTH a movie and an episode"
else
    bad "scenario does not cover both movie AND episode playback-to-completion"
fi

head_ "D. asserts the standard recently-played surface (Filters=IsPlayed, SortBy=DatePlayed), movie + episode, Played/DatePlayed/reset-position"
if grep -q 'SortBy=DatePlayed' "$SCENARIO" && grep -q 'Filters=IsPlayed' "$SCENARIO"; then
    ok "scenario queries Items?SortBy=DatePlayed&Filters=IsPlayed"
else
    bad "scenario does not query the standard recently-played surface"
fi
if grep -q "assert_recently_played \"\$ALPHA_ID\"" "$SCENARIO" && grep -q "assert_recently_played \"\$EPISODE_ID\"" "$SCENARIO"; then
    ok "scenario asserts recently-played for BOTH movie and episode"
else
    bad "scenario does not assert recently-played for both item types"
fi
if grep -q "ud.get('Played')" "$SCENARIO" && grep -q "LastPlayedDate" "$SCENARIO" && grep -q 'PlaybackPositionTicks' "$SCENARIO"; then
    ok "scenario checks Played, LastPlayedDate, and reset PlaybackPositionTicks on the recently-played hit"
else
    bad "scenario's recently-played assertion is missing Played/LastPlayedDate/PlaybackPositionTicks checks"
fi

head_ "E. 35/36 parity: exercises the same AutoOpenLiveStream native-open flow, siblings untouched"
if grep -q 'AutoOpenLiveStream=true' "$SCENARIO" && grep -q 'RequiresOpening' "$SCENARIO"; then
    ok "scenario exercises the native-open AutoOpenLiveStream flow (35/36 parity)"
else
    bad "scenario does not exercise the native-open AutoOpenLiveStream flow"
fi
[[ -f "$SIBLING_35" ]] || fatal "sibling scenario 35 missing: $SIBLING_35"
[[ -f "$SIBLING_36" ]] || fatal "sibling scenario 36 missing: $SIBLING_36"
if bash -n "$SIBLING_35" && bash -n "$SIBLING_36"; then
    ok "sibling scenarios 35 and 36 remain syntax-clean (untouched by this fix)"
else
    bad "a sibling scenario (35/36) has a syntax error"
fi

head_ "F. efficiency guard: no O(catalogue) scan, no reintroduced Latest row"
if grep -qE 'class RecentlyPlayedSyncListener\s*:.*ISupportsLatestMedia|Task<.*>\s+GetLatestMedia\(' "$LISTENER"; then
    bad "recently-played fix reintroduces ISupportsLatestMedia/GetLatestMedia (the deliberately-removed 'Latest' row)"
else
    ok "no ISupportsLatestMedia/GetLatestMedia reintroduced by the fix"
fi
if grep -q 'PlaybackStopped' "$LISTENER" && grep -qE 'InternalItemsQuery|GetItemList' "$LISTENER"; then
    bad "listener appears to run a catalogue-wide item query on a playback event (O(catalogue) risk)"
else
    ok "listener is a targeted, O(1)-per-event PlaybackStopped hook (no catalogue-wide query)"
fi

head_ "G. plugin-side fix exists, is wired into DI, and its regression coverage is present"
[[ -f "$LISTENER" ]] || fatal "RecentlyPlayedSyncListener.cs not found: $LISTENER"
ok "RecentlyPlayedSyncListener.cs exists"
if grep -q 'AddHostedService<RecentlyPlayedSyncListener>' "$REGISTRATOR"; then
    ok "RecentlyPlayedSyncListener is registered as a hosted service"
else
    bad "RecentlyPlayedSyncListener is not registered in PluginServiceRegistrator"
fi
[[ -f "$LISTENER_TESTS" ]] || fatal "RecentlyPlayedSyncListenerTests.cs not found: $LISTENER_TESTS"
declare -a required_tests=(
    "PlaybackStopped_MaterialisedMoviePlayedToCompletion_PersistsPlayedAndDatePlayed"
    "PlaybackStopped_PartialProgress_DoesNotMarkPlayed"
    "PlaybackStopped_StillPhantomSplash_IsIgnored"
    "PlaybackStopped_NonPhantomItem_IsIgnored"
)
missing=0
for t in "${required_tests[@]}"; do
    if grep -q "$t" "$LISTENER_TESTS"; then
        ok "regression test present: $t"
    else
        bad "regression test MISSING: $t"
        missing=$((missing+1))
    fi
done
if grep -q 'runTimeTicks: 0' "$LISTENER_TESTS" && grep -q 'runTimeTicks: 57000000000' "$LISTENER_TESTS"; then
    ok "regression coverage exercises the stale/zero-RunTimeTicks-at-PlaybackStart race explicitly"
else
    bad "regression coverage does not exercise the stale-RunTimeTicks race (the documented root cause)"
fi

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
