# playback-error-reduction-014: day-14 analysis

DIAGNOSE-AND-ENQUEUE ONLY. Observed 2026-10-23 at approximately 04:08 UTC.
Read the shared series contract and corrected day-13/day-14 guidance first.

## Baseline before ranking

Instant outcomes returned API success at evaluation time 1792728482.118:
32 canonical tuples on the attributed inactive/dev exporter, all zero.
Every combination of two flows, movie/episode, and eight causes is present.
The existing seed-deployment Check independently passed its exact tuple,
instance, finite nonnegative value, untouched-zero and actual sample-age
([0,120) seconds) assertions. The global count is also 32, but that weaker
predicate alone is not acceptance evidence.

The 30-day `max_over_time` query at 1792728482.121 returned 34 series:
the current instance's 32 zeros and these unchanged historical rig maxima:

| Flow | Item type | Cause | Historical maximum |
| --- | --- | --- | --- |
| materialise_then_play | episode | magnet_dead_stale | 2 |
| materialise_then_play | movie | success | 1 |

**No organic traffic yet observed.** The current observed denominator is zero,
so its error rate is undefined, not 0%. The historical maxima are neither new
attempts nor reset-aware totals; do not combine them with the dev series to
claim a population error rate. Neither these zeros nor absent production
series prove that uninstrumented production traffic did not occur.

## Deployment attribution

The seed deployment has landed on inactive/dev, not production. The current
container started 2026-10-22T08:03:07Z, after the seed history milestone at
2026-10-21T07:33:32Z, with a new digest matching the recorded producer receipt.
Startup provenance independently identifies loaded plugin 0.5.7.7, source
`d9a58ece9e7111f86663efe0c608147c099205a3`, its module MVID and exporter identity.
`f402fa9` (seed implementation) is an ancestor of that loaded source revision.
Pod UID/container ID match the receipt, with zero restarts. These facts plus
the strict current endpoint check establish attribution; pod age alone does not.

Live Ingress still assigns the newly deployed instance to inactive/dev.
The exporter logs direct HTTP/protobuf OTLP ingestion into Mimir, not an
assumed collector hop. Exact site identifiers, commands and receipt links
are in the hive change doc. No deployment or promotion was performed here.

## Priority ranking, not statistical ranking

1. Durable metrics-queryable attribution: current `target_info` contains only
   SDK metadata and service identity, not loaded plugin/build metadata.
   Startup provenance logs solved today's attribution, but queries alone
   cannot establish it once the startup log is unavailable.
2. Historical episode/cold `magnet_dead_stale` remains the only positive
   failure bucket (maximum 2). This is insufficient to rank organic causes
   or judge the efficacy of previous candidate-quality fixes.
3. Other causes and warm flows have no positive observed current volume.

## Exactly one next fix

Enqueue `otlp-runtime-info-001`, design `otlp-runtime-info-001.md`: export a
traffic-independent loaded-assembly information gauge through the actual
metrics provider. This extends the existing log-only provenance without
duplicating zero seeding, deployment acceptance, or playback pipeline tests.
The current source logs provenance but emits no such gauge; a live query for
`phantom_runtime_info` also returned API success with an empty result.
Its real-provider regression Check matches the approved `dotnet-test` framework.
It is a measurement-enabling priority, not a claimed playback-rate improvement.

## Successor and verification

Enqueue `playback-error-reduction-015`, not before 2026-10-24T04:10:00Z.
Repeat baseline first, distinguish dev/rig from organic production traffic,
and re-attribute if the process changes. Preserve the exact fresh 32-tuple
contract rather than reverting to a global count threshold.

This analytical documentation change is `check=none`: no behavioral regression
test is applicable. Read-only endpoint checks and provenance observations were
actually performed; no synthetic attempts, resets or production changes occurred.
