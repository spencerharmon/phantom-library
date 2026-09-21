# playback-error-reduction-009 — analysis (day 9 baseline + rank + enqueue)

Series: see `docs/tasks/playback-error-reduction-001.md` (read FIRST), then the
day-8 analysis `docs/tasks/playback-error-reduction-008-analysis.md` (found
the series' Mimir-queryable `phantom_playback_outcome_total` sample was a RIG
ARTIFACT, not the real per-attempt classification, and enqueued
`playback-outcome-real-cause-dual-emit-001` to fix the measurement itself).
Ninth daily analytical pass. Same DIAGNOSE-AND-ENQUEUE contract as days 1-8:
BASELINE FIRST (P5 discipline), RANK the dominant remaining failure cause,
ENQUEUE ONE concrete fix. Implements nothing itself.

## What has landed since day 8

- `playback-outcome-real-cause-dual-emit-001` is `DONE`
  (`commits=2ab41444a5ab72950d6b480bd51cfa554512e98b`). Verified in the code
  (`src/Jellyfin.Plugin.PhantomLibrary/Diagnostics/PhantomFlowMetrics.cs`):
  `RecordPlaybackOutcome` now increments BOTH the OTLP `Meter` counter
  (`PlaybackOutcomes`, unchanged) AND a new Prometheus-net
  `Counter PlaybackOutcomesPrometheus` of the identical name
  `phantom_playback_outcome_total` with the identical `{flow,item_type,cause}`
  labels — the fix day 8 enqueued landed exactly as designed. It also
  retired the rig's own synthetic LIVE-branch classification off the real
  series' name: `tools/rig-scenarios/47-loadtime-flows.sh`'s LIVE branch now
  explicitly emits under `phantom_loadtime_rig_outcome_total` (a clearly
  distinct, clearly-labelled metric; its `HELP` text says "NOT
  phantom_playback_outcome_total"), leaving `phantom_playback_outcome_total`
  free for the real per-attempt signal only.
- All prior dials/fixes (dial #1 `availability-probe-reconcile-001`, dial #2
  `browse-prune-dead-swarm-001`, day-4 `availability-stale-candidate-reprobe-001`,
  day-6 `gostream-fuse-wait-eager-reregister-001`, day-7
  `materialise-inflight-wait-eager-reclaim-001`) remain unchanged, `DONE`.
- Day-5 follow-up `availability-stale-candidate-reprobe-verify-001` — still
  `NEEDS-HUMAN` (`category=external-permission`). Checked again: no new
  comment/commit in `PLAN.md` since day 8 shows the operator resolved the
  rig-seed refresh. Still a correct, narrow, already-actionable human gate.

## Baseline (P5 discipline) — the dual-emit code landed, but the pipeline has not yet produced a real sample

Live Mimir query, same technique as day 8
(`kubectl -n monitoring exec deploy/grafana -c grafana -- curl -sf
http://mimir.monitoring.svc:8080/prometheus/api/v1/query --data-urlencode
'query=phantom_playback_outcome_total'`):

```
phantom_playback_outcome_total{cause="gostream_register_fail",flow="materialise_then_play",item_type="episode",job="phantom-loadtime"} 1
phantom_playback_outcome_total{cause="gostream_register_fail",flow="materialise_then_play",item_type="movie",job="phantom-loadtime"}   1
```

This is the IDENTICAL sample day 8 read and flagged as a rig artifact —
**not a new data point.** Three checks confirm it is now doubly stale, not
fresh evidence of anything:

1. **No real samples exist under the real plugin's scrape job.** Querying the
   full metric-name label set (`.../api/v1/label/__name__/values`) shows
   `phantom_playback_outcome_total` exists ONLY under `job="phantom-loadtime"`
   (the rig's pushgateway group). The plugin's own scraped job
   (`job="jellyfin-phantom-library"`, confirmed live via other real counters
   — `phantom_flow_duration_ms_*`, `phantom_flow_items`) carries browse-flow
   metrics only; it has NEVER emitted `phantom_playback_outcome_total`,
   meaning no real playback attempt has hit `RecordPlaybackOutcome` since the
   dual-emit code deployed to dev.
2. **The rig's own LIVE branch no longer pushes under this name at all** —
   confirmed in the current `tools/rig-scenarios/47-loadtime-flows.sh`: the
   LIVE `emit_playback_outcome` call sites now pass
   `phantom_loadtime_rig_outcome_total` explicitly, so a fresh LIVE run would
   push metrics under the RENAMED name, not `phantom_playback_outcome_total`.
   Querying `phantom_loadtime_rig_outcome_total` directly returns an EMPTY
   result too.
3. **Conclusion: the `job="phantom-loadtime"` sample under the OLD name is a
   leftover from a push that predates the rename's merge.** The Pushgateway's
   PUT-replaces-the-whole-group contract (documented in
   `scripts/phantom-loadtime-push.sh`) means the very next real
   `phantom-library-loadtime-daily` CronJob run (`17 6 * * *`) will overwrite
   this group and the stale old-named sample will disappear — but that has
   not happened yet in this pass's window, and no genuine real-classification
   signal has appeared anywhere yet either.

**This pass therefore still cannot rank a dominant cause from real data** —
not because the fix from day 8 didn't land (it did, correctly), but because
no real playback attempt (dev traffic) and no fresh rig run has occurred
since it landed to actually exercise and surface the real per-attempt series.
Ranking against the stale `job="phantom-loadtime"` sample would repeat
exactly the day-8 mistake.

### Fallback coarse baseline — and a second, independent measurement-fidelity gap

Re-baselining from the P8 rig fallback used by days 1-7
(`errors_total` + `phantom_availability_probes_total`) surfaced a further
degradation: **`phantom_availability_probes_total` no longer exists in Mimir
at all** (absent from the full `__name__` label-value list) — it is not
merely zero, it has never been ingested in the queryable window. The
`errors_total`/`runs_total` family IS present, but under the RENAMED labels
`phantom_loadtime_errors_total` / `phantom_loadtime_runs_total` (the P8 rig's
own naming, unaffected by the playback-outcome rename). Read live:

```
flow=get_sources        item_type=movie/episode   1 error / 1 run  (100%)
flow=info_open          item_type=movie/episode   1 error / 1 run  (100%)
flow=materialise        item_type=movie/episode   1 error / 1 run  (100%)
flow=play_materialised  item_type=movie/episode   1 error / 1 run  (100%)
flow=list_load          item_type=movie/episode   0 error / 1 run  (0%)
flow=sort_change        item_type=movie/episode   0 error / 1 run  (0%)
```

Same shape as day 7/8's coarse read: the cold `materialise`/`get_sources` and
the immediately-dependent `info_open`/`play_materialised` flows still show a
100% failure rate at this coarse resolution. This is the SAME single stale
rig run day 8 also read (the CronJob has not run again since day 8), so this
is not new movement evidence either — it is confirmation the coarse signal is
unchanged, consistent with (not proof of) the six landed dials not yet having
a fresh coarse sample to show against.

### Can this pass rank a dominant cause? — No, honestly, for a different reason than day 8

Day 8 could not rank because the only queryable signal was a rig artifact.
Day 9 cannot rank because the now-CORRECT real signal has simply never fired
yet — the measurement fix is code-complete but functionally unverified end to
end (dual-emit → Prometheus scrape of `job=jellyfin-phantom-library` →
Mimir). Re-deriving a ranking from the stale `job=phantom-loadtime` sample
would repeat day 8's exact mistake; this pass declines to do so.

## The one fix enqueued (biggest win): finish verifying the measurement pipeline end-to-end

**`playback-outcome-pipeline-verify-002`** — trigger a fresh
`phantom-library-loadtime-daily` CronJob run (`kubectl create job
--from=cronjob/phantom-library-loadtime-daily`, the same technique used by
`loadtime-rig-image-missing-dotnet`'s verification) to exercise the renamed
rig LIVE branch (confirming `phantom_loadtime_rig_outcome_total` lands and
the OLD `phantom_playback_outcome_total{job="phantom-loadtime"}` group is
replaced/cleared by the Pushgateway's PUT-replace semantics, per
`scripts/phantom-loadtime-push.sh`'s documented contract) AND drive at least
one REAL plugin playback attempt (dev Jellyfin instance, via the existing rig
scenarios `tools/rig-scenarios/35-channel-e2e-playback.sh` /
`36-channel-episode-e2e-playback.sh`, movie+episode parity required) to
confirm `phantom_playback_outcome_total{job="jellyfin-phantom-library"}`
actually appears in Mimir with a real `cause` label for the first time. This
closes the loop day 8 opened (dual-emit code landed) but did not — could not,
in a single pass — verify end-to-end, because verification requires a
CronJob tick / real playback attempt that had not yet happened. Without this,
every future daily pass risks either reading nothing or misreading the stale
group, exactly the failure this series exists to prevent. Design doc:
`docs/tasks/playback-outcome-pipeline-verify-002.md`. `Check:` a live Mimir
query asserting the real job's series exists:
`kubectl -n monitoring exec deploy/grafana -c grafana -- curl -sf
http://mimir.monitoring.svc:8080/prometheus/api/v1/query --data-urlencode
'query=phantom_playback_outcome_total{job="jellyfin-phantom-library"}' | grep
-q '"result":\[{'` (matches the `endpoint` CHECKS.md framework — a live curl
assertion, not a source grep).

This is the biggest available win: every dial's true effect remains
unverifiable until the real signal is confirmed flowing at least once — a
strictly bigger, more durable unlock for day 10's first genuine ranking than
any further coarse-signal dial-tuning guess would be.

## Successor

`playback-error-reduction-010` appended `[TODO]`, held on `not_before ~=
+24h`. It should check whether `playback-outcome-pipeline-verify-002` has
landed and whether `phantom_playback_outcome_total{job="jellyfin-phantom-library"}`
now shows real samples in Mimir before attempting the series' first REAL
cause ranking. If the pipeline is verified but still shows only sparse/zero
real attempts (dev traffic is naturally low), that is a legitimate basis to
note "insufficient real volume yet" rather than to force a premature
ranking — do not rank from a single or zero real sample.
