# ttfb-list-load-movie-render-profile

Enqueued by `ttfb-reduction-009` (ninth daily TTFB pass, first with a fresh
Mimir sample). ROI Priority 9, weight 9.

## Problem

The first genuinely-fresh `phantom_loadtime_seconds` sample (bucket wall-clock
`~1789928681`, `color=green`) re-ranks the flows. The prior dominant stage,
`movie-materialise`, no longer hangs at 4.169275s — it now returns in ~0.036s
but with `errors_total=1` (a fast-fail, tracked separately by
`ttfb-reduction-010`). The new dominant **successful** flow (`errors_total=0`)
is:

- `list_load{item_type="movie"} = 432.610197s` (~7.2 minutes)
- `list_load{item_type="episode"} = 66.15218s`

`list_load` is the full movie/episode channel list render (see
`tools/rig-scenarios/47-loadtime-flows.sh` header: "open a library/channel
list view"). 7+ minutes to render the movie channel list is the single biggest
remaining, genuinely-completing TTFB cost.

## Scope (this is a FIX task, not another analysis pass)

1. **Profile** where the 7.2 minutes goes for movie `list_load`. Candidate
   dominant sub-costs to attribute:
   - per-item DB round-trips against `phantom.db` / `jellyfin.db` `BaseItems`
     (N+1 query shape across the full movie list);
   - synchronous TMDB / availability fan-out per list item;
   - Jellyfin channel-cache rebuild on list open;
   - gostream / external-file enumeration per item.
   Use the rig (`docs/agents/testing.md`, `tools/rig-scenarios/47-loadtime-flows.sh`,
   `48-list-materialise-dom.mjs`) on a prod-DB clone at `:18096` to reproduce
   and attribute the cost. Record the attribution in the change doc.
2. **Land the narrowest fix** for the dominant sub-cost (e.g. batch the per-item
   query, cache/precompute the availability fan-out, scope the channel-cache
   rebuild). Do NOT rewrite the channel list path wholesale.
3. **Movie/TV parity (mandatory, per submodule AGENTS.md):** episode
   `list_load` (66.2s) must not regress; if the fix is movie-only, audit and
   document the episode path in the same change and add/extend episode rig
   coverage. Neither `35-channel-e2e-playback.sh` nor
   `36-channel-episode-e2e-playback.sh` may regress.
4. Add a failing-first regression test (rig scenario or unit) that captures the
   list_load cost/shape and passes after the fix. Paste the exact command +
   passing output into the change doc.

## Definition of done — `Check:` (endpoint framework, CHECKS.md `endpoint`)

The DoD asserts the REAL deployed effect: after this fix lands and the daily
CronJob re-fires, the fresh `list_load{item_type="movie"}` Mimir sample must
drop materially below the recorded 432.610197s baseline. Because the fresh
sample only appears after merge + the next CronJob fire, this is authored as a
`Verify-After-Merge` check (the runner auto-spawns the successor check task
post-merge):

```
Verify-After-Merge: curl -sf http://mimir.monitoring.svc:8080/prometheus/api/v1/query --data-urlencode 'query=phantom_loadtime_seconds{flow="list_load",item_type="movie"} < 432.610197' | grep -q '"result":\[{'
```

(Matches the `endpoint` framework — a live curl against the Mimir HTTP API
asserting the movie list_load duration is now below the pre-fix baseline. The
non-empty result vector is the pass condition; an empty vector = the sample is
not below baseline = fail.)

In-session, the implementer additionally proves the fix with the rig
regression test from scope §4 (a real reproduction on a prod-DB clone), since
the merged Mimir effect cannot be observed at NEEDS-REVIEW time.
