# playback-error-reduction-016: day-16 analysis

DIAGNOSE-AND-ENQUEUE ONLY. Read the shared series contract, day-15 analysis
and day-16 guidance first. Observed 2026-10-25 around 04:33 UTC.

## Baseline first

Instant outcomes at evaluation time 1792902781.469 returned API success,
32 canonical tuples, all zero on the same inactive/dev instance. The existing
seed-deployment endpoint Check independently passed exact instance/job-scoped
equality with two flows x two item types x eight causes, finite nonnegative
values and actual sample ages in [0,120) seconds. Global count and query
evaluation timestamps alone are not freshness or coverage evidence.

The simultaneous max_over_time[30d] query now returns only those 32 zeros,
not yesterday's 34 series. A targeted max_over_time[35d] query still returns
the prior rig's cold episode magnet_dead_stale=2 and cold movie success=1.
This is consistent with old samples leaving the rolling 30-day window, not
error reduction, new attempts, or a counter reset. No reset was performed.

**No organic traffic yet observed.** Current observed denominator is zero;
error rate is undefined, not 0%. Maxima are not reset-aware increments. No
population rate or statistical ranking can be established; absent production
series cannot exclude uninstrumented traffic.

## Attribution and existing fixes

Live ingress still maps the observed exporter to inactive/dev. Pod UID,
container ID, image digest, start time and zero restarts match day 15 and the
day-14 loaded-build receipt. Startup provenance logs again return no matching
lines. Attribution is continuity against that receipt, not freshly recovered
assembly metadata: version 0.5.7.7, source
`d9a58ece9e7111f86663efe0c608147c099205a3`. Seed ancestry check exits 0.
Re-attribute independently if process identity changes. Site identifiers and
literal replay commands are in the hive change doc.

`otlp-runtime-info-001` remains DONE; gauge implementation `87a022b` is present
and the real provider subscribes to its meter. Exact and suffix-tolerant live
queries still return successful empty vectors. Source completion is not live
ingestion; the observed process remains the older build. Do not re-file the
gauge, and never count its eventual value 1 as playback volume.

`playback-outcome-synthetic-push-guard-001` is now DONE, implementation
`0c380fa` merged by `04d3c69`. Its real-emitter harness was rerun: **30 passed,
0 failed**, including intercepted transport and promtool parsing. This proves
the source guard, not a deployed pipeline revision or absence of contamination.

## Priority, not statistical ranking

1. Measurement integrity: a syntactically valid incomplete batch reaches the
   group-replacing PUT despite the landed synthetic-family guard.
2. Historical cold-episode magnet_dead_stale remains the sole known positive
   failure bucket, now only visible in the wider diagnostic window. Two rig
   failures cannot rank organic causes or justify a playback-path change.
3. Other causes and warm flows have no positive current observed volume.

## Exactly one fix

Enqueue `loadtime-push-complete-batch-validation-001`, adjacent design doc.
The real emitter accepted one movie/materialise duration sample with no run
or error records, no episode, and no other flow; an exported curl function
intercepted one PUT and the command exited 0. No network occurred. PUT replaces
the entire group, so this permits incomplete data to remove previous coverage.
This is a reproduced transport-validation risk, not observed live data loss.

Require a complete measurement batch before transport. The existing synthetic
guard validates reserved-family provenance, not completeness. The same-batch
daily guard ensures the ratchet judges the pushed batch, not that a truncated
batch cannot be pushed. Newline validation checks syntax, not completeness.
The follow-up must exercise the actual emitter, not only a detached validator.

## Successor and scope

Enqueue `playback-error-reduction-017` TODO for 2026-10-26T04:40:00Z,
approximately +24h. Baseline and attribution first; compare rolling windows
honestly and inspect existing fixes without duplication. This analytical pass
is check=none. No implementation, deployment, synthetic live traffic, counter
reset, process mutation, or production promotion; no error-rate improvement
is claimed.
