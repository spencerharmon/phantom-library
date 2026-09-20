# playback-error-reduction-008 — analysis (day 8 baseline + rank + enqueue)

Series: see `docs/tasks/playback-error-reduction-001.md` (read FIRST), then the
day-7 analysis `docs/tasks/playback-error-reduction-007-analysis.md`. Eighth
daily analytical pass. Same DIAGNOSE-AND-ENQUEUE contract as days 1-7: BASELINE
FIRST (P5 discipline), RANK the dominant remaining failure cause, ENQUEUE ONE
concrete fix. Implements nothing itself.

## What has landed since day 7

- Dial #1 (`availability-probe-reconcile-001`), dial #2
  (`browse-prune-dead-swarm-001`), the day-4 follow-up
  (`availability-stale-candidate-reprobe-001`), the day-6 gostream-handoff fix
  (`gostream-fuse-wait-eager-reregister-001`), and the day-7 materialise-wait
  fix (`materialise-inflight-wait-eager-reclaim-001`) are all unchanged,
  `DONE`.
- Day-5 follow-up `availability-stale-candidate-reprobe-verify-001` — still
  `NEEDS-HUMAN` (`category=external-permission`). Checked: no new comment/
  commit in `PLAN.md` since day 7 shows the operator resolved the rig-seed
  refresh. Still a correct, narrow, already-actionable human gate — not
  re-litigated.

## Has "deploy the instrumentation to prod" ever been filed? — checked, and superseded by a bigger finding

Day 7 flagged this as unfiled after 7 days. This pass confirmed: no task in
`phantom-library`, `flux`, or `actions` PLAN.md is named for a PROD blue/green
promotion of the instrumentation, and — more importantly — `flux` ROI
Priority 1 ("Freeze the ACTIVE blue/green color against honeybee edits") shows
the operator has since implemented an **active-color-frozen mutation guard
directly** (`flux:phantom-library-green-scaled-to-zero`'s own note: "a targeted
`kubectl scale` ... should not trip the active-color-frozen mutation guard
which only blocks HelmRelease/chart edits to the active color"). So a
honeybee-driven PROD flip is now explicitly guarded against by design — filing
"flip prod" as a swarm-buildable task would be wrong; it is now a genuine
`external-permission` gate if it were ever needed. **It turns out not to be
needed**: see below.

## Baseline (P5 discipline) — FIRST GENUINE LIVE READ, and it changes everything

Every prior pass (days 1-7) could not get real cause-labelled data because
`phantom_playback_outcome_total` is only emitted OTLP-side
(`observe.spencerharmon.com`), and re-baselined from the coarse P8 rig
`errors_total`/`phantom_availability_probes_total` signals instead. This pass
tried the same in-cluster Mimir query technique the sibling `ttfb-reduction`
series already uses successfully
(`kubectl -n monitoring exec deploy/grafana -c grafana -- curl -sf
http://mimir.monitoring.svc:8080/prometheus/api/v1/query --data-urlencode
'query=phantom_playback_outcome_total'`) — and it WORKS. Live result:

```
phantom_playback_outcome_total{cause="gostream_register_fail",flow="materialise_then_play",item_type="episode",job="phantom-loadtime"} 1
phantom_playback_outcome_total{cause="gostream_register_fail",flow="materialise_then_play",item_type="movie",job="phantom-loadtime"}   1
```

No `success` samples, no `play_already_materialised` samples exist yet. This
comes from the `phantom-library-loadtime-daily` CronJob's one run today
(`phantom-library-loadtime-daily-29831717`, scheduled `17 6 * * *`, overall job
status **`Failed`** — its pod already GC'd, so its exit detail is unavailable
from this worktree, but the pushed metric batch clearly landed before the job
failed downstream).

**This reading is real, but it is NOT what it looks like — and reading it as
"100% `gostream_register_fail`" would be a mistake.** Tracing
`tools/rig-scenarios/47-loadtime-flows.sh`'s LIVE branch (the thing that
actually produced this exposition) shows it NEVER calls into the plugin's real
per-attempt classification (`PhantomMaterialisingMediaSourceProvider
.ClassifyMaterialiseFailure`, `Diagnostics/PhantomFlowMetrics
.RecordPlaybackOutcome`). It only inspects its OWN curl exit code for the
`materialise` HTTP call and, on ANY failure there, hard-codes the literal
string `gostream_register_fail` — never `availability_abstain`,
`no_candidate`, or `magnet_dead_stale`, and never the real
`gostream_register_fail` distinguished from those. The REAL classification
(fine-grained, per-attempt, movie+episode) exists and fires on every real
attempt, but is recorded ONLY on the OTLP `Meter` ("Phantom.Flows"), exported
to `observe.spencerharmon.com` — which this series still has no query access
to. **The number this and every future pass would read from Mimir today is a
rig artifact, not ground truth about which cause dominates.** This is a
distinct, more fundamental gap than "instrumentation isn't deployed to
prod" (day 7's framing) — the instrumentation IS deployed (to dev, via the
already-`DONE` `phantom-library-cd-autodeploy-dev` auto-pipeline) and IS
running on every real attempt; the gap is that ONLY the OTLP sink gets the
real signal, while the ONLY sink this series can currently query (Mimir, via
the rig's own separate/synthetic push) gets a crude guess.

### Can this pass still rank a dominant cause? — No, honestly

With the ONE queryable signal proven unreliable as a cause discriminator (it
structurally cannot distinguish 4 of the 7 possible causes from one another),
this pass does NOT rank a "dominant cause" from it — doing so would be
re-committing exactly the mistake this pass just found. The coarse P8 rig
`errors_total`/`phantom_availability_probes_total` fallback (days 1-7's
substrate) still shows the same shape as day 7 (cold `materialise_then_play`
flow dominant, no new signal since day 6-7's fixes at this coarse resolution)
— consistent with, but not more informative than, prior days.

## The one fix enqueued (biggest win): close the measurement-fidelity gap itself

**`playback-outcome-real-cause-dual-emit-001`** — dual-emit
`phantom_playback_outcome_total{flow,item_type,cause}` from the SAME call site
(`PhantomFlowMetrics.RecordPlaybackOutcome`) onto a Prometheus-net counter
(mirroring the existing pull-based `PhantomMetrics` class's pattern) alongside
the existing OTLP `Meter` counter (unchanged), so the REAL per-attempt
classification — not the rig's blanket guess — becomes scrapeable into Mimir
the same way `phantom_availability_probes_total` already is; then retire (or
clearly re-name) the rig's own synthetic LIVE-branch classification in
`47-loadtime-flows.sh` so it can never again be mistaken for ground truth.
Movie AND episode parity required. Design doc:
`docs/tasks/playback-outcome-real-cause-dual-emit-001.md`. `Check:`
`dotnet test --filter FullyQualifiedName~PhantomFlowMetricsTests` (matches the
`dotnet-test` CHECKS.md framework).

This is the single biggest win available: every one of the six behavioural
dials/fixes landed by days 2-7 is currently UNVERIFIABLE against real
cause-labelled data, and every future daily pass would otherwise keep reading
(and potentially mis-ranking against) the same rig artifact this pass caught.
Fixing the measurement itself, once, unblocks trustworthy ranking for every
subsequent day — a strictly bigger, more durable win than any single further
dial tuning or handoff-path stage this pass could otherwise have picked.

## Successor

`playback-error-reduction-009` appended `[TODO]`, held on `not_before ~=
+24h`. It should check whether `playback-outcome-real-cause-dual-emit-001`
has landed and whether the dual-emitted counter shows real samples in Mimir
(`kubectl -n monitoring exec deploy/grafana -c grafana -- curl ...
phantom_playback_outcome_total`, filtering out `job="phantom-loadtime"` rig
samples if the rename lands, or comparing against them if not) before
attempting the first REAL cause ranking this series will have ever done.
