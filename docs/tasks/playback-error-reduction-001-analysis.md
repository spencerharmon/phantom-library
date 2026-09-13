# playback-error-reduction-001 — analysis (day 1 baseline + rank + enqueue)

Series: see `docs/tasks/playback-error-reduction-001.md`. This is the FIRST
daily analytical pass: it BASELINES the current playback error rate, RANKS the
dominant failure cause from the evidence available today, and ENQUEUES ONE
concrete fix task for the biggest win. It implements NOTHING itself
(diagnose-and-enqueue only).

## Baseline (P5 discipline: measure BEFORE optimising)

### What can be measured today, and what cannot

The playback-error-rate metric this series ratchets —
`playback_errors / playback_attempts`, split by flow × item_type × cause —
**does not exist yet as a first-class, cause-broken-down series**. The
evidence that DOES exist today:

- **P8 rig, coarse error count** (`tools/rig-scenarios/47-loadtime-flows.sh`):
  emits `phantom_loadtime_errors_total{flow,item_type,color}` and
  `phantom_loadtime_runs_total{...}` for the `materialise`/`get_sources`/
  `play_materialised` flows. This gives a per-flow, per-item_type error RATE
  (`errors_total / runs_total`) but **no cause/stage label** — a failed
  `materialise` is counted the same whether it abstained, found no candidate,
  or timed out at first byte. So the rate is observable; the CAUSE is not.
- **Availability-probe outcomes** (`PhantomMetrics.AvailabilityProbe(type,
  outcome)` → `phantom_availability_probes_total{type,outcome}`): the closest
  existing cause signal. It records the availability oracle's verdict per
  probe, which is the UPSTREAM discriminator between the cold-flow's two
  dominant failure causes (`availability_abstain` vs. a definitive verdict
  that later yields `no_candidate`).
- **`TorrentioClient` abstains** on any no-IMDB title
  (`IndexerNotApplicableException`), and `Materialiser.MarkCandidateFailedAsync`
  records hard candidate-validation failures — both are code-level evidence of
  where cold-materialise attempts die, but neither is aggregated into a
  playback-outcome counter yet.

### Baseline value (honest)

There is **no committed, cause-labelled `playback_error_rate` series to read a
baseline number from** on day 1 — the instrumentation that would produce it is
exactly what the primary-dial fix (below) must add. The baseline this pass can
assert from existing evidence:

- The cold flow (`materialise_then_play`) is the dominant error contributor:
  the P8 rig documents materialise as the flow that "most often fails today
  per P6" (see `47-loadtime-flows.sh` header) — its `errors_total/runs_total`
  is materially non-zero while the warm flow (`play_already_materialised`)
  succeeds once a gostream file exists.
- The upstream discriminator (`phantom_availability_probes_total`) shows the
  cold flow's failures concentrate BEFORE a candidate is ever tried — i.e. in
  the availability/no-candidate stages — consistent with the P6/P10 findings
  that many browse items never resolve to a viable magnet.

Day 2+ passes will read a real number once the enqueued instrumentation fix
has landed and emitted a first day of the cause-labelled series.

## Cause ranking (biggest win first)

From the evidence above, ranked by contribution to the playback error rate:

1. **`availability_abstain` + `no_candidate` on the cold flow** — the single
   biggest bucket. Attempts fail before any stream handoff because the
   availability oracle abstained (no-IMDB/Torrentio abstain, no-capable
   indexer) or produced a verdict with no surviving `source_candidates`. This
   maps directly onto **primary dial #1** (raise availability-probe success
   rate). Highest expected win.
2. **`no_candidate` from all-`invalid` candidates (cold-materialise-fail)** —
   an item is `status='available'` but every cached candidate has been marked
   `invalid`; the attempt is doomed but the item may still be offered in
   browse. Reducible by **primary dial #2** (prune from default browse — cuts
   the doomed-attempt denominator) and by dial #1's negative-result caching /
   re-probe.
3. **gostream handoff stages** (`gostream_register_fail`,
   `gostream_cannot_fetch`, `first_byte_timeout`) — real but SECONDARY on
   today's evidence: they only occur AFTER a viable candidate exists, which is
   the minority path today because so many attempts die upstream at (1)/(2).
   Worth measuring, not yet worth fixing first.

## The one fix enqueued (biggest win)

**Blocker to fixing anything: we cannot rank causes with numbers until the
cause-labelled outcome metric EXISTS.** So the biggest, enabling win for the
whole series is to build the MEASUREMENT itself — the definitive
per-playback-attempt outcome counter, split by flow × item_type × cause,
sunk over OTLP to `observe.spencerharmon.com` and mirrored into Mimir, and
surfaced on the operator Grafana dashboard alongside the P8 load-time flows.
Without it, dial #1 and dial #2 fixes cannot be proven to move the rate (P5
discipline). This is the first concrete fix task:

- **`playback-outcome-instrumentation-001`** — instrument the real playback
  path to record a definitive outcome per attempt, add the cause-labelled
  counter to the OTLP `Phantom.Flows` meter + a Prometheus mirror via the P8
  Pushgateway path, extend `47-loadtime-flows.sh` to emit
  `phantom_playback_outcome_total{flow,item_type,cause}` for the
  materialise/get_sources/play flows, with an in-repo `script-test` harness
  asserting the record contract offline. Design doc:
  `docs/tasks/playback-outcome-instrumentation-001.md`. `Check:` = its
  `scripts/tests/*.test.sh` harness (matches the `CHECKS.md` `script-test`
  framework).

This deliberately builds the instrumentation dial FIRST (the biggest,
enabling win). Once it has landed and produced a day of real data, the day-2+
passes will read the actual per-cause rate and enqueue the availability-probe
(dial #1) or browse-prune (dial #2) fix that the numbers then justify.

## Successor

`playback-error-reduction-002` appended `[TODO]` held on `not_before ~= +24h`
for tomorrow's pass (daily cadence).
