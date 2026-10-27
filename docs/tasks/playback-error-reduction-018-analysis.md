# playback-error-reduction-018 analysis (day 18, 2026-10-27)

Diagnose-and-enqueue only. No implementation, deployment, counter reset,
synthetic live traffic or production promotion.

## Baseline (read-only, single pinned evaluation time)

- Instant `count by (instance,job)(phantom_playback_outcome_total{job=...})`:
  one instance (inactive/dev colour exporter) = 32 series; `sum` = 0.
- `max_over_time[30d]`: count 32, sum 0 — only those zeros.
- Targeted 35d on the retired rig instance still returns cold episode
  `magnet_dead_stale=2` and cold movie `success=1`. Window aging, not improvement.
- `phantom_runtime_info*`: still empty.
- Process identity of the inactive colour pod unchanged since 2026-10-22
  (same pod UID, image digest, 0 restarts) — prior attribution stands.
- Serving production colour still runs the older image (unchanged digest) and
  emits no outcome series and no exporter log lines.

## Coverage analyser (playback-outcome-coverage-report-001, DONE)

Run against live API JSON with an expected list {inactive colour instance,
serving colour}: inactive `status=OK coverage=32/32`, `attempts=0`,
`error_rate=undefined (no observed attempts)`; serving `status=UNINSTRUMENTED
coverage=0/32`; exit 2. Organic production traffic is UNMEASURED, not zero.

Friction found running it on real data (motivates today's fix):
1. `--window` rejects the raw `max_over_time(...)` response
   ("window: expected phantom_playback_outcome_total family") because
   Prometheus/Mimir drop `__name__` from range-function output. It only worked
   after wrapping the query in `label_replace(..., "__name__", ...)`. The
   script already tolerates the missing name for `timestamp()`, not for
   `max_over_time()` — inconsistent and contrary to its own doc's input recipe.
2. Inputs fetched in sequence fail "inputs must use one common Prometheus
   evaluation time" unless every query passes an explicit `time=`. That
   requirement is correct but undocumented in the usage recipe.

## Ranking

No attributed real volume (attempts=0 everywhere measured; serving colour
uninstrumented). No statistical cause ranking is possible; none is claimed.

## Enqueued fix (exactly one)

`playback-outcome-coverage-window-input-001` — accept the documented raw
`max_over_time` response (missing `__name__`) for `--window`, and document the
pinned `time=` requirement, with regression tests. See its design doc.

Not enqueued (operator-scoped): promoting the instrumented image to the
serving colour is the only way to measure organic traffic; that is a
production promotion and outside this loop's authority.

Successor: playback-error-reduction-019, not_before ~+24h.
