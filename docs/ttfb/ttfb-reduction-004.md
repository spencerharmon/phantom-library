# ttfb-reduction-004 — daily TTFB-reduction analysis

ROI Priority 9, successor of `ttfb-reduction-003`. DIAGNOSE-AND-ENQUEUE
ONLY: measures materialise/get_sources TTFB against the live daily-rig
Mimir sample, ranks the dominant stage, records the cold-materialise
failure rate, files exactly one concrete fix task plus the
`ttfb-reduction-005` successor. No fix code is shipped in this pass
(`check=none`).

## Data source

Queried Grafana Mimir's `phantom_loadtime_seconds` / `phantom_loadtime_errors_total`
/ `phantom_loadtime_runs_total` series directly against the real deployed
color, exactly as `ttfb-reduction-002` and `-003` did:

```
$ kubectl -n monitoring port-forward svc/mimir 18081:8080 &
$ NOW=$(date +%s)
$ curl -s 'http://localhost:18081/prometheus/api/v1/query_range' \
    --data-urlencode 'query=phantom_loadtime_seconds{flow="materialise"}' \
    --data-urlencode "start=$((NOW-1209600))" --data-urlencode "end=$NOW" \
    --data-urlencode 'step=3600'
```

An instant query returns EMPTY (all series are older than Mimir's ~5m
instant-query staleness window), so a two-week range query was used to
recover the last-pushed sample.

## Live measurement (Mimir, real deployed color, 2026-09-15T18:3xZ)

Last-pushed sample bucket timestamp `1789439685` (~15h+ stale), values:

```
flow=materialise   item_type=movie    duration_s=4.169275   errors=1  runs=1
flow=materialise   item_type=episode  duration_s=0.015307   errors=1  runs=1
flow=get_sources   item_type=movie    duration_s=0.315222   errors=0  runs=1
flow=get_sources   item_type=episode  duration_s=0.181446   errors=1  runs=1
```

Unique values across the entire 14-day window:
- movie-materialise: `{4.169275, 64.720732}`
- episode-materialise: `{0.011467, 0.015307}`
- movie-get_sources: `{0.046751, 0.315222}`
- episode-get_sources: `{0.011981, 0.181446}`

## Finding — the sample is STILL byte-for-byte unchanged (fourth read)

The movie-materialise value `4.169275s` is IDENTICAL to the reading both
`ttfb-reduction-002` and `-003` made; the whole series has not advanced
since `-002`'s pass. This is now the FOURTH consecutive daily analytical
pass to read the exact same stale sample. Per the task's explicit
guidance — "if still stale, note that explicitly again rather than
re-deriving the same ranking a third time" — this pass does NOT re-derive
the stage ranking a fourth time; the ranking below is carried forward from
`-003` unchanged because the underlying data is unchanged.

### Ranking (carried forward — data unchanged)

1. **movie-materialise = 4.169275s** — by far the dominant stage; two
   orders of magnitude above every other movie/episode flow. This is the
   whole cold-materialise wait (availability → candidate/source probe →
   magnet select → gostream register → first byte).
2. movie-get_sources = 0.315222s
3. episode-get_sources = 0.181446s
4. episode-materialise = 0.015307s (already effectively warm/instant)

### Cold-materialise failure rate — 100%

`phantom_loadtime_errors_total{flow="materialise"} = 1` with
`runs_total = 1` for BOTH movie and episode item types → a 100%
cold-materialise failure rate in the recorded run, identical to `-002` /
`-003`. So the 4.169275s movie figure is a FAILED cold materialise, not a
successful first byte — the item never actually served.

## Root cause of the staleness (confirmed) — no Gitea mirror to run the daily rig

The daily rig never re-runs because its trigger
`.gitea/workflows/phantom-loadtime-daily.yaml` (`cron: '17 6 * * *'`) is a
**Gitea Actions** workflow, but `phantom-library`'s only git remote is
GitHub (`origin=git@github.com:spencerharmon/phantom-library.git`) and no
Gitea-hosted mirror of this repo exists (confirmed via the live Gitea
instance's `repos/search?q=phantom` API returning zero results). GitHub
never reads `.gitea/workflows/*`, so this cron — and every other
`.gitea/workflows/*.yaml` in the repo — has never had anywhere to execute.
This is why three (now four) successive daily passes read the identical
sample: the pipeline that would refresh it is not wired to fire.

The open questions `-002` and `-003` left unanswerable remain
unanswerable for the same reason:
- Whether `ttfb-magnet-cache-drain-worker` measurably improved cold
  materialise TTFB for opportunistically-touched items — no fresh sample.
- Whether `ttfb-fast-indexer-early-return` (default-disabled via
  `FastIndexerEarlyReturnEnabled`; not confirmed enabled anywhere)
  reduced movie-materialise duration — no fresh sample.

Both are DEFERRED to `ttfb-reduction-005`, which must first confirm a
genuinely fresh sample exists before attempting either assessment.

## Enqueued fix (the biggest remaining win)

`ttfb-daily-rig-gitea-mirror-wiring` (ROI P9, weight 9): mirror
`phantom-library` to `git.spencerharmon.com/spencer/phantom-library`
(prefer a pull-mirror from GitHub), enable Gitea Actions + confirm the
self-hosted runner label, wire the workflow's declared secrets/vars, and
add `tools/ci/gitea-mirror-sync.sh` (env-configured
`PHANTOM_GITEA_REMOTE_URL`, never baked in) plus a regression test
proving it fails clearly with no remote, pushes the tracked tip to a
synthetic mirror, and is idempotent. This follows the already-working
`jellyfin` fork submodule's dual-remote (origin + gitea) pattern.
`Check: ./scripts/tests/gitea-mirror-sync.test.sh`.

Rationale: staleness of the measurement pipeline is the biggest remaining
win because it gates ALL further TTFB analysis — no fix's live impact
(drain-worker, early-return, prefetch) can be measured until fresh
samples flow again. Fixing the fix-measurement loop unblocks the whole
self-perpetuating cadence.

**Status (this pass):** `ttfb-daily-rig-gitea-mirror-wiring` has already
LANDED its committable portion (commit `f648940`: the sync script +
regression test + `docs/ci-gitea-actions.md`) and is `[DONE]` in
`PLAN.md`. The residual Gitea-admin-only steps (create the mirror repo,
enable Actions, register the runner, populate secrets) are correctly
scoped as an `external-permission`/`secret`-class operator escalation.

## Successor

`ttfb-reduction-005 [TODO]` appended, held `not_before` ~= +24h (daily
cadence). It must first check whether the mirror wiring landed and a
genuinely fresh `phantom_loadtime_seconds` sample appeared (different
timestamp/value than `1789439685` / `4.169275s`) before attempting the
drain-worker / early-return impact assessment.

## Fix directions carried for future passes (ROI P9)

- Re-evaluate `ttfb-fast-indexer-early-return`'s live impact once it is
  actually enabled (currently default-disabled).
- Speculative first-byte prefetch into gostream — still unaddressed across
  all four prior passes; a candidate once fresh data confirms materialise
  is still the dominant stage.
- The daily-rig-pipeline-staleness gap itself — addressed by
  `ttfb-daily-rig-gitea-mirror-wiring`; `-005` verifies it actually
  produced a fresh sample.
