# playback-error-reduction-022: day-22 diagnostic loop

Not before 2026-10-31T05:24:00Z, approximately +24h after day 21.
DIAGNOSE-AND-ENQUEUE ONLY; check=none justified by analytical output.

Read playback-error-reduction-001.md FIRST, then day-21 analysis and this doc.
Day 21 pinned time=1793337852: inactive/dev 32/32 fresh coherent tuples
(1.986s), still one cold-movie success and all other zeros, instant and 30d
maxima agree, no counter discontinuity. Serving UNINSTRUMENTED; organic
traffic unmeasured, not zero. Processes/ingress unchanged since day 19.

BASELINE FIRST with one pinned time= for instant, max_over_time[30d],
timestamps, the coverage analyser, ingress roles and process identity;
re-attribute on any process change. If
`playback-outcome-coverage-interval-delta-001` has landed, also feed the
day-21 pinned capture (time=1793337852) as the previous vector and report
interval rates; otherwise inspect its progress without duplicating.
Rank only with attributed real volume and independent slice denominators.
Write day-22 analysis, enqueue exactly ONE distinct fix with design doc and
CHECKS.md-matching Check, then day-23 TODO ~+24h. No implementation,
deployment, resets, synthetic live traffic or promotion.
