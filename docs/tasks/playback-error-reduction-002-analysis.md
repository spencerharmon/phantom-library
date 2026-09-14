# playback-error-reduction-002 — analysis (day 2 baseline + rank + enqueue)

Series: see `docs/tasks/playback-error-reduction-001.md` (read FIRST). This is
the SECOND daily analytical pass. Same DIAGNOSE-AND-ENQUEUE contract as day 1:
BASELINE the current playback error rate, RANK the dominant failure cause, and
ENQUEUE ONE concrete fix for the biggest win. It implements NOTHING
(diagnose-and-enqueue only).

## Baseline (P5 discipline: measure BEFORE optimising)

### What is now instrumented since day 1

Day 1 enqueued `playback-outcome-instrumentation-001` as the enabling win: the
series could not rank causes with numbers until a cause-labelled per-attempt
outcome metric EXISTED. That task is now **DONE** (PLAN
`commits=feb06dfb84b8aef4efe88f0b135e0b75c645d3fd`). It added:

- `PhantomFlowMetrics.RecordPlaybackOutcome(flow, item_type, cause)` on the
  OTLP-native `Phantom.Flows` meter, sunk to `observe.spencerharmon.com` and
  mirrored to Mimir (`mimir.spencerharmon.com`) via the P8 Pushgateway path.
- The `phantom_playback_outcome_total{flow,item_type,cause}` counter emitted by
  `tools/rig-scenarios/47-loadtime-flows.sh` (with the offline dry-run synthetic
  fixture), covering both flows (`materialise_then_play`,
  `play_already_materialised`), both item_types (`movie`, `episode`), and the
  full cause set (`success`, `availability_abstain`, `no_candidate`,
  `magnet_dead_stale`, `gostream_register_fail`, `gostream_cannot_fetch`,
  `first_byte_timeout`, `plugin_host_error`).

So the METRIC CONTRACT the series ratchets now exists and is regression-guarded
(`./scripts/tests/phantom-playback-outcome.test.sh`).

### Can day 2 read a real cause-labelled number yet? — Honest gap

**Not yet.** The instrumentation landed, but a real, queryable, cause-labelled
`playback_error_rate` SAMPLE from live traffic is not available to this
analytical pass:

- The `phantom_playback_outcome_total` series in Mimir/`observe` only
  accumulates once the DONE instrumentation is DEPLOYED to the operator's
  Jellyfin (patched DLL + gostream) AND real playback attempts have flowed
  through it for a day. `playback-outcome-instrumentation-001` built and
  regression-tested the MEASUREMENT; deploying it to prod and letting a day of
  real attempts accrue is a separate, later-converging step. The DONE stamp
  certifies the metric contract, not a day of accumulated prod data.
- This analytical pass has no live prod Mimir/`observe` query access from its
  worktree, and the rig's `phantom_playback_outcome_total` output is a
  DETERMINISTIC SYNTHETIC FIXTURE (a contract test — every flow × item_type ×
  cause emitted exactly once), not a real error-rate sample. Counting the
  fixture would fabricate a baseline, which the P5 discipline forbids.

Per the day-2 task's explicit fallback clause ("if it has NOT yet landed [as a
readable real series], re-baseline from the coarse P8 rig errors_total +
phantom_availability_probes_total as day 1 did, and note the gap"), this pass
re-baselines from the coarse signals and records the gap. The FIRST pass that
can read a real per-cause number is the first daily pass AFTER the
instrumentation has been deployed to prod and emitted a day of live data.

### Baseline value (honest, coarse — same substrate as day 1)

Unchanged in KIND from day 1 (no real cause-labelled prod series to read yet),
so the coarse baseline stands:

- **P8 rig coarse error rate** (`phantom_loadtime_errors_total /
  phantom_loadtime_runs_total`, per flow × item_type): the cold flow
  (`materialise_then_play`) remains the dominant error contributor — its
  `errors_total/runs_total` is materially non-zero while the warm flow
  (`play_already_materialised`) succeeds once a gostream file exists. No
  cause/stage label on this signal (a failed materialise is counted the same
  whether it abstained, found no candidate, or timed out).
- **`phantom_availability_probes_total{type,outcome}`** — the upstream
  discriminator — continues to show the cold flow's failures concentrate BEFORE
  a candidate is ever tried (availability/no-candidate stages), consistent with
  the P6/P10 evidence that many browse items never resolve to a viable magnet.

No movement can be PROVEN yet: day 1 enqueued only the measurement dial (no
behaviour change), so the coarse rate is expected to be unchanged from day 1.
That is the correct, honest day-2 reading — the first behaviour-changing fix is
what THIS pass enqueues.

## Cause ranking (biggest win first)

Ranking is unchanged from day 1's evidence (no new behaviour has shipped to move
it; the new metric confirms the contract but has no live sample yet):

1. **`availability_abstain` + `no_candidate` on the cold flow** — the single
   biggest bucket. Cold attempts fail BEFORE any stream handoff because the
   availability oracle abstained (no-IMDB/Torrentio abstain, no capable indexer)
   or produced a verdict with no surviving `source_candidates`. Maps directly
   onto **primary dial #1** (raise the availability-probe success rate). Highest
   expected win, and now the natural next fix because the measurement dial has
   landed.
2. **`no_candidate` from all-`invalid` candidates (cold-materialise-fail)** — an
   item is `status='available'` but every cached candidate is `invalid`; the
   attempt is doomed but the item may still be offered in browse. Reducible by
   dial #2 (browse prune) and by dial #1's negative-result caching / re-probe.
3. **gostream handoff stages** (`gostream_register_fail`,
   `gostream_cannot_fetch`, `first_byte_timeout`) — real but SECONDARY: they
   only occur AFTER a viable candidate exists, the minority path today because
   so many attempts die upstream at (1)/(2). Worth measuring, not yet worth
   fixing first.

## The one fix enqueued (biggest win)

Day 1 built the MEASUREMENT (`playback-outcome-instrumentation-001`, now DONE).
With the metric contract in place and the cause ranking putting
`availability_abstain` + `no_candidate` on the cold flow as the single biggest
bucket, day 2 enqueues the biggest BEHAVIOUR win — **primary dial #1: raise the
availability-probe success rate**:

- **`availability-probe-reconcile-001`** — at materialise time, reconcile the P6
  Torrentio availability oracle with the Prowlarr high-confidence magnet set
  before abstaining, so a cold attempt that Torrentio alone would abstain on
  (e.g. no-IMDB titles) can still reach a definitive `available` verdict when
  Prowlarr holds a high-confidence magnet; re-probe on TTL expiry rather than
  serving a stale abstain; and cache NEGATIVE probe results with bounded
  exponential backoff so a genuinely-unavailable item is not re-probed on every
  attempt (protecting the oracle) yet is eventually re-checked. This attacks
  buckets (1) and (2) of the ranking directly — it converts
  `availability_abstain`/`no_candidate` cold failures into definitive-available
  attempts (movie AND episode parity). Design doc:
  `docs/tasks/availability-probe-reconcile-001.md`. `Check:` = the
  `47-loadtime-flows.sh`-driven `script-test` harness
  `./scripts/tests/availability-probe-reconcile.test.sh` (matches the
  `CHECKS.md` `script-test` framework), asserting the reconcile/TTL/negative-
  cache behaviour offline against the synthetic fixture for both item_types.

This is dial #1 (the highest-expected-win behaviour change), enqueued now that
the measurement dial has landed. Once it has shipped and the deployed
`phantom_playback_outcome_total` series has emitted a real day of data, later
passes will read the actual per-cause movement and choose between further dial
#1 tuning and dial #2 (browse prune).

## Successor

`playback-error-reduction-003` appended `[TODO]` held on `not_before ~= +24h`
for tomorrow's pass (daily cadence).
