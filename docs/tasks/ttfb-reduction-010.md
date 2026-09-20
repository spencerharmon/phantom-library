# ttfb-reduction-010

Successor of `ttfb-reduction-009` in the self-perpetuating DAILY
TTFB-reduction loop (ROI Priority 9). Held `not_before` ~= +24h
(`2026-09-21T18:24:00Z`, daily cadence). DIAGNOSE-AND-ENQUEUE ONLY — never
implement a fix in this analytical pass.

Read `docs/ttfb/ttfb-reduction-009.md` (prior analysis — the FIRST fresh
sample) FIRST.

This pass — now that the eight-pass staleness is broken and data flows daily —
must:

1. Check whether `ttfb-list-load-movie-render-profile` (enqueued by `-009`)
   has LANDED and RECONCILED and whether its `Verify-After-Merge` check
   passed (fresh `list_load{item_type="movie"}` sample below the 432.610197s
   baseline).
2. Query Mimir directly for the latest fresh `phantom_loadtime_seconds`
   sample AND the companion `phantom_loadtime_runs_total` /
   `phantom_loadtime_errors_total` counters (per `-009`'s method):
   ```
   kubectl -n monitoring port-forward svc/mimir 18083:8080 &
   curl -s 'http://localhost:18083/prometheus/api/v1/query' \
       --data-urlencode 'query=phantom_loadtime_seconds'
   curl -s 'http://localhost:18083/prometheus/api/v1/query' \
       --data-urlencode 'query={__name__=~"phantom_loadtime.*"}'
   ```
3. RE-EXAMINE the 100%-cold-materialise **failure** surfaced by `-009`: on
   the `-009` sample, `materialise`, `get_sources`, `info_open`, and
   `play_materialised` all reported `errors_total=1 / runs_total=1` (fast-fail,
   ~0.036s to error) for BOTH movie and episode. Now that the loop emits data,
   that failure is itself a candidate dominant issue despite its small
   duration — assess whether it is a rig/probe defect (the loadtime rig cannot
   complete those flows in-cluster) or a real deployed regression, and enqueue
   the appropriate fix.
4. RANK the dominant stage against the newest fresh sample (successful flows
   only — `errors_total=0`); record the cold-materialise failure rate.
5. Write the analysis doc at `docs/ttfb/ttfb-reduction-010.md`; APPEND ONE
   concrete fix task (own design doc + a CHECKS.md-matching `Check`) for the
   biggest remaining win; APPEND the successor `ttfb-reduction-011 [TODO]`
   held with `not_before` ~= +24h.

If the sample is stale again (the CronJob stopped firing), note that
explicitly and check on the daily-enablement CronJob's health
(`flux:flux-phantom-loadtime-daily-enablement` + its remediation cluster,
e.g. `flux-loadtime-daily-dev-host-sso-bypass`) rather than re-deriving the
same ranking.
