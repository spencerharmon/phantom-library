# playback-error-reduction-014: corrected successor guidance

This clarifies the existing day-14 task, not a new successor. Keep
`not_before=2026-10-23T04:05:00Z` and the DIAGNOSE-AND-ENQUEUE contract.

Read the shared series contract and day-13 analysis first. Baseline the instant
outcomes and 30-day historical maxima before ranking. No organic traffic yet
observed is not proof of no traffic; historical maxima are not new attempts.
Rank statistically only when attributed organic volume supports it.

Check the existing `playback-outcome-zero-seed-deploy-001` without duplicating it.
Require runtime loaded-plugin/build and exporter-to-Mimir instance attribution
FIRST. Pod dates, image digests without provenance, and SDK-only `target_info`
do not prove deployment lag. Treat lag as a hypothesis until attributed.

Verify all 2 flows x 2 item types x 8 causes on the intended inactive/dev
instance, using fresh actual sample timestamps, exact tuple equality, and zero
values for untouched tuples, while preserving real positive counters. Other
colors, rig instances, duplicates, and historical samples cannot satisfy the
fix. Dev validation does not establish production error rates.

Any separately queued deployment must re-resolve live Ingress apex ownership
and target only inactive/dev; active/prod digests and promotion are operator-gated.
The daily pass itself deploys nothing. Keep exactly one next fix and one next
daily successor; keep site identifiers and deployment commands in hive docs.
