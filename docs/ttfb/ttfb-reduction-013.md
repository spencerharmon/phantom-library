# TTFB Reduction 013

Analysis: 2026-10-21T04:11Z. Diagnose-and-enqueue only.

## Prior Analysis and Landed Work
The requested full `ttfb-reduction-012.md` is absent from source and the hive analysis directory. Its committed hive change record was read instead: September 23 had all 12 error series at zero and episode list load at 0.308686s; it filed durable daily-stage metrics to avoid relying on garbage-collected pod logs.

Stage telemetry landed in `8dcf268a38717ef979dbc60b48a4685670e76c34` and is observable in Mimir. Episode season batching also already landed at current tip `9abb08b8777c4ad992a90723e012a5640afc99d3`. The next-day task design survived an earlier attempt; do not duplicate the episode fix or successor ID.

## Newest Authoritative Sample
Queried Mimir through the Kubernetes service proxy. Latest flow `push_time_seconds` VALUE: **1792551489.0324533 = 2026-10-21T02:58:09Z**. Stage push: 1792551491.7177577. These values establish freshness, unlike query evaluation timestamps. All 12 error values are zero; all 12 run values are one. These are single synthetic measurements, not percentiles or real-playback-success evidence. Color is green; this label alone does not attest current ingress role or deployed plugin version. The metric does not identify the latest producer, so do not attribute this newer push to the retained scheduled Job.

| Rank | Flow | Item Type | Seconds |
| --- | --- | --- | ---: |
| 1 | list_load | episode | 0.440571 |
| 2 | sort_change | episode | 0.250568 |
| 3 | list_load | movie | 0.217867 |
| 4 | sort_change | movie | 0.166316 |
| 5 | info_open | movie | 0.070412 |
| 6 | info_open | episode | 0.065701 |
| 7 | materialise | movie | 0.059269 |
| 8 | get_sources | movie | 0.057826 |
| 9 | materialise | episode | 0.053262 |
| 10 | get_sources | episode | 0.050960 |
| 11 | play_materialised | movie | 0.047297 |
| 12 | play_materialised | episode | 0.045668 |

Historical evaluation at 2026-10-20T12:00Z: episode list 0.289849s, movie list 0.142492s, episode sort 0.202045s; flow push 1792495052.9492252. The newest observation is slower, not a demonstrated batching improvement. Cache, load and deployed build are uncontrolled; do not infer a statistically established regression or promise savings.

## Daily Failure: Root Cause
October 19/20 scheduled Jobs reached BackoffLimitExceeded after 36/37 seconds. Mimir shows `phantom_loadtime_daily_stage_exit_code{job="phantom-loadtime-daily-run",stage="guard"}=2`, guard duration 2s, both now and at the October 20 historical evaluation. No failed-job pod logs were read.

The live rig image is still the original Debian-only artifact. A separate disposable pod using that exact digest with `dotnet --info` terminated with **exitCode 127**. Private registry pin and reproducible commands are in the hive change record. `tools/perf/loadtime-guard.sh:118-131` runs `dotnet` and maps a nonzero/non-3 tool result to 2; the wrapper records it and fails at `tools/ci/loadtime-daily-run.sh:260-266`. Successful measure/push precede the guard, explaining zero flow errors alongside failed Jobs. A latency breach would be code 3.

SDK source repair `a5f551b77c56d0e70e752d7693d3fac90a5f3755` already exists, but its doc explicitly excluded publication/repinning. The init container's fresh checkout cannot repair an old pinned image. This is a missing deployment step, not a need for host SDK installation or more logging.

Telemetry caveat: `record_stage` PUTs one stage to the same grouping URL, replacing earlier stage samples. Only guard survives; its presence proves the wrapper reached guard, not separately retained measure/push codes. POST alone would still replace this whole metric family. Future retention repair should use cumulative current-run payloads or carefully scoped groups with freshness, never raw stderr labels.

## TV Batching Assessment
The requested season optimization already exists: `GetEpisodesForSeasonAsync` uses `UpsertTmdbEpisodesAsync` and `ListMaterialisedEpisodesForSeasonAsync`, with a 3-versus-40 episode connection regression in `PhantomShowsChannelTests`. Do not enqueue it again.

The metric's scope is important: `tools/rig-scenarios/47-loadtime-flows.sh:450-483` times the Shows **root** without FolderId, plus badge fan-out/DOM work, labelling it `item_type="episode"`. It does not time a season episode list. The channel dispatches these paths separately, so a season improvement cannot be credited to this metric without request and build verification.

Remaining movie-pattern candidate: cached external TV resolution (`PhantomShowsChannel.cs:452-472,907-953`) performs per-directory path/metadata/hidden reads, and navigation scans the external catalogue. Reuse movie bulk primitives for series with cold/warm-cache, hidden-set, duplicate-directory and movie/TV parity tests if profiling confirms this path. Separately, `PhantomDb.cs:5396-5421` uses one transaction but still executes one episode command per row; connection-count tests do not prove bounded PostgreSQL round trips. Neither candidate has a measured savings estimate yet.

## One Fix and Successor
File **flux:ttfb-loadtime-sdk-repin**: publish the already-fixed image, update the shared daily CronJob pin, and verify an actual fresh completed Job plus Mimir guard code. This closes a proven broken regression loop before using the single fix slot for a less-certain optimization. Design and deployment-specific evidence live in the hive.

Retain/create **ttfb-reduction-014** with a daily not-before gate, without duplicate IDs. Next pass: verify SDK repin, re-rank fresh samples, confirm deployed plugin/request identity, then assess external-series batching. Continue one concrete fix plus a +24h successor.

## Verification and Limits
This analytical doc has no honest behavioral regression test (`check=none`). Evidence is the live/historical Mimir query, exact-image exit-code probe and source trace; exact infrastructure commands are in the private hive change record. The existing source-image regression ran with **27 passed, 0 failed**, proving the landed Dockerfile, not deployment. No fix, publication, repin, production change or operator action was performed by this pass.
