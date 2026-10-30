# playback-outcome-coverage-interval-delta-001

ROI Priority 12 measurement fix from the day-21 analysis. The daily loop must
measure the playback error rate over each day, but
`scripts/phantom-playback-outcome-coverage.sh` only reports cumulative
counter snapshots; day-over-day trends are computed by hand in prose.

## Change
Extend the EXISTING analyser and harness (no parallel tool):
- New optional inputs `--previous-instant` and `--previous-timestamps`: raw
  API JSON of the same selectors captured at an earlier pinned `time=`.
  Both or neither; one alone is a usage error (exit 2).
- Per expected identity, per exact tuple: interval = current - previous.
  Report interval aggregate and the four flow/item slices
  (attempts/errors/error_rate), each ranked only on its own interval volume
  above `--min-attempts`, serving only, as for cumulative output.
- Withhold that identity's interval rates/rankings and exit 2 with an
  `INTERVAL_DISCONTINUITY` line naming tuple/current/previous when: any
  current < previous; tuple sets differ; the previous source timestamp is not
  strictly earlier than the current one. Zero interval attempts => undefined.
- Never substitute window maxima or infer reset time/count. Without the new
  flags output is byte-identical to today (harness asserts this).
- Update the `--help` contract.

## Check
`./scripts/tests/phantom-playback-outcome-coverage.test.sh` (CHECKS.md
`script-test`): add red/green cases (normal delta, decreased counter,
tuple-set mismatch, non-monotonic timestamps, one-flag usage error,
independent healthy identity preserved, absent-flag regression).
No live traffic, resets, deployment or promotion.
