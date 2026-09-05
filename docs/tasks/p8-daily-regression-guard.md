# P8 daily dual-purpose improvement + regression guard

ROI Priority 8, item 5. Extends the P5 ratcheting-regression-guard
mechanism (`tools/perf/ratchet-guard/`) to the six ROI-named load-time
flows measured by `tools/rig-scenarios/47-loadtime-flows.sh` (P8 item 1):
`list_load`, `sort_change`, `info_open`, `get_sources`, `materialise`,
`play_materialised` — each captured for both a movie and an episode.

## What this adds (reuse, not reinvention)

- `RatchetEngine`/`RatchetThresholds`/`ScenarioThreshold` (P5's pure decision
  engine) gained **optional per-scenario overrides**:
  `improvement_margin_ratio`, `ratchet_headroom_ratio` (fall back to the
  global knobs when null) and an informational `target_ms`. This is how
  `materialise` + `play_materialised` ratchet *hardest* toward the
  operator's 0.5s target: `tools/perf/loadtime-thresholds.json` gives those
  two flows a tighter margin (5% vs the default 10%) and headroom (2% vs
  5%) so a smaller improvement still tightens their ceiling. `target_ms` is
  never enforced (it does not floor a ratchet or gate a breach) — it is
  carried through purely for reporting/visibility of progress toward the
  goal.
- `FilingPlan.Build`/`TaskIdFor` gained an optional `taskIdPrefix` (default
  unchanged: `p5-perf-regression`) so the daily guard can file under
  `p8-daily-perf-regression-<scenario>` — never colliding with P5's
  `p5-perf-regression-<scenario>` ids for the same flow name (e.g.
  `materialise` exists in both guards).
- `tools/perf/loadtime-thresholds.json` — the six-flow x (movie, episode)
  threshold registry, quantile fixed at `"single"` (a load-time flow
  records one wall-clock duration per run, not a distribution).
- `tools/perf/loadtime-expo-to-measurements.py` — converts the Prometheus
  exposition `47-loadtime-flows.sh` emits
  (`phantom_loadtime_seconds{flow,item_type,color}` in seconds) into the
  ratchet-guard `MeasurementSet` JSON contract (`flow`, `backend` = the
  flow's `item_type`, `quantile="single"`, `value_ms` = seconds*1000).
- `tools/perf/loadtime-guard.sh` — the daily wrapper: runs the measurement
  engine (dry run by default; `--live` drives the real rig via the same
  `PHANTOM_LOADTIME_*` knobs `47-loadtime-flows.sh` already exposes),
  converts, runs `phantom-ratchet-guard` against
  `loadtime-thresholds.json` with `--task-prefix p8-daily-perf-regression`,
  and on breach files one `beehive task add` + `beehive task block` per
  breached scenario against `--source-task p8-daily-regression-guard`
  (mirrors `ratchet-guard.sh`'s filing loop verbatim). `--no-file` skips
  the beehive CLI entirely for local/CI dry runs.

## What is intentionally NOT in this task

- The daily **schedule** (`p8-daily-schedule-job`) and the **Mimir
  pushgateway sink** (`p8-mimir-pushgateway-emit`) are separate, still-open
  P8 tasks; this guard does not depend on either — it consumes
  `47-loadtime-flows.sh`'s exposition directly rather than reading back
  from a metrics backend, so it works standalone today and the schedule
  job can simply invoke `loadtime-guard.sh --live --apply` once wired.

## Evidence

- `dotnet test` (full solution, `jellyfin/` nested submodule initialized):
  **1097 total, 0 failed** across all four test assemblies (23 in
  `PhantomRatchetGuard.Tests`, up from 18 — 5 new tests covering the
  per-scenario override fallback, the never-enforced `target_ms`, and the
  configurable task-id prefix; plus the pre-existing 610/15/… suites
  unaffected).
- `scripts/tests/p8-daily-regression-guard.test.sh` (new, sandboxed,
  `--no-file` only — never shells out to `beehive`): **11/11 passed** —
  converter contract correctness, first-run seeding of all 12 scenarios,
  second-run idempotent hold, and a forced-low-ceiling breach that exits 3
  and explicitly declines to file tasks in `--no-file` mode.

```
$ bash scripts/tests/p8-daily-regression-guard.test.sh
...
11 passed, 0 failed
```

## Environment note (unrelated to this change)

`dotnet test` initially failed in this worktree with `CS0246` errors
(`ChannelItemInfo` etc. not found) because the nested `jellyfin/` submodule
was not yet initialized (`git submodule update --init jellyfin`) — a
one-time environment gap, not a code defect. Once initialized, the full
solution builds and all 4 test assemblies pass.
