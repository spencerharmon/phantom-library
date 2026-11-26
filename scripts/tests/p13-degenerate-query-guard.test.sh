#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/p13-degenerate-query-guard.test.sh
#
# Definition-of-done harness for p13-degenerate-query-guard (ROI P13): no
# indexer request (Prowlarr, per-indexer Torznab via Prowlarr, Torrentio) may
# be issued with an empty or punctuation/whitespace-only title term — movie
# AND episode, interactive AND background. Production evidence: 43 searches in
# 6 h were sent as `Term: [. 2026]` (a "." title + the year).
#
# This runs REAL .NET tests (`dotnet test`, filtered to
# DegenerateQueryGuardTests) that drive the actual MagnetSelector,
# MagnetCacheBuilder (production DI ctor), AvailabilityProbeWorker,
# ProwlarrClient, TorrentioClient and TmdbQueryTitleRepairSource/TmdbClient
# against a RECORDING fake HTTP backend and assert on the real outbound URLs:
#   * zero outbound requests carry a degenerate title term;
#   * recoverable items are queried with the REPAIRED term (TMDB original
#     title, then alternative/translated title, then IMDB-id-only);
#   * unrecoverable items get the distinct `degenerate_query_skipped` outcome
#     (probe outcome, magnet-cache job error, counter) with ZERO indexer calls.
# The tests fail without the fix (verified by neutralising the guard: the
# recorder captures Prowlarr `query=. 2026` / `. S01E03`).
#
# The plugin references the patched Jellyfin source tree in the nested
# `jellyfin` submodule; it is initialised here if absent (the same step every
# reviewer of a dotnet-test DoD in this repo performs).
#
# Exit 0 = all assertions passed; non-zero otherwise.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
TESTS_CSPROJ="$REPO_ROOT/tests/Jellyfin.Plugin.PhantomLibrary.Tests/Jellyfin.Plugin.PhantomLibrary.Tests.csproj"
TEST_FILE="$REPO_ROOT/tests/Jellyfin.Plugin.PhantomLibrary.Tests/DegenerateQueryGuardTests.cs"
FILTER='FullyQualifiedName~Jellyfin.Plugin.PhantomLibrary.Tests.DegenerateQueryGuardTests'
# Minimum number of test cases the filter must execute (facts + theory rows);
# guards against the suite being silently gutted or the filter going stale.
MIN_TESTS=33

fatal() { printf 'FATAL: %s\n' "$*" >&2; exit 2; }

command -v dotnet >/dev/null 2>&1 || fatal "'dotnet' not found on PATH"
[[ -f "$TESTS_CSPROJ" ]] || fatal "test project not found: $TESTS_CSPROJ"
[[ -f "$TEST_FILE" ]]    || fatal "regression tests not found: $TEST_FILE"

if [[ ! -f "$REPO_ROOT/jellyfin/MediaBrowser.Controller/MediaBrowser.Controller.csproj" ]]; then
    echo "== initialising nested jellyfin submodule (plugin build dependency)"
    git -C "$REPO_ROOT" submodule update --init jellyfin \
        || fatal "could not initialise the jellyfin submodule required to build the plugin"
fi

LOG="$(mktemp "${TMPDIR:-/tmp}/p13-degenerate-query-guard.XXXXXX.log")"
echo "== dotnet test --filter $FILTER"
set +e
MSBUILDDISABLENODEREUSE=1 dotnet test "$TESTS_CSPROJ" -c Release -p:UseSharedCompilation=false \
    --filter "$FILTER" --logger "console;verbosity=normal" >"$LOG" 2>&1
rc=$?
set -e
dotnet build-server shutdown >/dev/null 2>&1 || true

RESULT_RE='^[[:space:]]+(Passed|Failed) Jellyfin\.Plugin\.PhantomLibrary\.Tests\.DegenerateQueryGuardTests\.'
grep -E "$RESULT_RE" "$LOG" || true
passed="$(grep -cE '^[[:space:]]+Passed Jellyfin\.Plugin\.PhantomLibrary\.Tests\.DegenerateQueryGuardTests\.' "$LOG" || true)"
failed="$(grep -cE '^[[:space:]]+Failed Jellyfin\.Plugin\.PhantomLibrary\.Tests\.DegenerateQueryGuardTests\.' "$LOG" || true)"
grep -E '(Passed|Failed)!|Total tests:|Passed:|Failed:' "$LOG" | tail -3 || true

if [[ $rc -ne 0 ]]; then
    echo "FAIL: dotnet test exited $rc — tail of $LOG:" >&2
    tail -n 60 "$LOG" >&2
    exit 1
fi

if [[ "${passed:-0}" -lt "$MIN_TESTS" || "${failed:-0}" -ne 0 ]]; then
    echo "FAIL: expected >= $MIN_TESTS passing DegenerateQueryGuardTests and 0 failures (passed=${passed}, failed=${failed})" >&2
    exit 1
fi

rm -f "$LOG"
echo "p13-degenerate-query-guard: ${passed} degenerate-query guard tests passed (movie+episode, interactive+background)"
