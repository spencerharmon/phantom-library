# TTFB Reduction 017

Analysis on 2026-10-25 after reading `ttfb-reduction-016.md` first.
Diagnose-and-enqueue only; no runtime changes.

## Latest Measurement

Mimir query succeeded at 2026-10-25T04:22:03Z. Flow push VALUE
1792840634.5612185 identifies 2026-10-24T11:17:14Z, about 17.08 hours
earlier. Query evaluation time is not measurement time. All twelve errors
are zero and runs are one, color green; both materialise-then-play outcome
series report success=1.

| Rank | Flow | Item Type | Seconds |
| --- | --- | --- | ---: |
| 1 | sort_change | episode | 0.298508 |
| 2 | list_load | episode | 0.193686 |
| 3 | list_load | movie | 0.132165 |
| 4 | sort_change | movie | 0.131747 |
| 5 | info_open | episode | 0.045115 |
| 6 | info_open | movie | 0.042720 |
| 7 | materialise | episode | 0.034253 |
| 8 | get_sources | episode | 0.033800 |
| 9 | play_materialised | movie | 0.032357 |
| 10 | get_sources | movie | 0.031066 |
| 11 | play_materialised | episode | 0.030613 |
| 12 | materialise | movie | 0.030222 |

Episode list fell 0.130459s (-40.2471%) from 0.324145s; sort rose
0.105126s (+54.3618%) from 0.193382s. These opposing movements again
suggest variability, not evidence for one common runtime regression. Episode
list/sort still measure the Shows ROOT, not a season's episode listing.
These are synthetic full-flow wall times including pagination, badges and
local DOM-helper work, not isolated server TTFB or real-browser percentiles.

Movie info-open fell another 10.8700% from 0.047930s to 0.042720s, 72.0049%
below the earlier 0.152598s spike and near 014's 0.041991s. Movie list/sort,
materialise and play also decreased from 016; sources increased slightly
from 0.029735s to 0.031066s. The earlier broad movie slowdown is not
persistent in these observations. No controlled causal experiment was run;
zero synthetic errors do not establish universal playback reliability.

## Build and Measurement Identity

The dev Ingress still selects green. The ready pod is unchanged since
October 22 08:02:54Z; its process started 08:03:07Z with zero restarts and
the same image digest as 016 (infrastructure identifiers remain in the hive
change doc). The installed DLL contains version 0.5.7.7 and revision
`d9a58ece9e7111f86663efe0c608147c099205a3`; its modification/change times
predate or coincide with process startup. Together with 016's actual startup
provenance, this corroborates the same loaded build. The old startup log has
expired and `phantom_runtime_info` returned an empty vector, so this pass
does NOT claim a newly observed in-memory MVID/provenance event. Root batching
`ce39fdd973a2125aa9f5c8df4591555e6d177317` is an ancestor of that revision.

Same-batch guard merge `b05613a7f497c2a7d543aa69435ea8b7eb45607a` landed
October 24 04:33:34Z, before this Job. Current wrapper lines 254-258 pass the
published EXPO to the real guard; its supplied-input path bypasses the engine.
The recorded review ran 89 assertions successfully. The Job cloned `main`
after the merge, so its declared source path should consume the fix, unlike
016's older Job. However the pod is gone, the checkout log records only a ref,
and stage metrics contain no checkout SHA or engine-call count: timing and
configuration support consumption, not direct proof of that ephemeral SHA.
The guard's duration fell from 6s to 4s; that alone does not prove identity.

Analysis HEAD `12b7d00bb0aad8df504e6ea2a3a6b0fead0d9680` introduces per-flow
N-sample median measurement, default three. It landed October 25 03:26:35Z,
AFTER the observed Job. This observation has runs=1 and must not be called
a median-of-three result. The next daily Job can consume it without a
Jellyfin restart; 018 must inspect sample counts and distinguish the changed
measurement method from a plugin performance improvement.

## Daily Health and Retention

October 24 Job started 11:17:00Z, completed 11:17:22Z, succeeded=1,
CompletionsReached. October 22/23 are also successful; October 19/20 failures
remain old history. CronJob is unsuspended, Forbid, schedule `17 6 * * *`,
timeZone unset, with last success matching this Job.

| Stage | Exit Code | Seconds |
| --- | ---: | ---: |
| measure | 0 | 2 |
| push | 0 | 0 |
| guard | 0 | 4 |

Run-start VALUE 1792840632 = October 24 11:17:12Z. Stage push VALUE
1792840638.5256298 = 11:17:18Z. All three pairs and run-start survive in
Mimir after pod removal and align with flow push and completed Job. The
sample is newer than 016 by one daily run, not a stale evaluation timestamp.
This confirms success-path retention, not live failure retention or durable
cross-day regression protection.

## One Fix Enqueued

Filed `ttfb-loadtime-daily-durable-thresholds`, weight 8, with a separate
hive design doc and Check `./scripts/tests/p8-loadtime-daily-workflow.test.sh`
(existing CHECKS.md `script-test`). This is not a duplicate of same-batch
guarding or median sampling; 016 explicitly left persistence out of scope.

The live Job and CronJob have only an `emptyDir` source volume. Every Job
clones into it. The daily wrapper applies the guard without overriding its
checkout-local `tools/perf/loadtime-thresholds.json`; `Program.cs:42-47`
saves there through `Model.cs:101`'s `File.WriteAllText`. All twelve shipped
ceilings are zero, which `RatchetEngine.cs:127-145` seeds rather than treats
as breaches. Learned ceilings disappear with the checkout. Thus a green
guard can repeatedly accept a fresh baseline instead of comparing days.
This is source/configuration diagnosis, not a claim to have recovered the
deleted Job's threshold file.

Reuse the existing guard's `--thresholds` with a configured durable path,
generic chart existing-claim wiring, absent-only initialization and atomic
updates. Corrupt/unwritable state must fail visibly, never silently reseed.
Keep actual claim provisioning on the infrastructure side. Preserve the
same-batch invariant, stage retention and dry-run no-write/no-file behavior.
Do not auto-commit runtime measurements or add a parallel ratchet.

Acceptance must exercise real wrapper/guard/converter/ratchet across three
distinct scratch checkouts sharing ONLY state: list_load 1.0s seeds 1050ms,
0.5s tightens to 525ms, then 0.8s breaches without loosening (guard=3,
wrapper=0). Cover movie and episode independently, captured publication,
stage/filing boundaries, missing/corrupt/unwritable state and dry-run byte
identity. Render the chart to assert the claim/path/env connects to the same
tested entrypoint. Require fail-before/pass-after, not source grep or an
isolated persistence helper. Infrastructure activation and actual next-Job
consumption must be distinguished from a source/chart merge.

## Successor and Scope

Successor `ttfb-reduction-018` was already filed (hive commit `d446cc53d1`),
weight 8, check=none, no deps, held until `2026-10-26T03:20:00Z`, roughly
23 hours after this pass. The attempted append was safely refused as an
existing task; preserve that one successor rather than duplicate it or
rewrite its card. Its separate design doc exists in the hive.

018 should read this report first, compare 0.193686s / 0.298508s episode
list/sort and 0.042720s movie info, identify loaded build separately from
measurement source, verify daily freshness/retention, and check median
sample counts and durable-threshold activation if landed. Continue with
one concrete fix and a roughly +24h 019 successor, independent of repair
completion.

No behavioral regression test honestly applies to this analytical document
(`check=none`). Actual read-only probes and arithmetic are recorded in the
hive change doc. No deployment, configuration, ROI or gitlink changed; no
operator action is required.
