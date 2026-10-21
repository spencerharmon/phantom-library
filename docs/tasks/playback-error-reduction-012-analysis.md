# playback-error-reduction-012 — day-12 analysis

## 0. Dependency check
`availability-deadswarm-eager-reprobe-001` has LANDED (PLAN `[DONE]`, commit
e54ba78 on origin/main).

## 1. Baseline (P5 discipline — recorded before any ranking)
Instant query `phantom_playback_outcome_total{job="jellyfin-phantom-library"}`
-> `result: []` (EMPTY). The series are stale: the plugin restarted since day 11
and the counter only materialises a series on the first `Add`, so a fresh
instance with zero attempts exports nothing.

30-day window `max_over_time(phantom_playback_outcome_total{job="jellyfin-phantom-library"}[30d])`:

| flow | item_type | cause | count |
|---|---|---|---|
| materialise_then_play | episode | magnet_dead_stale | 2 |
| materialise_then_play | movie | success | 1 |

Same single instance id (4859600b...) as days 10-11. **No organic traffic yet.**
No statistical ranking is forced from 3 rig samples; the eager-reprobe fix's
effect on the episode `magnet_dead_stale` rate is unmeasurable (zero
post-landing attempts).

## 2. Ranking (code-level)
1. **Measurement blind spot.** After every restart the outcome series vanish
   instead of reading 0, so `increase()`/success-ratio queries have no
   denominator and a restart is indistinguishable from an export outage. Every
   "has fix X moved rate Y" question in this loop depends on continuous series.
2. `magnet_dead_stale` (episode) — signal (day 10) and action (day 11) halves
   both landed; awaiting volume.
3. Residual buckets — zero samples, deferred.

## 3. Chosen fix — enqueued
`playback-outcome-zero-seed-001` (design: `playback-outcome-zero-seed-001.md`,
Check: `dotnet test`).

## 4. Successor
`playback-error-reduction-013` [TODO], not_before ~= +24h.

DIAGNOSE-AND-ENQUEUE ONLY; no fix implemented. ROI.md untouched.
