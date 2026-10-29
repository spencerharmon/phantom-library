# playback-outcome-coverage-counter-continuity-001

## Problem

Day 20 reproduced a blind spot in the existing offline coverage analyser:
`scripts/phantom-playback-outcome-coverage.sh` lines 190-192 at base e93f3a2
only report positive window maxima when the tuple is absent from instant.
A present, freshly seeded error counter can be zero while the same labelled
counter's window maximum is positive; no diagnostic survives. Source-time
coherence does not establish common counter lifetime.

This is a distinct extension of the existing analyser, not another source-skew,
window-input, slice-denominator, instrumentation or deployment task.

## Required behavior

Invariant: a matched window maximum greater than its current counter must
never silently yield an OK identity or numeric aggregate/slice rates or rankings.

Compare parsed numeric values for exact identity + flow + item_type + cause
matches between current and window. If window_max > current, report a distinct
COUNTER_DISCONTINUITY diagnostic containing the tuple, current value and
window maximum; exit 2 and withhold that identity's derived ratios/rankings.
Apply to success counters and all failure causes, serving and inactive alike.
Preserve coverage/freshness/coherence diagnostics even when several fail.
Unaffected identities must retain independently gated healthy reports.

Describe this only as evidence that counter continuity is not established:
possible reset/reseed, identity reuse, or capture inconsistency. Do not claim
an exact reset cause, reset count/time or production error-rate improvement.
Do not sum/substitute window maxima into attempts/errors, and do not implement
reset-adjusted rates from these inputs. Raw counts may be diagnostic only.

Keep partial and empty window vectors valid. Absence of a matching historical
tuple is not proof of continuity or an error by itself. Preserve window-only
aged-out/stale handling and the cumulative-not-window disclaimer. Equal or
smaller window maxima do not trigger this guard. Document this conservative
policy in the existing coverage contract/help; no new network transport,
metric family, live traffic, reset, restart or deployment is needed.

## Reproduction and acceptance

One serving identity `{"instance":"example.com"}`, canonical 32 tuples,
evaluation 1000 for all three vectors, coherent source timestamps 990,
`--now 1000 --max-age 120 --min-attempts 100`. Instant cold-movie success=1000,
all others=0. Raw max_over_time window: same 32 tuples without __name__,
success=1000 and cold-movie no_candidate=101, others=0. At e93f3a2 this exits
0 and reports OK, attempts=1000/errors=0, 0.000000%, ranking none (no errors),
with no historical diagnostic. The new behavior must reject that conclusion.

Extend the real executable harness to assert exit 2, exact tuple/current/max
diagnostic and withheld numeric aggregate/slice rates and rankings. Rotate the
drop through all four flow/item slices; cover serving/inactive, a dropped
success counter, partial windows, equal/lower maxima controls, empty window,
window-only positives, and a mixed healthy/discontinuous identity report.
Preserve all existing input validation, source coherence, freshness, threshold,
stub-rejection and read-only-input assertions. Demonstrate new assertions fail
on the old analyser and pass on the change, not just that a stub is rejected.

Check: `./scripts/tests/phantom-playback-outcome-coverage.test.sh`

Matches hive CHECKS.md `script-test`. Record the exact branch-scoped invocation:
`beehive submodule worktree exec phantom-library bee-playback-outcome-coverage-counter-continuity-001 -- ./scripts/tests/phantom-playback-outcome-coverage.test.sh`.
No Jellyfin rig is needed for an offline report-only change.
