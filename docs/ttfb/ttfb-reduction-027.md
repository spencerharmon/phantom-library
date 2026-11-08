# TTFB Reduction 027

Diagnosis on 2026-11-08 UTC after reading day 026 first. Diagnose-and-enqueue
only: no runtime, deployment, measurement or threshold changes.

## Discovery Repaired; Measurement Resumed

Read-only evidence probe, manifests newer than run_start 1793967449:

| run_start | checkout | status | discovery | measure | failures |
| --- | --- | --- | --- | --- | --- |
| 1794035653 | f522e67 | failed, exit 1 | ok, 26s | exit 124 at 1500s | 11 page_fetch curl 28 (http 000), list_load/sort_change, movie+episode |
| 1794053824 | cbc9edf | complete, exit 0 | ok, 21s | ok, 817s; guard exit 3 | 6 episode: materialise http 500 x3, play_materialised http 400 x3 |

Both resolved color=green with typed Movie/Series/Season/Episode targets, so
`7026ebf` (kubectl in-cluster config) fixed discovery. No discovery_failure
reason was recorded on either manifest.

## Did the 7e28c4a request bounds fire?

Yes, in run 1794035653: every list_load/sort_change page fetch ended with
curl exit 28 (transport/no-response) instead of hanging, i.e. the per-request
bound fired. The bounds did not save that batch: the cumulative measure stage
still hit its 1500s wall (exit 124), no sample was pushed. In the completed
run 1794053824 no curl 28 occurred; the bounds did not fire.

## Comparison to October 30 (run_start 1793359027)

Live Mimir now holds run_start 1794053824 (a genuinely new batch). Seconds and
errors/runs, green:

| flow | movie Oct 30 | movie Nov 7 | episode Oct 30 | episode Nov 7 |
| --- | --- | --- | --- | --- |
| list_load | 10.12s, 3/3 err | 92.15s, 0/3 | 343.66s, 2/3 | 26.28s, 0/3 |
| sort_change | 30.39s, 3/3 | 85.43s, 0/3 | 36.43s, 3/3 | 24.45s, 0/3 |
| info_open | 0.041s, 0/3 | 0.054s, 0/3 | 0.049s, 3/3 | 0.037s, 0/3 |
| get_sources | 0.030s, 0/3 | 0.043s, 0/3 | 0.030s, 3/3 | 0.028s, 0/3 |
| materialise | 0.031s, 0/3 | 0.040s, 0/3 | 0.030s, 3/3 | 30.09s, 3/3 |
| play_materialised | 0.032s, 0/3 | 0.044s, 0/3 | 0.033s, 3/3 | 0.029s, 3/3 |

Errors fell from 31 to 6. Movie list_load/sort_change now *complete*; the Oct
30 movie numbers were fast failures, so 92s/85s is the first honest movie
browse latency, not a regression. Episode browse improved 13x/1.5x. All twelve
guard breaches remain 1 (ceilings are 0.03-0.20s).

## Remaining Episode Failure: Dead Rig Target

The rig's own label says `gostream_register_fail`, which is synthetic. The
real server series `phantom_playback_outcome_total` over 24h shows
materialise_then_play episode cause=magnet_dead_stale (~4) and movie
success (~1). The discovered Episode (first child of first Season of first
Series) has a dead magnet; materialise spends its 30s budget then 500s, and
play 400s on an unmaterialised item. Episode materialise/play TTFB is thus
unmeasurable every day by target choice, not by a server latency fault.

## Enqueued

- **ttfb-loadtime-episode-playable-target** — episode discovery in
  `tools/rig-scenarios/47-loadtime-flows.sh` skips dead-magnet episodes
  (bounded scan) so materialise/play measure a playable item; a dead-only
  library records a discovery failure rather than timing a dead target.
  Not a duplicate of ttfb-loadtime-episode-target-discovery (type-validated
  traversal, kept).
- **ttfb-reduction-028** ~+24h, independent.

Day 028: compare against run_start 1794053824; movie list_load (92s) is the
largest remaining browse latency target once episode playback is measurable.
