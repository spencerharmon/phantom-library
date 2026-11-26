#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/p13-parallel-indexer-search.test.sh
#
# Definition-of-done harness (CHECKS.md `script-test`) for
# p13-parallel-indexer-search (ROI Priority 13): per-indexer Prowlarr fan-out
# under a search budget, late results kept in the magnet cache.
#
# Runs REAL .NET tests (ParallelIndexerSearchTests + ProwlarrClientTests +
# MagnetSelectorTests + DegenerateQueryGuardTests) against an in-process fake Prowlarr that serves
# /api/v1/indexer plus per-indexer Torznab (/{id}/api) with one indexer fast,
# one HUNG, one SLOW past the budget, one 5xx, one disabled and one usenet.
# They assert, for movie AND episode:
#   - ranked candidates return within the budget (+ tolerance) despite the
#     hung/slow indexers; early return once N high-confidence (seeders >=
#     MinSeeders) candidates are in;
#   - the slow indexer's late result lands in the P6 magnet cache
#     (source_candidates, source=late_indexer_result) afterwards;
#   - OTel metrics recorded: per-indexer request count by status/phase,
#     latency histogram, candidates, budget-expired-with-N-candidates;
#   - indexers are discovered at runtime (TTL-cached), never the legacy
#     aggregate /api/v1/search (the fake counts any hit on it).
# On the pre-change aggregate path these fail: the single aggregate call
# waits on the hung indexer, so nothing returns inside the budget and no
# per-indexer late result is cached.
#
# Initialises the nested `jellyfin` submodule (patched core the plugin
# ProjectReferences) when it is absent. Exit 0 = all assertions passed.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
TESTS_CSPROJ="$REPO_ROOT/tests/Jellyfin.Plugin.PhantomLibrary.Tests/Jellyfin.Plugin.PhantomLibrary.Tests.csproj"
PROWLARR="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Clients/ProwlarrClient.cs"

pass_count=0
fail_count=0
ok()    { printf '  PASS %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()   { printf '  FAIL %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n== %s\n' "$*"; }
fatal() { printf 'FATAL: %s\n' "$*" >&2; exit 2; }

[[ -f "$TESTS_CSPROJ" ]] || fatal "test project not found: $TESTS_CSPROJ"
[[ -f "$PROWLARR" ]] || fatal "ProwlarrClient.cs not found: $PROWLARR"
command -v dotnet >/dev/null 2>&1 || fatal "'dotnet' not found in PATH"

if [[ ! -f "$REPO_ROOT/jellyfin/MediaBrowser.Controller/MediaBrowser.Controller.csproj" ]]; then
    printf 'jellyfin/ submodule absent; initialising (patched core for ProjectReferences)\n'
    git -C "$REPO_ROOT" submodule update --init --depth 1 jellyfin
fi

LOG="$(mktemp "${TMPDIR:-/tmp}/p13-parallel-indexer-search.XXXXXX.log")"
cleanup() { dotnet build-server shutdown >/dev/null 2>&1 || true; }
trap cleanup EXIT

# =====================================================================
head_ "A. the aggregate /api/v1/search call is gone from ProwlarrClient"
if awk '!/^[[:space:]]*\/\//' "$PROWLARR" | grep '"{0}/api/v1/search' >/dev/null; then
    bad "ProwlarrClient still builds the aggregate /api/v1/search URL"
else
    ok "ProwlarrClient no longer builds the aggregate /api/v1/search URL"
fi

# =====================================================================
head_ "B. real dotnet tests against the fake Prowlarr (movie + episode)"
FILTER='FullyQualifiedName~ParallelIndexerSearchTests|FullyQualifiedName~ProwlarrClientTests|FullyQualifiedName~MagnetSelectorTests|FullyQualifiedName~DegenerateQueryGuardTests'
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
if dotnet test "$TESTS_CSPROJ" -p:UseSharedCompilation=false --filter "$FILTER" \
        --logger "console;verbosity=normal" > "$LOG" 2>&1; then
    ok "dotnet test passed: $(grep -E '^Total tests:|^ +(Passed|Failed):' "$LOG" | tr -s ' ' | paste -sd' ')"
else
    bad "dotnet test FAILED — log: $LOG"
    grep -E '\[FAIL\]|Error Message|Assert' -A3 "$LOG" | tail -n 60 >&2 || tail -n 60 "$LOG" >&2
fi

declare -a required=(
    'Budget_ReturnsWithinBudget_DespiteHungAndSlowIndexers_LateResultLandsInMagnetCache(type: "movie")'
    'Budget_ReturnsWithinBudget_DespiteHungAndSlowIndexers_LateResultLandsInMagnetCache(type: "episode")'
    'EarlyReturn_OnceNHighConfidenceCandidatesArrive_WellBeforeBudget(type: "movie")'
    'EarlyReturn_OnceNHighConfidenceCandidatesArrive_WellBeforeBudget(type: "episode")'
    'Budget_NothingArrivedYet_IsTransient_NotDefinitiveUnavailable_AndLateResultStillCached'
    'CallerCancellation_StillPropagates_AndCancelsInFlightRequests'
    'Discovery_IsCachedOnTtl_AndRefreshedAfterExpiry_NewIndexerPickedUp'
    'Discovery_FailsWithNoCache_ProbeIsTransient_TorrentioStillAnswers'
    'SearchAsync_FansOut_Over_Discovered_Indexers_Never_Aggregate_Endpoint'
    'AsymmetricMovieTimeout_PreservesSuccessfulLateSubqueryInCache(imdbResponds: True)'
    'AsymmetricMovieTimeout_PreservesSuccessfulLateSubqueryInCache(imdbResponds: False)'
    'RedirectTimeout_PreservesOtherParsedLateItemsInCache(type: "movie")'
    'RedirectTimeout_PreservesOtherParsedLateItemsInCache(type: "episode")'
    'Budget_EmptyOrRejectedSuccessWithPendingSibling_RemainsTransient(type: "movie", rejected: False)'
    'Budget_EmptyOrRejectedSuccessWithPendingSibling_RemainsTransient(type: "episode", rejected: False)'
    'Budget_EmptyOrRejectedSuccessWithPendingSibling_RemainsTransient(type: "movie", rejected: True)'
    'Budget_EmptyOrRejectedSuccessWithPendingSibling_RemainsTransient(type: "episode", rejected: True)'
    'EarlyReturn_WakesAtMinimumEvenWhenNoMoreRequestsComplete(type: "movie", minimumMs: null)'
    'EarlyReturn_WakesAtMinimumEvenWhenNoMoreRequestsComplete(type: "episode", minimumMs: null)'
    'EarlyReturn_WakesAtMinimumEvenWhenNoMoreRequestsComplete(type: "movie", minimumMs: 600)'
    'EarlyReturn_WakesAtMinimumEvenWhenNoMoreRequestsComplete(type: "episode", minimumMs: 600)'
    'PartialMovieResponse_CallerCancellationStillPropagatesWithoutCaching(directClient: False)'
    'PartialMovieResponse_CallerCancellationStillPropagatesWithoutCaching(directClient: True)'
    'ProwlarrClient_DegenerateEpisodeTitle_AbstainsWithoutRequest'
    'Movie_DotTitle_RepairedFromOriginalTitle_NoDegenerateTermSent'
    'Episode_PunctuationTitle_RepairedFromOriginalTitle_NoDegenerateTermSent'
)
for t in "${required[@]}"; do
    # Drain the pipe: grep -q can SIGPIPE the producer under pipefail once
    # the test log exceeds the pipe buffer, falsely reporting a missing test.
    if grep -F "Passed " "$LOG" | grep -F "$t" >/dev/null; then
        ok "ran + passed: $t"
    else
        bad "required test did not pass (missing/renamed/failed): $t"
    fi
done

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[[ "$fail_count" -eq 0 ]]
