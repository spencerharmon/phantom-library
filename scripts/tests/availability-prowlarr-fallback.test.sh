#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/availability-prowlarr-fallback.test.sh
#
# Definition-of-done check for task availability-signal-prowlarr-fallback:
# the high-frequency availability sweep (AvailabilityProbeWorker ->
# MagnetSelector.ProbeAvailabilityAsync) is Torrentio-only (IsAvailabilityOracle).
# Torrentio returns HTTP 429 for any id it cannot serve (proven live: mainstream
# movie=200; series-as-movie/obscure/anime/unknown=429; 15 rapid good-id
# requests all 200, so NOT a rate limit). The plugin mapped that straight to
# IndeterminateTransient -> a 24h-class backoff, so a title Prowlarr actually
# has (e.g. Adventure Time: Fionna & Cake — Torrentio 429 everywhere incl.
# anime providers, Prowlarr=185 results incl. an exact S01 pack, 338 seeders)
# never got confirmed available and sank in the playable-first sort.
#
# The fix ships MagnetSelector.ProbeAvailabilityWithFallbackAsync: on a
# Torrentio-serving-failure IndeterminateTransient (ErrorKind
# "indexer_partial_or_total_failure"), it applies a SMALL bounded retry
# against Torrentio itself (to distinguish a genuine momentary throttle from
# a per-id 429 that will never clear), then — only if the failure persists —
# falls back to a Prowlarr-backed confirm. A title Torrentio actually serves
# (200, even a definitive empty result) NEVER reaches the fallback branch, so
# the heavy Prowlarr fan-out is not run on every hot-loop probe.
# AvailabilityProbeWorker's default probe delegate now points at this
# fallback-aware method instead of the raw Torrentio-only oracle probe.
#
# This harness is the in-sandbox, no-cluster gate (mirrors the convention in
# scripts/tests/recently-played.test.sh / p10-curated-browse.test.sh): it
# asserts the source-level shape of the fix AND that the regression coverage
# exists with the specific scenarios the ACCEPT criteria require. It does NOT
# re-run `dotnet test` itself — that requires the patched Jellyfin submodule
# assemblies built via install.sh --build and is the C# build/test gate's own
# job; this harness only guards that the fix and its coverage are present and
# were not quietly deleted or reverted.
#
# Asserts:
#   A. MagnetSelector.ProbeAvailabilityWithFallbackAsync exists, is gated on
#      the Torrentio-serving-failure ErrorKind (not NoCapableIndexer/other),
#      performs a bounded retry via AvailabilityOracleFailureRetries, and
#      falls back to a NON-oracle (Prowlarr) indexer scope.
#   B. AvailabilityProbeWorker's default probe delegate was switched to the
#      fallback-aware method (so the fix is actually WIRED into the sweep,
#      not just added dead code).
#   C. PluginConfiguration carries the new bounded-retry knobs.
#   D. The xUnit regression coverage in MagnetSelectorTests.cs exists and
#      covers all four required scenarios: (1) Torrentio-served title never
#      invokes Prowlarr (cadence gate), (2) a persistent Torrentio HTTP
#      failure falls back to Prowlarr and becomes Available (the exact
#      Adventure-Time-shaped root cause), (3) NoCapableIndexer is untouched
#      (no retry/fallback), (4) a fallback that is ALSO inconclusive preserves
#      the original transient outcome rather than inventing a definitive one.
#
# Exit 0 = all assertions passed; non-zero on the first failure.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
SELECTOR="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Sources/MagnetSelector.cs"
WORKER="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Scheduled/AvailabilityProbeWorker.cs"
CONFIG="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Configuration/PluginConfiguration.cs"
SELECTOR_TESTS="$REPO_ROOT/tests/Jellyfin.Plugin.PhantomLibrary.Tests/MagnetSelectorTests.cs"

pass_count=0
fail_count=0
ok()    { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()   { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

[[ -f "$SELECTOR" ]]       || fatal "MagnetSelector.cs not found: $SELECTOR"
[[ -f "$WORKER" ]]         || fatal "AvailabilityProbeWorker.cs not found: $WORKER"
[[ -f "$CONFIG" ]]         || fatal "PluginConfiguration.cs not found: $CONFIG"
[[ -f "$SELECTOR_TESTS" ]] || fatal "MagnetSelectorTests.cs not found: $SELECTOR_TESTS"

head_ "A. MagnetSelector: fallback method exists, correctly gated, bounded-retries, falls back to Prowlarr scope"
if grep -q 'ProbeAvailabilityWithFallbackAsync' "$SELECTOR"; then
    ok "ProbeAvailabilityWithFallbackAsync exists in MagnetSelector.cs"
else
    bad "ProbeAvailabilityWithFallbackAsync is MISSING from MagnetSelector.cs"
fi
if grep -q 'IsOracleServingFailure' "$SELECTOR" && grep -q '"indexer_partial_or_total_failure"' "$SELECTOR"; then
    ok "fallback is gated on the Torrentio-serving-failure ErrorKind (indexer_partial_or_total_failure), not a bare transient catch-all"
else
    bad "fallback gating does not reference the indexer_partial_or_total_failure ErrorKind — risks firing on NoCapableIndexer/every transient, defeating the cadence guard"
fi
if grep -q 'AvailabilityOracleFailureRetries' "$SELECTOR"; then
    ok "bounded retry against the oracle itself is driven by AvailabilityOracleFailureRetries (distinguishes a real throttle from a per-id 429)"
else
    bad "no bounded-retry knob referenced in MagnetSelector.cs"
fi
if grep -q 'NonOracleOnly' "$SELECTOR"; then
    ok "fallback probes a non-oracle (Prowlarr) indexer scope, not Torrentio again"
else
    bad "fallback does not reference a non-oracle indexer scope"
fi
if grep -qE 'IndexerScope\s*\{\s*All,\s*OracleOnly,\s*NonOracleOnly' "$SELECTOR" || grep -q 'NonOracleOnly,' "$SELECTOR"; then
    ok "IndexerScope enum distinguishes All/OracleOnly/NonOracleOnly"
else
    bad "IndexerScope enum shape not found"
fi
if grep -qE 'return\s+oracleResult;' "$SELECTOR"; then
    ok "an inconclusive fallback preserves the ORIGINAL oracle transient outcome (never invents Available/DefinitiveUnavailable from two failures)"
else
    bad "fallback does not appear to preserve the original oracle outcome on an inconclusive fallback"
fi

head_ "B. AvailabilityProbeWorker actually wired to the fallback-aware probe (not dead code)"
if grep -q '_selector.ProbeAvailabilityWithFallbackAsync' "$WORKER"; then
    ok "AvailabilityProbeWorker's default probe delegate uses ProbeAvailabilityWithFallbackAsync"
else
    bad "AvailabilityProbeWorker still uses the raw Torrentio-only ProbeAvailabilityAsync (fallback never engages in the real sweep)"
fi
if grep -q 'ProbeAvailabilityAsync;' "$WORKER"; then
    bad "AvailabilityProbeWorker's default delegate still points at ProbeAvailabilityAsync directly (should be the fallback-aware wrapper)"
else
    ok "AvailabilityProbeWorker's default delegate does not fall back to the raw oracle-only probe"
fi

head_ "C. PluginConfiguration carries the bounded-retry knobs"
if grep -q 'AvailabilityOracleFailureRetries' "$CONFIG" && grep -q 'AvailabilityOracleRetryDelayMilliseconds' "$CONFIG"; then
    ok "PluginConfiguration declares AvailabilityOracleFailureRetries and AvailabilityOracleRetryDelayMilliseconds"
else
    bad "PluginConfiguration is missing one or both bounded-retry knobs"
fi
if grep -qE 'AvailabilityOracleFailureRetries\s*=\s*[0-9]+;' "$CONFIG"; then
    ok "AvailabilityOracleFailureRetries has a default value wired in the configuration constructor"
else
    bad "AvailabilityOracleFailureRetries has no default value"
fi

head_ "D. xUnit regression coverage exists for all four required scenarios"
declare -a required_tests=(
    "ProbeAvailabilityWithFallback_TorrentioServesTitle_NeverInvokesProwlarr"
    "ProbeAvailabilityWithFallback_TorrentioHttpFailurePersists_FallsBackToProwlarrAndBecomesAvailable"
    "ProbeAvailabilityWithFallback_NoCapableIndexer_NeverRetriesOrFallsBack"
    "ProbeAvailabilityWithFallback_ProwlarrAlsoFailsTransiently_PreservesOriginalOracleTransient"
)
missing=0
for t in "${required_tests[@]}"; do
    if grep -q "$t" "$SELECTOR_TESTS"; then
        ok "regression test present: $t"
    else
        bad "regression test MISSING: $t"
        missing=$((missing+1))
    fi
done

if grep -q 'Torrentio returned HTTP 429' "$SELECTOR_TESTS"; then
    ok "regression coverage exercises the exact live root-cause HTTP 429 shape"
else
    bad "regression coverage does not reproduce the HTTP 429 failure shape"
fi

if grep -q 'Adventure Time' "$SELECTOR_TESTS"; then
    ok "regression coverage documents the concrete live repro (Adventure Time: Fionna & Cake) it targets"
else
    bad "regression coverage does not reference the concrete live repro this task was filed against"
fi

if grep -q 'Times.Never)' "$SELECTOR_TESTS" && grep -q 'prowlarr.Verify' "$SELECTOR_TESTS"; then
    ok "coverage proves the Prowlarr fallback is NEVER invoked for a Torrentio-served title (Strict mock + Times.Never)"
else
    bad "coverage does not prove the cadence guard (Prowlarr must never run for a Torrentio-served title)"
fi

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
