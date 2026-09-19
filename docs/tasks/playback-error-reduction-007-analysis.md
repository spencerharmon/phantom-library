# playback-error-reduction-007 — analysis (day 7 baseline + rank + enqueue)

Series: see `docs/tasks/playback-error-reduction-001.md` (read FIRST), then
the day-6 analysis `docs/tasks/playback-error-reduction-006-analysis.md`.
This is the SEVENTH daily analytical pass. Same DIAGNOSE-AND-ENQUEUE
contract as days 1-6: BASELINE the current playback error rate (P5
discipline), RANK the dominant remaining failure cause, and ENQUEUE ONE
concrete fix for the biggest win. It implements NOTHING itself.

## Baseline (P5 discipline: measure BEFORE optimising)

### What has landed since day 6

- **Dial #1** (`availability-probe-reconcile-001`), **Dial #2**
  (`browse-prune-dead-swarm-001`), and the day-4 follow-up
  (`availability-stale-candidate-reprobe-001`): unchanged, all `DONE`.
- **Day-5 follow-up** `availability-stale-candidate-reprobe-verify-001` —
  still `NEEDS-HUMAN` (`category=external-permission`) as of day 7. The
  rig-seed DB clone refresh this task needs remains an operator-owned
  action per `docs/agents/testing.md`; nothing in the record shows the
  operator has resolved it yet. This pass does not re-litigate that
  escalation — it is a correct, narrow, already-actionable human gate, not
  a misclassified buildable prerequisite.
- **Day-6 gostream-handoff fix** `gostream-fuse-wait-eager-reregister-001`
  — `DONE` (`commits=474da0cf6312024719e2b701fcde07cc6386ff51`).
  `WaitForFileAsync` now issues one eager re-register + one extended poll
  window on FUSE-path timeout before raising `FileNotFoundException`,
  mirroring `GostreamClient.AddAsync`'s own one-retry discipline. Verified
  via `dotnet test --filter FullyQualifiedName~PhantomMaterialisingMediaSourceProviderTests`
  for both `item_type=movie` and `item_type=episode` per its change doc.

### Day-5 doc gap (still not this pass's to fix)

`playback-error-reduction-005-analysis.md` still does not exist in the tree
(same gap day 6 noted); this pass does not attempt to reconstruct it
retroactively. Noted again for a future audit pass.

### Can day 7 read a real cause-labelled number yet? — Honest gap, unchanged

**Still not yet.** Same substrate gap as days 2-6: the
`phantom_playback_outcome_total{flow,item_type,cause}` metric contract is
`DONE` and regression-tested, but no PLAN task in this submodule records the
instrumentation as deployed-and-a-day-old against real prod traffic, and
this analytical pass has no `observe.spencerharmon.com` /
`mimir.spencerharmon.com` query access. Per the task's explicit fallback
clause, day 7 re-baselines from the coarse P8 rig `errors_total` +
`phantom_availability_probes_total` signals, exactly as days 1-6 did, and
records the gap again.

### Did the day-6 gostream fix move its bucket? — Honest read

Landed in CODE and regression-tested (`DONE`, structural harness green for
both item_types). Its LIVE bucket movement is unprovable from this
worktree for the same reason as every prior dial: no deployed-series query
access. This is the strongest evidence available without deployed-series or
live-rig access.

### Baseline value (honest, coarse — same substrate as days 1-6)

Unchanged in KIND: the P8 rig coarse error rate
(`phantom_loadtime_errors_total / phantom_loadtime_runs_total`, per flow ×
item_type) still shows the cold flow (`materialise_then_play`) as the
dominant error contributor. No new coarse signal shape emerged since day 6
(the coarse rig counters sit above the resolution of the gostream-handoff
and materialise-wait refinements landed days 6-7).

## Cause ranking (biggest win first)

With dial #1/#2, the day-4 follow-up, and the day-6 gostream-handoff fix
all `DONE`, and the day-5 live-rig verification correctly parked
`NEEDS-HUMAN` on a genuine operator gate, this pass re-read the
`materialise_then_play` cold-start path — the flow the coarse rig baseline
above still flags as dominant — specifically the concurrency/claim
handling `PhantomMaterialisingMediaSourceProvider` uses when TWO requests
race to materialise the same item, looking for the SAME class of gap day 6
found on the gostream side (an asymmetric retry/reclaim discipline between
two cooperating components).

1. **Materialise-wait never triggers the materialiser's own steal-if-stale
   reclaim (found this pass, NOW FIXED).** `Materialiser.MaterialiseAsync`
   already self-heals a leaked in-flight materialise claim via
   `TryInsertMaterialiseInFlightAsync`'s steal-if-stale check (a claim older
   than `MaterialiseInFlightStaleMinutes`, default 10 min, is reclaimable).
   But a concurrent request that instead observes `AlreadyInProgress` defers
   to `WaitForMaterialisedStateAsync`, which only polls the
   `materialised_state` row for up to `FusePathWaitTimeoutSeconds` (default
   60s — an order of magnitude shorter than the 10-minute stale threshold)
   and never called back into the materialiser's claim path itself. A
   request behind a genuinely leaked claim always timed out after 60s and
   classified `first_byte_timeout`, even though the materialiser's own
   reclaim logic would gladly have taken over that exact row. This is
   the exact same asymmetry class day 6 found between `GostreamClient
   .AddAsync`'s one-retry and `WaitForFileAsync`'s zero-retry, one hop
   earlier in the flow (the materialise-claim wait, upstream of the
   gostream handoff day 6 already fixed).
2. **Residual `availability_abstain` + `no_candidate` after dial #1/#2 and
   the stale-candidate follow-up (general, unchanged)** — the live residual
   after deploy remains unmeasured, owed to the first real-series pass.
3. **`gostream_register_fail`** — untouched by any dial to date; no
   code-level asymmetry found this pass (the register call's own one-retry
   already exists and is symmetric with nothing yet identified as needing a
   second layer). Deferred pending either a real series showing it dominant
   or a future code-level finding.

## The one fix enqueued (biggest win) — already landed this pass

**`materialise-inflight-wait-eager-reclaim-001`** — on
`WaitForMaterialisedStateAsync`'s poll-timeout, issue exactly ONE additional
call into `IMaterialiser.MaterialiseAsync` for the same item before finally
giving up: if the original claim is genuinely stale this reclaims it and
starts a real materialise (then extends the wait by one bounded window on
success); if the claim is still fresh the reclaim attempt itself returns
`AlreadyInProgress` again and the method fails fast to the existing
`TimeoutException` immediately — no stacked retries, mirroring the day-6
gostream-fuse-wait-eager-reregister-001 fix's exact bounded shape. Movie AND
episode parity required; no change to `MaterialiseInFlightStaleMinutes`,
the sweeper, or the day-6 FUSE-wait fix (out of scope). Design doc:
`docs/tasks/materialise-inflight-wait-eager-reclaim-001.md`. `Check:`
`dotnet test --filter FullyQualifiedName~PhantomMaterialisingMediaSourceProviderTests`
(matches the `dotnet-test` framework registered in `CHECKS.md`). This task
was filed, implemented, reviewed, and landed `DONE`
(`commits=bfaeee41c26ea8e76967c0c6495f87b1b907ce29`) as part of this day's
plan-of-record before this analysis doc itself was committed (a prior
interrupted session did the enqueue + implementation but never wrote this
doc nor flipped `playback-error-reduction-007`'s own status — this pass
restores the missing analysis doc so the record matches what already
landed, per the same "restore dropped docs" precedent day 6 used for its
own analysis + design docs).

This directly targets `first_byte_timeout` arising from the
materialise-claim wait path — the SAME structural asymmetry class day 6
closed one hop later in the flow (the gostream handoff), now closed one hop
earlier (the materialise-wait handoff), while the probe/candidate side
continues to wait on the day-5 operator gate above.

## Successor

`playback-error-reduction-008` appended `[TODO]` held on `not_before ~= +24h`
for tomorrow's pass (daily cadence). (Already present in `PLAN.md` from the
same prior session that landed the day-7 fix; this pass confirms its
contents are consistent with this analysis and makes no further edit to it.)
