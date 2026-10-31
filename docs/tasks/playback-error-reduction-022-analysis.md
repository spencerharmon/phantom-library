# playback-error-reduction-022: day-22 analysis

DIAGNOSE-AND-ENQUEUE ONLY. Baseline pinned to 2026-10-31T05:26:41Z,
Prometheus `time=1793424401`, before selecting the follow-up. Exact site
identities and reproducible commands are in the hive change doc.

## Baseline and attribution

The card's earlier day-21 capture was superseded by its analysis: use
`1793342005`, not `1793337852`, as the previous evaluation.

Inactive/dev CHANGED. A new process started at 05:24:25Z, plugin 0.5.7.9,
source 59b784859b0b5070dca55771e2cb6df286b977bf. Startup provenance and
runtime-info agree on exporter identity, loaded version, source and MVID.
Its 32/32 coherent counters are all zero; source 1793424388.777 is 12.223s
old. Aggregate and all cold/warm movie/episode denominators are zero and
rates undefined, NOT 0%. Inactive rankings are withheld.

The preceding dev process (0.5.7.8/source 8858739, attributed by the day-23
TTFB receipt) is still returned by the instant query: 32 zero counters,
source 1793424257.131, age 143.869s > 120s. It is STALE, not a second serving
exporter; its rates and rankings are unavailable. The day-21 dev identity
is absent from instant results, with its one cold-movie success retained
only in the 30d window. That historical maximum is excluded from totals.
The new and preceding dev window vectors contain only zeros.

Serving process/image/ingress role are unchanged. Its absent-pod expected
entry remains an absence marker, not discovered exporter metadata. Unfiltered
family discovery plus separate runtime-info discovery return only the two dev
identities. Serving is UNINSTRUMENTED 0/32: organic attempts/errors/rate are
unmeasured, not zero. Bounded serving logs return no playback/provenance lines.

Day-21 RAW vectors were supplied to the landed interval analyser. All three
dev identities correctly report INTERVAL_DISCONTINUITY (old 32->0 tuples;
new identities 0->32). No cross-process subtraction, interval totals or
improvement is claimed. Previous endpoint age was 14.987s at its own capture.
The analyser exits 2 for real coverage/freshness/interval failures.

Historical rig 36d is now empty; separately labelled 37d still contains
cold-episode magnet_dead_stale=2 and cold-movie success=1. This is window
aging, not remediation. The companion gostream container also restarted
after OOMKilled at 05:11:33Z; there is no attributed outcome volume tying
that event to a playback cause. No action was taken against it.

## Ranking and exactly one fix

No production cause ranking is supportable. Fresh zero dev counters after a
process replacement do not demonstrate error reduction. Prioritize reliable
attribution rather than claiming a playback cause from these observations.

Interval freshness landed at 8843981; the existing harness passes 227 offline
cases. Do not duplicate freshness, continuity, coherence or interval delta.
Enqueue only `playback-outcome-coverage-ranking-attribution-001`, with its
same-named design doc and CHECKS.md script-test Check:
`./scripts/tests/phantom-playback-outcome-coverage.test.sh`.

Reproduced at the current source: complete fresh coherent serving-labelled
vectors with current evaluation/source 1000/990 and previous 600/590,
cold-movie success/timeout 900/100 versus 400/50, all other tuples zero,
produce exit 0, cumulative timeout ranking 100 and interval timeout ranking
50 (550 attempts, 50 errors, 9.090909%). Only CURRENT role can be supplied.
If the process was dev throughout those source samples and promoted at 995,
none of that measured volume is serving traffic. Promotion is an external
counterexample, NOT an event observed in today's deployment or encoded in
the reproduction. The interface cannot express the historical distinction.

The fix must require explicit attribution covering each ranked population,
not retroactively trust today's role. Preserve numerical snapshots/deltas as
unattributed and withhold rankings without that evidence. This is distinct
from build provenance and from arithmetic/freshness guards.

## Successor and scope

Enqueue day 23 not before 2026-11-01T05:26:41Z, exactly +24h from the baseline.
Re-query all identities, runtime provenance and ingress; use this capture as
previous while respecting per-identity freshness and process discontinuities.
No implementation, deployment, promotion, resets, synthetic live traffic or
process mutation occurred. No operator action is needed.
