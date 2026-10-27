# TTFB Reduction 019

Analysis on 2026-10-27 (query 04:28:44Z), corroborated by review at
04:32:15Z, after reading `ttfb-reduction-018.md`.
Diagnosis/enqueue only; no runtime, deployment or threshold changes.

## Latest Measurement

Run-start VALUE 1793013431 = 2026-10-26T11:17:11Z. Flow-group push VALUE
1793013436.0143259 = 11:17:16Z; stage-group push VALUE 1793013440.1775858 =
11:17:20Z. Both are about 17.19h before the initial query and belong to the
October 26 run, not new measurements at query time. All twelve flow series
have runs_total=3 and errors_total=0; both materialise_then_play outcomes = 1.

| Flow | Episode s | vs 018 | Movie s | vs 018 |
| --- | ---: | ---: | ---: | ---: |
| list_load | 0.290105 | -6.36% | 0.135362 | +2.23% |
| sort_change | 0.188389 | -2.46% | 0.146568 | +9.95% |
| info_open | 0.042260 | +0.29% | 0.039589 | +5.28% |
| get_sources | 0.034553 | | 0.029086 | |
| materialise | 0.035893 | | 0.028458 | |
| play_materialised | 0.029275 | | 0.030760 | |

Aggregation vs runtime: 017 was single-sample, 018 and 019 are both
median-of-three, so 018->019 is the first pair using the same estimator.
Episode list/sort changed from 0.193686/0.298508s in 017 to
0.309806/0.193141s in 018 (+59.95%/-35.30%). The estimator changed, but
runtime/cache/load effects were not controlled; the shift cannot be attributed
solely to estimator noise. Today's 0.290105/0.188389s are near 018, not
controlled evidence of a plugin improvement. Unchanged build identity does
not fix those runtime variables.
Movie list/info remain near 018 (0.132408/0.037604s); movie sort +9.95%
(0.133304->0.146568s) is the largest relative move. It is a candidate for
the guard breach below, not an identified cause: breach detail was not retained.
These are synthetic full-flow wall times, not isolated server TTFB or browser
percentiles.

## Build Identity

The dev pod was created 2026-10-22T08:02:54Z and has zero restarts; its image
digest is unchanged from 017/018. Prior DLL evidence from 018 identified
plugin 0.5.7.7 revision `d9a58ece9e7111f86663efe0c608147c099205a3`;
this is not newly observed in-memory identity. The runtime identity metric
is defined in source (`PhantomFlowMetrics.RuntimeInfoName`), but querying
`phantom_runtime_info` still returns an empty vector. Concrete pod/image
identifiers, including the full digest, are retained only in the hive evidence
doc. The daily Job's ephemeral checkout SHA and selected target IDs were not
retained; do not infer them from current main.

## Daily Job Health

The October 26 scheduled Job started at 11:17:00Z and SUCCEEDED at 11:17:23Z
(first successful daily Job since October 24; October 25 failed guard=2).
Retained stage exit-code/duration pairs: measure 0/4s, push 0/1s, guard **3/4s**.
Run-start and both push values align with this Job, not stale October 25 data.
Exit 3 is a legitimate ratchet BREACH, not tool error 2: the durable registry
is now consumed. The wrapper intentionally does not fail the Job on 3, so
Kubernetes completion is not a guard=0 latency-health result.

## Durable Thresholds Activation

`flux:flux-phantom-loadtime-durable-thresholds` and its post-merge verification
have completed. The retained claim is Bound; the CronJob mounts it and sets
PHANTOM_LOADTIME_THRESHOLDS_PATH to the persistent registry. Four retained
verification Jobs on October 26 succeeded. The Flux evidence records two
replacement checkouts with identical first-final/second-initial registry bytes
and no reseeding; the later scheduled Job consumed the registry and returned
guard=3 instead of 2. This is actual cross-pod consumption, not merely a
source/chart merge. Deployment identifiers and registry path are in hive evidence.

## Diagnosis: Breach Detail Is Lost

The daily wrapper adds `--no-file` when `beehive` is not on PATH
(`tools/ci/loadtime-daily-run.sh:254-255`); the deployed rig image lacks beehive.
The guard detected a breach but filed no regression task, and per-flow detail
went only to the now-removed pod log. The retained signal is
stage_exit_code{stage="guard"}=3; breached flow/item_type, observed value and
ceiling cannot be recovered from it. Job completion alone hides that failure
from consumers that do not inspect the stage metric.

## Episode Discovery Fidelity

An earlier 019 attempt filed `ttfb-loadtime-episode-target-discovery`, now
completed in source at `8f233f55fccb7388e22510f306ded2dc587567a6`
(2026-10-27T03:34:38Z). It validates user-scoped Series->Season->Episode
traversal. This landed after the measured October 26 Job. The next scheduled
run is the first expected consumer, but its actual checkout/target evidence
must be checked, not assumed. Episode item-flow shifts may reflect changed
target selection rather than runtime improvement. List/sort still measure
channel roots, and curl-only playback outcomes remain synthetic.

## Existing Fixes

The later 019 attempt also filed `ttfb-loadtime-guard-breach-metric`: on guard
exit 3 retain per-breach flow/item_type, observed seconds and ceiling seconds
with the stage group; guard 0 clears the breach set. Check:
`./scripts/tests/p8-loadtime-daily-workflow.test.sh` (CHECKS.md script-test).
Its existing design now records the review's baseline harness failure:
incomplete fixtures are rejected by the publisher before reaching the guard.
Future regression proof must first use valid complete batches.

Thus 019's attempt history contains the completed discovery fix and the
subsequently filed guard-metric fix, not just one filing. This documentation
rework preserves both, adds no duplicate or third fix, and implements neither.

## Successor

The existing `ttfb-reduction-020` has weight 8, check=none, no dependencies,
and not_before=2026-10-28T03:35:00Z (approximately +24h). It remains independent
of either fix's completion. Compare discovery-fixed episode flows only after
confirming consumption, track movie sort 0.146568s, and inspect retained breach
series if landed. No duplicate successor was added.

No automated behavioral regression test applies to this analytical report
(check=none). Read-only observation evidence and the documentation rework's
validation are retained in the hive change doc; no operator action is required.
