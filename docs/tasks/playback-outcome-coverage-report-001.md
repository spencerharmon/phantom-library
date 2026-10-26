# playback-outcome-coverage-report-001

## Problem

Seventeen daily passes have hand-assembled the playback baseline from ad hoc
PromQL: tuple counts, exact instance/job scope, sample freshness, zero
denominators and window aging. Day 17 again found 32 fresh all-zero tuples from
one exporter instance attributed to the inactive/dev colour, while the serving
production colour runs an older image that emits no
`phantom_playback_outcome_total` series at all. Nothing machine-distinguishes
"0 errors / 0 attempts" (undefined) from "0% error rate", nor "serving colour
emits nothing" from "no traffic". Window aging (old rig failures leaving the
30d window) has repeatedly needed manual explanation.

## Design

Add `scripts/phantom-playback-outcome-coverage.sh`, an offline, read-only
analyser over Prometheus HTTP API JSON (instant vector, and a
`max_over_time[<window>]` vector) plus an operator-supplied expected-instance
list with a role per instance (serving / inactive). No hard-coded hosts, jobs
or instance ids (neutral placeholders in tests only); inputs by file/stdin.

Per instance it reports: tuple coverage against the canonical product
(2 flows x 2 item types x 8 causes, exact label equality, duplicates and
unknown causes rejected), sample age vs a max-age, attempts (sum incl.
success), errors (non-success), and rate = errors/attempts ONLY when
attempts > 0, else the literal `undefined (no observed attempts)`. An expected
serving instance with no series is reported `UNINSTRUMENTED` and makes the
exit status non-zero (2) so a daily pass cannot read it as zero errors.
Window-only nonzero series absent from the instant vector are reported as
`aged-out/stale`, never as improvement. Cause ranking is printed only when
attributed attempts exceed a configurable minimum; otherwise
`no organic traffic yet observed`.

No network transport inside the script (callers fetch JSON), no counter
resets, no deployment. Do not duplicate the zero-seed Check, runtime-info gauge
or loadtime-push validators; never count `phantom_runtime_info` as volume.

Invariant: a zero denominator is never rendered as a 0% rate and a serving
instance with no outcome series always fails the report.

## Acceptance

Check (CHECKS.md `script-test`):
`./scripts/tests/phantom-playback-outcome-coverage.test.sh` (committed
executable). Fixtures must cover: complete fresh zeros (undefined rate, exit
0), missing serving instance (exit 2, UNINSTRUMENTED), incomplete/duplicate
tuples, stale samples, window-only aged-out failures, positive volume with
rate and ranking above the minimum, and below-minimum suppression; movie and
episode parity. Each assertion must fail against a stub that prints 0%.
