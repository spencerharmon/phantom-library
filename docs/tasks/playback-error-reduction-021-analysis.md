# playback-error-reduction-021: day-21 analysis

DIAGNOSE-AND-ENQUEUE ONLY. Baseline pinned to 2026-10-30T05:24:12Z,
Prometheus `time=1793337852`. Site identities and exact live commands are
in the hive change doc; no infrastructure identifiers are added here.

## Baseline first

Inactive/dev exporter: instant and 30d-maximum vectors each have 32 tuples,
sum 1, the sole positive the same cold-movie success as day 20. All 32
source timestamps are 1793337850.014 (age 1.986s). The landed analyser
(now including counter-continuity, e09530d) reports OK 32/32, no
COUNTER_DISCONTINUITY, cumulative aggregate and cold-movie 0/1 = 0%; the other
three slices have zero denominators. Rankings withheld (inactive).

Serving remains UNINSTRUMENTED 0/32; attempts/errors/rate unavailable.
Unfiltered `count by(instance)` finds only the inactive exporter; the
runtime-info query is empty. Organic serving traffic is unmeasured, not zero.

Ingress roles (blue=apex, green=dev), both pod UIDs, container IDs, image
digests, start times and restart counts are identical to days 19-20, so no
re-attribution was needed. The one inactive success still has no organic
provenance. Historical rig maxima (cold-episode magnet_dead_stale=2, cold-movie
success=1) stay historical and are never mixed into current denominators.

## Existing work

`playback-outcome-coverage-counter-continuity-001` is DONE (e09530d, merged
0a06254); the harness now passes 177 offline cases. Source-coherence (38232c7),
window-input and flow/item reporting remain landed. Not duplicated.

## Trend and ranking

Days 19 -> 20 -> 21 inactive cumulative: 0 -> 1 success -> 1 success (no new
attempts in ~24h). That delta was computed by hand: the analyser reports only
cumulative snapshots ("NOT a window increase or improvement measurement"),
so the loop has no tool to measure the interval error rate it must drive
down. No statistical cause ranking is defensible: serving volume is
unavailable and inactive volume is one unattributed success.

## Exactly one distinct fix

Enqueue `playback-outcome-coverage-interval-delta-001`: extend the existing
analyser with an optional prior pinned capture (`--previous-instant`,
`--previous-timestamps`) and report per-identity, per-slice INTERVAL
attempts/errors/rate (current minus previous) with independent denominators.
Any tuple with current < previous, a tuple-set mismatch, or a previous source
time not strictly earlier than current withholds that identity's interval
rates/rankings (exit 2). Output unchanged when the options are absent.
Check (CHECKS.md `script-test`):
`./scripts/tests/phantom-playback-outcome-coverage.test.sh`.

## Successor and limits

Day 22 TODO not before 2026-10-31T05:24:00Z (~+24h). No implementation,
deployment, promotion, counter reset, synthetic live traffic or process
mutation occurred.
