# TTFB Reduction 020

Diagnosis on 2026-10-28 after reading day 019. Documentation/enqueue only;
no runtime, deployment, measurement, or threshold changes.

## Measurement Provenance

Mimir evaluation 1793158647.791 = 2026-10-28T03:37:27.791Z is NOT the
measurement time. Run-start VALUE 1793099831 = 2026-10-27T11:17:11Z,
16.34h earlier. Flow-group push VALUE 1793100652.1563063 = 11:30:52.156Z;
stage-group push VALUE 1793100658.1108878 = 11:30:58.111Z. These align
with the October 27 daily Job (11:17:00Z to 11:31:02Z), replacing the
October 26 run-start 1793013431. This is a new retained batch, not a new
measurement executed by this diagnosis.

| Flow | Episode seconds | Movie seconds | Errors/runs, each type |
| --- | ---: | ---: | --- |
| list_load | 52.666402 | 87.422842 | 0/3 |
| sort_change | 0.294188 | 0.151312 | 0/3 |
| info_open | 0.035040 | 0.044675 | 3/3 |
| get_sources | 0.026815 | 0.035371 | 3/3 |
| materialise | 0.026131 | 0.028068 | 3/3 |
| play_materialised | 0.027199 | 0.035319 | 3/3 |

All twelve runs_total values are 3. Eight item-flow series have errors_total=3;
their fast responses are failures, not fast successful playback. Both synthetic
materialise_then_play outcomes report gostream_register_fail=1. This is the
driver's coarse curl-error label, NOT an identified gostream root cause.

## Comparison

019 episode list/sort 0.290105/0.188389s were -6.36%/-2.46% against
018 median-of-three 0.309806/0.193141s. 017 single-sample values were
0.193686/0.298508s. Changing the estimator between 017 and 018 and uncontrolled
cache/load variation prevent attribution to either estimator noise alone or
runtime optimization. 018, 019 and current source use median-of-three.

Today episode list is 181.54x 019 and movie list is 645.84x 019's 0.135362s.
Episode sort rises 56.16%; movie sort rises another 3.24% to 0.151312s after
019's 9.95% rise to 0.146568s. Movie info_open rises 12.85% from 0.039589s,
but now all its attempts fail. This is not stability. List/sort still time
root fetches, badge fanout and local DOM-helper work, not isolated TTFB or
browser percentiles. Retained medians lack per-sample counts/timing breakdown.

## Runtime Identity

The dev pod/image are unchanged: created October 22 08:02:54Z, running since
08:03:07Z with zero restarts. A fresh read of its deployed DLL identifies
0.5.7.7 revision d9a58ece9e7111f86663efe0c608147c099205a3; mtime precedes
process startup. This supports the deployed identity, but is not a new
in-memory assembly attestation. phantom_runtime_info still returns an empty
vector. Full pod/image identifiers and exact probes live only in the hive doc.
Do not substitute current source HEAD for the loaded plugin or the Job checkout.

## Guard And Durable State

Fresh retained stage exit-code/duration pairs: measure **0/821s**, push **0/0s**,
guard **3/6s**. Zero push seconds is integer-duration resolution, not no push.
Kubernetes success intentionally accepts guard=3; measure=0 also permits flow
errors as data. Neither signal is a latency/playback-health result.

The guard-detail fix has landed (cada4e4, merged through eb00368), and its real
effect is visible: seven retained breach=1 tuples, with observed/ceiling seconds:

| Flow/type | Observed | Ceiling |
| --- | ---: | ---: |
| list_load/episode | 52.666402 | 0.201845 |
| list_load/movie | 87.422842 | 0.145203 |
| sort_change/episode | 0.294188 | 0.197808 |
| sort_change/movie | 0.151312 | 0.136298 |
| info_open/movie | 0.044675 | 0.038513 |
| get_sources/movie | 0.035371 | 0.030854 |
| play_materialised/movie | 0.035319 | 0.028798 |

Thus movie sort is now an observed breach, not merely 019's candidate. No breach
on another tuple is NOT success when errors=3. Shared chart 2.12.0 and the Bound
thresholds claim remain wired into the CronJob. Flux activation and post-merge
verification previously proved first-final/second-initial registry byte equality
across replacement Jobs with no second-run reseeding. That historical proof is
not a newly run durability test. No registry was reseeded, read through a new
mount, loosened, or manually rewritten in this diagnosis.

## Available Failure Evidence

The daily pod has been removed, so its logs/checkout SHA and selected discovery
targets are unavailable. However, server logs survive: the 11:17-11:31 UTC
window contains 13,251 item-removal messages, a warmup reporting 16 root items
at 11:30:50.975Z, and two stream IDs each producing three
ResourceNotFoundException failures (11:27:25 and 11:30:52). Their order matches
the movie-then-episode driver, but without the Job's target log that mapping
is inference. Removal/warmup activity is a concrete workload/cache confounder
and possible stale-item explanation, not proven causation for either latency
or all eight failing flows. No HTTP status/body or per-request trace survived
for info_open/get_sources/materialise; do not call these gostream failures.

## Discovery And One Fix

ttfb-loadtime-episode-target-discovery landed at 8f233f5 before this Job.
Current source validates user-scoped Series -> Season -> Episode traversal;
the daily template clones main, so consumption is expected, not SHA-attested.
New guard metrics independently prove consumption of the guard-detail feature,
not the exact checkout or target IDs. Old episode IDs must not be reused.

One new task: **ttfb-loadtime-root-list-user-context**. In
tools/rig-scenarios/47-loadtime-flows.sh:539, pass the already resolved USER_ID
on EVERY timed movie/Shows-root page, including sort pages. Discovery already
does this, list/sort do not. PhantomMoviesChannel.cs:179-201 and
PhantomShowsChannel.cs:213-245 distinguish empty-user latest-media roots from
interactive catalogue/sort paths. The omission is a demonstrated fidelity gap;
which context the deployed token induced is not established by source alone.
Fixing it is not claimed to solve the observed item-removal storm or playback
failures. Keep labels, root scope, medians, pagination and thresholds unchanged.

Design and task are in the hive's docs/tasks directory. Approved script-test
Check: `./scripts/tests/p8-loadtime-flows.test.sh`. Require the real LIVE branch
against deterministic user-sensitive, multi-page fixtures for BOTH roots, with
all page users/sort parameters and badge IDs asserted; fail before/pass after.
Do not duplicate the already-landed discovery or guard-detail tasks.

## Successor

Append independent **ttfb-reduction-021**, weight 8, check=none, no dependencies,
not_before=2026-10-29T03:45:00Z (approximately +24h). Recheck retained VALUE
timestamps, all errors/runs, seven breach pairs and loaded identity. Follow the
new root-context fix if landed, but treat its changed workload separately from
runtime improvement. Investigate item-removal/stream-not-found evidence and
recover actual checkout/targets when available. Diagnose and enqueue one fix,
not implement it; continue the independent daily loop.

No behavioral regression test honestly applies to this analytical change.
Read-only probes and manual source/evidence review are recorded in the hive doc.
No operator action required.
