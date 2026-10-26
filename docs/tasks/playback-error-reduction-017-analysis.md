# playback-error-reduction-017: day-17 analysis

DIAGNOSE-AND-ENQUEUE ONLY. Read the series contract, day-16 analysis and
day-17 guidance first. Observed 2026-10-26 around 04:43 UTC.

## Baseline first

Instant outcomes: API success, exactly 32 tuples, all on the same single
inactive/dev exporter instance as day 16, summed value 0. The existing
seed-deployment endpoint Check passed again (exact instance/job-scoped
canonical product, finite nonnegative values, sample ages within [0,120)s).
The simultaneous max_over_time[30d] query: same 32 series, sum 0. A targeted
35d query on the prior rig instance still returns cold episode
magnet_dead_stale=2 and cold movie success=1. Unchanged from day 16: window
aging only, not improvement, new attempts or a reset. No reset performed.

**No organic traffic yet observed.** Observed denominator is zero; the error
rate is undefined, not 0%. No population rate or statistical ranking.

## Attribution

Pod UID, container ID, image digest, start time and zero restarts of the
inactive/dev process are identical to days 14-16; startup provenance log lines
again return no match. Attribution remains continuity against the day-14
loaded-build receipt (0.5.7.7, source `d9a58ec`), not fresh metadata.

New today: the serving production colour runs an older image (started before
the outcome instrumentation was deployed) and contributes **no** outcome
series and no exporter log lines. Real user traffic is therefore
uninstrumented, not zero. Absence of production series cannot exclude errors.

## Existing fixes

- `otlp-runtime-info-001` DONE in source; exact and suffix-tolerant live
  queries still empty (observed process predates it). Not re-filed; never
  playback volume.
- `playback-outcome-synthetic-push-guard-001` DONE (30-test harness, day 16).
- `loadtime-push-complete-batch-validation-001` is now DONE (merged
  `e3c4f38`). Source completeness fix, not a deployed-pipeline proof; not
  duplicated.

## Priority, not statistical ranking

1. Measurement integrity: every day the baseline is hand-assembled; nothing
   machine-distinguishes undefined from 0%, a serving colour emitting nothing
   from no traffic, or window aging from improvement.
2. Historical rig cold-episode magnet_dead_stale (n=2) remains the only known
   positive failure bucket; too small to rank or justify a playback change.
3. All other causes/flows have zero observed volume.

## Exactly one fix

Enqueue `playback-outcome-coverage-report-001` (adjacent design doc): an
offline read-only analyser over Prometheus API JSON plus an expected
serving/inactive instance list, enforcing exact coverage and freshness,
rendering zero denominators as undefined, failing (exit 2) on an
uninstrumented serving instance, labelling window-only failures aged-out,
and ranking only above a minimum attributed volume. Check: the
`script-test` harness `./scripts/tests/phantom-playback-outcome-coverage.test.sh`.
Distinct from the zero-seed Check, runtime-info and loadtime-push validators.

## Successor and scope

Enqueue `playback-error-reduction-018` TODO for 2026-10-27T04:55:00Z (~+24h).
No implementation, deployment, synthetic live traffic, counter reset,
process mutation or production promotion; no improvement claimed.
