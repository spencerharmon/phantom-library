# playback-outcome-coverage-flow-item-breakdown-001

## Problem and scope

The daily series requires error rates split by flow and item type, but
`scripts/phantom-playback-outcome-coverage.sh` currently computes one
instance-wide denominator, error rate and cause ranking (lines 145-158 at
453e894). The original coverage task specifies per-instance reporting;
this follow-up adds the missing stratification rather than reimplementing
coverage validation. Existing single-slice parity cases do not test mixed
populations. No statistical production ranking motivates this task: day 18
has no attributed organic volume.

Extend the SAME offline analyser, not a parallel reporter. Retain the
explicitly labelled aggregate for each exact identity and add all four
canonical flow/item slices. For each slice report attempts (all causes),
errors (non-success), and errors/attempts; a zero denominator is undefined.
Label rates cumulative snapshots, never daily deltas or improvement.

Only a fresh, complete, serving instance can rank a slice, and that slice's
attempts must strictly exceed `--min-attempts`. Another slice's volume must
not unlock its ranking. Inactive slices may show measurements but never
attributed-serving rankings. Preserve fail-closed identity, tuple coverage,
freshness, input and window handling: incomplete/stale instances must not
gain apparently authoritative slice rates or rankings. Window maxima remain
context only. No instrumentation, network collection, deployment, production
promotion or live test traffic is in scope.

Invariant: every fresh complete identity exposes separate movie/episode
and cold/warm denominators; one slice's volume cannot authorize another
slice's ranking, and missing observations are never rendered as success.

## Acceptance

Extend the existing executable harness and drive the real analyser:

- Mixed fresh serving fixture: 1000 warm-movie successes, two cold-episode
  magnet_dead_stale failures, all other canonical counters zero. Assert the
  aggregate remains 1002 attempts / 2 errors; warm movie is 1000 / 0 / 0%,
  cold episode is 2 / 2 / 100% but ranking suppressed at minimum 100; cold
  movie and warm episode have zero attempts and undefined rates.
- Assert four distinct labelled slices exactly once per complete identity;
  do not satisfy the test with an extra disconnected report or a substring
  found only in the aggregate. Rotate the sparse slice through all four
  combinations to preserve movie/episode and cold/warm parity.
- At threshold equality suppress each slice's ranking; strictly above it
  permit deterministic cause ordering. High traffic in other slices or
  identities must not change this decision.
- Preserve inactive-role suppression, exact identity isolation, stale and
  incomplete-instance withholding, and exclusion of window-only failures.
- Run the new assertions against the pre-change analyser and record their
  failure, then run them against the implementation and record passing
  output. Keep all existing tests green and update the CLI documentation.

Check (CHECKS.md `script-test`):
`./scripts/tests/phantom-playback-outcome-coverage.test.sh`

Worktree invocation for the implementation task:

```sh
beehive submodule worktree exec phantom-library bee-playback-outcome-coverage-flow-item-breakdown-001 -- ./scripts/tests/phantom-playback-outcome-coverage.test.sh
```

Pure offline reporting: no plugin release or operator action required.
