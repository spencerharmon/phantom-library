# TTFB Reduction 017

Analysis on 2026-10-25 after reading `ttfb-reduction-016.md` first.
Diagnose-and-enqueue only; no runtime changes.

## Latest Measurement

Mimir query (2026-10-25 ~03:20Z). Run-start VALUE 1792840632 =
2026-10-24T11:17:12Z; flow push_time VALUE 1792840634.56 = 11:17:14Z, stage
push_time 1792840638.53 = 11:17:18Z. Fresh (~16h old), one day after 016's
2026-10-23T11:17:06Z sample. All 12 error series 0, all 12 run series 1, color
green, both materialise_then_play rig outcomes 1.

| Flow | Item | 016 (s) | 017 (s) | Delta |
| --- | --- | ---: | ---: | ---: |
| list_load | episode | 0.324145 | 0.193686 | -40.25% |
| sort_change | episode | 0.193382 | 0.298508 | +54.36% |
| list_load | movie | 0.137035 | 0.132165 | -3.55% |
| sort_change | movie | 0.137167 | 0.131747 | -3.95% |
| info_open | movie | 0.047930 | 0.042720 | -10.87% |
| info_open | episode | 0.044965 | 0.045115 | +0.33% |
| get_sources | episode | 0.035106 | 0.033800 | -3.72% |
| get_sources | movie | 0.029735 | 0.031066 | +4.48% |
| materialise | episode | 0.035210 | 0.034253 | -2.72% |
| materialise | movie | 0.031382 | 0.030222 | -3.70% |
| play_materialised | episode | 0.033785 | 0.030613 | -9.39% |
| play_materialised | movie | 0.040347 | 0.032357 | -19.80% |

The two episode (Shows ROOT) flows swapped magnitudes: list fell ~0.13s and
sort rose ~0.105s, with the same loaded plugin. Their sum moved only
0.517527 -> 0.492194s (-4.9%). Over 015/016/017 the episode list read
0.296525/0.324145/0.193686 and sort 0.289795/0.193382/0.298508: a ~0.19-0.32s
band that each flow visits in turn. This is single-sample noise (first-hit
cache/page warmth landing on whichever request runs first), not a regression
or a win. Movie flows are stable; info_open 0.042720s confirms 015's 0.152598s
was a transient spike (014 0.041991, 016 0.047930).

## Loaded Build

Green pod `phantom-library-green-0` creationTimestamp 2026-10-22T08:02:54Z,
0 restarts, image digest unchanged
(`jellyfin-phantom@sha256:1d6d7602...`). Same pod/image as 016, whose retained
startup log identified plugin 0.5.7.7 / revision `d9a58ece9e71...`. Today the
Oct-22 startup log has rotated out (only 20261023..25 retained), so the build
is identified by pod+digest continuity, not a fresh log line.
`phantom_runtime_info` (87a022b) returns no series yet: that exporter is not in
the loaded build, as expected.

## Daily Job and Retention

Job `phantom-library-loadtime-daily-29880677`: start 2026-10-24T11:17:00Z,
complete 11:17:22Z, Complete 1/1. Oct 22/23 also Complete; Oct 19/20 Failed are
historical. Retained stage pairs: measure 0/2s, push 0/0s, guard 0/4s, plus a
fresh run-start series. Retention continues to work.

## Same-Batch Guard: Landed and Consumed

`ttfb-loadtime-daily-same-batch-guard` commit `40adfc7` merged into main via
`b05613a` at 2026-10-24T04:33:34Z. The Job clones `REPO_REF=main` fresh, and
started 11:17:00Z, ~6.7h after the merge, so it consumed the fix. Consistent
corroboration: guard stage time dropped 6s -> 4s (no second engine batch).
The pod's log is gone, so the exact cloned SHA is inferred from timing, not
logged. The guard now judges the published batch -- which makes the
single-sample noise above the next limiting factor: a ratchet/guard decision
on one sample per flow can flip on cache order alone.

## One Fix Enqueued

`ttfb-loadtime-median-of-n-samples`: `47-loadtime-flows.sh` emits every flow
with `runs=1` (`emit_record ... 1`, lines ~362/418/475). Add a configurable
sample count (default e.g. 3, daily wrapper passing it) and emit the median
duration with `runs=N` and summed errors, keeping the exposition schema.
Check: `./scripts/tests/p8-loadtime-flows.test.sh` (CHECKS.md `script-test`).
No plugin optimisation is enqueued: no flow shows a sustained regression.

## Successor

`ttfb-reduction-018`, check=none, held ~+24h. Compare against this table
(episode list 0.193686s, sort 0.298508s) and, if the median fix landed and a
Job consumed it, note runs_total=N and whether the episode band narrowed.
