# TTFB Reduction 016

Analysis on 2026-10-24 after reading `ttfb-reduction-015.md` first.
Diagnose-and-enqueue only; no runtime changes.

## Latest Measurement

Mimir returned success at 2026-10-24T03:13:23Z. Flow `push_time_seconds`
VALUE 1792754226.9630458 identifies 2026-10-23T11:17:06Z, approximately
15.94 hours earlier. The query evaluation timestamp is not a new measurement.
All 12 error series are zero, all 12 run series are one, and color is green.
Both materialise-then-play outcome series report success=1.

| Rank | Flow | Item Type | Seconds |
| --- | --- | --- | ---: |
| 1 | list_load | episode | 0.324145 |
| 2 | sort_change | episode | 0.193382 |
| 3 | sort_change | movie | 0.137167 |
| 4 | list_load | movie | 0.137035 |
| 5 | info_open | movie | 0.047930 |
| 6 | info_open | episode | 0.044965 |
| 7 | play_materialised | movie | 0.040347 |
| 8 | materialise | episode | 0.035210 |
| 9 | get_sources | episode | 0.035106 |
| 10 | play_materialised | episode | 0.033785 |
| 11 | materialise | movie | 0.031382 |
| 12 | get_sources | movie | 0.029735 |

Episode list increased from 0.296525s by 0.027620s (+9.3146%); sort decreased
from 0.289795s by 0.096413s (-33.2694%). Episode list remains the slowest flow,
but these opposite movements do not establish a common runtime regression.
As in 015, list/sort labeled episode measure the Shows ROOT, not a season's
episode listing. Timings include pagination, badges and DOM work: full-flow
wall times, not isolated server TTFB, distributions or percentiles.

All six movie timings decreased from 015: list -12.7371%, sort -25.7147%,
info-open -68.5907%, sources -55.2123%, materialise -49.1724%, play -40.2426%.
Info-open is now 0.047930s versus the prior 0.152598s spike, near 014's
0.041991s. The broad movie slowdown did not persist in this sample, consistent
with transient load/cache noise rather than evidence for a targeted movie fix.
No controlled repeats or causal attribution were performed. Zero synthetic
errors are not proof of universal playback reliability.

## Deployed Build

Live Ingress still maps the measured dev host to green. Its ready pod was
created 2026-10-22T08:02:54Z; the container started 08:03:07Z, with zero restarts.
The image digest is unchanged from 015 (recorded in the infrastructure-side
change doc). Retained application startup logs independently identify plugin
0.5.7.7, informational version
`0.5.7.7+d9a58ece9e7111f86663efe0c608147c099205a3`, source revision
`d9a58ece9e7111f86663efe0c608147c099205a3`, and assembly MVID
`9e8d6937-bf3d-4cbf-9cf8-2c06cccfd716`.

Root batching `ce39fdd973a2125aa9f5c8df4591555e6d177317` is an ancestor of
that loaded revision. Both daily samples therefore observe the same loaded
plugin with root batching present. Do not re-enqueue root/season batching or
attribute day-to-day variation to a newly loaded plugin. The analysis checkout
tip `94f6e7ed9ccd2d851534cf41243f5bd8d2de4da5` is NOT the deployed revision.

## Daily Job and Retention Confirmed

The October 23 Job started 11:17:00Z, completed 11:17:17Z, succeeded=1 with
CompletionsReached. October 21 and 22 also succeeded; October 19/20 failures
remain historical, not evidence of a new SDK failure. CronJob is unsuspended,
concurrencyPolicy=Forbid, schedule `17 6 * * *`, timeZone unset; its latest
success matches the October 23 Job. Source is cloned from `REPO_REF=main` per
Job, so wrapper updates do not need a Jellyfin restart.

Stage retention fix `a8dc94d731093f06f73f46be4f10a03de3b9965c` landed
2026-10-23T03:15:04Z and is in the analysis checkout. Unlike 015, the retained
observation is newer than that fix and demonstrates its live intended effect:

| Stage | Exit Code | Seconds |
| --- | ---: | ---: |
| measure | 0 | 2 |
| push | 0 | 0 |
| guard | 0 | 6 |

Run-start VALUE 1792754224 = 2026-10-23T11:17:04Z; stage push VALUE
1792754232.9624944 = 11:17:12Z. These align with the completed Job and flow
push. All three pairs plus the stable run-start series survive in Mimir,
confirming retention beyond the pod lifetime. This does not exercise live
failure retention, nor identify the ephemeral checkout's exact SHA. Do not
enqueue another retention or SDK fix.

## One Fix Enqueued

Filed `ttfb-loadtime-daily-same-batch-guard`, weight 8, with its own hive design
doc and Check `./scripts/tests/p8-loadtime-daily-workflow.test.sh`, matching
the existing CHECKS.md `script-test` framework. No new registry stub is needed.

The confirmed defect is measurement attribution: the daily wrapper measures
batch A into EXPO and pushes it (`loadtime-daily-run.sh:214-237`), but then
invokes the guard with `--live` (`:244-262`). `loadtime-guard.sh:71-109` has no
recorded-input option and always measures batch B. Cache/materialisation state
can differ between batches; a green guard does not prove the published batch
passed. Its existing SAME measurement comment is misleading.

The fix should add `--exposition <file>` to the existing guard and supply EXPO
from both daily branches. Reuse converter/ratchet, preserve caller-owned input,
standalone measurement, live apply/filing, dry-run no-apply/no-file, stage
retention, and exit semantics. No parallel guard implementation or metrics
readback is needed. Acceptance requires the real wrapper, guard, converter and
ratchet with a counting engine fixture, scratch thresholds and controlled
external boundaries. Distinct A/B observations with opposite decisions must
prove one engine call and A-based publication/guarding for movie AND episode,
in both pass/breach directions. Forced guard exit codes or source grep alone
cannot prove this seam. The implementation must record fail-before/pass-after
results; this analytical task does not claim to have implemented that test.

## Successor and Scope

Filed `ttfb-reduction-017`, weight 8, check=none, held until
`2026-10-25T03:17:08Z` (24h from filing), with its own design doc. Read this
report first, compare episode list/sort against 0.324145s / 0.193382s, recheck
movie variability, identify the loaded build, and verify fresh daily success
and retained stages. Inspect the same-batch fix if landed, distinguishing a
merge from a Job that consumed it. Continue with exactly one fix and a delayed
018 successor. No dependency on the fix blocks the analytical daily loop.

No automated behavioral regression test honestly applies to this document
(`check=none`). Read-only telemetry, Job/CronJob, Ingress/pod, retained startup
log, ancestry and arithmetic checks are recorded in the hive change doc.
No deployment, plugin behavior, configuration, ROI or gitlink was changed.
No operator action is required.
