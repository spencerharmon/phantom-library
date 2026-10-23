# TTFB Reduction 015

Diagnosis 2026-10-23 ~03:10Z. Read `docs/ttfb/ttfb-reduction-014.md` first.
Diagnose-and-enqueue only; no runtime/implementation change in this pass.

## Latest Measurement

Mimir (`{job=~"phantom-loadtime|phantom-loadtime-daily-run"}`) flow
`push_time_seconds` VALUE 1792667833.67 = 2026-10-22T11:17:13Z: the scheduled
October 22 daily run, ~8h after `ttfb-shows-root-bulk-enrichment` landed
(`ce39fdd`, 2026-10-22T03:15:55Z). All 12 errors series 0, all 12 runs 1, color green.

| Flow | Item | 014 (Oct 21) | 015 (Oct 22) | Delta |
| --- | --- | ---: | ---: | ---: |
| list_load | episode | 0.305713 | 0.296525 | -3.0% |
| sort_change | episode | 0.305823 | 0.289795 | -5.2% |
| sort_change | movie | 0.134463 | 0.184649 | +37.3% |
| list_load | movie | 0.135794 | 0.157037 | +15.6% |
| info_open | movie | 0.041991 | 0.152598 | +263% |
| play_materialised | movie | 0.033531 | 0.067518 | +101% |
| get_sources | movie | 0.029449 | 0.066391 | +125% |
| info_open | episode | 0.047660 | 0.061025 | +28% |

Episode root moved down slightly while every movie flow moved UP, several
doubling. With one synthetic sample per flow and uncontrolled cache/load this
is noise-dominated: the episode improvement (3-5%) is far inside the
run-to-run variance the movie flows just displayed. Deployed build identity
is NOT established (blue deployment pins image digest `sha256:2cb8cb0a...`;
nothing maps it to a plugin commit), so the root fix cannot be credited.

## Daily Job

`phantom-library-loadtime-daily-29877797` (2026-10-22T11:17:00Z) completed
11:17:23Z, succeeded=1, CompletionsReached; 2nd consecutive green day after
the Oct 19/20 failures. Stage metric: `stage_exit_code{stage="guard"}=0`,
seconds=6. Only `guard` exists in Mimir: measure/push outcomes are still
overwritten (014's defect, `tools/ci/loadtime-daily-run.sh` `record_stage`,
each stage PUTs to the same `/metrics/job/phantom-loadtime-daily-run` key).

## Larger Win?

No new evidence-backed larger TTFB win: the dominant flows' only queued fix
just landed and cannot be judged until variance is understood and the deployed
build is identifiable. Filing another speculative perf change on a single noisy
sample would be guessing. Per the 014 plan, the ONE fix this cycle is the
record_stage grouping-key overwrite fix: `ttfb-loadtime-daily-stage-retention`.
It makes all three stage outcomes durable (cumulative per-run payload rewritten
on each stage, so the final PUT carries measure+push+guard), with a run-id /
run-start timestamp gauge for freshness. Check: the daily-workflow script test.

## Successor

`ttfb-reduction-016` [TODO], weight 8, not_before 2026-10-24T03:10:00Z:
re-sample; compare list_load{episode} 0.296525 / sort_change{episode}
0.289795; investigate movie-flow regression vs noise (two samples); try to
identify deployed plugin build; verify stage-retention fix if landed.

## Limits

Analytical (`check=none`); read-only kubectl/Mimir queries only. Commands and
outputs in the hive change doc.
