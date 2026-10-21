# playback-error-reduction-012 — day-12 analysis

## 0. Dependency check
`availability-deadswarm-eager-reprobe-001` has LANDED in source: commit
`e54ba784b786c4f21a2831d0032ed52fbe582d3e` is an ancestor of this worktree's
tracked base `d8001f7dcc7e4b6ef24fc3abd3b7b57d0c1c8c03`.
`beehive submodule git merge-base --is-ancestor e54ba784b786c4f21a2831d0032ed52fbe582d3e HEAD`
exited 0. This establishes source landing, not deployment to the measured instance.

## 1. Baseline (P5 discipline — recorded before any ranking)
Re-queried on 2026-10-21 at approximately 05:17:25Z. Instant query
`phantom_playback_outcome_total{job="jellyfin-phantom-library"}`
returned API success with `result: []` (EMPTY). This establishes no current
matching series, NOT zero attempts or a confirmed plugin restart. An unseeded
fresh instance is a code-supported hypothesis; no timestamped deployed-version
or process-start evidence was collected to establish that explanation.

30-day window `max_over_time(phantom_playback_outcome_total{job="jellyfin-phantom-library"}[30d])`:

| flow | item_type | cause | historical counter maximum |
|---|---|---|---|
| materialise_then_play | episode | magnet_dead_stale | 2 |
| materialise_then_play | movie | success | 1 |

Evaluation timestamp: `1792559845.512` (2026-10-21T05:17:25.512Z); both series
have the same single instance label as days 10-11. These are two historical
series with maxima totaling 3, consistent with the prior rig baseline, not
three new samples. `max_over_time` is not an attempt count across counter
resets and does not date the underlying attempts.

**No organic traffic yet observed in these counters.** Absent series cannot
exclude unobserved traffic or establish zero post-landing attempts. No
statistical ranking is justified by this unchanged handful. The eager-reprobe
fix's effect on the episode `magnet_dead_stale` rate remains unmeasurable here.

Export context: `count by (__name__) ({job="jellyfin-phantom-library"})` at
`1792559845.495` returned 48 duration-bucket series, 3 duration-count series,
3 duration-sum series, 3 flow-items series, and 1 target_info series. Other job
metrics are present, so this is not evidence of complete job telemetry loss.
It does not prove the outcome-specific export path is healthy. Exact deployment
query commands and returned instance label are recorded in the hive change doc.

## 2. Ranking (code-level)
1. **Measurement blind spot (highest-priority enabling fix, not a playback
   failure cause).** At the tracked base, `PhantomFlowMetrics.cs:102-116,161-168`
   creates labelled Prometheus children and records OTLP outcomes only on an
   attempt. A fresh untouched process therefore has no outcome samples. Also,
   `Plugin.cs:30-34` precedes the hosted exporter's provider subscription at
   `PhantomMetricsExporter.cs:86-98`: an early synchronous `Add(0)` is not
   replayed to a later MeterProvider. Lifecycle-aware seeding removes this known
   local ambiguity; it neither repairs an export outage nor recovers lost
   attempts. A seeded zero denominator still means an undefined error rate,
   not 0% errors.
2. **Episode cold-flow `magnet_dead_stale`** remains the only observed failure
   bucket (2 historical rig attempts). Signal (day 10) and action (day 11) fixes
   landed in source; their deployed efficacy is not established. It remains the
   directional playback cause, not a statistically dominant population cause.
3. Other causes and the warm flow have no observed samples in this query;
   population rates and relative weights remain unknown.

## 3. Chosen fix — enqueued
`playback-outcome-zero-seed-001` (design: `playback-outcome-zero-seed-001.md`,
Check: `dotnet test`, matching the hive CHECKS.md `dotnet-test` framework).
Keep this ONE existing fix; do not enqueue a duplicate. Its originating design
now requires early Prometheus initialization, post-provider OTLP seeding, count
preservation on reseed, and actual startup-order SDK/exposition tests. This
aligns with the existing fix's reviewed pre-provider Add(0) failure and ongoing
rework; no implementation is included in this analytical pass.

## 4. Successor
The already-filed `playback-error-reduction-013` remains the successor, held at
`not_before=2026-10-22T04:02:46Z` (approximately +24h from original authorship).
Its guidance now separates source merge/local seeding from deployment and
Mimir visibility. Do not append a second successor or reset its cadence during
this review rework.

DIAGNOSE-AND-ENQUEUE ONLY; no fix implemented. ROI.md untouched.
