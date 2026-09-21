# playback-outcome-pipeline-verify-002 — design

ROI Priority 12 fix, enqueued by `playback-error-reduction-009` (day 9).

## Problem

`playback-outcome-real-cause-dual-emit-001` (day 8's fix) landed correctly:
`PhantomFlowMetrics.RecordPlaybackOutcome` now dual-emits the real
per-attempt `phantom_playback_outcome_total{flow,item_type,cause}`
classification onto a Prometheus-net counter (alongside the pre-existing
OTLP `Meter` counter), and `tools/rig-scenarios/47-loadtime-flows.sh`'s LIVE
branch was retired off that metric name onto
`phantom_loadtime_rig_outcome_total` so the two series can never be
conflated again.

But as of day 9, this pipeline has never been exercised end-to-end:

- No real playback attempt has hit `RecordPlaybackOutcome` since the plugin
  deployed with the dual-emit change (the plugin's own scraped job,
  `job="jellyfin-phantom-library"`, has never emitted
  `phantom_playback_outcome_total` — confirmed via a live Mimir
  `__name__` label-value query).
- The `phantom-library-loadtime-daily` CronJob has not run since the rename
  merged, so the rig's LIVE branch has not yet pushed under either the new
  name (`phantom_loadtime_rig_outcome_total`) or exercised the Pushgateway's
  PUT-replace-the-group semantics that should retire the OLD, now-stale
  `phantom_playback_outcome_total{job="phantom-loadtime"}` sample still
  sitting in Mimir from before the rename.

Until BOTH of these fire at least once, every future daily
playback-error-reduction pass either sees nothing under
`job="jellyfin-phantom-library"` (and must keep re-baselining from coarse
signals) or risks misreading the stale `job="phantom-loadtime"` group if it
forgets day 8/9's finding. This task closes that verification gap.

## Fix

1. **Trigger a fresh rig run** to exercise the renamed LIVE branch and the
   Pushgateway replace semantics:
   `kubectl create job --from=cronjob/phantom-library-loadtime-daily
   phantom-library-loadtime-daily-verify-<ts> -n <ns>` (namespace per
   `LOCALS.md`/`INFRASTRUCTURE.md`). Confirm the job completes (or at least
   reaches the push step — `loadtime-rig-image-missing-dotnet`'s guard-step
   failure is a KNOWN, separate, already-filed issue and does not block this
   verification since the push happens before the guard step).
2. **Confirm the stale sample is gone / renamed correctly**: re-query
   `phantom_playback_outcome_total{job="phantom-loadtime"}` — it should now
   be either absent or carry a genuinely fresh timestamp with a real cause
   from THIS run (never re-introduce the old-name emission; if it still
   appears, that is a regression in the rename and must be fixed here, not
   deferred).
3. **Confirm `phantom_loadtime_rig_outcome_total{job="phantom-loadtime"}`
   now has fresh samples** from the renamed LIVE branch.
4. **Drive at least one real plugin playback attempt** via the existing rig
   scenarios (`tools/rig-scenarios/35-channel-e2e-playback.sh` for movie,
   `36-channel-episode-e2e-playback.sh` for episode — movie/TV parity
   required) against the dev Jellyfin instance the plugin's own metrics
   endpoint is scraped from (`job="jellyfin-phantom-library"`), to confirm
   `PhantomFlowMetrics.RecordPlaybackOutcome` actually fires on a real
   attempt and the Prometheus-net counter is scraped into Mimir.
5. **Confirm via live Mimir query** that
   `phantom_playback_outcome_total{job="jellyfin-phantom-library"}` now
   returns at least one real sample with a real `cause` label (whatever it
   is — success or a genuine failure cause; the point is presence, not a
   specific value).
6. Record the live-effect confirmation (query + result) in the change doc.
   No code change is expected unless step 2 finds a regression in the
   rename — in that case, fix `47-loadtime-flows.sh` so the old name is
   never re-emitted.

## Non-goals

- No new dial/behavioural fix — this is purely a measurement-pipeline
  verification task, matching the series' "diagnose-and-enqueue" discipline
  (this fix task itself is the biggest win day 9 identified; it may in turn
  enqueue nothing further — day 10 resumes the daily cadence).
- Does not change `PhantomFlowMetrics`, `RecordPlaybackOutcome`, or any
  playback/dial logic.

## Check

A live endpoint assertion (not a source grep), matching the `endpoint`
`CHECKS.md` framework:

```
kubectl -n monitoring exec deploy/grafana -c grafana -- curl -sf \
  http://mimir.monitoring.svc:8080/prometheus/api/v1/query \
  --data-urlencode 'query=phantom_playback_outcome_total{job="jellyfin-phantom-library"}' \
  | grep -q '"result":\[{'
```

Passes only once a real per-attempt sample has been observed under the real
job label — the actual definition of done for this verification task.
