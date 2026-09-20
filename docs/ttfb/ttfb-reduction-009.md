# ttfb-reduction-009 — daily TTFB-reduction analysis

ROI Priority 9, successor of `ttfb-reduction-008`. DIAGNOSE-AND-ENQUEUE
ONLY: this is the NINTH consecutive daily analytical pass, and the FIRST
one to read a **genuinely fresh** `phantom_loadtime_seconds` sample.
`check=none` — no fix code ships here; the only submodule commits are this
analysis doc plus the one enqueued fix task's design doc.

## Headline: the eight-pass staleness is BROKEN

`flux:flux-phantom-loadtime-daily-enablement` (enqueued by `-008`) LANDED
and reconciled — it is `[DONE]` in `submodules/flux/PLAN.md`
(commits `3f0ef2e…`, `c1e034b…`, `1de3f2d…`). Its Verify-After-Merge
freshness curl is now satisfiable: the daily in-cluster CronJob has fired
and Mimir now serves a fresh sample. The byte-for-byte-identical stale read
(`movie-materialise=4.169275s`, bucket `~1789440939`) that stranded passes
`002`–`008` is GONE.

## Data source (unchanged method, per 002–008)

```
kubectl -n monitoring port-forward svc/mimir 18083:8080 &
curl -s 'http://localhost:18083/prometheus/api/v1/query' \
    --data-urlencode 'query=phantom_loadtime_seconds'
```

An **instant** `query` (no range) now returns a FULL vector (it was EMPTY
in `-008`) — proof of recent, in-staleness-window samples. Sample wall-clock
`1789928681` (vs the frozen historical push at bucket `~1789440939`).

## Live measurement (Mimir, real deployed green color, 2026-09-20T~18:24Z)

Fresh `phantom_loadtime_seconds{color="green"}` (seconds), plus the companion
`phantom_loadtime_runs_total` / `phantom_loadtime_errors_total` counters:

| flow              | movie dur_s | movie err/runs | episode dur_s | episode err/runs |
|-------------------|------------:|:--------------:|--------------:|:----------------:|
| list_load         | **432.610197** | 0/1 (success) | 66.15218      | 0/1 (success)    |
| sort_change       | **196.173978** | 0/1 (success) | 28.932303     | 0/1 (success)    |
| info_open         | 0.035748    | 1/1 (FAILED)   | 0.036801      | 1/1 (FAILED)     |
| get_sources       | 0.035093    | 1/1 (FAILED)   | 0.035746      | 1/1 (FAILED)     |
| materialise       | 0.035984    | 1/1 (FAILED)   | 0.036036      | 1/1 (FAILED)     |
| play_materialised | 0.035954    | 1/1 (FAILED)   | 0.036321      | 1/1 (FAILED)     |

### Reading the counters — the sub-100ms flows are FAST-FAILS, not wins

`materialise`, `get_sources`, `info_open`, `play_materialised` each report
`errors_total=1` against `runs_total=1` — a **100% failure rate**. Their
tiny ~0.036s durations are the time-to-ERROR of a run that aborted early,
NOT a successful first byte. So the old dominant `movie-materialise=4.169275s`
did not "drop to 0.036s by optimisation"; on this run the materialise flow
never completed — it errored fast. The two flows that GENUINELY completed
(`errors_total=0`) are `list_load` and `sort_change`, and those are the two
that are catastrophically slow.

## Ranking the dominant stage (successful flows only)

1. **movie list_load = 432.610197s (~7.2 min)** — DOMINANT by a wide margin,
   a real success (err=0). This is the full movie channel list render.
2. **movie sort_change = 196.173978s (~3.3 min)** — second, real success.
3. episode list_load = 66.15218s.
4. episode sort_change = 28.932303s.

The materialise/get_sources/play/info_open flows cannot be ranked as
successes this run — they failed; see failure-rate section.

## Cold-materialise failure rate — 100% (movie AND episode), but fast-fail now

`phantom_loadtime_errors_total{flow="materialise"} = 1` over
`phantom_loadtime_runs_total = 1` for BOTH `item_type=movie` and
`item_type=episode` → **100% cold-materialise failure this run**. Distinct
from `002`–`008`, where the failure showed as a slow 4.169275s hang; here it
is a fast error (~0.036s to abort). Same movie/TV parity: both fail.

## Did the two DONE fixes land the expected effect?

- `ttfb-fast-indexer-early-return-enable-default` (DONE, flag defaults `true`
  in source): the materialise flow no longer HANGS at 4.169275s — it now
  returns in ~0.036s. But it returns with `errors_total=1`, so the early
  return is currently an early *failure*, not an early success. Whether the
  flag is rolled out in the deployed config cannot be separated from the
  fast-fail here; the deployed rig clearly exercises a different, faster
  code path than the 4.169275s hang, consistent with the flag being active.
- `ttfb-magnet-cache-drain-worker` (DONE): its intended win is cold-
  materialise TTFB for opportunistically-touched items. With materialise at
  100% failure this run, no positive TTFB improvement is observable — the
  drain worker's benefit is masked by (or downstream of) whatever makes the
  materialise flow error. Not disproven, but not confirmed either.

Net: the fixes moved materialise OFF the top of the ranking, but the new #1
is a genuinely-completing, genuinely-slow **list_load** at 7+ minutes for
movies — the biggest remaining win, and unambiguously a real success rather
than a fast-fail artifact.

## Enqueued fix (the biggest remaining, swarm-buildable win)

`ttfb-list-load-movie-render-profile` (ROI P9, weight 9) — profile and
reduce the movie channel `list_load` stage, currently 432.6s (7.2 min) for
movies / 66.2s for episodes, now the dominant successful flow. The task
FIRST profiles where the 7 minutes goes (per-item DB round-trips vs. TMDB/
availability fan-out vs. channel-cache rebuild vs. gostream/external-file
enumeration) via the rig scenario `47-loadtime-flows.sh` / the channel list
path, then lands the narrowest fix for the dominant sub-cost, holding
movie/TV parity (episode list_load 66s must not regress). Design doc:
`docs/tasks/ttfb-list-load-movie-render-profile.md`. `Check:` uses the
`endpoint` framework — a live Mimir query asserting the freshly-emitted
`list_load{item_type="movie"}` sample dropped materially below the recorded
432.610197s baseline. See the design doc for the exact invocation.

## Successor

`ttfb-reduction-010 [TODO]` appended, held `not_before` ~= +24h (daily
cadence, `2026-09-21T18:24:00Z`). It must (1) check whether
`ttfb-list-load-movie-render-profile` landed and whether a fresh sample
shows `list_load{movie}` below the 432.6s baseline; (2) re-examine the
100%-cold-materialise **failure** surfaced here (the fast-fail
`errors_total=1` on materialise/get_sources/info_open/play_materialised) —
now that data is flowing, that failure is itself a candidate dominant issue
even though its duration is small; (3) re-rank against the next fresh sample.
Design doc: `docs/tasks/ttfb-reduction-010.md`.
