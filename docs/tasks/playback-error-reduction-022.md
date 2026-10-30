# playback-error-reduction-022: day-22 diagnostic loop

ROI Priority 12, DAILY playback-error-rate-reduction loop, day 22.
Existing successor not before 2026-10-31T05:24:00Z (approximately +24h after
day 21; retained from the earlier attempt rather than enqueued twice).
DIAGNOSE-AND-ENQUEUE ONLY; check=none justified by analytical output.

Read docs/tasks/playback-error-reduction-001.md FIRST, then day-21 analysis
and this doc. Day21 pinned time=1793342005: inactive/dev 32/32 fresh coherent
counters, source=1793341990.013 (age 14.987s). Still one cold-movie success,
all other tuples zero; instant and 30d maxima agree. Not attributed organic
volume. Serving remains UNINSTRUMENTED; organic traffic unmeasured, not zero.
Both process identities and ingress roles unchanged; runtime-info absent.

Historical rig 35d query is now EMPTY; a separately labelled 36d query still
has cold-episode magnet_dead_stale=2 and cold-movie success=1. This is aging,
not improvement. Re-query rather than perpetually copying old maxima.

BASELINE FIRST: pin a common time= for instant, max_over_time[30d] and source
timestamps; run the coverage analyser; verify live ingress/process/image
attribution and independently check serving outcomes. Re-attribute on process
change. Expected absent-serving pod labels are only an absence marker, not
invented exporter metadata. Inspect provenance before treating positives as
organic. Rank only with attributed real volume and independent slice denominators.

Counter continuity e09530d and interval delta e5ad2c0 landed; 220-case harness
passes. Day20-to-day21 interval used real endpoint vectors and source span
90660s: inactive aggregate and all slices have zero attempts/errors and undefined
rates. Endpoint source ages 29.987s/14.987s were fresh at their own captures;
this does not prove absence of intervening resets or organic serving traffic.
If using previous inputs, verify their freshness at THEIR evaluation time.
Check playback-outcome-coverage-interval-freshness-001 progress without
duplicating it. Source coherence, window-input and flow/item fixes also landed.

Write day-22 analysis, enqueue exactly ONE distinct concrete fix with design
doc and CHECKS.md-matching Check, then day-23 TODO approximately +24h.
No implementation, deployment, resets, synthetic live traffic or promotion.
