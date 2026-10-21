# Playback-error-reduction day 13

ROI Priority 12, DAILY DIAGNOSE-AND-ENQUEUE loop. Read
`docs/tasks/playback-error-reduction-001.md` FIRST, then
`docs/tasks/playback-error-reduction-012-analysis.md`: current outcome series
absent, historical maxima still consistent with 3 rig attempts, no organic
traffic yet observed in these counters. Restart is only a hypothesis; missing
series do not establish zero traffic. Other job metrics are present.

1. Check whether the EXISTING `playback-outcome-zero-seed-001` has landed,
   including its post-MeterProvider-subscription seeding and startup-order SDK
   collection/untouched Prometheus exposition tests. Do not duplicate that fix.
2. Separate three facts: source merged, local collection/exposition tested, and
   the measured deployment actually running that version with fresh Mimir
   exports. Record timestamped version/instance and export evidence if available;
   otherwise label deployment and export status unknown. A merge alone does NOT
   imply the instant query should contain seeded zeros. Do not deploy a fix as
   part of this analytical pass.
3. BASELINE FIRST: re-query
   `phantom_playback_outcome_total{job="jellyfin-phantom-library"}` in Mimir and
   its `max_over_time(...[30d])` companion. Record timestamps, instance labels,
   and other job-metric presence. Historical maxima are not reset-aware attempt
   totals. If interpreting a post-deployment rate, use an explicit time window
   and deployment/instance evidence with reset-aware counter increases; do not
   mix historical rig maxima into the new window.
4. Rank flow x item_type x cause statistically only with meaningful new volume.
   Otherwise state "no organic traffic yet observed in these counters" and
   continue code-level investigation without claiming missing series prove zero
   attempts. Seeded zeros mean no measured attempts, not a measurable 0% failure
   rate. Assess eager-reprobe efficacy only with a supported post-deployment
   baseline and enough episode attempts.
5. RANK and APPEND ONE concrete fix with its own design doc and CHECKS.md-matching
   Check, avoiding existing queued work. Write
   `docs/tasks/playback-error-reduction-013-analysis.md`. APPEND successor
   `playback-error-reduction-014` TODO held `not_before` approximately +24h.

Day 13 itself already exists with `not_before=2026-10-22T04:02:46Z`; day-12 review
rework does not create another task or postpone that schedule.
DIAGNOSE-AND-ENQUEUE ONLY: never implement the fix in this pass.
