# playback-error-reduction-018: day-18 analysis

DIAGNOSE-AND-ENQUEUE ONLY. Observed 2026-10-27 06:02 UTC, after reading the
series contract, day-17 analysis and day-18 guidance. Site-specific receipts
and commands are in the hive change doc, not this public source tree.

## Baseline first

At common Prometheus evaluation time 1793080920, raw instant outcomes and
`max_over_time(phantom_playback_outcome_total{...}[30d])` both return exactly
32 series from the same inactive/dev instance, sum 0. The companion
`timestamp(...)` vector has the same 32 tuples, all source samples aged
49.986 seconds. The exact instance/job-scoped canonical-product endpoint
check passes. These are fresh zero seeds, not measured successful playback.

The serving production colour still contributes no outcome series. Its
organic traffic is unmeasured, not zero. The measured inactive denominator
is zero and its rate is undefined; the production denominator/rate are
unavailable. No population error rate, statistical cause ranking, or
improvement can be claimed.

A separate 35d historical query still contains the prior rig's cold episode
`magnet_dead_stale=2` and cold movie `success=1`. The 30d exclusion remains
window aging, not improvement, new traffic, or a counter reset. Historical
maxima are not window increases and never enter today's denominator.

## Attribution and existing work

Live ingress still selects the same serving colour. Inactive pod UID,
container ID, image digest, start time and zero restarts match day 17.
Serving process/image are also unchanged. Recent logs (last 3000 lines per
colour) provide no new exporter/provenance receipt. Attribution therefore
continues from the day-14 loaded-build receipt (0.5.7.7, source d9a58ec),
not fresh runtime metadata. A suffix-tolerant runtime-info query is empty;
`target_info` contains only the existing inactive exporter.

`playback-outcome-coverage-report-001` has landed at `6915de4`, with raw
window-vector correction `453e894`. Its real harness passes all 72 cases.
Used the analyser on this capture: inactive `OK coverage=32/32`, attempts=0,
errors=0, rate undefined; expected serving workload `UNINSTRUMENTED
coverage=0/32`, rates unavailable (exit 2). The absent serving exporter has
no known instance ID: its expected entry uses the observed pod identity,
not an invented OTLP instance. This entry is a missing-workload marker;
the independent unfiltered outcome query and attribution establish the gap.
Neither the analyser nor its raw-window repair is re-filed.

## Priority, not statistical ranking

1. Measurement integrity remains the defensible priority: no attributed
   production volume supports selecting a playback-path optimisation.
2. The two historical rig failures are the only positive failure bucket,
   insufficient to rank real user causes or estimate a production rate.
3. Current measurement tooling pools all four flow/item slices. That loses
   the series' required cold/warm and movie/episode breakdown even when
   valid traffic eventually arrives.

## Exactly one distinct fix

Enqueue `playback-outcome-coverage-flow-item-breakdown-001`: extend the
existing analyser with four per-flow/item summaries and independently
volume-gated rankings, keeping its explicitly labelled instance aggregate.
Source inspection of the current sums/ranking shows it pools every tuple;
existing parity tests exercise one positive slice at a time, not mixed
populations. The adjacent design specifies a mixed-population regression:
1000 warm-movie successes plus two cold-episode failures must not hide an
undefined empty slice or unlock the two-attempt slice's ranking.

Check: `./scripts/tests/phantom-playback-outcome-coverage.test.sh`, matching
CHECKS.md `script-test`. This is a new breakdown requirement, not a duplicate
of coverage, raw-vector parsing, runtime-info, seeding or synthetic guards.
It does not itself instrument or promote production, nor claim a rate win.

## Successor and scope

Day 19 (`playback-error-reduction-019`) was already enqueued TODO by an
earlier attempt, not before 2026-10-28T05:10:00Z, approximately +24h.
The duplicate add was refused; retain that single existing successor and
its schedule. That attempt also filed the now-landed raw-window repair;
this pass does not duplicate it. Recheck baseline and serving
coverage first; re-attribute on any process change; use landed measurement
tools without duplicating them. No implementation, deployment, promotion,
synthetic live traffic, counter reset, or process mutation in this pass.
