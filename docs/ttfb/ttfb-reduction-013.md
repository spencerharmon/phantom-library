# TTFB Reduction 013

Analysis resumed 2026-10-21; latest observation at 06:42Z. Diagnose-and-enqueue only.

## Prior Analysis and Landed Work

The requested full `ttfb-reduction-012.md` is absent from source and the hive analysis directory. Read its surviving committed hive change report instead: September 23 had all 12 error series at zero and episode list load at 0.308686s. It filed durable daily-stage metrics because daily investigation cannot reliably recover garbage-collected pod logs.

Stage telemetry landed in `8dcf268a38717ef979dbc60b48a4685670e76c34` and is observable in Mimir. Episode season batching landed in `9abb08b8777c4ad992a90723e012a5640afc99d3`. This resumed pass restores the saved analysis from `bd5a0943ed98a108ccd5a27a8a85df7535997e23`, with independently refreshed observations, not another implementation or duplicate task filing.

## Newest Authoritative Sample

Mimir flow `push_time_seconds` VALUE: **1792561224.1304245 = 2026-10-21T05:40:24Z**. Stage push: **1792561230.8962243**. These values, not the evaluation timestamp, establish freshness. The 06:42Z query returned the same sample as the previous resume. All 12 error values are zero and all 12 run values are one. These are single synthetic measurements, not percentiles or proof of real playback reliability. Color is green; the label does not attest ingress role or deployed plugin version. The gauge has no producer/run identity.

| Rank | Flow | Item Type | Seconds |
| --- | --- | --- | ---: |
| 1 | list_load | episode | 0.375142 |
| 2 | sort_change | episode | 0.237887 |
| 3 | list_load | movie | 0.156981 |
| 4 | sort_change | movie | 0.152692 |
| 5 | info_open | episode | 0.050888 |
| 6 | get_sources | episode | 0.049756 |
| 7 | info_open | movie | 0.047916 |
| 8 | materialise | episode | 0.042643 |
| 9 | get_sources | movie | 0.042620 |
| 10 | play_materialised | episode | 0.042195 |
| 11 | play_materialised | movie | 0.039508 |
| 12 | materialise | movie | 0.036604 |

Episode list remains dominant. Earlier in this task, the 02:58:09Z push measured 0.440571s episode list and 0.217867s movie list. Neither comparison establishes a regression or batching improvement: cache, load, sample count and deployed build are uncontrolled.

## Daily Failure and Verified Recovery

October 19/20 scheduled Jobs reached BackoffLimitExceeded after 36/37 seconds. The earlier attempt queried Mimir at an October 20 historical evaluation and observed `phantom_loadtime_daily_stage_exit_code{job="phantom-loadtime-daily-run",stage="guard"}=2`, duration 2s. No failed-job pod logs were needed.

The earlier exact-image diagnostic pod running `dotnet --info` terminated with exit 127: the pinned rig image lacked the SDK. `tools/perf/loadtime-guard.sh:118-131` maps this tool failure to 2; `tools/ci/loadtime-daily-run.sh:260-266` records and fails on 2, while accepting breach code 3. Successful measurement/push before guard explains zero flow errors alongside failed Jobs. SDK source repair `a5f551b77c56d0e70e752d7693d3fac90a5f3755` already existed but did not publish or repin the runtime image; a fresh init-container checkout cannot repair a stale toolchain image.

The ONE corrective task filed by this analysis, **flux:ttfb-loadtime-sdk-repin**, has since delivered that deployment step. Its post-merge verification records SDK execution and fresh completed Jobs. This resumed pass independently observed three completed verification Jobs (05:38:31-05:38:55Z, 05:39:02-05:39:19Z, 05:40:10-05:40:35Z), and current Mimir **guard=0, duration=6s** with the fresh push above. The retained failed scheduled Jobs predate this recovery; no newer failed daily Job was listed. The next scheduled run is still a separate observation, not proven by these manual verification Jobs.

Telemetry limitation: `record_stage` PUTs one stage to the same grouping URL, replacing earlier stage samples. Only guard survives. Its presence proves the wrapper reached guard, not independently retained measure/push results. POST alone also replaces the metric family; a future retention repair needs cumulative current-run payloads or stage-scoped groups with freshness. Never encode raw stderr in metric labels.

## TV Batching Assessment

The requested season optimization already exists: `GetEpisodesForSeasonAsync` uses `UpsertTmdbEpisodesAsync` and `ListMaterialisedEpisodesForSeasonAsync`, with a 3-versus-40 episode connection regression in `PhantomShowsChannelTests`. Do not enqueue this again.

Crucially, `tools/rig-scenarios/47-loadtime-flows.sh:450-483` measures the Shows **root**, without FolderId, including pagination and badge fan-out, but labels it `item_type="episode"`. It does not measure a season episode list. A season-path optimization cannot be credited to this metric without confirming the request and deployed build.

The movie batching pattern remains a candidate for the measured root: `PhantomShowsChannel.cs:452-472` serially enriches external series and checks hidden state, with per-directory cached path/metadata reads in `TryResolveEnrichedGostreamSeriesAsync`. Profile root listing and reuse bulk movie primitives where applicable, preserving hidden-set filtering, duplicate-directory handling, cold/warm cache behavior and movie/TV parity. No savings estimate is yet established. Separately, batching connections/transactions for episode upserts does not by itself prove bounded PostgreSQL command round trips.

## One Fix and Daily Successor

Retain the already-filed **flux:ttfb-loadtime-sdk-repin**, with its own hive design doc and approved `integration-test` Check `tests/phantom-loadtime-sdk-repin.test.sh`; post-merge Check `tests/phantom-loadtime-sdk-repin.test.sh --live` verifies runtime recovery. It closed a proven broken regression loop rather than speculatively optimizing an unprofiled query. Do not file a second fix merely because the first landed while this analysis waited for handoff repair.

Retain existing **ttfb-reduction-014 [TODO]**, weight 8, `not_before=2026-10-22T03:03:13Z`, the approximately +24h gate from the original attempt. Next pass: verify the next scheduled run through durable stage metrics, re-rank a fresh sample, confirm request/build identity, and assess bulk external-series enrichment for the Shows root. Continue one concrete fix plus a +24h successor. Its existing design doc already carries this recovery; no new successor ID is needed.

## Verification and Limits

No honest behavioral regression test applies to this analytical document (`check=none`). Manual evidence consists of Mimir, Job status and source inspection; exact infrastructure commands and recorded outputs are in the hive change doc. Earlier source-image regression evidence was 27 passed, 0 failed, proving the Dockerfile rather than the deployed artifact. This resumed pass made no runtime, schema, query or configuration changes and requires no operator action.
