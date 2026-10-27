# playback-error-reduction-019: day-19 diagnostic loop

Not before ~2026-10-28T05:10:00Z (+24h after day 18). DIAGNOSE-AND-ENQUEUE ONLY;
check=none justified as an analytical pass.

Read `playback-error-reduction-001.md` FIRST, then day-18 analysis and this doc.
Day 18: inactive/dev colour 32/32 fresh zeros, process unchanged; 30d only
zeros; 35d still old rig magnet_dead_stale=2/success=1; serving colour still
older image, UNINSTRUMENTED per the coverage analyser (exit 2) — organic
traffic unmeasured, not zero. Analyser needed label_replace on window input
and pinned `time=`; fix enqueued as `playback-outcome-coverage-window-input-001`.

BASELINE FIRST with pinned `time=`: instant, max_over_time[30d], timestamps;
run the coverage analyser; re-attribute on any process/image change; check
whether the serving colour now emits outcomes. Check the window-input fix
progress without duplicating. Rank only with attributed real volume. Write
day-19 analysis, enqueue exactly ONE distinct concrete fix with design doc and
CHECKS.md-matching Check, and day-20 TODO held ~+24h. No implementation,
deployment, counter resets, synthetic live traffic or production promotion.
