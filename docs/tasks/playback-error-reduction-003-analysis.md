# playback-error-reduction-003 — analysis (day 3 baseline + rank + enqueue)

Series: see `docs/tasks/playback-error-reduction-001.md` (read FIRST), then the
day-2 analysis `docs/tasks/playback-error-reduction-002-analysis.md`. This is
the THIRD daily analytical pass. Same DIAGNOSE-AND-ENQUEUE contract as days 1-2:
BASELINE the current playback error rate (P5 discipline), RANK the dominant
remaining failure cause, and ENQUEUE ONE concrete fix for the biggest win. It
implements NOTHING (diagnose-and-enqueue only).

## Baseline (P5 discipline: measure BEFORE optimising)

### What has landed since day 2

Day 2 enqueued **primary dial #1** — `availability-probe-reconcile-001` (raise
the availability-probe success rate). That task is now **DONE** (PLAN
`commits=af58d0925fb526d07e2e3ed15494d1e41273cf19,cf2a5e88b15afe3186e5d66e1d091598dc569ed2,81f8e3fe476fb4b8e98cad1ee422e6c40c88d1f7`).
It reconciles the P6 Torrentio availability oracle with the Prowlarr
high-confidence magnet set before abstaining, re-probes on TTL expiry, and
caches negative probe results with bounded exponential backoff — attacking the
`availability_abstain` + `no_candidate` cold-flow bucket (movie AND episode
parity), regression-guarded by
`./scripts/tests/availability-probe-reconcile.test.sh`.

The measurement dial `playback-outcome-instrumentation-001` (day 1) remains
DONE: the cause-labelled `phantom_playback_outcome_total{flow,item_type,cause}`
contract exists and is regression-guarded
(`./scripts/tests/phantom-playback-outcome.test.sh`).

### Can day 3 read a real cause-labelled number yet? — Honest gap

**Still not yet.** Same substrate as day 2: the metric CONTRACT is DONE and
regression-tested, but a real, queryable, cause-labelled `playback_error_rate`
SAMPLE from live prod traffic is not available to this analytical pass:

- `phantom_playback_outcome_total` only accumulates in Mimir/`observe` once the
  DONE instrumentation is DEPLOYED to the operator's Jellyfin (patched DLL +
  gostream) AND real playback attempts have flowed through it for a day. The
  DONE stamp certifies the metric contract, not a day of accumulated prod data.
- This pass has no live prod Mimir/`observe` query access from its worktree, and
  the rig's `phantom_playback_outcome_total` output is a DETERMINISTIC SYNTHETIC
  FIXTURE (every flow × item_type × cause emitted exactly once), not a real
  error-rate sample. Counting the fixture would fabricate a baseline, which P5
  forbids.

Per the day-3 task's explicit fallback clause ("if a real queryable series is
still not available, re-baseline from the coarse P8 rig errors_total +
phantom_availability_probes_total as days 1-2 did, and note the gap"), this pass
re-baselines from the coarse signals and records the gap. The FIRST pass that
can read a real per-cause number is the first daily pass AFTER the
instrumentation is deployed to prod and has emitted a day of live data.

### Did dial #1 move the availability_abstain + no_candidate bucket? — Honest read

`availability-probe-reconcile-001` has LANDED (code merged, DONE) but its
running effect on the live error rate cannot be PROVEN from this worktree: the
same deployed-prod-series gap above applies. What IS provable now, offline:

- The reconcile/TTL/negative-cache behaviour is exercised and passing against
  the synthetic fixture (its `script-test` Check is green) for both item_types,
  so the cold-flow abstain→definitive-available conversion is CORRECT by
  construction.
- The live bucket movement (how much `availability_abstain` + `no_candidate`
  actually shrank) is a deployed-and-converged reading owed to the first daily
  pass with a real prod series. Recorded as the standing gap, not asserted.

### Baseline value (honest, coarse — same substrate as days 1-2)

Unchanged in KIND from days 1-2 (no real cause-labelled prod series to read
yet), so the coarse baseline stands:

- **P8 rig coarse error rate** (`phantom_loadtime_errors_total /
  phantom_loadtime_runs_total`, per flow × item_type): the cold flow
  (`materialise_then_play`) remains the dominant error contributor; the warm
  flow (`play_already_materialised`) succeeds once a gostream file exists. No
  cause/stage label on this coarse signal.
- **`phantom_availability_probes_total{type,outcome}`** — the upstream
  discriminator — continues to show the cold flow's failures concentrate BEFORE
  a candidate is ever tried (availability/no-candidate stages), consistent with
  P6/P10 evidence that many browse items never resolve to a viable magnet.

## Cause ranking (biggest win first)

With dial #1 landed (attacking the top bucket at the source), day 3 re-ranks the
REMAINING dominant cold-flow cause the coarse evidence still exposes:

1. **`magnet_dead_stale` on the cold flow — doomed-but-visible dead-swarm
   items.** A candidate that resolves then dies at the swarm is a SOFT/transient
   failure: `Materialiser.MarkCandidateFailedAsync` only marks a candidate
   `validation_status='invalid'` for a HARD reason (`IsHardValidationReason`); a
   dead/stale swarm is normalised to a transient status
   (`unknown`/`transient`) with a short retry TTL. P10's browse-prune subquery
   (`ListVisibleMovieRowsAsync` / `ListVisibleSeriesRowsAsync`) excludes an item
   only when EVERY candidate is `'invalid'`, so a dead-swarm candidate keeps the
   item VISIBLE in default browse — yet every cold attempt re-resolves the same
   dead swarm and records `magnet_dead_stale`. This is the largest remaining
   avoidable cold-flow denominator now that dial #1 addresses the abstain/
   no_candidate upstream bucket. Maps onto **primary dial #2** (prune
   unlikely-playable items from default browse).
2. **Residual `availability_abstain` + `no_candidate`** — dial #1 attacks this
   at the source but the live residual after deploy is unmeasured; revisit once
   a real prod series exists (owed to day 4+).
3. **gostream handoff stages** (`gostream_register_fail`,
   `gostream_cannot_fetch`, `first_byte_timeout`) — real but SECONDARY: they
   only occur AFTER a viable candidate exists, still the minority path because
   so many attempts die upstream. Worth measuring, not yet worth fixing first.

## The one fix enqueued (biggest win)

Day 2 shipped dial #1. With the ranking putting doomed-but-visible dead-swarm
items as the largest remaining avoidable cold-flow bucket, day 3 enqueues
**primary dial #2: prune unlikely-playable items from default browse** —
specifically the dead-swarm gap P10 explicitly left open:

- **`browse-prune-dead-swarm-001`** — extend the P10 correlated-subquery prune
  in BOTH `ListVisibleMovieRowsAsync` and `ListVisibleSeriesRowsAsync` (movie
  AND episode/series parity — REQUIRED) so an `'available'` item is excluded
  from DEFAULT BROWSE when every one of its `source_candidates` rows is EITHER
  `validation_status='invalid'` OR a confirmed threshold-exceeded
  `magnet_dead_stale` dead-swarm transient, while staying searchable/badged (P6
  split) and reappearing automatically when a fresh non-dead candidate is
  cached. Additive, no bar-lowering, no new background loop, prefer no schema
  change. This directly shrinks the cold-flow `magnet_dead_stale` denominator
  (bucket #1). Design doc: `docs/tasks/browse-prune-dead-swarm-001.md`.
  `Check:` = the `47-loadtime-flows.sh`-driven `script-test` harness
  `./scripts/tests/browse-prune-dead-swarm.test.sh` (matches the `CHECKS.md`
  `script-test` framework), asserting the prune/searchable/reappear behaviour
  offline against the synthetic fixture for both item_types.

This is dial #2, enqueued as the biggest remaining behaviour win now that dial
#1 has landed. Once it ships and the deployed `phantom_playback_outcome_total`
series emits a real day of data, later passes will read the actual per-cause
movement and choose between dial #1 tuning, further dial #2 pruning, and the
gostream handoff stages.

## Successor

`playback-error-reduction-004` appended `[TODO]` held on `not_before ~= +24h`
for tomorrow's pass (daily cadence).
