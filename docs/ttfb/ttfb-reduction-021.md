# TTFB Reduction 021

Diagnosis on 2026-10-29, after reading day 020 first. Analytical documentation
and enqueue only: no runtime, workload, deployment or threshold changes.

## Retained Batch

Evaluation 1793249485.926 (2026-10-29T04:51:25.926Z) is NOT measurement time.
Run-start VALUE 1793186224 is October 28 11:17:04Z, age 17.57h. Flow push
VALUE 1793187516.068634 and stage push VALUE 1793187522.8801506 are
11:38:36.069Z and 11:38:42.880Z. The daily Job ran 11:17:00Z-11:38:46Z.
This replaces October 27's 1793099831 batch; this pass ran no measurement.

All runs_total are 3. Values below are median-of-three seconds, INCLUDING
failed attempts, not successful-request percentiles or isolated TTFB.

| Flow | Episode seconds | Episode errors | Movie seconds | Movie errors |
| --- | ---: | ---: | ---: | ---: |
| list_load | 55.153632 | 2 | 34.577482 | 3 |
| sort_change | 24.156506 | 3 | 20.531805 | 3 |
| info_open | 0.037481 | 3 | 0.038588 | 0 |
| get_sources | 0.027468 | 3 | 0.027179 | 0 |
| materialise | 0.027840 | 3 | 0.713056 | 0 |
| play_materialised | 0.030149 | 3 | 0.031860 | 0 |

The synthetic movie outcome is success=1; episode is gostream_register_fail=1.
These are coarse curl-status outcomes, NOT validated media sources/stream bodies
or a gostream root-cause classification. Fast episode failures are not improvement;
movie curl success alone does not establish successful playback or stability.

Relative to day 020, episode list rises 4.72%, movie list falls 60.45%, while
episode/movie sort grows 82.11x/135.69x. The movie list's lower number consists
entirely of failed samples. Do not rank it as a latency win.

## Workload And Identity

Root-list user context landed at 5100aec (merged through 9d7dfb2), before the
October 28 Job. Current code sends the resolved userId on both roots, every
page and both sort modes. This corrects the workload toward the full interactive
catalogue, separately from plugin performance. The Job clones mutable main:
consumption is expected, but its exact checkout and selected targets were NOT
retained. Current HEAD 68f5b4b is not the Job SHA. That list-error attribution
change was committed October 29 03:55:07Z, AFTER this measured batch; its new
metrics cannot retroactively explain October 28. Typed Series -> Season ->
Episode discovery already exists at 8f233f5; do not duplicate it or reuse old IDs.

Dev still routes to the same pod created October 22 08:02:54Z, process/container
running since 08:03:07Z, zero restarts, same image digest as day 020. Fresh deployed
DLL read yields version 0.5.7.7 and revision
d9a58ece9e7111f86663efe0c608147c099205a3; mtime October 22 07:22:12Z precedes
startup. phantom_runtime_info is still an empty vector. This is container and
deployed-file identity evidence, not an in-memory assembly attestation. Full
deployment identifiers and commands are in the private hive change doc.

## Guard And Storage

Retained measure/push/guard exit-duration pairs are 0/1292s, 0/0s, 3/6s.
Job success accepts guard=3 and measure=0 accepts flow errors as data. Zero
push duration is integer resolution, not absence of a push. Both retained
push_failure_time_seconds values are zero.

Ten breach=1 tuples now replace the previous seven; observed/ceiling seconds:

| Flow/type | Observed | Ceiling |
| --- | ---: | ---: |
| list_load/episode | 55.153632 | 0.201845 |
| list_load/movie | 34.577482 | 0.145203 |
| sort_change/episode | 24.156506 | 0.197808 |
| sort_change/movie | 20.531805 | 0.136298 |
| info_open/episode | 0.037481 | 0.036792 |
| info_open/movie | 0.038588 | 0.038513 |
| materialise/episode | 0.027840 | 0.026654 |
| materialise/movie | 0.713056 | 0.029027 |
| play_materialised/episode | 0.030149 | 0.027743 |
| play_materialised/movie | 0.031860 | 0.028798 |

get_sources has no retained breach, but episode errors=3 still means failure.
Shared chart 2.12.0 and Bound durable thresholds PVC remain wired. Day 020's
historical two-replacement-Job byte-continuity proof remains valid evidence,
not a test rerun today. No reseeding, loosening, new mounts or registry writes.

## Failure Evidence

No daily Job pod remains. Container stdout retained only from 11:56:18Z, AFTER
the run: an empty earlier-window selection is missing coverage, not no errors.
The server's retained daily log does cover the window. Selecting timestamped
11:17-11:38 UTC entries gives 17,748 lines including 17,332 removal messages,
six channel-request errors on one root and five on the other. Representative
errors at 11:23:48.177 and 11:37:25.919 are DbUpdateException/Npgsql 23503:
BaseItemImageInfos violates FK_BaseItemImageInfos_BaseItems_ItemId. This
identifies concrete server failures, not their initiating client or deletion race.

Three stream-request errors at 11:38:35.902/.943/.985 use the same ID observed
on day 020 and raise ResourceNotFoundException from GetStreamingState. That
timing fits the final episode flow; mapping is still inferred without Job targets.
It does not explain all info/source/materialise failures or prove a gostream fault.

Warmup logged 16 root items at 11:13:40.157 (before the run), 6663 at
11:44:46.450 (after), then 17 at 11:58:36.356. No warmup-completion line occurs
inside the selected window. Current ChannelBaseItemWarmupWorker.cs:130-146
enumerates both roots without a user; its timer self-overlap guard does not
serialize HTTP clients. This explains why completion counts are not necessarily
the timed user catalogue; it does not prove warmup caused removals, foreign-key
errors, or stale targets. Loaded source is older than current HEAD. No cache,
database or service mutation was attempted as a speculative cure.

## One Fix And Successor

Enqueue **ttfb-loadtime-retained-run-evidence**: retain a bounded, atomic per-run
manifest outside ephemeral checkout/logs, with actual checkout SHA, run identity,
typed discovered targets and sanitized sample failure evidence. The current
time_flow discards stderr and loses individual HTTP/transport outcomes; stage
metrics retain neither SHA nor targets. Reuse existing list attribution, not a
parallel classifier. Keep target IDs out of normal metric labels and keep secrets,
raw bodies and arbitrary stderr out of artifacts. Separate artifacts from the
threshold registry; no baseline reset. The design and exact acceptance cases live
in the hive task doc. Approved script-test Check:
`./scripts/tests/p8-loadtime-flows.test.sh && ./scripts/tests/p8-loadtime-daily-workflow.test.sh`.
It must exercise the real LIVE engine through the daily wrapper with deterministic
HTTP failures, persistence across scratch deletion, and failure/dry-run safety.
This is not the already-landed root context/discovery/list-attribution work, nor
the existing loaded-plugin runtime-provenance task.

Independent **ttfb-reduction-022** already exists, no dependencies, weight 8,
check=none, not_before=2026-10-30T03:45:00Z (approximately +24h). The filing
command refused a duplicate; preserve that task and enrich its design doc. Read this
report first; recheck timestamps, identity, all errors/runs and breaches, recover
Job provenance if available, and distinguish changed measurement workload from
runtime improvement. Investigate retained FK/removal/stream evidence without
causal assumptions, then enqueue one nonduplicate fix and the next daily task.

No behavioral test honestly applies to this analytical documentation. Read-only
probes and manual evidence/source review are recorded in the hive doc. No operator
action required.
