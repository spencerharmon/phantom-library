# playback-error-reduction-007 — analysis (day 7 baseline + rank + enqueue)

Series: see `docs/tasks/playback-error-reduction-001.md` (read FIRST), then the
day-6 analysis `docs/tasks/playback-error-reduction-006-analysis.md`. This is
the SEVENTH daily analytical pass. Same DIAGNOSE-AND-ENQUEUE contract as days
1-6: BASELINE the current playback error rate (P5 discipline), RANK the
dominant remaining failure cause, and ENQUEUE ONE concrete fix for the
biggest win. It implements NOTHING itself beyond the analysis + the enqueued
task/doc pair.

## What has landed since day 6

- **Dial #1** (`availability-probe-reconcile-001`) and **Dial #2**
  (`browse-prune-dead-swarm-001`): unchanged, both `DONE`.
- **Day-4 follow-up** `availability-stale-candidate-reprobe-001`: unchanged,
  `DONE` (structural/behavioural harness only, see day 6).
- **Day-5 follow-up** `availability-stale-candidate-reprobe-verify-001`:
  **still `NEEDS-HUMAN`** (`category=external-permission`). Re-checked
  `PLAN.md` this pass — the task still carries the same `Human-needed:`
  block from day 6 (rig-seed DB clone at
  `/var/tmp/jf-test/data/data/jellyfin.db` is 0-byte/corrupted; refreshing it
  is explicitly an operator action per `docs/agents/testing.md`). No
  indication the operator has resolved it yet (no new commit, no status
  change, no updated `Human-needed:` note). This pass does not re-litigate
  it — it is a correctly narrow, already-actionable human gate, unchanged.
- **Day-6 fix** `gostream-fuse-wait-eager-reregister-001` — **landed and
  verified in this worktree**: `PLAN.md` shows it `DONE`
  (`commits=474da0cf6312024719e2b701fcde07cc6386ff51`). Read the actual code
  this pass (`Channels/PhantomMaterialisingMediaSourceProvider.cs`,
  `WaitForFileAsync` ~L449-501): on FUSE-path poll-timeout it now issues
  exactly one eager re-register call to gostream and, on success, extends
  the wait by one additional bounded poll window before raising
  `FileNotFoundException` — exactly as designed. The dotnet regression
  (`PhantomMaterialisingMediaSourceProviderTests`) asserting the eager
  re-register invocation and the no-stacked-retry-on-failure case is present
  in the tree. This closes the specific `gostream_cannot_fetch` asymmetry
  day 6 found; whether it actually MOVED the live bucket remains unprovable
  from this worktree (same deployed-series gap as every prior day).

## Can day 7 read a real cause-labelled number yet? — Honest gap, unchanged

**Still not yet.** `playback-outcome-instrumentation-001` remains `DONE` in
code (the `phantom_playback_outcome_total{flow,item_type,cause}` OTLP/
Prometheus counter contract, regression-tested), but no PLAN task in this
submodule records it as deployed-and-a-day-old against real prod traffic,
and this pass has no `observe.spencerharmon.com` / `mimir.spencerharmon.com`
query access from the sandbox. **Seven days into this series, "deploy the
instrumentation to prod" itself has never been filed as its own task** — it
has only ever been referenced as a precondition inside each day's analysis
prose. That gap is itself now the most durable, code-visible blocker to this
whole series ever reading a real number, independent of which dial or
handoff stage actually dominates. Per the task's explicit fallback clause,
day 7 again re-baselines from the coarse P8 rig `errors_total` +
`phantom_availability_probes_total` signals, as days 1-6 did.

### Baseline value (honest, coarse — same substrate as days 1-6)

Unchanged in KIND: the P8 rig coarse error rate
(`phantom_loadtime_errors_total / phantom_loadtime_runs_total`, per flow ×
item_type) still shows the cold flow (`materialise_then_play`) as the
dominant error contributor. No new coarse signal shape emerged since day 6
(the coarse rig counters sit above the resolution of the gostream-handoff
fix day 6 shipped, exactly as they sat above dial #1/#2's resolution in
prior days).

## Cause ranking (biggest win first)

With both primary dials `DONE`, the day-4 follow-up `DONE`, the day-5
live-rig verification correctly parked `NEEDS-HUMAN` (unresolved, not this
pass's to force), and the day-6 gostream FUSE-wait asymmetry now closed,
this pass re-read the cold-materialise concurrency path
(`Materialiser.MaterialiseAsync`,
`PhantomMaterialisingMediaSourceProvider.OpenMediaSourceCore`/
`WaitForMaterialisedStateAsync`) the same way day 6 re-read the gostream
handoff — looking for a CODE-VISIBLE structural gap, not a traffic-volume
guess.

**Found: the SAME asymmetry class as day 6, one call earlier in the
handoff, on the `AlreadyInProgress` branch.**

- `Materialiser.MaterialiseAsync` (`Materialisation/Materialiser.cs:284-297`)
  already has a "steal-if-stale" discipline: a `TryInsertMaterialiseInFlightAsync`
  claim older than `MaterialiseInFlightStaleMinutes` (default 10 min) is
  reclaimed by the NEXT caller who attempts to claim it, so a leaked
  in-flight row from a hard-killed materialise self-heals **on the next
  materialise attempt**.
- But when a concurrent request instead observes `AlreadyInProgress` (someone
  else already holds the claim) and defers to
  `WaitForMaterialisedStateAsync` (`Channels/PhantomMaterialisingMediaSourceProvider.cs:409-429`),
  that method ONLY polls the `materialised_state` row — it never re-attempts
  the claim itself. Its wait window is `FusePathWaitTimeoutSeconds` (default
  **60s**), an order of magnitude SHORTER than `MaterialiseInFlightStaleMinutes`
  (default **600s**). So a request that lands behind a claim that is actually
  LEAKED (stale) will always time out after 60s and classify as
  `first_byte_timeout` — even though `Materialiser.MaterialiseAsync`'s own
  steal-if-stale logic would gladly reclaim that exact row, because nothing
  in the 60s poll loop ever calls back into `MaterialiseAsync` to trigger the
  reclaim. Only some UNRELATED future caller (or the once-at-startup
  `MaterialiseInFlightSweeper`) eventually clears it. This is the identical
  shape of gap day 6 fixed one step later in the handoff (a call that gives
  up with zero retry where a sibling call one step over already has a
  self-healing retry discipline) — same fix pattern applies: mirror the
  existing steal-if-stale reclaim into the wait loop's own timeout instead
  of only into a fresh top-level `MaterialiseAsync` call.
- This targets `first_byte_timeout`'s OTHER source that day 6 explicitly
  deferred ("upstream of gostream... noted for a future pass") — now
  justified by the same kind of code-level asymmetry read, not a
  traffic-volume assumption.
- Residual `availability_abstain` / `no_candidate` (item 2 from day 6):
  unchanged, still owed to the first real deployed-series read.

## The one fix enqueued (biggest win)

**`materialise-inflight-wait-eager-reclaim-001`** — on
`WaitForMaterialisedStateAsync`'s poll-timeout (the `materialised_state` row
still hasn't appeared after `FusePathWaitTimeoutSeconds`), issue exactly ONE
additional call back into the materialiser's claim path for the same item
before finally giving up: if the original claim is genuinely stale
(`MaterialiseInFlightStaleMinutes` elapsed), this reclaims it and starts a
real materialise attempt, then extends the wait by one additional bounded
poll window; if the claim is still fresh (materialise is genuinely still in
flight), the reclaim attempt itself returns `AlreadyInProgress` again and the
method fails fast to the existing `TimeoutException` immediately — no
stacked retries, no unbounded backoff, mirroring the exact bounded shape of
the day-6 gostream-FUSE-wait fix. Movie AND episode parity required. No
change to `MaterialiseInFlightStaleMinutes`, no change to the sweeper, no
change to the FUSE-wait fix from day 6 (out of scope). Design doc:
`docs/tasks/materialise-inflight-wait-eager-reclaim-001.md`. `Check:` `dotnet
test --filter FullyQualifiedName~PhantomMaterialisingMediaSourceProviderTests`
(matches the `dotnet-test` framework registered in `CHECKS.md`).

This directly targets `first_byte_timeout`'s upstream (materialise-wait)
source, complementing day 6's downstream (gostream-FUSE-wait) fix for the
same cause label, while the availability/candidate side waits on the
operator gate above and the deployed-series gap (noted above, not this
pass's to close) remains the open precondition for ever reading a real
number.

## Successor

`playback-error-reduction-008` appended `[TODO]` held on `not_before ~=
+24h` for tomorrow's pass (daily cadence).
