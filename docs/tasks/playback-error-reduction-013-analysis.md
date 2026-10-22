# playback-error-reduction-013: day-13 analysis

DIAGNOSE-AND-ENQUEUE ONLY. This revision supersedes the first attempt's
unsupported deployment conclusion and unsafe live-color deployment proposal.

## 1. Baseline before ranking

Requeried Mimir on 2026-10-22; the 30-day query evaluation timestamp was
1792642388.468. Instant
`phantom_playback_outcome_total{job="jellyfin-phantom-library"}` returned
API success with `result: []`. The corresponding `max_over_time(...[30d])`
returned the same two historical series as day 12:

| Flow | Item type | Cause | Historical maximum |
| --- | --- | --- | --- |
| materialise_then_play | episode | magnet_dead_stale | 2 |
| materialise_then_play | movie | success | 1 |

These maxima total 3, consistent with the earlier rig baseline, not three new
attempts. Maxima neither count attempts across resets nor date their occurrence.
**No organic traffic yet observed in these counters.** Missing series cannot
exclude unobserved traffic, prove zero attempts, or establish a restart. No
statistical cause ranking or error-rate improvement claim is justified.

## 2. Source landing is not runtime attribution

`f402fa9` (post-subscription outcome seeding) is an ancestor of the tracked base
`ce39fdd`. `PhantomFlowMetrics.SeedPlaybackOutcomes` enumerates the complete
2 flows x 2 item types x 8 causes; the exporter calls it after provider creation.
Thus `playback-outcome-zero-seed-001` landed in source.

Deployment lag is only a hypothesis. Pod start dates do not date container
restarts; unmapped image digests do not establish the loaded plugin assembly,
especially with persistent plugin mounts. The current `target_info` has an
instance distinct from the historical outcomes and SDK metadata only, with no
pod or plugin revision attribution. Other job metrics do not prove that the
outcome export path or any particular deployment is healthy. The exact runtime
identifiers, query commands, and environment authority are in the hive change doc.

## 3. Code-level priority, not statistical rank

1. Measurement blind spot: attribute the running plugin and exporter, then
   establish fresh complete seed coverage on the intended inactive/dev instance.
   Deployment lag, export misconfiguration, and a seed-path failure remain
   distinguishable hypotheses, not established causes.
2. Episode cold-flow `magnet_dead_stale`: the only historical failure bucket
   (maximum 2). Days 10/11 fixes landed in source; runtime efficacy is unknown.
3. Other causes and warm flows: no measured population rates.

## 4. One existing fix

Retain `playback-outcome-zero-seed-deploy-001`; do not create another fix.
Its revised design is `playback-outcome-zero-seed-deploy-001.md`. First establish
runtime build/exporter attribution. Any required deployment targets ONLY the
inactive/dev color resolved from live Ingress authority, failing closed on
ambiguity. Active/prod pin changes and promotion remain operator-gated.
Acceptance uses the hive's approved `endpoint` framework with exact fresh
intended-instance coverage, never a global count threshold.

## 5. Existing successor, unchanged cadence

Retain `playback-error-reduction-014` with
`not_before=2026-10-23T04:05:00Z`; do not duplicate it or restart its clock.
Read `playback-error-reduction-014.md` before interpreting the inherited task
prose. Missing seeds do not prove deployment lag. Require loaded-build/exporter
attribution first, separate dev/rig observations from organic traffic, and
verify the exact instance-scoped coverage contract before claiming visibility.
This analytical pass has no behavioral regression test (`check=none`); it makes
no deployment, playback, or seeding-code changes.
