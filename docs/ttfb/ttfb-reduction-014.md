# TTFB Reduction 014

Diagnosis resumed 2026-10-22 at approximately 04:19Z. Read
`docs/ttfb/ttfb-reduction-013.md` first. Diagnose-and-enqueue only; no runtime
or implementation changes in this pass.

## Latest Available Measurement

Mimir returned `status=success`. The flow `push_time_seconds` VALUE is
1792581433.6400185 (2026-10-21T11:17:13Z), not the query evaluation timestamp
1792642764.803. This is a roughly 17-hour-old retained daily sample, not a
new measurement taken during this investigation. All 12 error series are zero
and all 12 run series are one, with color label green.

| Rank | Flow | Item Type | Seconds |
| --- | --- | --- | ---: |
| 1 | sort_change | episode | 0.305823 |
| 2 | list_load | episode | 0.305713 |
| 3 | list_load | movie | 0.135794 |
| 4 | sort_change | movie | 0.134463 |
| 5 | info_open | episode | 0.047660 |
| 6 | info_open | movie | 0.041991 |
| 7 | get_sources | episode | 0.038348 |
| 8 | materialise | episode | 0.035388 |
| 9 | play_materialised | movie | 0.033531 |
| 10 | materialise | movie | 0.031288 |
| 11 | play_materialised | episode | 0.031223 |
| 12 | get_sources | movie | 0.029449 |

`list_load{episode}` is 0.015864s (5.4732%) ABOVE the requested 0.289849s
baseline. It is below 013's final 0.375142s observation, but single synthetic
samples with uncontrolled cache/load and unidentified deployed builds do not
establish an optimization's effect. These are full-flow timings, not isolated
server TTFB or latency percentiles. The color label does not prove ingress role
or plugin identity.

## Landed Work and the Measured Path

`ttfb-list-load-episode-render-profile` landed at
`9abb08b8777c4ad992a90723e012a5640afc99d3`. It batches season episode upserts
and materialised lookups. However, `tools/rig-scenarios/47-loadtime-flows.sh:450`
requests the channel root without `FolderId`; lines 480-483 label the Shows
root's pagination and badge fan-out as episode list/sort. The season optimization
does not target these measured root requests. Do not enqueue it again.

The ONE fix already filed by this task's earlier attempt is
`ttfb-shows-root-bulk-enrichment`. It targets the root's serial external-series
enrichment and hidden checks and has now landed at
`ce39fdd973a2125aa9f5c8df4591555e6d177317` (2026-10-22T03:15:55Z).
`PhantomShowsChannel.cs:452-462` now prefetches eligible series; the cached path
uses bulk path/metadata resolution and a single hidden-set read, retaining the
cold title/year fallback and duplicate filtering. Its separate hive design
requires 3-versus-40-series connection-count regression coverage; its `Check:
dotnet test` matches the approved `dotnet-test` CHECKS.md framework. The landed
change record reports that regression failing before the fix and passing after.

The latest measurement predates this root fix's commit. It CANNOT demonstrate
the deployed improvement. Successor 015 must obtain a newer sample and identify
the deployed build before crediting any change. Do not file another root fix
merely because the first landed while this analysis's handoff was being repaired.

## Daily Guard Recovery and Remaining Observability Gap

The latest stage push VALUE is 1792581439.4303367 (2026-10-21T11:17:19Z):
`phantom_loadtime_daily_stage_exit_code{stage="guard"}=0`, duration=6s.
The October 21 scheduled daily Job started at 11:17:00Z and completed at
11:17:23Z with succeeded=1 and CompletionsReached. Retained October 19/20
failures predate the SDK repin. Unlike 013's manual verification Jobs, this
confirms a subsequent scheduled run recovered; October 22's scheduled run has
not yet occurred. There is no current evidence of the old guard tool error 2.

The independent telemetry defect remains in
`tools/ci/loadtime-daily-run.sh:122-140`: each stage PUT contains only that
stage and replaces the same Pushgateway grouping key. Only guard survives;
measure and push outcomes are not independently retained. POST alone also
replaces same-named metric families. A future fix should use cumulative
current-run payloads or stage-scoped groups with explicit freshness and stale-run
handling, tested against retained gateway state rather than log strings. Never
encode raw stderr in labels. SDK repinning already repaired the tool error;
do not enqueue another SDK fix.

The wrapper also guards a second live measurement rather than the published
batch, and applies threshold tightening inside a disposable checkout. These
previously documented concerns remain separate from this cycle's one fix.
The measurement script's episode selection at lines 427-430 descends only one
level and falls back to a series container, so zero synthetic errors alone are
not proof of actual episode playback reliability.

## One Fix and Daily Successor

Retain the existing `ttfb-shows-root-bulk-enrichment` task and its own design
doc, not a second fix. Retain existing `ttfb-reduction-015 [TODO]`, weight 8,
`not_before=2026-10-23T03:08:00Z`, approximately +24h from the original 014
attempt. Its separate design already requires reading this report, comparing
episode list 0.305713s / sort 0.305823s after root batching, checking daily
completion, and considering stage-retention repair if no larger win remains.
Do not move that gate or create another successor on a resumed handoff.

## Verification and Limits

This restores the missing source analysis promised by the surviving hive change
record, with independently refreshed Mimir and Job observations. No behavioral
regression test honestly applies to an analytical document (`check=none`).
Exact read-only live commands, outputs, source references and plan-validation
evidence are in the hive change doc. Earlier implementation test results are
attributed to their change record, not claimed as rerun here. No deployment,
database, runtime configuration or infrastructure change was made, and no
operator action is required.
