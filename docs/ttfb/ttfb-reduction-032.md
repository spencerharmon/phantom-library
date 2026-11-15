# TTFB Reduction 032

Diagnosed 2026-11-15 UTC; read day 031 first. Diagnose-and-enqueue only.
No runtime, deployment, thresholds or measurement behavior changed.

## Recovery And Attribution

Exactly one retained manifest is newer than 1794572228: run_start
**1794658629**, attempt `29d93210833a4992b4ad3c77cd1f31ff`, checkout
`4d97d6d6dbd9685a628b08448d32f24cd53a2ffe`, inactive color green, retention 32.
Discovery 19s/0, measure 607s/0, push 0s/0, guard 4s/3; status complete,
`failures: []`. Job `phantom-library-loadtime-daily-29910977` ran
2026-11-14T12:17:00Z-12:27:25Z, succeeded=1. Same four targets as day 031.
Still the backend Service path (OIDC fix `b87f853` in ancestry): TLS/ingress/
OIDC excluded; the Oct 30 edge batch is a different population.

| daily run_start | flow push_time_seconds | daily push_time_seconds |
| --- | --- | --- |
| 1793359027 | 1793361169.4467623 | 1793361174.3147707 |
| 1794053824 | 1794054641.644034 | 1794054646.763412 |
| 1794140228 | 1794140993.9278193 | 1794140998.84607 |
| 1794572228 | 1794573078.935169 | 1794573085.3758104 |
| 1794658629 | 1794659236.9590526 | 1794659241.4343555 |

Flow push 1794659236.96 falls inside the manifest push stage (start
1794659236); daily push follows guard. Mimir at evaluation 1794721418.441
returns run_start 1794658629 and these pushes. Temporal binding, not an
attempt-ID receipt (known limitation). Failed Nov 10-12 discovery jobs remain
separate failed batches, never in flow denominators.

## Durations And Errors (median s, errors/runs)

| flow / type | Oct 30 | Nov 7 | Nov 8 | Nov 13 | Nov 14: 1794658629 |
| --- | --- | --- | --- | --- | --- |
| list_load / movie | 10.118952, 3/3 | 92.149758, 0/3 | 91.135839, 0/3 | 103.210877, 0/3 | 75.935962, 0/3 |
| sort_change / movie | 30.393210, 3/3 | 85.425209, 0/3 | 90.006179, 0/3 | 111.128244, 1/3 | 74.556553, 0/3 |
| info_open / movie | 0.041445 | 0.053861 | 0.058460 | 0.033739 | 0.030881, 0/3 |
| get_sources / movie | 0.029544 | 0.042807 | 0.039677 | 0.025077 | 0.015017, 0/3 |
| materialise / movie | 0.031143 | 0.039514 | 0.037035 | 0.022006 | 0.013739, 0/3 |
| play_materialised / movie | 0.031667 | 0.043975 | 0.058742 | 0.028043 | 0.013604, 0/3 |
| list_load / episode (Shows root) | 343.661371, 2/3 | 26.283003 | 27.062847 | 30.365053 | 22.302643, 0/3 |
| sort_change / episode (Shows root) | 36.425254, 3/3 | 24.448986 | 26.264750 | 32.689070 | 21.934159, 0/3 |
| info_open / episode | 0.048825 | 0.037072 | 0.044795 | 0.048761 | 0.020936, 0/3 |
| get_sources / episode | 0.030021 | 0.027547 | 0.035536 | 0.023320 | 0.013652, 0/3 |
| materialise / episode | 0.029920 | 30.085435, 3/3 | 0.053166 | 0.031458 | 0.012804, 0/3 |
| play_materialised / episode | 0.032538 | 0.028753, 3/3 | 0.033450 | 0.029975 | 0.012791, 0/3 |

(Earlier columns' errors as in day 031.) Totals: **23/36 -> 6/36 -> 0/36 ->
1/36 -> 0/36**. Versus Nov 13 movie list/sort -27.274915s/-36.571691s;
Shows-root -8.062410s/-10.754911s. Versus Nov 8 movie -15.199877s/-15.453626s.
No plugin change is attributed: day-to-day variance (Nov 13 had a timeout
sample) is uncontrolled; these are descriptive.

## Stage Decomposition (landed: ttfb-loadtime-browse-stage-durations)

Per-sample manifest `browse_samples`, all success. Movie (movies_root),
6 samples: 83 pages `channel_items` 37.40-40.08s (~0.46s/page), 42 batches
`badge_states` 27.35-28.76s (~0.66s/batch), `dom_materialise` ~0.024s,
`residual` 8.69-9.42s. Shows root: 49 pages 11.67-11.94s, 25 batches
4.82-5.78s, residual 5.13-5.25s. Each sample total equals its component sum
(e.g. movie list s1: 40.084838+28.758493+0.024275+9.255274=78.12288).
Movie: pagination ~51%, badges ~37%, residual ~12%.

## Bounds

Request/list 60s/120s: max page ~0.5s, max list 78.1s < 120s, no curl 28.
Engine 1410s / wall 1500s: measure 607s, no exit 124. Job deadline 5400s
separate. Do not raise limits. Guard exit 3 = breach series vs ratcheted
thresholds, not sample errors.

## One Nonduplicate Fix

Enqueue **ttfb-loadtime-page-parse-residual**. Residual is 8.7-9.4s/movie
sample (5.1s Shows) and scales with pages: the LIVE page loop
(`47-loadtime-flows.sh` ~:830-833) spawns three `python3` processes per page
to re-parse the same JSON (count, TotalRecordCount, ids) — 249 interpreter
starts per movie sample. That harness time is inside the reported flow
duration, inflating the measured TTFB metric by ~12% and not reflecting
server or browser cost. Fix: parse each page once (single python call or jq)
emitting count/total/ids; preserve stage semantics, totals, errors, deadlines,
batch sizes, selection and ratchet policy. Not a duplicate of stage
durations (measures, does not reduce), DB batching (server), target
selection, failure attribution, error-aware ratchet or OIDC fix. Movie and
Shows-root parity: same loop serves both.

Check: `./scripts/tests/p8-loadtime-daily-workflow.test.sh`, extended with a
real LIVE engine against a local multi-page HTTP fixture asserting per-sample
python/parse invocation count is O(1) per page (red before: 3/page) and that
item counts, ids handed to badge fan-out, and stage reconciliation are
unchanged for movie and Shows root.

## Independent Successor

Enqueue **ttfb-reduction-033**, no deps, not before 2026-11-16T05:40:00Z.
Inspect manifests newer than 1794658629; bind flow/daily pushes; compare
durations, errors AND stage components vs all five batches; watch residual
after the parse fix lands. Neither day 032 nor its fix blocks day 033.
