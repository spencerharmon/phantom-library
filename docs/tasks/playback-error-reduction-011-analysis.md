# Playback-error-reduction — day 11 analysis (ROI Priority 12)

DIAGNOSE-AND-ENQUEUE pass. Read `docs/tasks/playback-error-reduction-001.md`
(shared series doc) and the day-10 record
(`submodules/phantom-library/docs/bee-playback-error-reduction-010-playback-error-reduction-010.md`,
hive layer) first. This pass adds no submodule code.

## 0. Did badge-deadswarm-episode-parity-001 land?

YES. It is `[DONE]` in PLAN.md (`commits=c614aafadfa115ca4c64977181b238d65b6fc7d0`,
"Badge stale-reprobe-pending for confirmed dead-swarm available items") and the
commit is an ancestor of this worktree's tip — verified:

```
$ beehive submodule git merge-base --is-ancestor c614aaf HEAD && echo IS-ANCESTOR
IS-ANCESTOR
$ beehive submodule git log origin/main --oneline | grep -i deadswarm
c614aaf Badge stale-reprobe-pending for confirmed dead-swarm available items
```

The badge layer (`PhantomLibraryBadgesController`) now returns a distinct
stale-reprobe-pending state — reusing `PhantomDb.CandidateIsThresholdDeadSwarmSql`
— for an `availability_items.status='available'` item (movie AND episode) whose
every surviving `source_candidates` row is a threshold-exceeded dead swarm. So the
user now gets a truthful WARNING before a doomed playback attempt.

## 1. Baseline (P5 discipline — measure BEFORE ranking)

Real per-attempt counter re-queried in Mimir:

```
$ kubectl -n monitoring exec deploy/grafana -c grafana -- curl -sf \
    http://mimir.monitoring.svc:8080/prometheus/api/v1/query --data-urlencode \
    'query=phantom_playback_outcome_total{job="jellyfin-phantom-library"}'
```

Result (unchanged from day 10 — SAME 3 rig-driven samples):

| flow                 | item_type | cause             | value |
|----------------------|-----------|-------------------|-------|
| materialise_then_play| episode   | magnet_dead_stale | 2     |
| materialise_then_play| movie     | success           | 1     |

**No organic traffic yet.** Volume is still only the 3 rig-driven samples day 10
read (2 episode failures + 1 movie success); no new buckets, no accumulated
weight. Per the task card, this pass does NOT force a premature statistical
ranking from a handful of samples — it makes an HONEST directional read and
continues the code-level investigation approach day 10 established.

`playback_error_rate` (rig, directional): episode `materialise_then_play` =
2/2 = 100% (`magnet_dead_stale`); movie `materialise_then_play` = 0/1 = 0%.

## 2. Directional cause ranking (code-level, not statistical)

The single observed failing bucket remains **episode `magnet_dead_stale` in the
cold `materialise_then_play` flow** — identical to day 10. Day 10 correctly
traced the movie/TV asymmetry to a badge-parity gap and closed the SIGNAL half of
it (badge-deadswarm-episode-parity-001, landed). But a badge only WARNS the user;
it does not lower the actual `magnet_dead_stale` failure rate. The item is still
listed, still playable, and still fails when its only cached candidate is a
confirmed dead swarm.

Root-cause of the RESIDUAL failure (code reading — the biggest remaining win):

- The eager re-probe worker `AvailabilityProbeWorker` promotes stale-available
  rows to the front of the claim queue via
  `PhantomDb.MarkStaleAvailableItemsDueAsync`
  (`src/.../State/PhantomDb.cs:3008`, availability-stale-candidate-reprobe-001).
- But its promotion predicate only fires for rows with `status='available'`,
  `candidate_magnet IS NOT NULL`, AND **zero** surviving `source_candidates`
  (`NOT EXISTS (SELECT 1 FROM source_candidates …)`) — i.e. the candidate
  EXPIRED OUT of the cache.
- It does NOT promote a row whose candidate row still EXISTS but is a
  **confirmed threshold dead swarm** (`dead_swarm_confirmations >= threshold`).
  That is precisely the day-10 episode `magnet_dead_stale` case: the candidate is
  present (so the "no live candidate" clause is false), it is `status='available'`,
  it badges stale-reprobe-pending (day-10 fix knows this), yet the eager-reprobe
  worker never touches it. The dead candidate lingers indefinitely until the
  ordinary slow background sweep happens to re-probe it, and every playback
  attempt in the interim fails `magnet_dead_stale`.

So the badge KNOWS the state; the re-prober is blind to it. Closing that blind
spot — promoting confirmed-dead-swarm available rows to eager re-probe, reusing
the SAME `CandidateIsThresholdDeadSwarmSql` predicate the browse-prune and the
day-10 badge fix already use — actively refreshes the dead candidate to a live
one (or confirms unavailability, which then prunes it from browse) BEFORE the
next playback attempt. That directly attacks the observed `magnet_dead_stale`
failure rather than merely labelling it.

Residual buckets (`availability_abstain`, `no_candidate`, `gostream_*`,
`first_byte_timeout`, `plugin_host_error`) show ZERO real samples — explicitly
deferred until organic volume accumulates; not ranked from an empty bucket.

## 3. Chosen fix (biggest win) — enqueued

`availability-deadswarm-eager-reprobe-001`: extend
`MarkStaleAvailableItemsDueAsync` (and its `AvailabilityProbeWorker` caller
comment) so it ALSO promotes an `availability_items.status='available'` row
(movie AND episode, parity) whose EVERY surviving `source_candidates` row is a
threshold-exceeded dead swarm — reusing the existing
`CandidateIsThresholdDeadSwarmSql` predicate (do NOT duplicate the SQL). Design
doc + `dotnet test` check on the task. This is the ACTION half that
complements day-10's SIGNAL half.

## 4. Successor

`playback-error-reduction-012` appended `[TODO]`, held `not_before ≈ +24h`
(daily cadence). It will re-query for accumulated organic volume and — if the
eager-reprobe fix has landed — assess whether the episode `magnet_dead_stale`
rate has moved.

## Discipline note

DIAGNOSE-AND-ENQUEUE ONLY. No fix implemented here. Baseline recorded before the
ranking. `ROI.md` untouched.

## Reconciliation note (added by later session)
This file was authored by an earlier `playback-error-reduction-011` session
that filed `availability-deadswarm-eager-reprobe-001` and
`playback-error-reduction-012` (both now landed/progressed) but whose branch
push raced/orphaned before the runner could merge it, stranding the task
claim. A later session independently re-derived and wrote an equivalent
analysis, then discovered this orphaned branch on reconciliation; the two
were merged, keeping this (the original, contemporaneous) analysis as the
canonical record since it predates and matches the already-landed follow-up
work.
