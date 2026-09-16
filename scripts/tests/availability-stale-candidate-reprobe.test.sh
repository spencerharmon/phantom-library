#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# scripts/tests/availability-stale-candidate-reprobe.test.sh
#
# In-repo regression harness (CHECKS.md `script-test` framework) for
# availability-stale-candidate-reprobe-001 — ROI P12 dial #1/#2 follow-up:
# close the "stale available, no live candidates" browse gap.
#
# ListVisibleMovieRowsAsync / ListVisibleSeriesRowsAsync previously admitted
# an 'available'-status availability_items row to default browse whenever
# NO source_candidates row currently failed validation/dead-swarm — but the
# viable-candidate EXISTS predicate never checked source_candidates.expires_at
# at all, so a candidate set that was cached, then aged out of the
# MagnetCacheTtlHours TTL window, still counted as "viable" even with zero
# LIVE candidates. This harness is a static/code-presence check (no live
# Jellyfin, no network, bash + grep only) mirroring
# availability-probe-reconcile.test.sh's pattern: the actual behavioural
# proof lives in the xUnit regression tests this harness asserts are present
# (`dotnet test` is the C# build/test gate's job, not re-run here) — this
# harness's job is to guard that the staleness-detection CODE and its named
# test coverage exist and were not quietly deleted/reverted. Movie AND
# episode/series parity is asserted explicitly.
#
# Exit 0 = all assertions passed; non-zero on the first failure.
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$HERE/../.." && pwd)"
DB="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/State/PhantomDb.cs"
WORKER="$REPO_ROOT/src/Jellyfin.Plugin.PhantomLibrary/Scheduled/AvailabilityProbeWorker.cs"
DB_TESTS="$REPO_ROOT/tests/Jellyfin.Plugin.PhantomLibrary.Tests/PhantomDbTests.cs"

pass_count=0
fail_count=0
ok()    { printf '  \033[32mPASS\033[0m %s\n' "$*"; pass_count=$((pass_count+1)); }
bad()   { printf '  \033[31mFAIL\033[0m %s\n' "$*"; fail_count=$((fail_count+1)); }
head_() { printf '\n\033[1m== %s\033[0m\n' "$*"; }
fatal() { printf '\033[31mFATAL: %s\033[0m\n' "$*" >&2; exit 2; }

for f in "$DB" "$WORKER" "$DB_TESTS"; do
    [[ -f "$f" ]] || fatal "expected source file not found: $f"
done

head_ "1. Movie viable-candidate predicate now requires a NON-expired candidate (ListVisibleMovieRowsAsync)"
if awk "/WHERE m.type='movie' AND \(/{f=1} f&&/sc.expires_at > @now/{print;exit}" "$DB" | grep -q 'sc.expires_at > @now'; then
    ok "movie browse query's viable-candidate EXISTS predicate checks sc.expires_at > @now"
else
    bad "movie browse query does not gate the viable-candidate predicate on expires_at"
fi

head_ "2. Episode/series viable-candidate predicates (both available_count and display_count subqueries) require non-expired candidates"
expires_hits=$(grep -c 'sc.expires_at > @now' "$DB" || true)
if [[ "$expires_hits" -ge 3 ]]; then
    ok "found $expires_hits expires_at freshness checks across the movie + episode (available_count/display_count) predicates"
else
    bad "expected at least 3 expires_at freshness checks (movie + 2 episode subqueries), found $expires_hits"
fi

head_ "3. Never-probed distinction preserved: NOT EXISTS branch (no source_candidates row at all) is untouched"
if grep -q 'NOT EXISTS (' "$DB" && grep -qE 'SELECT 1 FROM source_candidates sc\s*$' "$DB"; then
    ok "the never-probed NOT EXISTS branch is still present (unvalidated/never-probed items stay visible)"
else
    bad "the never-probed NOT EXISTS branch appears to have been removed or altered"
fi

head_ "4. Eager re-probe: stale-available-no-live-candidates rows are reprioritised via the existing AvailabilityProbeWorker plumbing (dial #1)"
if grep -q 'PrioritizeStaleAvailableForReprobeAsync' "$DB"; then
    ok "PhantomDb exposes PrioritizeStaleAvailableForReprobeAsync"
else
    bad "PhantomDb is missing PrioritizeStaleAvailableForReprobeAsync"
fi
if grep -q 'StaleAvailableReprobePriority' "$DB"; then
    ok "a dedicated StaleAvailableReprobePriority constant is defined (above background default, below user-activity priority)"
else
    bad "StaleAvailableReprobePriority constant is missing"
fi
if grep -q 'PrioritizeStaleAvailableForReprobeAsync(' "$WORKER"; then
    ok "AvailabilityProbeWorker calls PrioritizeStaleAvailableForReprobeAsync on its tick, ahead of the normal batch claim"
else
    bad "AvailabilityProbeWorker never calls PrioritizeStaleAvailableForReprobeAsync"
fi
if grep -q 'lease_until IS NULL' "$DB"; then
    ok "the reprobe reprioritisation never disturbs a row with an active lease"
else
    bad "the reprobe reprioritisation does not guard against an in-flight lease"
fi

head_ "5. No relaxation of the existing dead-swarm / all-invalid prune (dial #2 / p10) — predicates still present alongside the new freshness check"
if grep -q "validation_status <> 'invalid'" "$DB" && grep -q 'CandidateIsThresholdDeadSwarmSql' "$DB"; then
    ok "validation_status <> 'invalid' and the dead-swarm predicate are both still enforced"
else
    bad "the existing invalid/dead-swarm predicates appear to have been weakened or removed"
fi

head_ "Movie/TV parity + regression test coverage (named xUnit tests, run by the C# dotnet-test gate)"
declare -a required_tests=(
    "ListVisibleMovieRows_ExcludesAvailableMovieWhenOnlyCandidateExpired"
    "ListVisibleMovieRows_KeepsAvailableMovieWithNoCandidatesEverProbed"
    "ListVisibleMovieRows_ReentersListWhenFreshCandidateReplacesExpiredOne"
    "ListVisibleSeriesRows_ExcludesSeriesWhenOnlyEpisodeCandidateExpired"
    "PrioritizeStaleAvailableForReprobe_OnlyReprioritisesStaleAvailableRowsWithExhaustedCandidates"
)
for t in "${required_tests[@]}"; do
    if grep -q "$t" "$DB_TESTS"; then
        ok "regression test present: $t"
    else
        bad "regression test MISSING: $t"
    fi
done
if grep -q 'ListVisibleMovieRows_ExcludesAvailableMovieWhenOnlyCandidateExpired' "$DB_TESTS" \
    && grep -q 'ListVisibleSeriesRows_ExcludesSeriesWhenOnlyEpisodeCandidateExpired' "$DB_TESTS"; then
    ok "movie AND episode/series stale-candidate exclusion both have dedicated coverage"
else
    bad "stale-candidate exclusion coverage does not span both movie and episode/series"
fi

printf '\n%d passed, %d failed\n' "$pass_count" "$fail_count"
[ "$fail_count" -eq 0 ]
