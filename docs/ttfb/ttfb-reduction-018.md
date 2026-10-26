# TTFB Reduction 018

Analysis on 2026-10-26 after reading `ttfb-reduction-017.md` first.
Diagnosis/enqueue only; no runtime or deployment changes.

## Latest Measurement

Mimir query succeeded at 2026-10-26T03:23:25Z (1792985005.367).
Flow push VALUE 1792927048.0576928 identifies October 25 11:17:28Z,
16.0993 hours earlier. Evaluation time is not measurement time. All twelve
flow series have runs_total=3 and errors_total=0; both synthetic
materialise-then-play outcome series report success=1.

| Flow | Episode Seconds | Movie Seconds |
| --- | ---: | ---: |
| list_load | 0.309806 | 0.132408 |
| sort_change | 0.193141 | 0.133304 |
| info_open | 0.042139 | 0.037604 |
| get_sources | 0.033884 | 0.028773 |
| materialise | 0.034590 | 0.028963 |
| play_materialised | 0.029310 | 0.029644 |

Episode list rose 0.116120s (+59.9527%) from 0.193686s; sort fell
0.105367s (-35.2979%) from 0.298508s. Both remain inside the preceding
roughly 0.19-0.32s single-sample band, now under a different estimator.
This is not controlled evidence of a plugin regression or improvement.
Movie list/sort are stable (+0.1839%/+1.1818%); info-open fell 11.9757%
from 0.042720s to 0.037604s. Other movie flows also decreased from 017.
The older broad movie slowdown has not reappeared in this batch.

These are synthetic full-flow wall times including pagination, badge calls
and local DOM-helper work, not isolated server TTFB or browser percentiles.
Episode list/sort still measure the Shows root, not season episode browsing.
Source inspection also found the item-specific episode discovery stops at
the first series child (`47-loadtime-flows.sh:491-494`), although the channel
routes series to seasons (`PhantomShowsChannel.cs:263-269`). It can fall back
to the series itself. Thus zero synthetic errors cannot establish real
episode playback fidelity. This is a candidate for 019, not a second fix
filed by 018; repairing the broken daily guard takes priority today.

## Build and Measurement Identity

The same dev pod remains Ready with zero restarts: created October 22
08:02:54Z, process started 08:03:07Z, unchanged image digest from 017.
Installed DLL still contains version 0.5.7.7 and revision
`d9a58ece9e7111f86663efe0c608147c099205a3`; modification time is 07:22:12Z,
change time 08:03:07.675780233Z. This corroborates the prior loaded-build
identification; it is not a newly captured in-memory MVID. Querying
`phantom_runtime_info` still returned success with an empty vector.
Concrete pod/image/routing identifiers and exact commands are in the hive doc.

Median sampling `12b7d00bb0aad8df504e6ea2a3a6b0fead0d9680` landed October 25
03:26:35Z, before this Job. The wrapper defaults PHANTOM_LOADTIME_SAMPLES
to 3 and the engine aggregates per-flow samples. Unlike 017, all twelve
runs_total=3 now directly support Job consumption of multi-sampling. This
is median-of-three measurement, not a newly deployed plugin optimization.
The exact ephemeral checkout SHA was not retained: init clones main and
logs the ref only; the Job's pods are gone. Do not invent that identity.

## Daily Health and Retention

The latest daily Job is NOT green. It started October 25 11:17:00Z and
reached BackoffLimitExceeded at 11:17:32Z, failed=2, no completion time.
The last successful Job remains October 24 11:17:22Z. CronJob remains
unsuspended, Forbid, schedule `17 6 * * *`, timeZone unset.

| Retained Stage | Exit Code | Seconds |
| --- | ---: | ---: |
| measure | 0 | 5 |
| push | 0 | 0 |
| guard | 2 | 0 |

Run-start VALUE 1792927043 = October 25 11:17:23Z. Stage push VALUE
1792927048.1096566 = 11:17:28Z, immediately after flow publication.
All three pairs plus run-start survive pod removal, including the failing
guard. These align with the latest Job's final attempt; they are newer than
017 by one day, not old metrics mistaken for a fresh evaluation. Only the
latest attempt is retained; this does not reconstruct both failed attempts.
Fresh successful measurement/publication does not imply a healthy guard.

## Diagnosis and One Fix

Durable-threshold merge `35da569432251dc15b546c3f96a94ee3652fb137` landed
October 25 04:43:16Z, before the failing Job. The current helper
`tools/ci/loadtime-daily-state.py:50-52` deliberately rejects an unset
PHANTOM_LOADTIME_THRESHOLDS_PATH; the wrapper calls it at lines 270-272.
Invoking the helper with live mode and an empty path reproduced exit 2:
`loadtime daily threshold state error: PHANTOM_LOADTIME_THRESHOLDS_PATH required for live daily state`.
This was read-only and failed before opening a state file or running the guard.

The actual Job and CronJob have ONLY a src emptyDir, mounted at /src,
and no threshold-path environment variable or envFrom. The shared deployment
still pins chart 2.11.0, without thresholds values. The existing source task
explicitly delivered capability, not infrastructure activation. Thus main's
new state requirement outran deployment wiring: strong source/configuration
diagnosis for guard=2, not a claim to have recovered the deleted pod's log.
Do not revert to ephemeral ceilings or weaken fail-closed validation.

Enqueue exactly one fix in the owning infrastructure target:
`flux:flux-phantom-loadtime-durable-thresholds`. Provision retained storage,
publish/pin the capable chart, wire existingClaim/mountPath into the shared
daily singleton, and prove actual replacement Jobs consume prior state.
Use the existing helper/guard/locking, preserving history and movie/episode
independent ceilings. Keep all active writers on the same registry/lock.
No Jellyfin restart, plugin deployment or production role change is needed.

Check `tests/phantom-loadtime-durable-thresholds.test.sh` uses Flux CHECKS.md
integration-test: render actual pinned chart plus values, assert connected
PVC/mount/env and safety, require fail-before/pass-after. Its
Verify-After-Merge `tests/phantom-loadtime-durable-thresholds.test.sh --live`
must prove claim readiness, two sequential replacement Jobs reading retained
state without reseeding, fresh run-start/push/stages, and guard=0 or legitimate
breach=3, never tool-error=2. Rendering alone cannot pass live acceptance.

## Successor and Scope

Enqueue `ttfb-reduction-019`, weight 8, no dependencies, check=none, held until
2026-10-27T03:25:00Z (approximately +24h). Read this report first; compare the
new three-sample baseline, recheck movie stability and runtime identity, and
inspect real durable-state activation rather than source merge alone. Follow
the episode-discovery fidelity concern if still relevant. Continue with one
fix and a +24h 020 successor regardless of the infrastructure fix's status.

No behavioral regression test honestly applies to analytical documentation
(check=none). Read-only probes, arithmetic and the helper reproduction are
recorded in the hive change doc. No ROI, gitlink, shared checkout, deployment,
configuration or threshold state changed; no operator action is required.
