# TTFB Reduction 015

Analysis refreshed 2026-10-23 at approximately 04:13Z after reading
`docs/ttfb/ttfb-reduction-014.md`. Diagnose-and-enqueue only: no runtime changes.
An earlier attempt already filed this cycle's ONE fix and successor; this pass
restores the missing report without duplicating either task.

## Latest Measurement

Mimir returned `status=success`. Flow `push_time_seconds` VALUE
1792667833.6702983 identifies 2026-10-22T11:17:13Z. Query evaluation time
1792728764.072 is not the measurement timestamp: this retained daily sample is
16.93 hours old. All 12 error series are zero and all 12 run series are one;
the flow color label is green.

| Rank | Flow | Item Type | Seconds |
| --- | --- | --- | ---: |
| 1 | list_load | episode | 0.296525 |
| 2 | sort_change | episode | 0.289795 |
| 3 | sort_change | movie | 0.184649 |
| 4 | list_load | movie | 0.157037 |
| 5 | info_open | movie | 0.152598 |
| 6 | play_materialised | movie | 0.067518 |
| 7 | get_sources | movie | 0.066391 |
| 8 | materialise | movie | 0.061742 |
| 9 | info_open | episode | 0.061025 |
| 10 | get_sources | episode | 0.040369 |
| 11 | materialise | episode | 0.036465 |
| 12 | play_materialised | episode | 0.034605 |

Episode list is 0.009188s / 3.0054% below 014's 0.305713s; episode sort is
0.016028s / 5.2409% below 0.305823s. Movie info-open rose from 0.041991s to
0.152598s (+0.110607s / 263.4064%). Movie timings generally increased, so the
single observation does not justify another narrowly targeted runtime fix.
Recheck persistence rather than treating a noisy sample as a causal profile.

These are full-flow wall times, not isolated server TTFB or percentiles.
`tools/rig-scenarios/47-loadtime-flows.sh` times pagination, badges and DOM work;
the episode list/sort label represents the Shows ROOT, not a season's episode
listing. Synthetic zero errors do not establish universal playback reliability.

## Root Enrichment Landed and Loaded

`ttfb-shows-root-bulk-enrichment` is DONE at
`ce39fdd973a2125aa9f5c8df4591555e6d177317`, committed 2026-10-22T03:15:55Z.
It is an ancestor of the provided tracked tip and of the running plugin's source
revision `d9a58ece9e7111f86663efe0c608147c099205a3`.

The live inactive-color pod started 2026-10-22T08:02:54Z. Startup logs report
loaded plugin 0.5.7.7 and informational version
`0.5.7.7+d9a58ece9e7111f86663efe0c608147c099205a3`, with the same
`source.revision` and assembly MVID `9e8d6937-bf3d-4cbf-9cf8-2c06cccfd716`.
Live Ingress rules independently map the measured dev host to green. The hive
change doc records the image digest and exact deployment-specific probes.

Unlike 014, this observation is newer than the root fix and startup of a build
containing it. That supports deployed-code attribution, NOT a causal claim that
batching alone produced the measured improvement: cache/load and sample count
remain uncontrolled. Do not re-enqueue root or season batching.

## Daily Job and Stage Retention

The October 22 scheduled daily Job started 11:17:00Z and completed 11:17:23Z,
with succeeded=1 and CompletionsReached. The CronJob is not suspended, uses
concurrencyPolicy=Forbid, and reports that run as its latest success. October
19/20 failures predate SDK recovery; October 21 also succeeded. No new guard
tool error is evidenced. Do not enqueue another SDK repair.

Stage `push_time_seconds` VALUE is 1792667839.8688116, October 22 11:17:19Z.
Only guard exit=0 and duration=6s remain; measure/push and the run-start series
are absent. The old `record_stage` PUT replaced the entire grouping key each
time, destroying earlier stages. POST also replaces same-named families.

This cycle ALREADY enqueued `ttfb-loadtime-daily-stage-retention`, now DONE at
`a8dc94d731093f06f73f46be4f10a03de3b9965c` (October 23 03:15:04Z).
`tools/ci/loadtime-daily-run.sh:123-160` now accumulates completed stages in a
fresh per-run file and PUTs the full payload plus a stable run-start timestamp.
Its separate hive design and change record carry the executable Check
`./scripts/tests/p8-loadtime-daily-workflow.test.sh`, matching CHECKS.md's
approved `script-test` framework. The implementation/review record reports
31 passed / 2 failed before the repair and 33 passed / 0 failed after it,
including guard-failure retention. Those results are attributed, not rerun here.

The latest daily sample PRECEDES that fix. Missing stage series are therefore
not evidence against it. The CronJob's init container clones `REPO_REF=main`
anew per Job, so the next scheduled run can consume the change without a
consumer restart. Live retention remains unverified until a post-fix run;
016 must check all three stage pairs and the run-start value for freshness.
The current test checks cumulative dry-run payloads, not a real gateway's
retained state; do not describe it as a live Pushgateway test.

## One Fix and Successor

Preserve the already-filed stage-retention fix and its design, not a second fix.
Preserve `ttfb-reduction-016 [TODO]`, weight 8,
`not_before=2026-10-24T03:10:00Z`, approximately +24h from the original 015
attempt. Its existing design requires reading this report, comparing episode
list/sort against 0.296525s / 0.289795s, checking the movie info-open jump and
daily completion, and verifying retained stage series. Do not shift its gate
on a resumed attempt.

A candidate for a LATER cycle, not an additional task in this one, is the
wrapper's second measurement: `loadtime-daily-run.sh:244-261` invokes the
guard with `--live` instead of the already-published exposition. Guarding the
same batch would avoid different cache/materialisation state affecting the
decision. Threshold tightening in a disposable checkout and episode selection
depth are also previously documented limitations, not newly fixed behavior.

## Verification and Scope

No automated behavioral regression test honestly applies to this analytical
document (`check=none`). Read-only Mimir, Job, CronJob, Ingress, startup-log,
git-ancestry and arithmetic evidence is recorded in the hive change doc.
No code behavior, deployment, schema, configuration, ROI or gitlink changed.
No operator action is required.
