# playback-error-reduction-020: day-20 analysis

DIAGNOSE-AND-ENQUEUE ONLY. Baseline pinned to 2026-10-29T05:22:40Z,
Prometheus `time=1793251360`. Site identities and exact live commands are
in the hive change doc; no infrastructure identifiers are added here.

## Baseline first

The inactive/dev exporter changed from day-19 zeros to ONE cold-movie
success. Instant and 30d maximum vectors each contain 32 tuples, sum 1;
every other tuple remains zero. All 32 source timestamps are 1793251330.013,
age 29.987 seconds. The landed analyser reports OK, complete and coherent.
Inactive cumulative aggregate and cold-movie ratios are 0/1 = 0%; the other
three slices have zero denominators and undefined ratios. None is ranked.

Serving remains UNINSTRUMENTED, 0/32, with attempts/errors/rate unavailable.
The expected serving pod label is only an absent-workload marker, not an
invented OTLP exporter identity. The unfiltered family query finds only the
inactive exporter; a separate runtime-info query is empty. target_info still
identifies only that inactive exporter. Organic serving traffic is unmeasured,
not zero. The single inactive success is not attributed to an organic user:
bounded recent logs provide no event provenance. Do not call it improvement.

Ingress roles, both pod UIDs, container IDs, image digests, start times and
restart counts match day 19. Attribution therefore continues from the same
process and day-14 loaded-build receipt (0.5.7.7/source d9a58ec), not new
runtime metadata. No re-attribution to a new process was necessary.

The pinned historical rig 35d query still shows cold-episode
magnet_dead_stale=2 and cold-movie success=1. Those are historical maxima,
not organic volume, current-window increases or evidence of a reset.

## Existing work and ranking

Source-coherence is reviewed and landed at 38232c7747785c3e769eb70f6e2827e431c3e907,
present at this pass's base e93f3a2. The real analyser harness passes 104
offline cases, including source-skew rejection and independent identities.
Window-input and per-flow/item reporting remain landed. Do not duplicate them.

There is no defensible statistical ranking of production failure causes:
serving is unmeasured, inactive volume is one unattributed success, and each
slice needs its own attributed denominator. Measurement integrity is the
priority; no playback-cause optimization is justified by these observations.

## Exactly one distinct fix

Enqueue `playback-outcome-coverage-counter-continuity-001`. At the current
script's window-report loop, historical maxima are reported only for tuples
ABSENT from instant input. A still-present counter that dropped below its
own historical maximum is silently ignored. Offline reproduction: coherent
complete serving vector, cold-movie success=1000 and all errors=0, same-scope
window success=1000 and no_candidate=101, all source times 990 at evaluation
1000. Current output is exit 0, OK, 0.000000%, ranking "none (no errors)",
with no window warning. This is not a live fault or a claimed reset cause.

The fix's design requires a per-identity counter-discontinuity diagnostic,
exit 2 and withheld derived rates/rankings when a matched window maximum
exceeds the current counter. Never replace current counts with maxima or
claim an exact reset time/count; maxima cannot establish that. Preserve
partial-window support and window-only aging diagnostics. Extend the existing
analyser/harness, not a parallel tool. Check matches CHECKS.md `script-test`:
`./scripts/tests/phantom-playback-outcome-coverage.test.sh`.

## Successor and limits

Enqueue day 21 TODO, not before 2026-10-30T05:23:00Z (approximately +24h).
Re-baseline and verify attribution; specifically retain the changed inactive
one-success baseline rather than copying prior zeros. No implementation,
deployment, promotion, counter reset, synthetic live traffic or process mutation
occurred. No operator action is required.
