# TTFB Reduction 021

Diagnosis on 2026-10-29 after reading day 020. Documentation/enqueue only;
no runtime, deployment, measurement, or threshold changes.

## Measurement Provenance

The card's October 27 batch (run-start 1793099831) is SUPERSEDED. Mimir now
retains the October 28 batch: run-start VALUE 1793186224 = 2026-10-28T11:17:04Z;
flow/stage push VALUE 1793187516.068634 / 1793187522.8801506 =
11:38:36.069Z / 11:38:42.880Z. CronJob Job `...-29886437` ran
11:17:00Z-11:38:46Z (Complete 1/1). Query evaluation time (~03:50Z Oct 29)
is NOT the measurement time. No measurement was executed by this diagnosis.

| Flow | Episode s | Movie s | Errors/runs ep | Errors/runs mv |
| --- | ---: | ---: | --- | --- |
| list_load | 55.153632 | 34.577482 | 2/3 | 3/3 |
| sort_change | 24.156506 | 20.531805 | 3/3 | 3/3 |
| info_open | 0.037481 | 0.038588 | 3/3 | 0/3 |
| get_sources | 0.027468 | 0.027179 | 3/3 | 0/3 |
| materialise | 0.027840 | 0.713056 | 3/3 | 0/3 |
| play_materialised | 0.030149 | 0.031860 | 3/3 | 0/3 |

All runs_total=3. The error PATTERN inverted versus 020: movie item flows now
succeed 3/3 (020: 3/3 failed); episode item flows still fail 3/3; list/sort,
error-free in 020, now fail on 11 of 12 samples. Stage pairs: measure
**0/1292s** (020: 821s), push **0/0s**, guard **3/6s**. Job success is not a
latency or playback-health result.

## Changed Workload (account separately)

ttfb-loadtime-root-list-user-context (5100aec, merged 9d7dfb2 at
2026-10-28T03:51Z) landed before this Job, so the daily clone of main is
EXPECTED to carry it (not SHA-attested: Job pod removed). List/sort pages now
pass userId; the workload changed. Movie list fell 87.42->34.58s and sort rose
0.151->20.53s, episode list 52.67->55.15s, sort 0.294->24.16s. These shifts
are a measurement-definition change plus unexplained errors, NOT runtime
improvement or regression. Sort now traverses the user-scoped catalogue just
like list does, which plausibly explains the sort magnitude; unproven.

## Runtime Identity

Same rig pod phantom-library-green-0, image digest sha256:1d6d7602...8bd8,
started 2026-10-22T08:02:54Z, 0 restarts: loaded plugin unchanged since 020
(0.5.7.7 d9a58ec per 020's DLL read; not re-attested in memory).
phantom_runtime_info still returns an empty vector. Current source HEAD
68dfec6 is NOT the loaded plugin.

## Guard

Ten breach=1 tuples now (020: seven). Observed/ceiling seconds:

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

Ceilings unchanged (shared chart 2.12.0, Bound durable PVC, two-Job continuity
already proven). No reseeding, loosening, or registry rewrite. The movie
materialise 0.713s is the first successful materialise in this loop; its
breach vs a ceiling learned from fast FAILURES is a baseline artefact, not
a regression. Note list/sort ceilings were learned on the pre-userId workload.

## Failure Evidence

Job pod removed: no checkout SHA, target IDs, or per-sample log. Server
container logs now begin 2026-10-28T11:56Z (rotated), so the 11:17-11:38Z
window's item-removal/warmup/ResourceNotFound evidence is NOT available
(an apparent 16:17-16:39Z log window is kubectl local time -05:00, not the
Job). Causes of the list/sort errors cannot be determined: in
`_full_list_sample` an error is set either by a failed page GET or by ANY
failed `/Plugins/PhantomLibrary/States` badge POST, with stderr discarded and
only a binary errors count retained. 020's removal-storm/ResourceNotFound
link remains inference only.

## One Fix

**ttfb-loadtime-list-error-stage-attribution**: in
tools/rig-scenarios/47-loadtime-flows.sh `_full_list_sample`, record WHICH
stage failed (page_fetch vs badge_states) and the HTTP status (curl
`%{http_code}`, 000 for transport/timeout), emitted as retained push series
`phantom_loadtime_list_errors_total{flow,item_type,stage,http_code}` and a
log line. Not a duplicate: guard-breach-metric retains observed/ceiling, not
error cause. Check: `./scripts/tests/p8-loadtime-flows.test.sh`.

## Successor

ttfb-reduction-022, weight 8, check=none, no deps,
not_before=2026-10-30T03:45:00Z.

No behavioral regression test applies (analytical). No operator action.
