# playback-outcome-coverage-window-input-001

Enqueued by playback-error-reduction-018. Measurement usability fix for
`scripts/phantom-playback-outcome-coverage.sh`.

## Problem
Prometheus-compatible APIs drop `__name__` from range-function results, so the
documented `--window` input `max_over_time(phantom_playback_outcome_total{...}[30d])`
is rejected with "window: expected phantom_playback_outcome_total family".
Day 18 had to wrap the query in `label_replace` to run the analyser. The
script already accepts a missing `__name__` for `--timestamps`.

## Design
- For `--window` (as for `--timestamps`) treat a missing `__name__` as the
  outcome family; still reject a PRESENT non-matching `__name__`.
- Keep requiring all three inputs share one evaluation time; document in the
  usage text/design doc that each query must pass the same `time=`.
- Tests in `scripts/tests/phantom-playback-outcome-coverage.test.sh`: a window
  fixture without `__name__` now passes; a window fixture with a foreign
  `__name__` still fails; existing cases unchanged.
- No network, no site identifiers, no deployment.

## Check
`./scripts/tests/phantom-playback-outcome-coverage.test.sh` (CHECKS.md script-test).
