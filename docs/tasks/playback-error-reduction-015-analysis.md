# playback-error-reduction-015: day-15 analysis

DIAGNOSE-AND-ENQUEUE ONLY. Observations on 2026-10-24, approximately 04:15 UTC.
Read the shared series contract, day-14 analysis and day-15 guidance first.

## Baseline first

At query evaluation time 1792815353.467, instant outcomes returned API success:
32 canonical tuples, all zero, on the same inactive/dev exporter as day 14.
The existing seed-deployment endpoint Check independently passed exact equality
with the two-flow x two-item-type x eight-cause product, scoped to that instance
and job, with finite nonnegative values and actual sample ages in [0,120) seconds.
This is stronger than a global count of 32. All-zero is observed in the instant
response; the supporting Check itself requires at least one zero, not all zeros.

The 30-day max_over_time query at the same evaluation time returned 34 series:
32 current zeros and unchanged prior rig maxima:

| Flow | Item type | Cause | Historical maximum |
| --- | --- | --- | --- |
| materialise_then_play | episode | magnet_dead_stale | 2 |
| materialise_then_play | movie | success | 1 |

**No organic traffic yet observed.** The observed current denominator is zero;
error rate is undefined, not 0%. Historical maxima are not deltas, reset-aware
totals, or new attempts. No population rate or statistical cause ranking is
established. Missing production series cannot rule out uninstrumented traffic.

## Attribution and runtime-info progress

Live Ingress still assigns the observed exporter to inactive/dev. Pod UID,
container ID, image digest, start time and zero restarts match day 14's verified
receipt. The container still started 2026-10-22T08:03:07Z. No process change was
observed. The retained receipt identifies loaded version 0.5.7.7, source
`d9a58ece9e7111f86663efe0c608147c099205a3`, assembly MVID and exporter identity;
seed implementation `f402fa9` remains an ancestor of that source (exit 0).

Today's log query returned no matching startup provenance lines. Therefore this
is continuity attribution against the prior receipt, not a claim of freshly
recovered loaded-assembly metadata. Re-attribute independently if any process
identity changes; pod age alone is insufficient. Exact commands and identifiers
remain in the hive change doc, not this source document.

`otlp-runtime-info-001` is DONE: implementation `87a022b` and merge `94f6e7e`
are in this checkout. The real provider subscribes to its traffic-independent
loaded-build gauge; its review receipt reports 48 tests passed. Those tests
were not rerun in this analytical pass. Exact and suffix-tolerant live queries
for `phantom_runtime_info` both returned success with empty results. The observed
runtime remains the prior build, so merged source is not a deployment claim.
Do not duplicate the gauge or count its eventual value 1 as playback volume.

## Priority, not statistical ranking

1. Protect measurement integrity: synthetic fixtures can still reach the push
   transport under the reserved real playback metric name. This is a directly
   reproduced defect, not evidence that today's backend is contaminated.
2. Historical cold-episode magnet_dead_stale is the only positive failure bucket,
   but a rig maximum of 2 cannot justify an organic-cause ranking or dial change.
3. Remaining causes and warm flows have no observed positive current volume.

## Exactly one fix

Enqueue `playback-outcome-synthetic-push-guard-001`; see its adjacent design doc.
`phantom-loadtime-push.sh` inherits PHANTOM_CI_DRYRUN into the measurement engine,
but suppresses transport only under PHANTOM_LOADTIME_PUSH_DRY_RUN / --dry-run.
The engine intentionally retains the reserved playback family in offline fixtures.
An exported shell-function curl stub intercepted **four reserved samples** from
the real emitter's run command with synthetic measurement and live push enabled.
No network, playback requests or counter resets were performed by that probe.

Reject synthetic run mode before network transport, and reject reserved playback
samples on every live push input path. Keep explicit push dry-run fixtures and
legitimate live rig-family payloads working. This is distinct from day 8's LIVE
engine rename/dual emission and day 9's pipeline verification: neither guards
the push boundary against offline fixtures supplied via run, file or stdin.
The chosen Check is the existing script-test harness extended with intercepted
transport assertions, not source grep or a detached validator test.

## Successor and scope

Enqueue `playback-error-reduction-016` for 2026-10-25T04:25:00Z, approximately
24 hours later. Repeat baseline and attribution first, check both queued guard
and runtime-info progress, and distinguish code completion from live ingestion.
This pass is check=none: analysis and queued designs only. No implementation,
deployment, promotion, synthetic live traffic or running-state mutations.
