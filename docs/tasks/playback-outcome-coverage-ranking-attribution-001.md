# playback-outcome-coverage-ranking-attribution-001

## Problem and evidence

Day 22 found that `scripts/phantom-playback-outcome-coverage.sh` authorizes
cumulative and interval cause rankings from a single current `serving` role.
Identity and loaded-build provenance do not establish that earlier requests
were organic serving traffic. A same-process routing promotion can relabel
historical dev volume without changing counters, timestamps or identity.

Reproduced on 59b7848: exact identity instance=example.com, all 32 tuples,
current evaluation/source 1000/990, previous 600/590, current cold-movie
success=900/first_byte_timeout=100, previous 400/50, others zero, empty
window, expected role serving, max-age=120, min-attempts=100. Exit 0 prints
cumulative timeout ranking=100 and interval attempts=550/errors=50,
rate=9.090909%, timeout ranking=50. If promotion happened at 995, those
samples predate serving. The existing interface cannot record that fact.
This is an offline counterexample, not an assertion of a live promotion.

## Required change

Invariant: a current serving role never retroactively attributes cumulative
or interval counts; every emitted serving ranking requires explicit evidence
covering that exact identity and the whole population being ranked.

Extend the existing offline analyser, not a parallel report. Add explicit
per-identity attribution input scoped separately to the cumulative population
and to a specific source-time interval. A receipt is caller-supplied evidence,
not telemetry inference: document that the caller must establish organic
serving provenance and continuous serving role throughout the covered span.
Cumulative authorization must cover the entire counter population, not just
the most recent interval. Exact start/end source timestamps bind an interval
receipt to the supplied capture pair; matching endpoint roles alone cannot
exclude an intervening flip. Do not infer a lifetime start from evaluation time.

Missing, unknown, expired or partial attribution withholds the corresponding
aggregate and four slice rankings with an explicit unattributed diagnostic.
Keep valid numeric cumulative counts/ratios and endpoint deltas labelled as
unattributed observations; never call them organic or an improvement. Preserve
existing input validation, coverage/freshness/coherence/continuity failures,
independent identity/slice volume thresholds and inactive suppression. Reject
malformed or contradictory receipts as input errors; absence alone need not
make otherwise valid observations an input error. Old invocations remain
usable but cannot authorize rankings from `role` alone. No network access,
infrastructure discovery, deployment or counter resets belong in this tool.

## Acceptance

Check: `./scripts/tests/phantom-playback-outcome-coverage.test.sh`

Matches hive CHECKS.md `script-test`; extend the real CLI subprocess harness,
not a source grep or independent helper test. Add failing-before/passing-after
assertions for missing receipts, dev-to-serving promotion, serving-to-dev,
intervening role changes, partial interval coverage and mismatched identities
or source spans. Verify explicit whole-span organic-serving attribution can
enable rankings, while interval-only evidence never enables cumulative ranks.
Exercise cold/warm movie AND episode, aggregate versus per-slice thresholds,
healthy/unattributed peers, malformed receipts and no-previous compatibility.
Retain the 0%-stub rejection and input-immutability checks. Update CLI help and
the coverage report contract with neutral examples. This task implements only
attribution gating, not the existing freshness or runtime-info work.
