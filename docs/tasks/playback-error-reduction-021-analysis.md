# playback-error-reduction-021: day-21 analysis

DIAGNOSE-AND-ENQUEUE ONLY. Baseline pinned to 2026-10-30T06:33:25Z,
Prometheus `time=1793342005`. Exact live commands and site identities are
in the hive change doc, not this source repository.

## Baseline first

Inactive/dev is unchanged from day 20: 32/32 complete, coherent counters,
one cold-movie success and all other values zero. Instant and 30d maximum
vectors agree. Source timestamp 1793341990.013 is 14.987 seconds old.
The current analyser reports OK for this exporter. Aggregate and cold-movie
cumulative ratios are 0/1 = 0%; the other three slices have zero denominators
and undefined ratios. All inactive rankings are withheld.

Serving remains UNINSTRUMENTED, 0/32, attempts/errors/rate unavailable.
An unfiltered family count and target_info identify only the inactive
exporter; a separate runtime-info query is empty. The serving pod entry in
the expected input is an absence marker, not a discovered exporter identity.
Both Jellyfin process identities, image digests, start times, restart counts
and ingress roles match day 20. Attribution therefore continues from the
unchanged process and day-14 loaded-build receipt (0.5.7.7/source d9a58ec).
Bounded recent logs yield no playback/provenance receipt. Organic production
traffic is unmeasured, not zero; the inactive success is not attributed organic
traffic and cannot demonstrate improvement.

NEW historical observation: the same rig selector over 35d is now empty.
A separately labelled 36d query still returns cold-episode
magnet_dead_stale=2 and cold-movie success=1. This is window aging, not
disappearance of failures through a fix. Never merge these maxima into totals.

The landed interval mode was also run using RAW day-20 instant/timestamp
responses at time=1793251360 and today's responses. Its inactive interval is
90660.000 source seconds with attempts=0, errors=0 and undefined rate for the
aggregate and all four slices. Previous source age at its own capture was
29.987s, so both actual endpoints satisfy the 120s policy. This is a difference
between observed cumulative endpoints, not proof of no intervening resets or
organic traffic. Serving remains independently unavailable. Exit 2 is the
expected missing-serving coverage result, not a playback failure.

## Existing work and ranking

Counter continuity landed at e09530d7f9ffa697b4aef3528c1ed25afb8bdbab, including
the day-20 reproduction. Interval deltas landed at this pass's base
e5ad2c051f52cfbf6a02ddcb1592572033dc1b2e. Source coherence (38232c7), raw window
input and independent flow/item reporting also remain present. Fresh execution
of the existing harness passes 220 offline cases; do not duplicate these tasks.

No statistically defensible production cause ranking is possible: serving
volume is unavailable, inactive cumulative volume is one unattributed success,
and each slice needs its own attributed denominator. Prioritize trustworthy
measurement, not an unsupported playback-cause optimization.

## Exactly one distinct fix

Enqueue `playback-outcome-coverage-interval-freshness-001`, designed in the
same-named task doc. The interval mode validates earlier source ordering and
coherence but never checks previous source age at its own evaluation. Offline
reproduction with current evaluation/source 1000/990 and previous 600/400
passes despite previous age 200 > max-age 120. Complete serving vectors with
current success/timeout 900/100 and previous 400/50 produce exit 0, interval
attempts=550, errors=50, rate=9.090909% and timeout ranking=50.

Require per-identity previous-endpoint freshness, exit 2 and withheld interval
rates/rankings for stale previous samples. Keep healthy current cumulative
reports and healthy peers. Evaluate old age against OLD evaluation, never
today's --now. This is distinct from counter decreases, coherence and tuple
coverage; no implementation is included here. The existing end-to-end script
harness is the CHECKS.md script-test framework:
`./scripts/tests/phantom-playback-outcome-coverage.test.sh`.

## Successor and limits

Day 22 was already enqueued by an earlier attempt, not before
2026-10-31T05:24:00Z (22h50m35s after this refreshed capture, approximately +24h).
Retain that single successor and refresh its design doc rather than duplicate it.
Re-baseline rather than copying zeros or historical 35d maxima. Check the new
freshness task's progress without duplication. No deployment, implementation,
promotion, counter reset, synthetic live traffic or process mutation occurred.
No operator action is needed.
