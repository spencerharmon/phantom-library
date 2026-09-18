# ttfb-reduction-008

Successor of `ttfb-reduction-007`, held `not_before` ~= +24h for the daily
TTFB-reduction cadence. Read `docs/ttfb/ttfb-reduction-007.md` FIRST.

Check FIRST whether `ttfb-daily-rig-image-build` (enqueued by `-007`) has
landed and whether its published image's `Verify-After-Merge` (`skopeo
inspect docker://git.spencerharmon.com/phantom-library/loadtime-rig:<tag>`)
has passed. If it has, check whether a follow-up flux-owned enablement task
(setting `loadtimeDaily.enabled=true` with real values) has been filed and/or
completed — file it via `beehive task add flux <id>` + `beehive task block`
if `-007`/`-008` did not already. Then query Mimir directly (`kubectl -n
monitoring port-forward svc/mimir` + the Prometheus HTTP API `query_range`,
per `ttfb-reduction-002` through `-007`'s method) for a genuinely FRESH
`phantom_loadtime_seconds` sample (a bucket timestamp beyond `1789440830` /
movie-materialise value != `4.169275s`, the value every pass 002-007 read).

If fresh data is available, FINALLY assess whether
`ttfb-magnet-cache-drain-worker` (DONE) measurably improved cold materialise
TTFB for opportunistically-touched items and whether
`ttfb-fast-indexer-early-return-enable-default` (DONE) reduced
movie-materialise duration — confirm the flag is actually ENABLED and rolled
out in the deployed config before expecting any effect. RANK the dominant
stage + record cold-materialise failure rate; write the analysis doc at
`docs/ttfb/ttfb-reduction-008.md`; APPEND ONE concrete fix task (own design
doc + a CHECKS.md-matching Check) for the biggest remaining win; APPEND the
successor `ttfb-reduction-009 [TODO]` held with `not_before` ~= +24h (daily
cadence). DIAGNOSE-AND-ENQUEUE ONLY — never implement the fix in this
analytical pass.

If STILL stale (an eighth identical read), note that explicitly and check on
the image-build task's and the flux enablement task's progress rather than
re-deriving the same ranking an eighth time.
