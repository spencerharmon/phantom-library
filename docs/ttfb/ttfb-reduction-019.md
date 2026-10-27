# TTFB Reduction 019

Analysis on 2026-10-27 (query 04:28:44Z) after reading `ttfb-reduction-018.md`.
Diagnosis/enqueue only; no runtime, deployment or threshold changes.

## Latest Measurement

Run-start 1793013431 = 2026-10-26T11:17:11Z; flow push 1793013440 =
11:17:20Z, 17.19h before query. All twelve series runs_total=3,
errors_total=0, color=green; both materialise_then_play outcomes = 1.

| Flow | Episode s | vs 018 | Movie s | vs 018 |
| --- | ---: | ---: | ---: | ---: |
| list_load | 0.290105 | -6.36% | 0.135362 | +2.23% |
| sort_change | 0.188389 | -2.46% | 0.146568 | +9.95% |
| info_open | 0.042260 | +0.29% | 0.039589 | +5.28% |
| get_sources | 0.034553 | | 0.029086 | |
| materialise | 0.035893 | | 0.028458 | |
| play_materialised | 0.029275 | | 0.030760 | |

Aggregation vs runtime: 017 was single-sample, 018 and 019 are both
median-of-three, so 018->019 is the first like-for-like pair. The 017->018
episode list/sort swap (+60%/-35%) was estimator noise, not runtime change:
019 episode list/sort (0.290/0.188) sit next to 018 (0.310/0.193). The
loaded plugin is unchanged (below), so no runtime improvement is claimed.
Movie list/info stable within ~5%; movie sort +9.95% (0.1333->0.1466s) is
the largest move and likely the guard breach below, but unattributable
because the breach detail was not retained.

## Build Identity

Dev pod `phantom-library-green-0`: created 2026-10-22T08:02:54Z, restarts 0,
image digest `sha256:1d6d7602...8bd8` unchanged from 017/018, i.e. still
plugin 0.5.7.7 revision d9a58ece (018 DLL corroboration). The runtime
identity metric is defined in source (`PhantomFlowMetrics.RuntimeInfoName`)
but the live Mimir query still returns an empty vector.

## Daily Job Health

`phantom-library-loadtime-daily-29883557` started 2026-10-26T11:17:00Z,
SUCCEEDED 11:17:23Z (first green since Oct 24; Oct 25 failed guard=2).
Retained stages: measure 0 (4s), push 0 (1s), guard **3** (4s).
Exit 3 = legitimate ratchet BREACH, not tool error 2: the durable registry
is now read. The wrapper intentionally does not fail the Job on 3.

## Durable Thresholds Activation

`flux:flux-phantom-loadtime-durable-thresholds` and its verify-after-merge
are DONE. Live: PVC `phantom-loadtime-thresholds` Bound (1Gi, 24h old);
CronJob mounts it and sets PHANTOM_LOADTIME_THRESHOLDS_PATH=
/var/lib/phantom-loadtime/loadtime-thresholds.json. Four `loadtime-durable-*`
verification Jobs (Oct 26 04:36-04:41Z) succeeded, then the scheduled Job
consumed the registry (guard ran 4s, returned 3 instead of 2). Activation
is confirmed by a real Job, not merely a chart merge.

## Diagnosis: breach is silent

In the Job the wrapper adds `--no-file` when `beehive` is not on PATH
(`tools/ci/loadtime-daily-run.sh:254-255`); the rig image has no beehive.
So the guard detected a breach, filed no `p8-daily-perf-regression-*` task
(none exist in PLAN), and its per-flow detail went only to the pod log,
which is GC'd. The only durable signal is stage_exit_code{guard}=3. The
breached flow/item_type, observed value and ceiling are unrecoverable.
The P8 contract "a breach is never silently accepted" is violated in prod.

## Episode discovery fidelity

Resolved in source: `ttfb-loadtime-episode-target-discovery` (8f233f5,
2026-10-27T03:34:38Z) walks series->seasons. It landed after the Oct 26
Job, so first consumption will be the Oct 27 11:17Z run; expect an episode
get_sources/materialise/play shift that is target change, not runtime.

## One Fix

`ttfb-loadtime-guard-breach-metric`: on guard exit 3 the daily wrapper must
push a retained per-breach series (`phantom_loadtime_guard_breach{flow,
item_type}` with observed and ceiling seconds) to the same retained
Pushgateway group as stage metrics, and clear it on guard 0. Check:
`./scripts/tests/p8-loadtime-daily-workflow.test.sh` (script-test).

## Successor

`ttfb-reduction-020`, weight 8, check=none, not_before 2026-10-28T04:30Z,
independent of the fix. Compare first discovery-fixed episode flows, check
movie sort 0.146568s, read breach series if landed.
