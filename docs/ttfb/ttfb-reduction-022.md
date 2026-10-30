# TTFB Reduction 022

Diagnosis on 2026-10-30 after reading day 021 first. Diagnose-and-enqueue only:
no runtime, workload, threshold, or deployment changes.

## New Retained Batch

Evaluation 1793335897.246 is query time, NOT measurement time. Run-start VALUE
1793272624 is October 29 11:17:04Z (17.576h old at evaluation), newer than
day 021's 1793186224. Flow/stage push VALUEs are 1793274780.7748363 and
1793274786.424604 (11:53:00.775Z / 11:53:06.425Z). Job window is
11:17:00Z-11:53:10Z. No new measurement was run in this pass.

Every flow/type has runs_total=3. Seconds are median-of-three wall durations,
INCLUDING failures, not successful-request percentiles or isolated TTFB.

| Flow | Episode seconds | Errors | Movie seconds | Errors |
| --- | ---: | ---: | ---: | ---: |
| list_load | 241.542937 | 2 | 150.896953 | 2 |
| sort_change | 46.203177 | 3 | 32.566501 | 3 |
| info_open | 0.038725 | 3 | 0.119823 | 0 |
| get_sources | 0.029661 | 3 | 0.030897 | 0 |
| materialise | 0.029335 | 3 | 0.031208 | 0 |
| play_materialised | 0.034384 | 3 | 0.031229 | 0 |

Episode/movie list medians are 4.379x/4.364x day 021; sort is 1.913x/1.586x.
Neither list median represents successful completion (two failures of three).
Movie materialise falling from 0.713056s to 0.031208s does not establish a
first-success improvement: repeated item flows and curl-only success do not
validate source contents or playback. Episode's fast failures are not wins.

## Provenance And Attribution

The ephemeral Job pod is gone, but its durable manifest was recovered through
a temporary READ-ONLY mount of the existing evidence volume. No registry write
or reseed was performed. Attempt 67ebf5978e8e4c4d9b5981351cd92b4b records
checkout **861df603cb53b736dc783c8d5c984f0e4558c11a**, run_start 1793272624,
status complete, exit_code 0; discovery succeeded in 162s. The manifest retains
typed Movie/Series/Season/Episode targets and 22 sanitized failure records.
Exact target IDs and deployment identifiers are confined to the private hive doc.

This proves consumption of root user context, typed episode discovery, list
stage attribution (68f5b4b), and retained evidence (861df60). The mutable main
checkout is no longer an inferred SHA for this batch.

All four nonzero phantom_loadtime_list_errors_total tuples have
stage=page_fetch,http_code=500: list_load episode/movie=2/2; sort_change=3/3.
The manifest identifies list samples 2 and 3 and all three sort samples for
both types, curl exit 22. No badge_states or transport attribution was retained.
All twelve episode item-flow attempts are HTTP **404**, curl exit 22, against
the same discovered Episode target. The three stream-log errors match that ID.
Synthetic cause=gostream_register_fail is therefore NOT evidence of a gostream
registration fault; it is the driver's coarse name for materialise curl failure.
Movie synthetic cause=success likewise does not validate sources/stream bodies.

## Guard And Workload

Measure/push/guard exit-duration pairs are 0/2156s, 0/0s, 3/6s. Both retained
push_failure_time_seconds values are zero. Successful orchestration accepts flow
errors and guard exit 3; zero push seconds is integer-resolution timing.

All twelve breach flags are 1. Observed values equal the table above (apart from
floating-point serialization); retained ceilings in seconds are:

| Flow | Episode ceiling | Movie ceiling |
| --- | ---: | ---: |
| list_load | 0.201845 | 0.145203 |
| sort_change | 0.197808 | 0.136298 |
| info_open | 0.036792 | 0.038513 |
| get_sources | 0.028156 | 0.028538 |
| materialise | 0.026654 | 0.029027 |
| play_materialised | 0.027743 | 0.028798 |

List/sort ceilings were learned BEFORE the user-context workload correction.
Keep that discontinuity separate from runtime performance; do not loosen or
reseed. Shared chart 2.12.0 still mounts the Bound durable thresholds volume.
Day 020's two-Job byte-continuity proof is historical, not rerun here.

## Runtime And Server Evidence

Dev pod remains the October 22 pod, process started 08:03:07Z, zero restarts,
same image digest as day 021. A fresh DLL read yields 0.5.7.7 and revision
d9a58ece9e7111f86663efe0c608147c099205a3. phantom_runtime_info remains [].
This is deployed-file/container identity, NOT an in-memory assembly attestation.

Container stdout now begins October 29 13:25:09Z, AFTER the Job; its outer
kubectl timestamps display -05:00. The daily log's bracket timestamps are UTC
and still cover the Job. Capture used the exact 11:17:00-11:53:10 UTC window:
23,597 timestamped lines, 22,999 removal messages (17,489 Movie, 5,482 Series,
26 Episode, 2 Folder). These are log-message counts, not distinct removed rows.
An initial broader minute-range probe through 11:54 produced 25,243 removals;
do not mix that count, or the prior design's 24,006, with this exact window.

There are five request errors for each channel root and three for the episode
stream. Sampled root exceptions at 11:33:50.212 and 11:48:46.862 are Npgsql
23503, FK_BaseItemImageInfos_BaseItems_ItemId. At 11:26:15.289 the warmup tick
fails with 40P01 deadlock while deleting a BaseItemProviders tuple. Warmup
completed with 17 items at 11:13:40.160 before the Job and 9,457 at
11:56:35.482 afterward. No successful warmup completion occurs inside it.
The sampled stream exception at 11:53:00.384 is ResourceNotFoundException in
GetStreamingState. No matching target-removal log was found in that hour;
missing evidence must not be presented as proof of a particular deletion race.

## One Fix And Independent Successor

Preserve **ttfb-warmup-userless-root-churn**, already filed by a previous
attempt of this diagnosis. Its full design and CHECKS.md-approved dotnet-test
Check are in the hive. Both add attempts (fix and day 023) refused duplicates;
do not file another fix merely to satisfy this pass's bookkeeping.

The fix is already committed at 80a119a51b2aad10e3398959431d8603ee423786
(October 30 04:02:21Z): ChannelWarmupScope marks the actual worker calls,
both channels choose the canonical full root in that scope, and their cache
keys isolate it from Home Latest. Latest outside the scope remains bounded.
Review recorded four passing worker/channel tests and two failing-before
channel assertions. That evidence was read, not rerun in this analytical pass.
The old userless-root behavior is a concrete churn mechanism; matching 500
counts plus sampled exceptions support it, but do not prove every failure's
causal chain. The loaded plugin predates this fix: no live latency/removal
improvement can yet be attributed to it. Deployment is outside this diagnosis.

**ttfb-reduction-023** already exists independently, deps empty, weight 8,
check=none, not_before=2026-10-31T03:45:00Z (approximately +24h). Read this
report first, recover the next manifest and server window, and distinguish
landed code from loaded runtime before assessing churn or latency changes.
No behavioral regression test honestly applies to this analytical report.
Manual read-only evidence and commands are in the hive change doc. No operator
action is required by this pass.
