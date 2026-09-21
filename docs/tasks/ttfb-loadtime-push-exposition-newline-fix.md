# ttfb-loadtime-push-exposition-newline-fix

Design doc for the ROI-9 fix enqueued by `ttfb-reduction-010` (tenth daily
TTFB pass). See the analysis in `docs/ttfb/ttfb-reduction-010.md`.

## Problem

The daily/live TTFB loadtime measurement engine
(`tools/rig-scenarios/47-loadtime-flows.sh`) runs to completion with fresh
data every run, but its final Pushgateway push fails with **HTTP 400** on
every live run (reproduced on scheduled CronJob runs and in a manual verify
run). The batch is silently discarded, so Mimir keeps serving the last
successfully-pushed sample forever and the whole ROI-9 daily TTFB loop
stalls.

### Root cause

`flush_records()` assembled the exposition by joining the base `$header`
block and the `$outcome_header` block with the only newline placed AFTER
`$outcome_header` rather than BETWEEN the two header blocks. Command
substitution strips `$header`'s trailing newline, so whenever any
playback-outcome metric is emitted (every live run), the last `# TYPE`
line of the base header and the first `# HELP` line of the outcome header
concatenate onto ONE physical line. Prometheus text format requires one
directive per line; the Pushgateway parser therefore rejects the entire
batch.

The existing harness `scripts/tests/p8-loadtime-push.test.sh` passed with
the bug present because it only checked record pass-through and PUT
targets — it never validated exposition well-formedness.

## Fix

Rewrite `flush_records()` to assemble the exposition from independent
segments, each emitted with a guaranteed trailing separator, so no two
segments can ever concatenate onto one line regardless of whether the
outcome header or outcome records are empty:

- base `$header` — always present, emitted with `printf '%s\n'`.
- `$outcome_header` — emitted only when non-empty; carries its own
  trailing newline per emitted `# TYPE` line.
- `$_records` / `$_outcome_records` — emitted only when non-empty; carry
  their own trailing newlines.

This is correct for all three outcome-metric cases: none, only
`phantom_loadtime_rig_outcome_total`, only
`phantom_playback_outcome_total`, and both.

## Definition of done / Check

`Check: ./scripts/tests/p8-loadtime-push.test.sh`

The harness matches the `script-test` framework registered in `CHECKS.md`
(`(\./)?\S*scripts/tests/\S+\.test\.sh`) and is extended with a well-formedness assertion (section
G) that pipes the DRYRUN engine exposition through `promtool check metrics`
and asserts exit 0, with a structural fallback (detecting two
directives/samples concatenated onto one line) when `promtool` is
unavailable. The assertion FAILS pre-fix (reproducing the parser
rejection) and PASSES post-fix.

## Scope

Measurement-pipeline repair only. It does NOT touch the deployed plugin,
and it does NOT itself re-rank any TTFB stage — it unblocks fresh samples
so subsequent passes (`ttfb-reduction-011`+) can re-rank against genuinely
converged data and finally verify `ttfb-list-load-movie-render-profile`'s
`Verify-After-Merge` fresh-sample check.
