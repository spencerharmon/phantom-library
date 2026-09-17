# ttfb-reduction-007

ROI Priority 9 — successor of `ttfb-reduction-006`, continuing the
self-perpetuating DAILY TTFB-reduction loop (seeded series, NOT
reconcile-managed). DIAGNOSE-AND-ENQUEUE ONLY — never implement a fix in this
analytical pass.

Check FIRST, by querying Mimir directly
(`kubectl -n monitoring port-forward svc/mimir` + the Prometheus HTTP API
`query_range`, per `ttfb-reduction-002` through `-006`'s method), whether a
genuinely FRESH `phantom_loadtime_seconds` sample has appeared — a bucket
timestamp BEYOND `1789440830` and/or a `movie-materialise` value different from
`4.169275s` (the value every pass 002–006 has read). Two independent things can
unblock the sample:
- `ttfb-daily-rig-incluster-cronjob` (enqueued by `-006`) landing + rolling out —
  the swarm-buildable in-cluster CronJob route.
- `gitea-mirror-provision-wiring` (`[NEEDS-HUMAN]`, external-permission) finally
  being provisioned by the operator — the Gitea-Actions route.
Check both.

If fresh data is available, FINALLY assess (the questions frozen since `-002`):
- did `ttfb-magnet-cache-drain-worker` (DONE) measurably improve cold materialise
  TTFB for opportunistically-touched items?
- did `ttfb-fast-indexer-early-return-enable-default` (DONE) reduce
  `movie-materialise` duration — confirm the flag is actually ENABLED and rolled
  out in the deployed config before expecting any effect.
Then RANK the dominant stage + record the cold-materialise failure rate, write
the analysis doc at `docs/ttfb/ttfb-reduction-007.md`, APPEND ONE concrete fix
task (own design doc + a CHECKS.md-matching Check:) for the biggest remaining
win, and APPEND the successor `ttfb-reduction-008 [TODO]` held with
`not_before` ~= +24h (daily cadence).

If STILL stale (a seventh consecutive identical read), note that explicitly and
check on BOTH `ttfb-daily-rig-incluster-cronjob`'s and
`gitea-mirror-provision-wiring`'s progress rather than re-deriving the same
ranking a seventh time.

Read `docs/ttfb/ttfb-reduction-006.md` (prior analysis) FIRST.
