# p5-postgres-loadtime-comparison — SQLite→Postgres browse-flow load-time comparison

P5 task 3. Re-run the five instrumented browse-flow scenarios against the
Postgres-backed deployment (P4 Stage A) once landed, quantify the before/after
delta per flow (**never assume the gain**), and feed the measured Postgres
"after" numbers into `p5-ratcheting-regression-guard`'s threshold table.

Depends on:

- `p5-baseline-metrics-instrumentation` — ships the `phantom_flow_duration_ms`
  OTLP histogram (tagged `flow` + `backend`) for the five flows: `list_view_load`,
  `sort_filter_change`, `season_listing`, `episode_listing`,
  `materialised_listing`.
- `p4-phantomdb-postgres-backend` — the Postgres backend the "after" run measures.

## What this task delivers (in-repo tooling)

The live capture of latency quantiles requires a deployed, instrumented Jellyfin,
real browse traffic, and reachability to the metrics collector — none of which
exist in the headless build sandbox (the exact scope boundary
`p5-baseline-metrics-instrumentation` and `p5-ratcheting-regression-guard` already
recorded). So the *deterministic, testable* deliverable is the comparison and
threshold-feed tooling; the live before/after capture is an operator step (below).

- `tools/perf/loadtime-compare/` — a net9.0 console tool + library
  (`phantom-loadtime-compare`), self-contained under `tools/perf/` and independent
  of the plugin and the `jellyfin/` nested submodule (references only the
  `ratchet-guard` library to share the `MeasurementSet` / `RatchetThresholds`
  model):
  - `Comparison.cs` — `LoadtimeComparer.Compare(baseline, after, neutralBandRatio)`.
    Matches scenarios by `(flow, quantile)` **ignoring backend** (the backend is
    the very thing that differs between the two sides), computes the measured
    `DeltaMs = after − before` and `DeltaPercent`, and classifies each flow against
    a neutral band: `Improved` (faster by more than the band), `Regressed` (slower
    by more than the band — **never assumed away**), or `Neutral`. A scenario
    present in only one run is reported as `Unpaired`, never silently dropped.
  - `ThresholdFeed.cs` — `ThresholdFeed.MergeAfter(existing, after, seed)`. Folds
    the Postgres `after` scenarios into a `RatchetThresholds` object. With
    `seed:false` it adds them unseeded (`threshold_ms=0`) — what the committed
    thresholds file uses, so no fabricated numbers land in git; with `seed:true` it
    seeds each new ceiling from the measured value plus headroom. Never overwrites
    an already-seeded ceiling (the ratchet guard owns tightening it) and never
    mutates its input.
  - `Program.cs` — CLI: `--baseline`, `--after`, `--neutral-band`, `--json`,
    `--fail-on-regression` (exit 3), `--thresholds`/`--seed`/`--apply` to feed the
    ratchet table.
- `tools/perf/loadtime-compare.sh` — operator wrapper: runs the comparison and
  feeds the ratchet table in one step.
- `tools/perf/ratchet-thresholds.json` — the five browse flows are now listed at
  `backend=postgres` (unseeded, `threshold_ms=0`) alongside the existing
  `backend=sqlite` entries, so `p5-ratcheting-regression-guard` tracks Postgres.
- `tools/perf/loadtime-compare-tests/` — xUnit coverage of the comparison contract
  and the threshold feed.

## Operator step — capture the real before/after

1. Deploy the instrumented plugin (`./install.sh --build`) with
   `MetricsOtlpEnabled=true` and the collector endpoint set.
2. On the **SQLite** deployment, exercise the five flows and record the
   `phantom_flow_duration_ms` p90 (tagged `backend=sqlite`) into `sqlite.json`
   (a `MeasurementSet`).
3. Cut over to the **Postgres** backend (P4 Stage A), repeat, recording
   `backend=postgres` into `postgres.json`.
4. `tools/perf/loadtime-compare.sh sqlite.json postgres.json --seed --apply` —
   prints the per-flow delta and seeds the Postgres ceilings into
   `ratchet-thresholds.json`. Commit the seeded thresholds; the ratchet guard
   thereafter tightens and guards them.
