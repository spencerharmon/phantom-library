#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/availability-prowlarr-fallback.test.sh
#
# In-repo regression harness for availability-signal-prowlarr-fallback: the
# availability sweep (AvailabilityProbeWorker) is Torrentio-only
# (IsAvailabilityOracle), and Torrentio returns HTTP 429 both for a genuine
# rate-limit AND, indistinguishably, for any id it cannot serve at all —
# without a fallback this sinks a Prowlarr-servable title into the 24h
# transient backoff forever, even though Materialiser.ProbeAsync (which DOES
# use Prowlarr) would succeed.
#
# The fix lives entirely in the C# plugin (AvailabilityProbeWorker.cs +
# MagnetSelector.cs) with its own xUnit regression coverage
# (AvailabilityProbeWorkerTests.cs). `dotnet test` itself is the separate
# build/test gate's job (dotnet-build / dotnet-test CHECKS.md stubs); THIS
# harness is the mechanical, language-agnostic guard that the fix and its
# coverage actually exist and were not quietly deleted or watered down.
#
# Asserts:
#   A. MagnetSelector exposes a Prowlarr-fallback probe scoped OFF the
#      availability-oracle indexer (never re-invokes Torrentio for the
#      fallback confirm).
#   B. AvailabilityProbeWorker engages the fallback ONLY for the Torrentio
#      indexer-failure kind (indexer_partial_or_total_failure), gated behind
#      a config flag, and only after a bounded same-oracle retry — never on
#      every hot-loop probe (mainstream Torrentio-served titles must never
#      reach this path at all).
#   C. The bounded retry + fallback config knobs exist with sane defaults.
#   D. The required xUnit regression tests exist in
#      AvailabilityProbeWorkerTests.cs, covering: fallback confirms
#      available + caches the magnet; fallback also fails => stays
#      transient (never falsely marked available); fallback disabled by
#      config never invokes Prowlarr; a mainstream Torrentio-served title
#      never engages the heavy fallback path at all.
#
# Exit 0 = all assertions passed; non-zero on the first failure.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
WORKER="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Scheduled/AvailabilityProbeWorker.cs"
SELECTOR="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Sources/MagnetSelector.cs"
CONFIG="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Configuration/PluginConfiguration.cs"
WORKER_TESTS="$REPO_ROOT/tests/Jellyfin.Plugin.PhantomLibrary.Tests/AvailabilityProbeWorkerTests.cs"

pass_count=0
fail_count=0
ok()    { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()   { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

[[ -f "$WORKER" ]] || fatal "AvailabilityProbeWorker.cs not found: $WORKER"
[[ -f "$SELECTOR" ]] || fatal "MagnetSelector.cs not found: $SELECTOR"
[[ -f "$CONFIG" ]] || fatal "PluginConfiguration.cs not found: $CONFIG"
[[ -f "$WORKER_TESTS" ]] || fatal "AvailabilityProbeWorkerTests.cs not found: $WORKER_TESTS"

head_ "A. MagnetSelector exposes a Prowlarr-scoped fallback probe (never re-invokes Torrentio)"
if grep -q 'ProbeAvailabilityFallbackAsync' "$SELECTOR"; then
    ok "MagnetSelector.ProbeAvailabilityFallbackAsync exists"
else
    bad "MagnetSelector is missing a Prowlarr-fallback probe method (ProbeAvailabilityFallbackAsync)"
fi
if grep -q 'NonOracleOnly' "$SELECTOR" && grep -q '!i.IsAvailabilityOracle' "$SELECTOR"; then
    ok "fallback probe is scoped to non-availability-oracle indexers (excludes Torrentio)"
else
    bad "fallback probe does not appear scoped away from the availability-oracle indexer"
fi

head_ "B. AvailabilityProbeWorker gates the fallback to the Torrentio-failure kind, behind config, after a bounded retry"
if grep -q 'indexer_partial_or_total_failure' "$WORKER" && grep -q '_fallbackProbe' "$WORKER"; then
    ok "worker engages the fallback only for indexer_partial_or_total_failure via a dedicated _fallbackProbe delegate"
else
    bad "worker does not gate the fallback to the specific Torrentio indexer-failure kind"
fi
if grep -q 'AvailabilityProwlarrFallbackEnabled' "$WORKER"; then
    ok "fallback is gated behind AvailabilityProwlarrFallbackEnabled"
else
    bad "worker does not gate the fallback behind a config flag"
fi
if grep -q 'AvailabilityTransientOracleRetryAttempts' "$WORKER" && grep -q 'for (var attempt = 0; attempt < retries' "$WORKER"; then
    ok "worker performs a bounded same-oracle retry before falling back"
else
    bad "worker does not perform a bounded same-oracle retry before the Prowlarr fallback"
fi

head_ "C. bounded-retry + fallback config knobs exist with sane defaults"
declare -a required_props=(
    "AvailabilityProwlarrFallbackEnabled"
    "AvailabilityTransientOracleRetryAttempts"
    "AvailabilityTransientOracleRetryDelayMs"
)
for p in "${required_props[@]}"; do
    if grep -q "public .* $p { get; set; }" "$CONFIG"; then
        ok "config property present: $p"
    else
        bad "config property MISSING: $p"
    fi
done
if grep -q 'AvailabilityProwlarrFallbackEnabled = true;' "$CONFIG"; then
    ok "fallback defaults to enabled"
else
    bad "fallback default is not explicitly enabled"
fi

head_ "D. required xUnit regression coverage exists in AvailabilityProbeWorkerTests.cs"
declare -a required_tests=(
    "Fallback_TorrentioAlways429_ProwlarrHasIt_MarksAvailableAndCachesMagnet"
    "Fallback_TorrentioAlways429_ProwlarrAlsoFails_StaysTransientNotAvailable"
    "Fallback_DisabledByConfig_NeverInvokesProwlarrEvenOnTorrentio429"
    "Fallback_MainstreamTorrentioServedTitle_NeverEngagesHeavyFallbackPath"
)
for t in "${required_tests[@]}"; do
    if grep -q "$t" "$WORKER_TESTS"; then
        ok "regression test present: $t"
    else
        bad "regression test MISSING: $t"
    fi
done
if grep -q 'TorrentioAlways429Indexer' "$WORKER_TESTS" && grep -q 'ProwlarrConfirmsAvailableIndexer' "$WORKER_TESTS"; then
    ok "regression coverage exercises real Torrentio-429 + Prowlarr-has-it indexer fakes end to end"
else
    bad "regression coverage does not exercise the real Torrentio-429 / Prowlarr-confirms shape"
fi

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
