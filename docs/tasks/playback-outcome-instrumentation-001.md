# playback-outcome-instrumentation-001 — definitive per-attempt playback outcome metric

Enqueued by `playback-error-reduction-001` (series ROI Priority 12) as the
biggest, enabling win: the playback-error-rate series cannot rank causes with
numbers until a cause-labelled per-attempt outcome metric EXISTS. This task
builds that measurement. It does NOT yet change playback behaviour (dial #1/#2
fixes come later, once this produces real data).

## Goal

Record, for EVERY playback attempt, a definitive outcome — success or exactly
one failure cause — split by flow × item_type × cause, and emit it over both
sinks the series requires:

- OTLP → `observe.spencerharmon.com` (via the existing `Phantom.Flows` meter
  + `PhantomMetricsExporter`, endpoint from config — NEVER baked).
- Prometheus → Mimir (`mimir.spencerharmon.com`) via the P8 Pushgateway
  emitter precedent (`scripts/phantom-loadtime-push.sh`), surfaced on the
  operator Grafana dashboard ALONGSIDE the P8 load-time flows.

## Labels (the metric contract)

`phantom_playback_outcome_total{flow,item_type,cause}` (counter):

- `flow` ∈ { `materialise_then_play`, `play_already_materialised` }
- `item_type` ∈ { `movie`, `episode` }
- `cause` ∈ { `success`, `availability_abstain`, `no_candidate`,
  `magnet_dead_stale`, `gostream_register_fail`, `gostream_cannot_fetch`,
  `first_byte_timeout`, `plugin_host_error` }

The playback error rate the series ratchets is then
`sum(cause!="success") / sum(all causes)`, sliceable by flow/item_type/cause.

## Where to instrument (the real playback path)

- `PhantomSourceManager.GetSourcesAsync` / `MaterialiseCandidateAsync` — record
  `availability_abstain` (oracle abstained / no definitive available verdict)
  and `no_candidate` (no surviving `source_candidates`, or all `invalid`).
- `Materialiser` — record `magnet_dead_stale` (candidate resolved but nothing
  fetchable) and route hard candidate-validation failures
  (`MarkCandidateFailedAsync`) to the right cause.
- The gostream register/first-byte handoff (`GostreamClient` +
  `PhantomMaterialisingMediaSourceProvider`) — record
  `gostream_register_fail`, `gostream_cannot_fetch`, `first_byte_timeout`, and
  `success` on a completed first-byte handoff. `plugin_host_error` is the
  catch-all for any other host-path exception.

Add a single `PhantomFlowMetrics.RecordPlaybackOutcome(flow, itemType, cause)`
helper on the existing OTLP-native `Phantom.Flows` meter (movie AND episode
parity — every call site tags item_type). Keep hosts out of tracked code.

## Rig + emitter

- Extend `tools/rig-scenarios/47-loadtime-flows.sh`: for the `materialise`,
  `get_sources`, and `play_materialised` flows, additionally emit
  `phantom_playback_outcome_total{flow,item_type,cause}` records (a success
  record carries `cause="success"`; a failed flow emits the definitive cause,
  never a silently-dropped failure), preserving the existing dry-run synthetic
  fixture so the in-repo harness runs offline.
- Reuse the P8 Pushgateway push path (`scripts/phantom-loadtime-push.sh`
  precedent) to mirror the exposition into Mimir — pass-through of the label
  set, `honor_labels` documented (not baked), host from config/Secret.

## Definition of done (Check)

`Check:` = `./scripts/tests/phantom-playback-outcome.test.sh` — an in-repo
`script-test` harness (matches the `CHECKS.md` `script-test` framework) that
drives `47-loadtime-flows.sh` in dry-run and asserts the
`phantom_playback_outcome_total` record contract: every flow × item_type emits
exactly one definitive `cause`, movie AND episode parity, a failed flow emits a
non-`success` cause (never dropped), and the label set matches the contract
above. It FAILS before this task's instrumentation exists and PASSES after.
The `.test.sh` runner is committed executable (sandbox denies `bash` as a check
word; exec'd directly by shebang path).

## Non-goals

- No change to playback BEHAVIOUR (candidate selection, probe cadence, browse
  visibility) — that is dial #1 (availability-probe success) / dial #2
  (browse prune), enqueued by later series passes once this metric produces
  the numbers that justify one over the other.
