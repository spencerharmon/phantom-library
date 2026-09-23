# playback-error-reduction-011 analysis (day 11)

## Context / prior work check
- Read `docs/tasks/playback-error-reduction-001.md` (series contract) and
  `docs/tasks/playback-error-reduction-010-analysis.md` (day 10: first real
  per-attempt Mimir sample, 2 episode `magnet_dead_stale` + 1 movie success,
  traced to a movie/TV badge-parity gap, enqueued
  `badge-deadswarm-episode-parity-001`).
- **`badge-deadswarm-episode-parity-001` has landed** — `PLAN.md` shows it
  `DONE` (review/commits `c614aafadfa115ca4c64977181b238d65b6fc7d0`, now part
  of tracked main). It adds a `StateStaleReprobePending` badge for a
  `status='available'` item (movie or episode) whose only surviving
  `source_candidates` row is a confirmed threshold dead swarm, reusing the
  existing `CandidateIsThresholdDeadSwarmSql` predicate. This is a **signal**
  fix (warns the user before playback) — it does not itself refresh the dead
  candidate.

## Note on this task's own history
A prior `playback-error-reduction-011` session was interrupted after filing
its two `PLAN.md` append actions (`plan: file task
availability-deadswarm-eager-reprobe-001 in phantom-library` and `plan: file
task playback-error-reduction-012 in phantom-library`) but before writing
this analysis doc or flipping the task's own status — a lost-work gap, not a
data problem. Both filed follow-ups are legitimate and have since been
independently worked to completion:
- `availability-deadswarm-eager-reprobe-001` — `DONE`
  (commit `e54ba784b786c4f21a2831d0032ed52fbe582d3e`,
  "Eager-reprobe available rows whose only candidate is a threshold dead
  swarm").
- `playback-error-reduction-012` — filed and already itself completed
  (its own analysis at `docs/tasks/playback-error-reduction-012-analysis.md`
  references this task's findings), continuing the daily cadence.
This pass backfills the day-11 analysis record; it does NOT re-file either
follow-up (they already exist, correctly, in `PLAN.md`) and does not append
another successor (`playback-error-reduction-012` already exists).

## Mimir re-query (accumulated volume)
```
kubectl -n monitoring exec deploy/grafana -c grafana -- curl -sf \
  http://mimir.monitoring.svc:8080/prometheus/api/v1/query \
  --data-urlencode 'query=phantom_playback_outcome_total{job="jellyfin-phantom-library"}'
```
Result (2026-09-23, ~turn time):
```json
{"status":"success","data":{"resultType":"vector","result":[
 {"metric":{"cause":"magnet_dead_stale","flow":"materialise_then_play","item_type":"episode",...},"value":[...,"2"]},
 {"metric":{"cause":"success","flow":"materialise_then_play","item_type":"movie",...},"value":[...,"1"]}
]}}
```
**Volume is unchanged from day 10: still exactly the same 3 rig-driven
samples** (2 episode `materialise_then_play` / `magnet_dead_stale`, 1 movie
`materialise_then_play` / `success`). No organic dev traffic has occurred.
Per the task's explicit instruction, this volume is NOT sufficient to force a
genuine statistical flow x item_type x cause ranking — noting "no organic
traffic yet" explicitly and continuing the code-level investigation approach
day 10 used, rather than fabricating statistical weight from 3 samples.

## Baseline (P5 discipline)
- Same 3-sample baseline as day 10: episode `magnet_dead_stale` = 2/2 failing
  attempts (100% of episode attempts sampled), movie = 1/1 succeeding.
- `badge-deadswarm-episode-parity-001` (the day-10 SIGNAL fix) has landed but
  cannot yet be assessed against a fresh sample set, because no new playback
  attempts have been recorded since day 10 — the counter values are
  byte-for-byte the same 2/1 split. There is no rig re-run or organic traffic
  between day 10 and day 11 to show whether the badge change altered user
  behavior (it wouldn't change the underlying `magnet_dead_stale` counter
  anyway, since it is a pre-playback warning, not a playback-path fix).

## Ranking (dominant remaining cause)
With the signal-only fix landed and no new samples, the dominant remaining
failure cause is still **episode `materialise_then_play` /
`magnet_dead_stale`** — unchanged from day 10, 2/2 of all episode attempts
sampled. The root cause (re-confirmed by code reading, since statistical
re-ranking isn't honestly possible from 3 samples) is that
`badge-deadswarm-episode-parity-001` only WARNS about a `status='available'`
item whose only surviving `source_candidates` row is a confirmed threshold
dead swarm — nothing actually REFRESHES that dead candidate before playback.
`AvailabilityProbeWorker`'s eager-reprobe promotion
(`PhantomDb.MarkStaleAvailableItemsDueAsync`) only promotes `status='available'`
rows with ZERO surviving `source_candidates` (candidate expired out); it
misses a row whose candidate still exists but is a confirmed threshold dead
swarm, so the dead candidate lingers and every playback attempt keeps failing
`magnet_dead_stale` until the slow sweep incidentally re-probes it.

## Fix enqueued for the biggest win
This is exactly the gap the prior (interrupted) session already correctly
identified and filed as `availability-deadswarm-eager-reprobe-001` — the
ACTION half complementing day 10's SIGNAL fix. That task is now `DONE`
(commit `e54ba784b786c4f21a2831d0032ed52fbe582d3e`): it widens
`MarkStaleAvailableItemsDueAsync`'s `WHERE` to also promote a
`status='available'`, unleased row that has >=1 surviving `source_candidates`
row where every surviving candidate is a confirmed threshold dead swarm,
reusing the existing `CandidateIsThresholdDeadSwarmSql` predicate, with
movie/episode parity and a passing dotnet regression test (both types +
negative control). No new fix task is enqueued by this pass since the
correct one already exists and has landed.

## Successor
`playback-error-reduction-012` was already filed (by the same interrupted
prior session) referencing this exact day-11 state (badge parity landed,
eager-reprobe gap diagnosed, `availability-deadswarm-eager-reprobe-001`
enqueued) and has itself already progressed with its own day-12 analysis. No
further successor append is needed from this pass.
