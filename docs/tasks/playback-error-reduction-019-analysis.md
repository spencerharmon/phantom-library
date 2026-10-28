# playback-error-reduction-019: day-19 analysis

DIAGNOSE-AND-ENQUEUE ONLY. Baseline captured at 2026-10-28T05:14:55Z
(Prometheus evaluation time 1793164495). Read the series contract, day-18
analysis and day-19 guidance first. Site-specific commands and identities
are recorded in the hive change doc, not this public source tree.

## Baseline first

Instant outcomes and `max_over_time(phantom_playback_outcome_total{...}[30d])`
each return 32 inactive/dev tuples, sum zero. The matching `timestamp(...)`
vector has 32 tuples with one source time, 1793164450.014: age 44.986 seconds.
All queries use the same explicit `time=1793164495`.

The landed coverage analyser reports inactive `OK coverage=32/32`, zero
attempts/errors and undefined error rate for the aggregate and all four
cold/warm movie/episode slices. Serving is `UNINSTRUMENTED coverage=0/32`,
with attempts/errors/rate unavailable; exit 2 is the expected coverage failure.
Its expected serving pod entry is only an absent-workload marker, NOT an
invented exporter instance. An unfiltered outcome-family query independently
finds only the attributed inactive exporter.

Both colour processes, pod UIDs, image digests, start times and restart
counts match day 18; ingress still assigns the same serving/inactive roles.
Recent logs (3000 lines each) contain no new provenance/exporter receipt.
Runtime-info remains absent; target_info has only the inactive exporter.
Attribution continues from the unchanged process and day-14 loaded-build
receipt (0.5.7.7/source d9a58ec), not newly observed runtime metadata.

The pinned 35d historical rig query still returns cold episode
`magnet_dead_stale=2` and cold movie `success=1`. These old maxima are neither
window increases nor organic production volume. Their absence from 30d is
window aging, not improvement or a counter reset.

## Existing work and priorities

`playback-outcome-coverage-window-input-001` is DONE, landed at `453e894`;
`playback-outcome-coverage-flow-item-breakdown-001` is DONE at `1841b8d`.
Both are present at this pass's base `9d7dfb2`. The existing analyser harness
passes 91 cases, including raw window vectors and independently gated slices.
Neither task, coverage-report, seeding nor runtime-info is duplicated.

No statistically defensible cause ranking is possible: inactive denominator
is zero; serving traffic is unmeasured, not zero. The historical rig's two
failures do not establish the dominant real-user cause. Measurement integrity
remains the priority, not an asserted playback-rate win.

## Exactly one distinct fix

Enqueue `playback-outcome-coverage-source-coherence-001`. The existing analyser
checks common query evaluation time and individual freshness, but not source
timestamp coherence within an identity. An offline reproduction with all 32
tuples, a 1000-success cold-movie counter timestamped 880, and 101 failures
plus the remaining zeros timestamped 990, at evaluation/now 1000, returns
`OK`, exit 0, rate 9.173479% and ranking `no_candidate:101`. Both samples are
fresh under max-age 120, but their 110-second skew means no common collection
boundary was established. This is a demonstrated tooling gap, not a claim
that today's coherent live zeros have skew or that production has 101 errors.

The adjacent design requires a fail-closed per-identity coherence diagnostic,
with numeric rates/rankings withheld on mismatch, tested across all four
slices. Check: `./scripts/tests/phantom-playback-outcome-coverage.test.sh`
(CHECKS.md `script-test`). Extend the one existing analyser; no second tool.

## Successor and limits

Enqueue day 20 TODO, not before 2026-10-29T05:15:00Z, approximately +24h.
Re-baseline first and re-attribute whenever process identity changes. Use
landed fixes without duplicating them. No implementation, deployment,
promotion, counter reset, synthetic live traffic or process mutation occurred.
No operator action is required for this analytical handoff.
