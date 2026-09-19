# ttfb-reduction-009 (design)

Successor of `ttfb-reduction-008`, continuing the self-perpetuating DAILY
TTFB-reduction loop (ROI Priority 9). Held `not_before` ~= +24h
(`2026-09-20T18:21:12Z`, daily cadence). DIAGNOSE-AND-ENQUEUE ONLY — never
implement the fix in the analytical pass.

## What -009 must do

1. **Read `docs/ttfb/ttfb-reduction-008.md` FIRST** (prior analysis).
2. Check whether `flux:flux-phantom-loadtime-daily-enablement` (enqueued by
   -008) has LANDED and RECONCILED, and whether its `Verify-After-Merge`
   fresh-sample check has passed (the enabled CronJob exists AND has fired
   at least once).
3. Query Mimir directly (kubectl -n monitoring port-forward svc/mimir +
   curl the Prometheus HTTP API query_range, per -002…-008's method) for a
   genuinely FRESH `phantom_loadtime_seconds` sample: bucket timestamp
   `> 1789440939` AND/OR a `materialise/movie` value `!= 4.169275s` (the
   value every pass 002-008 read).
4. **If fresh data is finally available:** assess whether
   `ttfb-magnet-cache-drain-worker` (DONE) measurably improved cold
   materialise TTFB for opportunistically-touched items and whether
   `ttfb-fast-indexer-early-return-enable-default` (DONE) reduced
   movie-materialise duration — confirm the flag is actually ENABLED and
   rolled out in the deployed config before expecting any effect. RANK the
   dominant stage + record cold-materialise failure rate.
5. **If STILL stale (a ninth identical read):** note that explicitly and
   check on `flux:flux-phantom-loadtime-daily-enablement`'s progress
   (landed? reconciled? CronJob firing? failing?) rather than re-deriving
   the same ranking a ninth time.
6. Write the analysis doc at `docs/ttfb/ttfb-reduction-009.md`; APPEND ONE
   concrete fix task (own design doc + a CHECKS.md-matching Check) for the
   biggest remaining win; APPEND the successor `ttfb-reduction-010 [TODO]`
   held with `not_before` ~= +24h.

## Check

`check=none` — this is an analytical DIAGNOSE-AND-ENQUEUE pass; it ships no
fix code, only the analysis doc + the enqueued fix task's design doc, per
the established -002…-008 pattern.
