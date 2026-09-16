# playback-error-reduction-004 — analysis (day 4 baseline + rank + enqueue)

Series: see `docs/tasks/playback-error-reduction-001.md` (read FIRST), then the
day-3 analysis `docs/tasks/playback-error-reduction-003-analysis.md`. This is
the FOURTH daily analytical pass. Same DIAGNOSE-AND-ENQUEUE contract as days
1-3: BASELINE the current playback error rate (P5 discipline), RANK the
dominant remaining failure cause, and ENQUEUE ONE concrete fix for the biggest
win. It implements NOTHING itself (diagnose-and-enqueue only).

## Baseline (P5 discipline: measure BEFORE optimising)

### What has landed since day 3

Both primary dials are now `DONE`:

- **Dial #1** — `availability-probe-reconcile-001`
  (`commits=af58d0925fb526d07e2e3ed15494d1e41273cf19,cf2a5e88b15afe3186e5d66e1d091598dc569ed2,81f8e3fe476fb4b8e98cad1ee422e6c40c88d1f7`),
  landed day 2/confirmed day 3.
- **Dial #2** — `browse-prune-dead-swarm-001`
  (`commits=698f9e8bfecb59b6302f8c0adff94f4aeadda51b`), enqueued day 3, now
  `DONE`. It extended `PhantomDb.ListVisibleMovieRowsAsync` /
  `ListVisibleSeriesRowsAsync` to exclude an `'available'` item from default
  browse when every `source_candidates` row is either `'invalid'` or a
  confirmed dead-swarm transient, for both `item_type=movie` and
  `item_type=episode`.
- The measurement dial `playback-outcome-instrumentation-001` (day 1) remains
  `DONE`.

### Can day 4 read a real cause-labelled number yet? — Honest gap

**Still not yet.** Same substrate gap as days 2-3: the
`phantom_playback_outcome_total{flow,item_type,cause}` metric CONTRACT is
`DONE` and regression-tested (`./scripts/tests/phantom-playback-outcome.test.sh`),
but:

- This analytical pass runs from a beehive submodule worktree with no live
  `observe.spencerharmon.com` / `mimir.spencerharmon.com` query access, and no
  PLAN task in this submodule records the instrumentation as deployed-and-a-
  day-old against real prod Jellyfin traffic (the `DONE` stamp on
  `playback-outcome-instrumentation-001` certifies the metric *contract*, not
  a day of accumulated prod data — deploys of this plugin flow through the
  linked `flux:phantom-library-bluegreen-deploy` GitOps pipeline, which this
  analytical pass cannot query for a running-time flag).
- The rig's `phantom_playback_outcome_total` output remains a DETERMINISTIC
  SYNTHETIC FIXTURE (every flow × item_type × cause emitted exactly once) —
  counting it would fabricate a baseline, which P5 forbids.

Per the task's explicit fallback clause, this pass re-baselines from the
coarse P8 rig `errors_total` + `phantom_availability_probes_total` signals, as
days 1-3 did, and records the gap again. The first pass that can read a real
per-cause number is still owed to the first daily pass **after** deploy +
a day of live data — noted here so day 5 checks first, before re-deriving this
same conclusion.

### Did dial #1 and dial #2 move their buckets? — Honest read

Both dials are `DONE` (code merged) but neither's LIVE bucket movement is
provable from this worktree — same deployed-prod-series gap as above. What IS
provable offline, by construction:

- **Dial #1** (`availability-probe-reconcile-001`): its `script-test` Check
  (`./scripts/tests/availability-probe-reconcile.test.sh`) is green for both
  item_types — the Torrentio→Prowlarr reconcile, TTL re-probe, and bounded
  negative-backoff paths are exercised. Additionally, tracing the real
  on-demand playback path confirmed dial #1's reconcile fallback lives in
  `AvailabilityProbeWorker` (the background sweep, which uses the cheaper
  Torrentio-only `ProbeAvailabilityAsync` and only escalates to the full
  indexer fan-out on abstain) — the SYNCHRONOUS on-demand path
  (`PhantomSourceManager.GetRankedCandidatesAsync`) already always calls the
  full-fan-out `MagnetSelector.ProbeAsync` directly, so it was never subject
  to the abstain gap dial #1 fixed. No further reconcile gap found in the
  synchronous path.
- **Dial #2** (`browse-prune-dead-swarm-001`): its `script-test` Check
  (`./scripts/tests/browse-prune-dead-swarm.test.sh`) is green for both
  item_types — the prune/searchable/reappear behaviour is exercised offline.
- Live bucket movement for both remains a deployed-and-converged reading owed
  to the first daily pass with a real prod series (unchanged gap from day 3).

### Baseline value (honest, coarse — same substrate as days 1-3)

Unchanged in KIND (still no real cause-labelled prod series to read):

- **P8 rig coarse error rate** (`phantom_loadtime_errors_total /
  phantom_loadtime_runs_total`, per flow × item_type): the cold flow
  (`materialise_then_play`) remains the dominant error contributor; the warm
  flow succeeds once a gostream file exists. No cause/stage label on this
  coarse signal.
- **`phantom_availability_probes_total{type,outcome}`**: unchanged shape from
  day 3 — still shows cold-flow failures concentrating before a candidate is
  ever tried.

## Cause ranking (biggest win first)

With BOTH primary dials landed, day 4 re-examined the actual prune/candidate
SQL dial #2 shipped, rather than re-deriving the same coarse ranking a fourth
time, and found a concrete residual gap in the SAME query dial #2 touched:

1. **Stale-`'available'`-with-zero-cached-candidates — a residual doomed-
   visible gap in dial #2's own prune predicate.** `ListVisibleMovieRowsAsync`
   / `ListVisibleSeriesRowsAsync` treat an `availability_items` row with
   `status='available'` as visible-and-playable whenever **no**
   `source_candidates` rows exist for that key at all (`NOT EXISTS (...)`) —
   the same "not yet assessed" branch dial #2 correctly used to admit a
   brand-new item that has never been probed. But that identical branch also
   admits an item whose candidates ALL **expired out of the cache** (the
   `source_candidates` TTL — `MagnetCacheTtlHours` — elapsed and the rows were
   pruned/aged out) while the stale `availability_items.status` row still
   reads `'available'` from the last successful probe. Such an item is
   VISIBLE in default browse with **zero live candidates to try**, so the
   cold `materialise_then_play` attempt against it goes straight to
   `no_candidate` (or `availability_abstain` if the re-probe itself misses).
   This is functionally identical to dial #2's original dead-swarm gap (an
   `'available'`-status item with no viable candidate stays visible) but on
   the "expired/never-refreshed cache" side rather than the "all-invalid /
   dead-swarm" side dial #2 already closed. Distinguishing "genuinely
   unprobed" (new item, never had a candidate — correctly stays visible) from
   "candidates existed, then all expired without ever refreshing" (should NOT
   stay visible, or must trigger an eager re-probe) is the concrete remaining
   win: it directly shrinks the cold-flow `no_candidate` denominator, the
   SAME top-ranked bucket dial #1 and dial #2 both targeted.
2. **Residual `availability_abstain` + `no_candidate` after dial #1 (general)**
   — dial #1 attacks the on-demand and background-probe abstain path; the
   live residual after deploy is unmeasured (owed to the first real-series
   pass).
3. **gostream handoff stages** (`gostream_register_fail`,
   `gostream_cannot_fetch`, `first_byte_timeout`) — read in code this pass:
   `GostreamClient.AddAsync` already retries the register call once
   (`PostWithOneRetryAsync`, 1s backoff on a 5xx/429), and
   `PhantomMaterialisingMediaSourceProvider.OpenMediaSource` classifies
   `TimeoutException`→`first_byte_timeout` and `FileNotFoundException`→
   `gostream_cannot_fetch` with no retry at all on either. Still SECONDARY
   per the task's own gate ("gostream handoff stages once the deployed series
   shows they dominate") — no real series exists yet to show that, so this
   pass does NOT enqueue a gostream fix, consistent with days 1-3's
   discipline of not jumping ahead of the evidence.

## The one fix enqueued (biggest win)

**`availability-stale-candidate-reprobe-001`** — when `GetRankedCandidatesAsync`
(on-demand, already always probes) or the visible-browse prune query encounters
an `availability_items` row with `status='available'` but a stale/expired (or
fully absent, non-empty-history) `source_candidates` set, force an eager
re-probe (reusing dial #1's existing TTL re-probe / negative-backoff plumbing)
BEFORE trusting the row for browse visibility, instead of silently treating
"no live candidates" the same as "never assessed." Movie AND episode parity
required. Design doc: `docs/tasks/availability-stale-candidate-reprobe-001.md`.
`Check:` = `./scripts/tests/availability-stale-candidate-reprobe.test.sh` (a
`script-test`-framework harness matching `CHECKS.md`), asserting the
distinguish-and-reprobe behaviour offline against the synthetic fixture for
both item_types.

This directly shrinks the cold-flow `no_candidate` bucket — the SAME
top-ranked cause dial #1 and dial #2 both targeted — by closing the one
residual "stale-available-but-uncandidated" loophole their own prune/reconcile
logic left open.

## Successor

`playback-error-reduction-005` appended `[TODO]` held on `not_before ~= +24h`
for tomorrow's pass (daily cadence).
