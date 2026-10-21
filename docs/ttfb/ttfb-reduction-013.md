# ttfb-reduction-013 — daily TTFB analysis (2026-10-21)

## 1. Stage-outcome metric landed — and diagnoses the daily failure
`ttfb-loadtime-daily-stage-outcome-metric` is DONE (8dcf268). Mimir now holds:
- `phantom_loadtime_daily_stage_exit_code{job="phantom-loadtime-daily-run",stage="guard"} = 2`
- `phantom_loadtime_daily_stage_seconds{stage="guard"} = 2`
- push_time_seconds daily-run 1792495054 ≈ flow push 1792495052 (stage 2 succeeded).
Daily jobs -29873477 and -29874917 both Failed (BackoffLimitExceeded).

Root cause (localised): stage 3 `tools/perf/loadtime-guard.sh` exits **2 = tool error**
(its 0/3/2 contract), after only ~2s — too fast for its `dotnet run` ratchet-guard build,
so it dies in the pre-guard section (`die`: engine/converter/thresholds missing, python3,
or conversion failure) or the guard's dotnet run returns non-0/3 immediately. A manual
reproduction (`kubectl create job ttfb013-diag --from=cronjob/phantom-library-loadtime-daily`)
failed identically (BackoffLimitExceeded in ~45s) but its pods were reaped on failure
before logs could be read — the exact durability gap, one level deeper: we now have the
exit CODE but not the REASON.

New defect found: `record_stage` PUTs to a single grouping key
(`job="phantom-loadtime-daily-run"`), so each stage push REPLACES the previous —
only `stage="guard"` survives; measure/push outcomes are overwritten. Handed to -014
(guard stderr-tail capture + per-stage grouping key) since this pass enqueues ONE fix.

## 2. Newest sample (Mimir, job="phantom-loadtime"), errors_total = 0 for all 12 series
| flow | episode | movie |
|---|---|---|
| list_load | **0.289849** | 0.142492 |
| sort_change | 0.202045 | 0.138752 |
| info_open | 0.055421 | 0.045908 |
| get_sources | 0.045792 | 0.036849 |
| materialise | 0.042742 | 0.036682 |
| play_materialised | 0.035654 | 0.037418 |

## 3. Ranking
list_load{episode} remains dominant (-011 0.3146, -012 0.3087, -013 0.2898), ~2x the movie
path, with sort_change{episode} second — both share the episode enumeration. The movie
DB-batching fix (ttfb-list-load-movie-render-profile) applies: the episode path
(PhantomShowsChannel/shelves resolving series→season→episode) shows the N+1 signature.

## 4. Enqueued
- `ttfb-list-load-episode-render-profile` (Check: `dotnet test`) — biggest TTFB win.
- `ttfb-reduction-014` held +24h.
