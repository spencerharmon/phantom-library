#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/availability-probe-reconcile.test.sh
#
# In-repo regression harness (CHECKS.md `script-test` framework) for
# availability-probe-reconcile-001 — raise the availability-probe success
# rate via three coordinated moves in AvailabilityProbeWorker/MagnetSelector/
# PhantomDb:
#
#   1. Reconcile: a Torrentio (availability-oracle) abstain is no longer
#      accepted as final without FIRST reconciling against the FULL indexer
#      fan-out (Prowlarr included, via MagnetSelector.ProbeAsync) — reached
#      at both entry points (the pre-filter HasCapableAvailabilityIndexer
#      short-circuit, and the post-probe NoCapableIndexer switch case).
#   2. TTL re-probe: unchanged from the pre-existing next_check_at/TTL
#      mechanism (already covered by AvailabilityProbeWorkerTests); this
#      harness asserts the reconcile step never bypasses it.
#   3. Bounded exponential negative backoff: a confirmed-negative item's
#      next re-probe interval doubles per consecutive DefinitiveUnavailable
#      completion (capped at AvailabilityUnavailableMaxTtlDays), tracked via
#      the new availability_items.negative_streak column, and resets to the
#      base TTL on the next `available` completion.
#
# This is a static/code-presence harness (no live Jellyfin, no network,
# bash + grep only) mirroring recently-played.test.sh's pattern: the actual
# behavioural proof lives in the xUnit regression tests this harness asserts
# are present (`dotnet test` is the C# build/test gate's job, not re-run
# here) — this harness's job is to guard that the reconcile/backoff CODE and
# its named test coverage exist and were not quietly deleted/reverted.
# Movie AND episode parity is asserted explicitly (separate reconcile tests
# for each entry point/item type).
#
# Exit 0 = all assertions passed; non-zero on the first failure.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
WORKER="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Scheduled/AvailabilityProbeWorker.cs"
SELECTOR="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Sources/MagnetSelector.cs"
DB="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/State/PhantomDb.cs"
CFG="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Configuration/PluginConfiguration.cs"
WORKER_TESTS="$REPO_ROOT/tests/Jellyfin.Plugin.PhantomLibrary.Tests/AvailabilityProbeWorkerTests.cs"

pass_count=0
fail_count=0
ok()    { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()   { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

for f in "$WORKER" "$SELECTOR" "$DB" "$CFG" "$WORKER_TESTS"; do
    [[ -f "$f" ]] || fatal "expected source file not found: $f"
done

head_ "1. Torrentio<->Prowlarr reconcile: full-fan-out fallback wired at BOTH entry points, bar never relaxed"
if grep -q 'TryReconcileWithFullIndexerSetAsync' "$WORKER"; then
    ok "AvailabilityProbeWorker has a reconcile helper (TryReconcileWithFullIndexerSetAsync)"
else
    bad "AvailabilityProbeWorker is missing the Torrentio<->Prowlarr reconcile helper"
fi
if grep -q '_reconcileProbe = reconcileProbe ?? _selector.ProbeAsync' "$WORKER"; then
    ok "reconcile probe is wired to the FULL indexer fan-out (MagnetSelector.ProbeAsync), not a relaxed bar"
else
    bad "reconcile probe is not wired to MagnetSelector.ProbeAsync"
fi
# Entry point A: the pre-filter (HasCapableAvailabilityIndexer) short-circuit.
if awk '/!_selector\.HasCapableAvailabilityIndexer\(imdb\)/{f=1} f&&/TryReconcileWithFullIndexerSetAsync/{print;exit}' "$WORKER" | grep -q TryReconcileWithFullIndexerSetAsync; then
    ok "pre-filter (no-imdb) entry point attempts reconcile before backing off"
else
    bad "pre-filter (no-imdb) entry point does not attempt reconcile before backing off"
fi
# Entry point B: the post-probe NoCapableIndexer switch case.
if awk '/case MagnetProbeOutcome\.NoCapableIndexer:/{f=1} f&&/TryReconcileWithFullIndexerSetAsync/{print;exit}' "$WORKER" | grep -q TryReconcileWithFullIndexerSetAsync; then
    ok "post-probe NoCapableIndexer entry point attempts reconcile before backing off"
else
    bad "post-probe NoCapableIndexer entry point does not attempt reconcile before backing off"
fi
if grep -q 'reconciled.Outcome != MagnetProbeOutcome.Available' "$WORKER"; then
    ok "reconcile only completes as available on a definitive Available verdict (never relaxes the bar)"
else
    bad "reconcile does not gate on a definitive Available verdict"
fi

head_ "2. TTL re-probe path untouched (next_check_at-driven, not bypassed by reconcile)"
if grep -q 'AvailabilityAvailableTtlDays' "$WORKER" && grep -q 'next_check_at' "$DB"; then
    ok "TTL re-probe scheduling (AvailabilityAvailableTtlDays / next_check_at) is intact"
else
    bad "TTL re-probe scheduling appears to have been removed"
fi

head_ "3. Bounded exponential negative backoff: negative_streak column + growth/cap/reset"
if grep -q 'negative_streak INTEGER NOT NULL DEFAULT 0' "$DB"; then
    ok "availability_items.negative_streak column is declared in the schema"
else
    bad "availability_items.negative_streak column is missing from the schema"
fi
if grep -q 'int NegativeStreak = 0' "$DB"; then
    ok "AvailabilityItemRow carries NegativeStreak"
else
    bad "AvailabilityItemRow is missing NegativeStreak"
fi
if grep -q 'AvailabilityUnavailableMaxTtlDays' "$CFG"; then
    ok "AvailabilityUnavailableMaxTtlDays cap is configured"
else
    bad "AvailabilityUnavailableMaxTtlDays cap is missing from PluginConfiguration"
fi
if grep -q 'lease.NegativeStreak + 1' "$WORKER" && grep -q '1L << Math.Min(negativeStreak - 1, 20)' "$WORKER"; then
    ok "DefinitiveUnavailable completion grows the backoff exponentially from the streak"
else
    bad "DefinitiveUnavailable completion does not compute an exponential backoff from the streak"
fi
if grep -q 'Math.Max(1, cfg.AvailabilityUnavailableTtlDays) \* (1L << Math.Min(negativeStreak - 1, 20)),' "$WORKER" \
    && grep -q 'Math.Max(1, cfg.AvailabilityUnavailableMaxTtlDays));' "$WORKER"; then
    ok "negative backoff is capped at AvailabilityUnavailableMaxTtlDays"
else
    bad "negative backoff does not appear to be capped"
fi
if grep -q 'negativeStreak: 0' "$WORKER" && grep -q 'negative_streak=0' "$DB"; then
    ok "an available completion resets the negative streak to 0"
else
    bad "an available completion does not reset the negative streak"
fi

head_ "Movie/TV parity + regression test coverage (named xUnit tests, run by the C# dotnet-test gate)"
declare -a required_tests=(
    "Sweep_NoImdb_TorrentioAbstains_ProwlarrHighConfidenceMagnet_ReconciledToAvailable"
    "Sweep_TorrentioAbstainsPostProbe_ProwlarrHighConfidenceMagnet_ReconciledToAvailable"
    "Sweep_NoImdb_ReconciledAgainstProwlarr_ThenDeepDefersWhenProwlarrAlsoFindsNothing"
    "Sweep_DefinitiveUnavailable_NegativeStreakBackoffGrowsExponentiallyAndResetsOnPositive"
)
for t in "${required_tests[@]}"; do
    if grep -q "$t" "$WORKER_TESTS"; then
        ok "regression test present: $t"
    else
        bad "regression test MISSING: $t"
    fi
done
if grep -q 'Sweep_NoImdb_TorrentioAbstains_ProwlarrHighConfidenceMagnet_ReconciledToAvailable' "$WORKER_TESTS" \
    && grep -q 'Sweep_TorrentioAbstainsPostProbe_ProwlarrHighConfidenceMagnet_ReconciledToAvailable' "$WORKER_TESTS"; then
    ok "movie (pre-filter entry) AND episode (post-probe entry) reconcile paths both have dedicated coverage"
else
    bad "reconcile coverage does not span both the movie pre-filter and episode post-probe entry points"
fi

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
