# TTFB Reduction 023

Diagnosis on 2026-10-31 after reading day 022 first. Diagnose-and-enqueue only:
no runtime, workload, threshold, or deployment changes.

## New Retained Batch

Run-start VALUE 1793359027 = 2026-10-30 11:17:07Z (newer than day 022's
1793272624). Measure/push/guard exit/seconds: 0/2142, 0/0, 3/5. runs_total=3 per
flow/type; values are median-of-three wall durations INCLUDING failures.

| Flow | Episode s | Err | Movie s | Err |
| --- | ---: | ---: | ---: | ---: |
| list_load | 343.661371 | 2 | 10.118952 | 3 |
| sort_change | 36.425254 | 3 | 30.393210 | 3 |
| info_open | 0.048825 | 3 | 0.041445 | 0 |
| get_sources | 0.030021 | 3 | 0.029544 | 0 |
| materialise | 0.029920 | 3 | 0.031143 | 0 |
| play_materialised | 0.032538 | 3 | 0.031667 | 0 |

Movie list 150.90s -> 10.12s is NOT an improvement: all three samples failed
(errors 2 -> 3), so it measures faster failure. Episode list worsened 1.42x.
All list/sort error tuples are stage=page_fetch, http_code=500. All twelve
guard breaches = 1; ceilings unchanged (pre-user-context baseline, do not reseed).
Episode item flows still fail 3/3 (rig cause gostream_register_fail = coarse
materialise curl failure, as day 022 established).

## Is The Fix Loaded? No.

ttfb-warmup-userless-root-churn is DONE at 80a119a (merged Oct 30 04:02Z), but
the dev pod phantom-library-green-0 is still the 2026-10-22T08:02:54Z pod, 0
restarts, same image digest as day 021/022. Its plugin DLL still reads 0.5.7.7 /
d9a58ece9e7111f86663efe0c608147c099205a3. Landed code != loaded runtime.

## Server Log Window (log_20261030.log, 11:17:00-11:53:00 UTC)

25,544 lines; 24,987 "Removing item" messages (Movie 19,499, Series 5,460,
Episode 26, Folder 2) - same shape as day 022's 22,999. 14 request errors.
No 40P01/23503 strings matched in-window this time, but the warmup tick failed
at 11:28:12.804 (ChannelBaseItemWarmupWorker). Churn reproduced on the unfixed
plugin, consistent with day 022's root cause.

## One Fix And Successor

Fix enqueued: **ttfb-warmup-fix-dev-load** - get a plugin build containing
80a119a loaded in dev (bump, build, roll, restart consumer), proving DLL
revision in the running pod. Nonduplicate: the code fix is done; its LOAD was
never tasked. Successor **ttfb-reduction-024** not_before ~+24h, no deps.
