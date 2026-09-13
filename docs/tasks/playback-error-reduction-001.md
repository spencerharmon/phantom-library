# Playback-error-reduction series (ROI Priority 12) — shared series doc

A self-perpetuating DAILY loop that MEASURES the phantom playback error rate,
RANKS its dominant failure cause, and ENQUEUES exactly one concrete fix per
day for the biggest win. Each daily pass is purely ANALYTICAL
(DIAGNOSE-AND-ENQUEUE): it never implements the fix itself — it files the fix
as its own `TODO` task (with a design doc + a `CHECKS.md`-matching `Check:`)
and appends the next day's successor held on a `not_before ~= +24h`.

This mirrors the P8 load-time ratchet's structure (measure → rank → enqueue
one improvement → re-arm tomorrow), but ratchets the PLAYBACK ERROR RATE
rather than load-time latency.

## The metric this series ratchets

`playback_error_rate = playback_errors / playback_attempts`, broken down by:

- **flow**: `materialise_then_play` (cold — probe/materialise then start) vs.
  `play_already_materialised` (warm — the gostream file already exists).
- **item_type**: `movie` vs. `episode` (movie/TV parity is required — a fix
  that helps only movies is half a fix).
- **cause/stage** (the definitive outcome of a failed attempt):
  - `availability_abstain` — the availability oracle abstained / never
    reached a definitive `available` verdict (no probe result).
  - `no_candidate` — probe said available but no source candidate survived
    to try (`source_candidates` empty or all `invalid`).
  - `magnet_dead_stale` — a candidate magnet resolved but the swarm/peers
    were dead or stale (nothing fetchable).
  - `gostream_register_fail` — the gostream register handoff failed
    (`GostreamClient` register call errored).
  - `gostream_cannot_fetch` — gostream registered but could not fetch the
    stream body.
  - `first_byte_timeout` — registered + fetching, but first byte never
    arrived inside the handoff deadline.
  - `plugin_host_error` — any other plugin/Jellyfin-host error on the
    playback path.

## Where the measurement lives

- **Rig substrate**: reuse the P8 rig `tools/rig-scenarios/47-loadtime-flows.sh`
  `materialise` and `get_sources` (+ `play_materialised`) flows as the baseline
  — they already drive the real cold/warm playback path against the rig
  (`:18096`, never prod). This series ADDS the outcome/cause COUNTERS on top;
  it does not re-time latency (that is P8's job).
- **Plugin instrumentation**: the real playback path
  (`PhantomSourceManager` GetSources/Materialise, `Materialiser`, the gostream
  register/first-byte handoff in `GostreamClient`) must record a DEFINITIVE
  outcome for every playback attempt — success or one of the causes above.
  The existing `PhantomFlowMetrics` (`Meter "Phantom.Flows"`, OTLP-native via
  `PhantomMetricsExporter`) and the pull-based `PhantomMetrics` counters are
  the two existing sinks to extend — an OTLP outcome counter for
  `observe.spencerharmon.com`, mirrored as a Prometheus metric into Mimir
  (`mimir.spencerharmon.com`) via the P8 Pushgateway emitter path
  (`scripts/phantom-loadtime-push.sh` precedent).
- **Sink**: OTLP → `observe.spencerharmon.com` AND Prometheus → Mimir
  (`mimir.spencerharmon.com`), surfaced on the operator Grafana dashboard
  ALONGSIDE the P8 load-time flows. Hosts are NEVER baked into tracked code
  (infra-identifier rule) — supplied at runtime from config/secrets, exactly
  as P8 does.

## The two primary dials (where the wins are)

1. **Raise the availability-probe success rate** — reconcile the P6 Torrentio
   oracle with the Prowlarr high-confidence magnet set at materialise time,
   re-probe on TTL, cache negative results with bounded backoff, fix
   cold-materialise root causes. Targets `availability_abstain` +
   `no_candidate` (the cold-materialise flow's dominant failures).
2. **Prune unlikely-playable items from default browse** — reuse P10
   prune-non-playable + the P6 search-vs-list split (an item stays
   searchable/badged but leaves default browse), so a user is not offered an
   item that will fail to play. Reduces the DENOMINATOR of doomed attempts.

## Discipline

- **Baseline BEFORE optimising** (P5 discipline): each pass measures the
  CURRENT rate first and records it in the analysis doc, so the next day's
  pass can prove movement.
- **Diagnose-and-enqueue ONLY**: the daily pass never implements the fix. It
  writes the analysis doc, appends ONE fix task for the biggest win, and
  appends the successor for tomorrow.

## Per-pass artifacts

- `docs/tasks/playback-error-reduction-<NNN>-analysis.md` — that day's
  baseline + cause ranking + the chosen fix.
- one appended `TODO` fix task (with its own design doc + a
  `CHECKS.md`-matching `Check:`).
- the successor `playback-error-reduction-<NNN+1>` held on `not_before ~= +24h`.
