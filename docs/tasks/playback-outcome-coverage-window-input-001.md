# Playback outcome window input

The offline coverage analyser now accepts a missing `__name__` on `--window`,
matching `--timestamps`: Prometheus `max_over_time` and `timestamp` drop the
metric name. An explicitly supplied name must still equal
`phantom_playback_outcome_total`; the raw `--instant` input still requires it.
No `label_replace` workaround is needed. Window data remains historical context,
never current instrumentation, attempts, or evidence of improvement.

Capture all three vectors using the same explicit `time=<evaluation_timestamp>`
parameter on `/api/v1/query`, with the same scope selector. Independent defaults
to "now" can yield mismatched timestamps and are rejected. The analyser's `--now`
only controls offline age checks; it does not align captures. See the
[input contract](playback-outcome-coverage-report-001.md#offline-invocation-and-input-contract)
for the queries and identity requirements.

## Verification

```sh
beehive submodule worktree exec phantom-library bee-playback-outcome-coverage-window-input-001 -- ./scripts/tests/phantom-playback-outcome-coverage.test.sh
```

The new raw-window regression fails before the validator change with
`window: expected phantom_playback_outcome_total family` (exit 1 instead of 0).
Coverage includes all movie/episode tuples for both flows, raw historical-only
windows, absent serving instrumentation, explicit foreign/empty names on all
three vectors, missing instant names, and mismatched window/timestamp evaluation
times. Existing named-window inputs remain covered. All tests are offline and
assert input files remain unchanged. No deployment or operator action is needed.
