# TTFB Reduction 014

Analysis 2026-10-22T03:07Z. Diagnose-and-enqueue only.

## Landed work
- `ttfb-list-load-episode-render-profile` is DONE (`9abb08b8`): season episode batching. 013 already showed the
  metric `list_load{item_type="episode"}` measures the Shows **root** (`tools/rig-scenarios/47-loadtime-flows.sh:450-483`),
  not a season list, so that fix cannot move this metric.
- `flux:ttfb-loadtime-sdk-repin` recovered the guard stage.

## Newest sample (Mimir, push_time_seconds=1792581433.64 = 2026-10-21T11:17Z)
All 12 errors=0. Stage exit `guard=0`.

| Flow | Type | Seconds | vs 013 baseline |
| --- | --- | ---: | --- |
| list_load | episode | 0.305713 | 0.289849 (+0.016, noise; no improvement) |
| sort_change | episode | 0.305823 | 0.237887 |
| list_load | movie | 0.135794 | |
| sort_change | movie | 0.134463 | |
| all others | | 0.029-0.048 | |

Shows root (list_load + sort_change episode) is ~2.2x the movie root and dominant.

## Daily job
`phantom-library-loadtime-daily-29876357` Complete 1/1 (23s), first green **scheduled** run after the repin;
the two older Failed jobs predate recovery. Guard no longer failing, so the guard fix is not needed.
The record_stage single-grouping-key overwrite defect remains (only `guard` survives) but is observability-only
and a smaller win than the Shows-root latency; deferred to a later pass.

## Enqueued fix
`ttfb-shows-root-bulk-enrichment`: batch the Shows root external-series enrichment / hidden-state lookups in
`PhantomShowsChannel.cs` (~452-472) with bulk primitives mirroring the movie root, with a unit test asserting
page-size-independent store calls. Successor: `ttfb-reduction-015` (+24h).
