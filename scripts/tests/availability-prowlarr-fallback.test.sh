#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/availability-prowlarr-fallback.test.sh
#
# Definition-of-done check for task availability-signal-prowlarr-fallback.
#
# ROOT CAUSE (proven live): the high-frequency availability sweep is
# Torrentio-only (IsAvailabilityOracle). Torrentio returns HTTP 429 for any id
# it cannot serve (mainstream movie=200; series-as-movie/obscure/anime/unknown
# =429), which the plugin maps to MagnetProbeOutcome.IndeterminateTransient
# (error-kind indexer_partial_or_total_failure). Left alone that loops to the
# escalated 24h backoff, so a title Torrentio cannot serve but Prowlarr HAS
# (e.g. Adventure Time Fionna & Cake) NEVER confirms available and sinks in the
# playable-first sort — even though the full Materialiser.ProbeAsync (which uses
# Prowlarr) can find it.
#
# FIX: on a Torrentio-HTTP-failure transient in the sweep, once the item has
# churned past a bounded attempt threshold, make ONE fall-through to the full
# multi-indexer probe (Prowlarr included). If it confirms available, mark the
# item available + cache the magnet instead of deferring to 24h. Gated on
# attempt_count so mainstream Torrentio-served titles (Available on their first
# probe, never a transient) NEVER trigger the heavy path.
#
# This in-repo harness is the deterministic, sandbox-runnable machine gate
# (bash + grep + python3 only; NO live Jellyfin, NO network, NO cluster). It
# asserts the fix's source change is present, wired, and gated, and that its
# xUnit regression coverage exists and wasn't quietly deleted. (`dotnet test`
# itself is the C# build/test gate's job — see the ProwlarrFallback_* facts in
# AvailabilityProbeWorkerTests.cs, run under `dotnet test`.)
#
# Exit 0 = all assertions passed; non-zero on the first failure.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
WORKER="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Scheduled/AvailabilityProbeWorker.cs"
CONFIG="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Configuration/PluginConfiguration.cs"
TESTS="$REPO_ROOT/tests/Jellyfin.Plugin.PhantomLibrary.Tests/AvailabilityProbeWorkerTests.cs"

pass_count=0
fail_count=0
ok()    { printf '  PASS %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()   { printf '  FAIL %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n== %s\n' "$*"; }
fatal() { printf 'FATAL: %s\n' "$*" >&2; exit 2; }

for f in "$WORKER" "$CONFIG" "$TESTS"; do
    [[ -f "$f" ]] || fatal "required source file not found: $f"
done

head_ "A. config knob exists (bounded fallback gate)"
if grep -q 'AvailabilityProwlarrFallbackAfterAttempts' "$CONFIG"; then
    ok "PluginConfiguration declares AvailabilityProwlarrFallbackAfterAttempts"
else
    bad "PluginConfiguration is missing the AvailabilityProwlarrFallbackAfterAttempts gate knob"
fi
if grep -q 'AvailabilityProwlarrFallbackAfterAttempts = ' "$CONFIG"; then
    ok "the fallback-after-attempts knob has a default in the ctor"
else
    bad "AvailabilityProwlarrFallbackAfterAttempts has no default value"
fi

head_ "B. worker: the IndeterminateTransient branch has a Prowlarr fall-through"
# The gate helper must exist and be evaluated inside the transient branch.
if grep -q 'internal static bool ShouldProwlarrFallback' "$WORKER"; then
    ok "ShouldProwlarrFallback gate helper is defined"
else
    bad "ShouldProwlarrFallback gate helper is missing"
fi
if grep -q 'internal static bool IsTorrentioHttpFailure' "$WORKER"; then
    ok "IsTorrentioHttpFailure classifier is defined"
else
    bad "IsTorrentioHttpFailure classifier is missing"
fi
# The classifier must key off Torrentio's real HTTP-failure error-kind(s), NOT
# a config gap like no_enabled_indexers.
if grep -q 'indexer_partial_or_total_failure' "$WORKER"; then
    ok "fallback keys off the Torrentio HTTP-failure error-kind (indexer_partial_or_total_failure)"
else
    bad "worker does not classify the Torrentio HTTP-failure error-kind"
fi

python3 - "$WORKER" <<'PY' || fail_count=$((fail_count+1))
import re, sys
src = open(sys.argv[1]).read()

def check(name, cond):
    print(("  PASS " if cond else "  FAIL ") + name)
    return cond

ok_all = True

# Locate the IndeterminateTransient switch branch.
m = re.search(r'case MagnetProbeOutcome\.IndeterminateTransient:(.*?)case MagnetProbeOutcome\.NoCapableIndexer', src, re.S)
branch = m.group(1) if m else ""
ok_all &= check("IndeterminateTransient branch found", bool(branch))
ok_all &= check("transient branch invokes ShouldProwlarrFallback gate",
                "ShouldProwlarrFallback(" in branch)
ok_all &= check("transient branch runs the FULL probe (_probeFull) on fall-through",
                "_probeFull(" in branch)
ok_all &= check("a confirmed Available fallback marks the item available (+caches magnet)",
                "MarkAvailableAsync(" in branch)
# It must STILL reschedule transient when the fallback did not resolve.
ok_all &= check("transient branch still reschedules transient when fallback does not resolve",
                "RescheduleAvailabilityTransientAsync(" in branch)

# The gate must AND together: enabled(knob>0 & ProwlarrBaseUrl) + HTTP-failure + attempt threshold.
g = re.search(r'internal static bool ShouldProwlarrFallback\(.*?return lease\.AttemptCount', src, re.S)
gate = g.group(0) if g else ""
ok_all &= check("gate requires AvailabilityProwlarrFallbackAfterAttempts > 0",
                "AvailabilityProwlarrFallbackAfterAttempts" in gate)
ok_all &= check("gate requires a configured ProwlarrBaseUrl",
                "ProwlarrBaseUrl" in gate)
ok_all &= check("gate requires the Torrentio HTTP-failure classification",
                "IsTorrentioHttpFailure(" in gate)
ok_all &= check("gate requires the item to have churned attempt_count (cadence gate)",
                "AttemptCount" in gate)

sys.exit(0 if ok_all else 1)
PY

head_ "C. xUnit regression coverage exists (movie + TV parity, both outcomes, cadence gate)"
declare -a required_tests=(
    "ProwlarrFallback_TorrentioHttpFailureButProwlarrHasIt_BecomesAvailable_Movie"
    "ProwlarrFallback_TorrentioHttpFailureButProwlarrHasIt_BecomesAvailable_Episode"
    "ProwlarrFallback_NeitherSourceServes_StaysUnavailable"
    "ProwlarrFallback_MainstreamTorrentioServed_NeverRunsHeavyPath"
    "ProwlarrFallback_Disabled_WhenNoProwlarrConfigured_DefersTransient"
    "ShouldProwlarrFallback_GateMatrix"
)
for t in "${required_tests[@]}"; do
    if grep -q "$t" "$TESTS"; then
        ok "regression test present: $t"
    else
        bad "regression test MISSING: $t"
    fi
done

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
