# TTFB Reduction 027

Diagnosis rechecked on 2026-11-08 UTC after reading day 026 first. This restores
the prior attempt's missing report, with independently verified evidence and
corrected totals. Diagnose-and-enqueue only: no runtime, deployment, measurement
or threshold changes.

## Discovery Recovered

The read-only evidence-volume probe found exactly two manifests newer than
run_start 1793967449:

| run_start | checkout | status | discovery | measure | retained failures |
| --- | --- | --- | --- | --- | --- |
| 1794035653 | f522e6744abe4e2d732806f687480404ac4ecc9e | failed, exit 1 | exit 0, 26s | exit 124, 1500s | 11 page_fetch timeout records |
| 1794053824 | cbc9edf3a171ac7b5fa41c9e92479953cc0f156f | complete, exit 0 | exit 0, 21s | exit 0, 817s | 6 episode HTTP failures |

Both resolved a color and typed Movie/Series/Season/Episode targets; neither
has a discovery_failure. Both executed checkouts contain the kubectl repair
`7026ebf`. This is actual post-repair discovery success, not merely a landed fix.
The scheduled November 7 Job ran 12:17:00Z to 12:30:50Z and completed. Its pods
are no longer retained, so no historical pod log was recovered.

## Request Bounds

Run 1794035653 contains eleven curl-exit-28 / HTTP-000 failures at page_fetch:
three movie list_load, three movie sort_change, three episode list_load and two
episode sort_change. Its checkout contains `7e28c4a`. The request/list timeout
handling therefore fired, but the manifest cannot distinguish curl's request
deadline from an exhausted aggregate list budget: both normalize to 28/000.
Do not claim every page request timed out or infer an unrecorded final sample.
The outer measurement wall also fired at 1500s (exit 124); no push/guard stage
is retained for this incomplete batch.

The completed run has no timeout records. Its six failures are episode
materialise HTTP 500 x3 and play_materialised HTTP 400 x3, all curl exit 22.
The 817s measurement stayed below the 1500s wall; its push succeeded and guard
exited 3. Completion means the batch was published, not that playback or the
latency guard passed.

## Comparison With October 30

Historical metrics at evaluation 1793361200 identify run_start 1793359027.
The new query at 1794113444.263 identifies run_start 1794053824, with flow/stage
push times 1794054641.644034 / 1794054646.763412. Values below are median
seconds and errors/runs, not raw server TTFB:

| flow | movie Oct 30 | movie Nov 7 | episode Oct 30 | episode Nov 7 |
| --- | --- | --- | --- | --- |
| list_load | 10.118952, 3/3 | 92.149758, 0/3 | 343.661371, 2/3 | 26.283003, 0/3 |
| sort_change | 30.393210, 3/3 | 85.425209, 0/3 | 36.425254, 3/3 | 24.448986, 0/3 |
| info_open | 0.041445, 0/3 | 0.053861, 0/3 | 0.048825, 3/3 | 0.037072, 0/3 |
| get_sources | 0.029544, 0/3 | 0.042807, 0/3 | 0.030021, 3/3 | 0.027547, 0/3 |
| materialise | 0.031143, 0/3 | 0.039514, 0/3 | 0.029920, 3/3 | 30.085435, 3/3 |
| play_materialised | 0.031667, 0/3 | 0.043975, 0/3 | 0.032538, 3/3 | 0.028753, 3/3 |

Errors fell from **23/36 to 6/36**, correcting the prior attempt's 31 total.
Movie browse now completes instead of failing; 92s/85s is still slow, but the
failed October samples cannot establish a successful-browse latency regression.
Episode browse durations are lower and errors disappear, though changed error
mix prevents a like-for-like speedup claim. Fast episode playback remains a
failure, not an improvement. The query contains eleven breach gauges, all 1;
get_sources/episode is absent, not a twelfth observed breach or an observed zero.

## Episode Target And Existing Fix

The rig outcome `gostream_register_fail` is synthetic, derived from HTTP
failure, not a server diagnosis. A historical 24h server-counter increase at
1794054700 reports episode materialise_then_play/magnet_dead_stale
4.007721359497076 and movie success 1.0026041666666665. These extrapolated,
aggregate counters corroborate dead/stale episode sources but do not identify
the retained target or prove the cause of each HTTP 500. No response body or
per-request server trace was recovered.

The distinct fix already filed by this task's prior attempt is
**ttfb-loadtime-episode-playable-target**, now landed as `020019b`. It replaces
first-child episode selection with a bounded, user-scoped 5-series x 5-season x
20-episode scan using existing availability states. Phantom/Materialised are
eligible; known unavailable/stale and missing states are skipped. Exhaustion
records no-playable-episode before measurement. Phantom is not a guarantee of
a healthy magnet; actual playback failures remain measured.

The repair receipt records 17 flow checks and 237 daily-workflow checks passing;
this analytical pass did not rerun or author those behavioral tests. Neither
new manifest executed that later repair. Its effect on the next scheduled batch
remains unobserved, not implied by availability filtering.

## Independent Successor

Preserved already-filed **ttfb-reduction-028**, independent of this report and
its fix. Append retries for both IDs returned already exists; no duplicate was
filed and no schedule moved. The successor retry requested 2026-11-09T04:51:08Z
(about +24h); rejection does not establish its existing exact schedule.

Day 028 should inspect manifests newer than 1794053824, verify their executed
checkout includes `020019b`, and compare durations AND errors to this completed
batch and October 30. If discovery fails, use the retained discovery_failure
reason. Check timeout records and the measurement wall separately. Movie browse
is the largest remaining successful-flow latency; preserve existing profiling
tasks rather than duplicating them. The earlier successor card's 31-error total
and definitive dead-target attribution are superseded by this report.

No behavioral regression test applies to analytical documentation. Exact probes
and validation results are in the private hive change doc. No operator action
is required.
