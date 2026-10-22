# playback-error-reduction-013 — day-13 analysis (DIAGNOSE-AND-ENQUEUE)

## 1. Baseline (Mimir, 2026-10-22T04:05:57Z)
- Instant `phantom_playback_outcome_total{job="jellyfin-phantom-library"}` → `[]` (unchanged from day 12).
- `max_over_time(...[30d])` → same 3 historical rig samples (instance `4859600b…`):
  episode/materialise_then_play/magnet_dead_stale = 2, movie/materialise_then_play/success = 1.
- `count by (__name__)` for the job: flow_duration (bucket/count/sum), flow_items, target_info present
  → job export is alive; only the outcome counter is absent.
- **No organic traffic yet** observed in the counters (absent series cannot prove zero traffic).

## 2. Did playback-outcome-zero-seed-001 land?
In source: YES — `SeedPlaybackOutcomes()` on origin/main (`PhantomFlowMetrics.cs:199`, called from
`Plugin.cs:34` and post-subscription in `PhantomMetricsExporter.cs:106`; merged ~2026-10-21T07:33Z).
Deployed: NO. Running Jellyfin pods (`kubectl -n phantom-library get pods`):
`phantom-library-blue-…` started 2026-09-13 (image `jellyfin-phantom@sha256:2cb8cb0a…`),
`phantom-library-green-0` started 2026-09-22 (`@sha256:610b605f…`). Both predate the seed merge by
~1 month, so the instant query staying `[]` is explained by deployment lag, not a seeding defect.

## 3. Ranking (code-level; no statistical volume)
1. **Seed not deployed** — measurement still blind; blocks every rate comparison. Enabling fix.
2. Episode cold-flow `magnet_dead_stale` — only observed failure bucket (2 rig attempts); source
   fixes from days 10/11 equally undeployed, efficacy unknown.
3. Everything else: no samples.

## 4. Enqueued fix (ONE)
`playback-outcome-zero-seed-deploy-001` — publish a jellyfin-phantom image from current main and
roll it to the live color (flux digest bump, cross-repo prerequisite filed by the implementer if
absent), then prove 32 zero-seeded series in Mimir. Design: `playback-outcome-zero-seed-deploy-001.md`.
Check: endpoint (`curl` Mimir + `jq`), matching CHECKS.md `endpoint`.

## 5. Successor
`playback-error-reduction-014` [TODO], not_before ≈ +24h.
