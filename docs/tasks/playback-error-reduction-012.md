# playback-error-reduction-012

Day-12 successor in the ROI Priority 12 DAILY playback-error-rate-reduction loop.
Same analytical DIAGNOSE-AND-ENQUEUE contract as days 001–011 — read
`docs/tasks/playback-error-reduction-001.md` (shared series doc) FIRST, then the
day-11 analysis (`docs/tasks/playback-error-reduction-011-analysis.md`).

Day 11 re-queried the real per-attempt
`phantom_playback_outcome_total{job="jellyfin-phantom-library"}` counter and found
volume UNCHANGED from day 10 (still the same 3 rig-driven samples: 2 episode
`magnet_dead_stale` + 1 movie success; no organic traffic). It confirmed
badge-deadswarm-episode-parity-001 (the SIGNAL half) had landed, then diagnosed
the residual code-level gap: the eager re-probe worker
(`MarkStaleAvailableItemsDueAsync`) promotes stale-available rows only when the
candidate EXPIRED OUT, never when a candidate STILL EXISTS but is a confirmed
threshold dead swarm — so day 11 enqueued `availability-deadswarm-eager-reprobe-001`
(the ACTION half) to actively refresh the dead candidate before playback.

Day 12 tasks:
1. Check FIRST whether `availability-deadswarm-eager-reprobe-001` has landed.
2. Re-query `phantom_playback_outcome_total{job="jellyfin-phantom-library"}` in
   Mimir for accumulated volume
   (`kubectl -n monitoring exec deploy/grafana -c grafana -- curl -sf
   http://mimir.monitoring.svc:8080/prometheus/api/v1/query --data-urlencode
   'query=phantom_playback_outcome_total{job="jellyfin-phantom-library"}'`).
3. If volume is now sufficient (meaningfully more than the handful of rig samples,
   spread across causes), perform the series' first genuine flow × item_type ×
   cause ranking with real statistical weight and assess whether the eager-reprobe
   fix (once landed) has moved the episode `magnet_dead_stale` rate. If volume is
   still only the same rig-driven handful, note "no organic traffic yet"
   explicitly and continue the code-level investigation approach.
4. BASELINE FIRST (P5 discipline), RANK the dominant remaining failure cause, and
   APPEND ONE concrete fix task for the biggest win (its own design doc + a
   CHECKS.md-matching `Check:`).
5. Write the analysis doc at `docs/tasks/playback-error-reduction-012-analysis.md`.
6. APPEND the successor `playback-error-reduction-013 [TODO]` held with
   `not_before ≈ +24h` (daily cadence).

DIAGNOSE-AND-ENQUEUE ONLY — never implement the fix in this analytical pass.
