# TTFB Reduction 029

Diagnosed 2026-11-12 UTC after reading day 028 first. Diagnose-and-enqueue
only: no runtime, measurement, deployment or threshold changes.

## Newer Evidence

Two retained manifests are newer than run_start 1794313046:

| run_start | attempt_id | status / exit | discovery seconds / exit | measure seconds / exit |
| --- | --- | --- | --- | --- |
| 1794399429 | 42500fa3c0404e37809577a13fcc0e7e | failed / 1 | 0 / 1 | 0 / 1 |
| 1794399558 | a194dc18c5da49ccb2db09e18ae3721b | failed / 1 | 0 / 1 | 0 / 1 |

Both Nov 11 attempts executed checkout
`6b07a9fd4520eb9e89edc5558c44d7ae22a3eae6`. Both retain
`discovery_failure={reason:user-invalid-response,curl_exit_code:0,http_status:302}`,
empty targets and failures, and no push/guard stage. The scheduled Job failed
with BackoffLimitExceeded; its pods are gone. These are two failed batches,
not 0/36 flow samples. Together with day 028, six attempts on Nov 9-11 failed
before timing flows; only the newest two have durable reasons.

The target-discovery-reasons fix `f0da20b` is an ancestor of the executed
checkout and demonstrably retained the reason. Engine discovery requests
`/Users` before Channels, and its parser rejected that response. Curl success
does not imply a valid API response: HTTP 302 is a redirect, not the expected
user array. Neither the destination nor the response body is retained. Do not
infer an authentication provider, a bad credential, an Ingress cause, or the
cause of earlier anonymous failures. This is no longer an unidentified
episode-selection failure, and re-filing discovery attribution is unnecessary.

Request/list bounds `7e28c4a` are also ancestors of the executed checkout.
Neither new attempt records curl 28/HTTP 000 or a timed list failure. Bounds
were present, not demonstrated to have fired. Separately, neither attempt
hit the 1500s measurement wall: the measurement stage lasted 0s. Run-start
to discovery-start gaps were 108s and 67s, outside that measured stage, not
fast browsing or an outer measurement timeout. No newer successful latency
measurement exists.

## Metric Attribution

These historical query evaluations bind the flow push to the daily run-start:

| evaluation | run_start | flow push_time_seconds | daily push_time_seconds |
| --- | --- | --- | --- |
| 1793361200 | 1793359027 | 1793361169.4467623 | 1793361174.3147707 |
| 1794113444.263 | 1794053824 | 1794054641.644034 | 1794054646.763412 |
| 1794141100 | 1794140228 | 1794140993.9278193 | 1794140998.84607 |
| 1794461575.817 (current probe) | 1794399558 | 1794140993.9278193 | 1794399625.2364848 |

The current daily stage is measure exit 1 / 0s, but the flow push still equals
Nov 8's. Thus the current durations and zero errors are stale Nov 8 values,
not Nov 11 recovery. Nov 8 guard exit 3 and all twelve breach gauges equal 1:
completed measurement is not a guard pass.

## Durations And Errors

Requeried all three historical batches. Each cell is median seconds,
errors/runs. These are full HTTP-flow durations, not raw server TTFB.
The episode list/sort label measures the Shows root, not episode navigation.

| flow / type | Oct 30: 1793359027 | Nov 7: 1794053824 | Nov 8: 1794140228 |
| --- | --- | --- | --- |
| list_load / movie | 10.118952, 3/3 | 92.149758, 0/3 | 91.135839, 0/3 |
| sort_change / movie | 30.393210, 3/3 | 85.425209, 0/3 | 90.006179, 0/3 |
| info_open / movie | 0.041445, 0/3 | 0.053861, 0/3 | 0.058460, 0/3 |
| get_sources / movie | 0.029544, 0/3 | 0.042807, 0/3 | 0.039677, 0/3 |
| materialise / movie | 0.031143, 0/3 | 0.039514, 0/3 | 0.037035, 0/3 |
| play_materialised / movie | 0.031667, 0/3 | 0.043975, 0/3 | 0.058742, 0/3 |
| list_load / episode | 343.661371, 2/3 | 26.283003, 0/3 | 27.062847, 0/3 |
| sort_change / episode | 36.425254, 3/3 | 24.448986, 0/3 | 26.264750, 0/3 |
| info_open / episode | 0.048825, 3/3 | 0.037072, 0/3 | 0.044795, 0/3 |
| get_sources / episode | 0.030021, 3/3 | 0.027547, 0/3 | 0.035536, 0/3 |
| materialise / episode | 0.029920, 3/3 | 30.085435, 3/3 | 0.053166, 0/3 |
| play_materialised / episode | 0.032538, 3/3 | 0.028753, 3/3 | 0.033450, 0/3 |

HTTP-flow errors remain **23/36 -> 6/36 -> 0/36**, excluding failed batches.
Movie list improved 1.013919s between Nov 7 and 8, but movie sort worsened
4.580970s; Shows-root list/sort worsened 0.779844s/1.815764s. No sustained
browse speedup is established. Failed October browse is not a successful
latency baseline. Movie list/sort remain first optimization candidates when
fresh measurements exist; the existing movie-render DB-batching work is not
duplicated here.

Day 028 established the same episode was selected on Nov 7 and 8 despite
landed `020019b`. No new targets overturn that evidence. Do not attribute
HTTP success causally to that selection repair or claim real stream playback,
validated MediaSources, stream bytes, or cold materialisation.

## One Nonduplicate Fix

Enqueue **ttfb-loadtime-error-aware-ratchet**. This is a source-proven input
eligibility defect, NOT a claim that the two failed-discovery attempts reached
the guard or corrupted deployed thresholds.

At this checkout, `47-loadtime-flows.sh:601-605,791-804` adds zero-duration
error samples for exhausted-budget/fail-fast skips. With N=3, one timed-out
list request followed by two skips has median zero, runs=3, errors=3.
`loadtime-expo-to-measurements.py:39-63` ignores error/run counters;
`loadtime-daily-run.sh:363-367,395-412` reports PARTIAL yet applies the guard.
`RatchetEngine.cs:127-143,163-170` can tighten a positive ceiling to zero,
then treat zero as unseeded on the next run. Thus skipped work can erase a
previous bound and allow a slower later observation to reseed it upward.
Positive-duration failed samples can also falsely tighten a ceiling.

Preserve complete failed-batch publication and evidence, but make erroring or
unmeasured tuples ineligible to seed/tighten thresholds. Conservatively exclude
tuples with errors>0 rather than pretending the aggregate is a successful-only
median. Preserve real initialization and healthy ratcheting. Require the real
LIVE engine -> wrapper -> converter -> C# guard -> durable-registry integration
to prove a failed batch cannot lower a positive ceiling, and a later slow
healthy batch breaches the original bound without reseeding. Exercise movie
and episode, timeout skips, positive-duration errors and all-ineligible data.

Existing same-batch-guard review explicitly excludes error-counter-aware
ratcheting. Timeout work owns bounds/error accounting; durable-threshold work
owns storage, and push completeness owns publication validity. None supplies
this eligibility contract. An older request-bound doc claims budget failure
never changes ceilings; the current code does not enforce that claim. This
fix is distinct from DB batching, target selection and failure attribution.

## Independent Successor

Enqueue **ttfb-reduction-030**, no dependencies, not before
2026-11-13T05:30:00Z (about +24h). Read this report first. Inspect manifests
newer than 1794399558, follow durable user/302 failures without guessing their
cause, bind fresh flow metrics to their own push/run identity, and compare
durations AND errors with Nov 8, Nov 7 and Oct 30. Check bounds and the 1500s
wall separately. Prioritize movie list/sort once fresh measurements exist;
append one nonduplicate fix and independent day 031. Do not depend on this
day or its fix.

No behavioral regression test applies to this analytical report. Exact
read-only probe commands and results are in the private hive change doc.
No operator steps required.
