# ttfb-reduction-010 — daily TTFB-reduction analysis

ROI Priority 9, successor of `ttfb-reduction-009`. `check=none` — no fix code
ships here; the only submodule commit is this analysis doc (the two enqueued
tasks' design docs live in the beehive hive layer per the `-009` precedent).

## Headline: the loop is NOT merely stale again — it is permanently stuck

Querying Mimir directly (`kubectl -n monitoring port-forward svc/mimir
18083:8080` + `curl .../prometheus/api/v1/query?query=phantom_loadtime_seconds`,
per `-009`'s method) returned the **byte-for-byte identical** sample `-009`
read: same `list_load`/`sort_change` durations, same `runs_total=1` counters
for every flow. At first glance this looks like the old "CronJob hasn't fired
yet" staleness. It is not: `kubectl -n phantom-library get jobs` shows

```
phantom-library-loadtime-daily-29831717   Failed   0/1   31h
phantom-library-loadtime-daily-29833157   Failed   0/1   7h10m
```

— the daily CronJob (`17 6 * * *`) HAS fired twice since `-009`'s sample and
**FAILED both times**. A manually-triggered verify job
(`loadtime-manual-verify-002`) reproduced the failure live, in full:

```
# flow=list_load          item_type=movie   duration_s=498.204316 errors=0 items=7241
# flow=sort_change        item_type=movie   duration_s=333.940399 errors=0 items=5200
# flow=info_open          item_type=movie   duration_s=0.043655   errors=1
# flow=get_sources        item_type=movie   duration_s=0.042335   errors=1
# flow=materialise        item_type=movie   duration_s=0.052196   errors=1
# flow=play_materialised  item_type=movie   duration_s=0.052432   errors=1
# flow=list_load          item_type=episode duration_s=318.052018 errors=0 items=4800
...
phantom-loadtime-push: ERROR: push to Pushgateway failed: http://prometheus-pushgateway.monitoring.svc.cluster.local:9091/metrics/job/phantom-loadtime
```

The measurement engine runs to completion with real, fresh, materially
different numbers than `-009`'s sample (movie `list_load` now 498.2s vs
432.6s, at a much larger catalogue — 7241 movie items vs unstated in `-009`;
episode `list_load` now 318.1s vs 66.2s, 4800 episode items). But the FINAL
push to the Pushgateway fails every time, so this fresh batch is **discarded**
and Mimir keeps serving the last successfully-pushed batch (`-009`'s)
forever. Every future daily run will keep failing the identical way until
this is fixed — this is a standing block on the whole ROI-9 daily loop, not
ordinary next-day staleness.

## Root cause (reproduced against the live cluster Pushgateway)

The Pushgateway returns `HTTP 400`:

```
text format parsing error in line 6: unknown metric type "counter# HELP phantom_loadtime_rig_outcome_total x"
```

`tools/rig-scenarios/47-loadtime-flows.sh`'s `flush_records()` joins the
metric-family `HELP`/`TYPE` header with the playback-outcome header via
`printf '%s%s\n%s%s' "$header" "$outcome_header" "$_records" "$_outcome_records"`.
Because `$header` (built via unquoted-here-doc command substitution) has its
trailing newline stripped, and the format string's only `\n` sits *after*
`$outcome_header` rather than between the two headers, the last line of
`$header` and the first line of `$outcome_header` are glued onto one line
with no separator whenever `$outcome_header` is non-empty — which is every
live run, since `emit_playback_outcome` always fires for the
`materialise_then_play` outcome. Confirmed independently two ways: (1)
reproducing the exact reported parser error against the live in-cluster
Pushgateway with a payload built from the script's own `emit_record`/
`emit_playback_outcome`/`flush_records` logic; (2) the identical error from
`promtool check metrics` run locally against the same payload.

This is a genuine deployed pipeline bug — distinct from, and unrelated to,
the separate real issue below.

## The 100%-cold-materialise-flow failure is CONFIRMED, not a rig fluke

`-009` first surfaced `materialise`/`get_sources`/`info_open`/
`play_materialised` all reporting `errors_total=1`/`runs_total=1` (fast-fail,
~0.036s) for both movie and episode, and asked whether it was a rig/probe
defect or a real regression. `-010`'s independent live diagnostic run
reproduces the SAME four-flow failure pattern again (durations now
0.04-0.05s, still errors=1 for all four, still both item types) — two
independent live executions, weeks apart, showing the identical failure
shape. This repeatability argues against a one-off rig glitch and FOR a real,
reproducible issue in either the rig's fixed-item-id probe target or the
deployed plugin's `PlaybackInfo`/materialise path for that item. This
analysis does not yet have in-cluster plugin-log access to distinguish the
two conclusively (out of scope for a DIAGNOSE-ONLY pass with a
higher-priority, fully-diagnosed, swarm-buildable fix already in hand this
pass); `ttfb-reduction-011` is directed to trace it with plugin/Jellyfin logs
rather than only Mimir counters.

## Ranking — cannot be finalized this pass

Because the `-010` live numbers were never successfully pushed to Mimir (the
exposition bug above), they are NOT an authoritative sample and must not be
treated as this pass's ranking baseline — the swarm's shared source of truth
for TTFB is Mimir, and this batch never reached it. The dominant-stage
ranking therefore stays whatever `-009` established
(`list_load{movie}=432.610197s` > `sort_change{movie}=196.173978s` >
`list_load{episode}` > `sort_change{episode}`) until a batch actually lands
in Mimir. Fixing the push bug is a PREREQUISITE for ever re-ranking on fresh
data again — which is exactly why it, not another re-ranking of stale data,
is this pass's enqueued fix.

## Enqueued fix (the single blocking, swarm-buildable win)

`ttfb-loadtime-push-exposition-newline-fix` (ROI P9, weight 9) — fix the
missing newline between `$header` and `$outcome_header` in `flush_records()`
so the exposition text stays valid Prometheus text format regardless of which
playback-outcome metric name(s) are populated, and add a regression
assertion (piping the engine's output through `promtool check metrics` or
equivalent) that the existing `p8-loadtime-push.test.sh` harness never had —
which is why this bug shipped and stayed invisible through `-009`'s single
lucky success (that push apparently predates, or ran under different
conditions than, the outcome-header addition; regardless, every LIVE run
since always hits it). Design doc:
`docs/tasks/ttfb-loadtime-push-exposition-newline-fix.md` (beehive layer).
`Check:` uses the `script-test` framework — the extended
`scripts/tests/p8-loadtime-push.test.sh`.

## Successor

`ttfb-reduction-011 [TODO]` appended, held `not_before` ~= +24h
(`2026-09-22T18:24:00Z`, daily cadence). It must (1) confirm the push fix
landed AND a subsequent real run actually succeeded end-to-end (not just that
the fix merged); (2) treat only a genuinely fresh, successfully-PUSHED sample
as the new ranking baseline; (3) continue tracing the confirmed-repeatable
cold-materialise-flow fast-fail with in-cluster plugin logs. Design doc:
`docs/tasks/ttfb-reduction-011.md` (beehive layer).
