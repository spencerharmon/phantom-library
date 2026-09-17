# ttfb-reduction-006 — daily TTFB-reduction analysis

ROI Priority 9, successor of `ttfb-reduction-005`. DIAGNOSE-AND-ENQUEUE ONLY:
this is the SIXTH consecutive daily analytical pass. `check=none` — no fix code
is shipped in this pass; the only submodule commit is this analysis doc plus the
two enqueued task design docs.

## Data source

Queried Grafana Mimir's `phantom_loadtime_seconds` / `phantom_loadtime_errors_total`
/ `phantom_loadtime_runs_total` series directly against the real deployed color
(`color=green`), exactly as `ttfb-reduction-002` through `-005` did:

```
kubectl -n monitoring port-forward svc/mimir 18082:8080 &
NOW=$(date +%s)
curl -s 'http://localhost:18082/prometheus/api/v1/query_range' \
    --data-urlencode 'query=phantom_loadtime_seconds{flow=~"materialise|get_sources"}' \
    --data-urlencode "start=$((NOW-1209600))" --data-urlencode "end=$NOW" \
    --data-urlencode 'step=300'
```

An instant query returns EMPTY (all series older than Mimir's ~5m instant-query
staleness window), so a 14-day range query recovers the last-pushed sample.

## Live measurement (Mimir, real deployed green color, 2026-09-17T18:0xZ)

Last-pushed sample bucket timestamp `1789440830`, values:

```
flow=materialise   item_type=movie    duration_s=4.169275   errors=1  runs=1
flow=materialise   item_type=episode  duration_s=0.015307   errors=1  runs=1
flow=get_sources   item_type=movie    duration_s=0.315222   errors=0  runs=1
flow=get_sources   item_type=episode  duration_s=0.181446   errors=1  runs=1
```

Distinct values across the entire 14-day window (the last real change to any
series was at bucket `1789204430`; every bucket after it is a fill-forward of
the same last-pushed value):
- movie-materialise:   `{4.169275, 64.720732}`
- episode-materialise: `{0.011467, 0.015307}`
- movie-get_sources:   `{0.046751, 0.315222}`
- episode-get_sources: `{0.011981, 0.181446}`

## Finding — the sample is STILL byte-for-byte unchanged (SIXTH read)

`movie-materialise = 4.169275s` is IDENTICAL to the value `ttfb-reduction-002`,
`-003`, `-004`, and `-005` all read; the whole series has not advanced. This is
the SIXTH consecutive daily analytical pass to read the exact same stale sample.
Per this task's explicit fallback — "if still stale, note that explicitly and
check on gitea-mirror-provision-wiring's progress rather than re-deriving the
same ranking a sixth time" — this pass does NOT re-derive the stage ranking. The
ranking below is carried forward from `-003`/`-004`/`-005` UNCHANGED because the
underlying data is unchanged.

### Ranking (carried forward — data unchanged)

1. **movie-materialise = 4.169275s** — by far the dominant stage; two orders of
   magnitude above every other flow. The whole cold-materialise wait
   (availability → candidate/source probe → magnet select → gostream register →
   first byte).
2. movie-get_sources = 0.315222s
3. episode-get_sources = 0.181446s
4. episode-materialise = 0.015307s (effectively warm/instant)

### Cold-materialise failure rate — 100%

`phantom_loadtime_errors_total{flow="materialise"} = 1` with `runs_total = 1`
for BOTH movie and episode → a 100% cold-materialise failure rate in the recorded
run, identical to `-002` through `-005`. The 4.169275s movie figure is a FAILED
cold materialise, not a successful first byte.

## Why still stale — the mirror route remains operator-gated

Root cause is unchanged and confirmed: the ONLY trigger for the measurement rig,
`.gitea/workflows/phantom-loadtime-daily.yaml` (`cron: '17 6 * * *'`), is a
**Gitea Actions** workflow, and no Gitea mirror of `phantom-library` exists yet.
`gitea-mirror-provision-wiring` — the operator-only remainder of
`ttfb-daily-rig-gitea-mirror-wiring` — is still `[NEEDS-HUMAN]`
(`category=external-permission`; PLAN.md), correctly scoped: creating the mirror
repo, enabling Actions, and registering the runner needs Gitea-instance admin
access the swarm does not hold. Confirmed still blocked this pass; no further
swarm action is available on that task itself.

## Progress since `-005` (what DID change)

- `ttfb-fast-indexer-early-return-enable-default` is now `[DONE]` — the
  `FastIndexerEarlyReturnEnabled` default-flip that `-005` enqueued has landed in
  source. Its LIVE impact on `movie-materialise` remains UNMEASURABLE for the same
  reason (no fresh sample), and must be confirmed actually enabled + rolled out in
  the deployed config before any effect is expected.
- `ttfb-magnet-cache-drain-worker` remains `[DONE]`; its cold-materialise impact
  is likewise unmeasurable pending fresh data.

The three open questions every pass 002–006 could not answer (drain-worker impact,
early-return impact, whether movie-materialise is still dominant post-fixes) ALL
remain blocked on a single thing: a fresh sample.

## Enqueued fix (the biggest remaining, swarm-buildable win)

`ttfb-daily-rig-incluster-cronjob` (ROI P9, weight 9): add a Kubernetes CronJob
(Helm template, values-gated) that fires the already-shipped
`tools/ci/loadtime-daily-run.sh` in-cluster on a daily schedule — mirroring the
existing `jellyfin-metadata-reaper-cronjob.yaml` pattern — so the measurement
cadence no longer depends on the operator-gated Gitea mirror at all. This is the
biggest remaining win because a fresh sample gates ALL further TTFB analysis, and
unlike waiting a fourth pass on the mirror it is entirely within swarm authority
(a buildable dependency is never a standing human gate). Design doc:
`docs/tasks/ttfb-daily-rig-incluster-cronjob.md`.
`Check: podman run --rm --entrypoint "" -v "$PWD":/chart:ro -w /chart docker.io/alpine/helm:latest helm template deploy/helm/phantom-library --set loadtimeDaily.enabled=true`
(matches the `helm-lint` framework stub in `CHECKS.md` — a chart-only change with
no plugin code).

## Successor

`ttfb-reduction-007 [TODO]` appended, held `not_before` ~= +24h (daily cadence).
It must first check whether EITHER the new in-cluster CronJob OR the Gitea mirror
has produced a genuinely fresh sample (bucket beyond `1789440830` / value ≠
`4.169275s`) before finally attempting the drain-worker / early-return impact
assessment. Design doc: `docs/tasks/ttfb-reduction-007.md`.
