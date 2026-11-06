# Pinned Playback Outcome Capture

`scripts/phantom-playback-outcome-capture.sh` wraps the existing
[offline coverage analyser](playback-outcome-coverage-report-001.md). It makes
only three read-only Prometheus instant-query GETs; it never starts playback,
uses cluster credentials, deploys, promotes, or creates attribution receipts.
Requirements: Linux (atomic `renameat2(RENAME_NOREPLACE)`), Bash, Python 3 standard
library, and the matching coverage script beside the entrypoint.

## Capture

Supply a Prometheus **base URL**, an explicit decimal Unix evaluation time, an
explicit role inventory, and a destination directory that does not yet exist.
Its parent must exist. For the first baseline omit `--previous`; subsequent
captures use the previous bundle, not fresh queries at the previous time.

```bash
./scripts/phantom-playback-outcome-capture.sh capture \
  --endpoint https://prometheus.example.com \
  --time 1800000000.2500 \
  --roles /evidence/roles.json \
  --previous /evidence/day-before \
  --output /evidence/today
```

Example inventory (the labels must exactly match the exported identity):

```json
[
  {"labels": {"instance": "serving.example.com", "job": "playback", "replica": "one"}, "role": "serving"},
  {"labels": {"instance": "retired.example.com", "job": "playback", "replica": "old"}, "role": "inactive"}
]
```

Identity is every exported label except `__name__`, `flow`, `item_type`, and
`cause`. Do not collapse it to `instance`. Supply an explicit `serving` or
`inactive` mapping for the union of current, window, previous metrics and previous
inventory identities. Missing mappings fail closed; roles are not inherited.
At least one serving identity is required. Keep serving entries even when they
have no instrumentation: an empty set of vectors publishes `UNINSTRUMENTED`, not
a healthy zero. Historical/inactive identities remain visible in reports.

All three requests use the **same literal** `--time`:

- `phantom_playback_outcome_total`
- `max_over_time(phantom_playback_outcome_total[30d])`
- `timestamp(phantom_playback_outcome_total)`

Every returned sample evaluation must equal that time, even if the three
responses agree with each other on some other time. Empty vectors have no sample
evaluation to assert; their requested time is recorded in the manifest. Source
sample times remain untouched and are validated by the coverage analyser.

Optional `--max-age` (default `120`) and `--min-attempts` (default `100`) are passed
to the analyser; `--now` is always the pinned evaluation, not replay wall time.
`--timeout` bounds each HTTP operation (default `30` seconds, maximum `3600`).
Redirects and endpoints containing credentials, a query, or a fragment are
refused. No authentication material is embedded or requested by this tool.

Optional `--attribution /evidence/receipts.json` copies caller evidence byte for
byte and lets the existing analyser validate it. A serving role does **not**
attest historical organic traffic. Without matching receipts, healthy counts
are unattributed observations and rankings remain withheld.

## Exit Status

| Exit | Meaning | Destination |
| --- | --- | --- |
| `0` | Valid capture, healthy analyser result | Published |
| `2` | Valid capture with unhealthy coverage/freshness/continuity | Published with exact analyser report |
| `1` | Transport, API, identity, time, integrity, or write failure | Not published before atomic rename |

CLI usage errors use argparse's exit `2` but never publish. Automation should
distinguish a successfully published bundle from a usage error, not treat every
exit `2` as retained evidence. Stdout and stderr for a valid capture are the
analyser's exact bytes, including on coverage exit `2`.

HTTP failures, warnings/infos, malformed/duplicate JSON, truncated responses,
invalid receipts, missing roles, mixed or wrong evaluation times, incomplete
bundles and hash mismatches are invalid inputs. Partial metric **coverage** is
different from a partial **bundle**: the former is a legitimate exit-2 report,
the latter is never published.

## Bundle And Replay

```bash
./scripts/phantom-playback-outcome-capture.sh replay /evidence/today
```

Replay requires no network or original input paths. The entire bundle can be
moved to another directory or machine with the matching analyser revision.

- `instant.json`, `window.json`, `timestamps.json`: exact HTTP response bodies.
- `roles.json`, optional `attribution.json`: exact caller input bytes.
- `report.txt`, `stderr.txt`: exact analyser outputs.
- `manifest.json`: version `1`, exact CLI arguments, literal evaluation time,
  thresholds, queries/URLs and request start/end Unix nanoseconds, source/output
  SHA-256 hashes, analyser source SHA-256, previous manifest hash and analyser exit.
- `manifest.sha256`: SHA-256 of the exact manifest bytes plus a newline.
- `previous/`: when provided, the complete verified previous bundle, recursively
  including earlier history, unchanged. The original bundle is never modified.

Replay and previous-bundle ingestion verify every hash, reject unexpected files
and symlinks, validate query/evaluation metadata and strict chronological order,
and reproduce the report and exit using the existing analyser on private byte
snapshots. They refuse an analyser-version mismatch rather than silently change
historical calculations. The analyser itself is read once and executed from a
private snapshot, so a concurrent checkout update cannot mislabel its version.
The manifest hash is an integrity checksum, **not an authenticated signature**;
protect the bundle store against an actor able to replace both evidence and
hashes. Exact argument/path/URL metadata can be sensitive; store bundles privately.

Capture stages in a private sibling directory, performs whole-bundle replay
validation, synchronizes files and directories, then atomically publishes with
no replacement (including a racing empty destination). It also synchronizes the
destination parent. SIGTERM and ordinary pre-publication failures leave no
destination; SIGKILL may leave an unpublished `.phantom-capture-*` directory.
Never treat staging directories as evidence. A failure after the rename (for
example parent-directory fsync failure or stdout pipe closure) may leave a
complete bundle; verify it with replay rather than overwrite it.
Full nested history favors self-contained evidence over storage efficiency;
there is no automatic pruning. Retain originals according to operator policy.

## Regression

```bash
./scripts/tests/phantom-playback-outcome-capture.test.sh
./scripts/tests/phantom-playback-outcome-coverage.test.sh
```

The capture suite runs the actual entrypoint and unchanged analyser with a local
fake HTTP server. It checks all 32 cold/warm movie/episode tuples, extra labels,
eight rolled identities, previous-only and uninstrumented identities, raw byte
retention, moved/offline replay, explicit receipts, unhealthy captures and
transport/time/identity/integrity failures. It injects interrupted/partial writes,
clock rollback, analyser replacement and a racing destination without touching
shared services. No real playback traffic or serving rollout is required.
