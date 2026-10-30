# playback-outcome-coverage-interval-freshness-001

ROI Priority 12; day-21 measurement-integrity follow-up. Extend the existing
offline analyser and its existing executable regression harness, not a second
reporter. Counter continuity and interval deltas have already landed.

## Defect and scope

`scripts/phantom-playback-outcome-coverage.sh` checks current source freshness,
and interval_reasons checks previous coherence, tuple sets, ordering and
decreases. It does not check previous evaluation minus previous source time.
Thus a snapshot rejected as stale when current can later become an accepted
interval baseline. Arithmetic over those source endpoints is not wrong; the
defect is silently certifying a measurement whose historical endpoint failed
the capture freshness policy.

Reproduced at e5ad2c0: 32 complete coherent serving tuples; current
evaluation/source=1000/990, previous=600/400, --now=1000, --max-age=120,
--min-attempts=100, empty window. Cold-movie current success=900,
first_byte_timeout=100; previous success=400, first_byte_timeout=50; other
tuples zero. Actual exit 0, status OK, interval source_seconds=590,
attempts=550, errors=50, rate=9.090909%, timeout ranking=50. Previous age
is 200 seconds, beyond the 120-second policy. This is an offline defect,
not an observed stale live sample or a production failure-cause ranking.

## Required behavior

Invariant: an interval report must never emit numeric interval rates or cause
rankings for an identity whose previous endpoint was stale at that endpoint's
own evaluation time; healthy current cumulative data and independent identities
remain reportable.

Use the existing --max-age, inclusively, against previous evaluation minus
previous source timestamp per full identity. Do NOT compare previous source
time to today's --now: a healthy daily baseline is intentionally old today.
On violation print an explicit previous-endpoint STALE diagnostic with the
observed age and limit, exit 2, and withhold that identity's aggregate and all
four interval slice rates/rankings. Do not invent counts or reset conclusions.
Keep existing current cumulative health/reporting, interval discontinuity
diagnostics, optional-input behavior and healthy-peer isolation intact.

## Acceptance

Check: `./scripts/tests/phantom-playback-outcome-coverage.test.sh`

Matches hive CHECKS.md script-test. Extend the existing harness to invoke the
real analyser with the reproduction above and assert exit 2, explicit stale
previous diagnostic, and absence of numeric interval aggregate/slice reports.
Run the added regression before implementation and record its failure.
Cover both roles, all four flow/item slices, boundary previous ages 120
(accepted) and 121 (rejected), and a healthy peer with independent labels.
Retain a valid daily-old previous evaluation/source pair whose source was fresh
then, verify current cumulative output survives, no-flags behavior is unchanged,
and preserve all 220 existing cases and input-immutability/negative controls.
Update help/input documentation for both endpoint freshness semantics.

No live traffic, deployment, reset or promotion is needed. No runtime/plugin
instrumentation or changes to interval subtraction are in scope.
