# playback-error-reduction-015: day-15 diagnostic loop

Not before 2026-10-24T04:10:00Z, approximately 24 hours after day 14.
DIAGNOSE-AND-ENQUEUE ONLY; `check=none` because the deliverable is analysis
and queued work, not a runtime change.

Read `playback-error-reduction-001.md`, then
`playback-error-reduction-014-analysis.md`. Day 14 proved attributed inactive/dev
deployment of the seed, with all 32 fresh tuples zero. Thirty-day maxima still
contained only the historical rig episode/cold failure maximum 2 and movie/cold
success maximum 1. No organic traffic yet observed; no defensible population
error rate or statistical cause ranking exists.

Re-query instant outcomes and `max_over_time(...[30d])` BEFORE choosing a fix.
Check current loaded-build/exporter identity and exact fresh tuple coverage;
if the process changed, re-attribute rather than blindly reusing the prior ID.
Check progress of `otlp-runtime-info-001` without duplicating it. Its metric is
diagnostic metadata, not organic playback volume. Keep dev, rig and production
observations distinct; never count seed zeros as attempts or maxima as deltas.

Rank statistically only with attributed real volume; otherwise explicitly say
'no organic traffic yet' with the observation limitation. Write day-15 analysis,
enqueue exactly one distinct concrete fix with design doc and CHECKS.md-matching
Check, and enqueue day 16 with not_before approximately +24h. No implementation,
deployment, resets, fabricated traffic or active/prod promotion in this pass.
Site-specific evidence belongs in hive docs, not source-repo documentation.
