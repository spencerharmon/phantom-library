# P13 — shared indexer health and admission

Plugin **0.5.7.48**, schema **25**. Replaces ungoverned production HTTP searches
with `IndexerHealthGate` → `IndexerHealthStore`, registered against the existing
`PhantomDb` singleton. No second in-memory rate-limit bucket.

## Admission and priority

- One `indexer_health` primary-key row per upstream. Keys hash normalized origin
  (plus the Prowlarr installation path) and discovered indexer ID. Torrentio
  configuration paths share its origin budget. Credentials, colours, item IDs
  and movie/episode type do not split the budget or become metric labels.
- Atomic conditional `UPDATE … RETURNING` admits one request, holding a fenced
  owner/35-second crash lease. HTTP is capped at 30 seconds, or just before the
  selector's per-request deadline. Each movie IMDB/title subquery and each
  download-to-magnet redirect spends a token separately.
- Default: at most one request per second per upstream, no burst, across all
  processes using the same database. Completion extends the cooldown for one
  second after the response/cancellation: a descheduled sender cannot compress
  two HTTP starts into a burst. Preemption/cancellation never refunds a token.
- Interactive requests announce a two-second renewable reservation in that SAME
  row. Background admission has a 150ms grace; contenders poll every 100ms for
  at most two seconds, then return a transient skip. Backoff skips immediately.
  In-flight background requests check the reservation every 200ms and cancel
  when interactive work arrives. The interactive request must still respect
  the existing cooldown/backoff; priority does not invent extra capacity.
- `MagnetSelector` captures priority in `IndexerQuery` before fan-out/late work.
  Availability-oracle probes, the availability worker's full-indexer reconcile,
  and claimed magnet-cache jobs are background. Details/materialisation and
  synchronous cache builds remain interactive. Async scope restoration prevents
  background work from changing another caller's priority. Movie/TV use the same
  admission path.

## Health and recovery

- HTTP 429 honours both delta-seconds and HTTP-date `Retry-After`. Other HTTP,
  network, parsing and internally bounded timeout failures back off exponentially:
  1s, 2s, 4s, …, capped at 300s. An explicit longer Retry-After is **not** capped.
- Prowlarr `/api/v1/indexerstatus` supplies `indexerId` / `disabledTill`.
  A separate shared control-row budget refreshes this at most every 15 seconds,
  independent of the 15-minute discovery cache. Its HTTP request is bounded to
  one second. Concurrent first callers wait for the status write, not race ahead
  of it. Failed/timed-out refreshes are logged and retain known backoff; they do
  not erase a prior disable deadline.
- Deadlines only extend, including overlapping disabled-status snapshots and
  Retry-After. Once due, the same single lease permits a recovery probe; success
  resets the failure streak. A stale owner cannot complete a successor's lease.
- Skips are `IndexerTransientException`, never successful empty availability or
  permanent abstention. Existing partial/late-result handling remains; directly
  usable magnets survive a failed/deferred redirect. Database errors fail closed.

## Expand and bounded storage

`v24_v25_indexer_health` adds **only** the new table, with a primary key and
literal/defaulted columns. It uses the existing advisory-serialized,
idempotent `SchemaExpandMigrator`; fresh creation uses the identical DDL.
Version 24 readers/writers ignore the table and keep working. New readers accept
future additive schema versions/columns. A per-instance schema-ready semaphore
also prevents parallel first requests from using a half-created schema.

Every admission, completion, priority check and disabled-deadline update is a
primary-key lookup/upsert, never a catalogue/table scan. The regression exercises
`EXPLAIN (ANALYZE, BUFFERS)` against 50,001 rows and requires
`Index Scan using indexer_health_pkey`, rejecting `Seq Scan`.

SQLite retains its single-instance backend and no-online-upgrade policy:
**a pre-v25 SQLite installation requires the documented wipe/rebuild**. Shared
blue/green admission requires both deployments to use the same PostgreSQL DB.
An old plugin tolerates the expand but cannot enforce code it does not yet have:
both colours must load this release before claiming fleet-wide rate enforcement.

## Metrics

Existing `Phantom.Indexers` request metrics now expose `429`, `skip_backoff`,
`skip_budget`, `skip_busy`, `skip_priority`, `skip_preempted` when applicable.
Additional low-cardinality instruments (same existing exporter/meter):

- `phantom_indexer_health_total`: `client`, `indexer`, `priority`
  (`interactive|background`), `status` (`ok|error|429|timeout|cancelled|preempted`
  or `skip_*`). Recorded at the HTTP/admission boundary, including direct clients.
- `phantom_indexer_http_duration_ms`: same labels, actual admitted requests only.

The row persists request/error/429 counts, consecutive failures and last latency,
alongside budget/lease/backoff/priority deadlines. Prowlarr status control traffic
uses label `indexerstatus`, not a user/title-derived label.

## Executable evidence

`./scripts/tests/p13-indexer-health-backoff.test.sh` builds the real plugin and
patched Jellyfin dependencies in a disposable tree, provisions an owned
PostgreSQL container (or uses `PHANTOM_TEST_POSTGRES_DSN`), then:

1. Disconnects admission **only in that disposable build** and requires all six
   movie/episode backoff, disabled-status and shared-budget assertions to FAIL.
2. Restores the real sources and runs 18 .NET integration cases: independent
   colour clients/repositories and restart against one database, real loopback
   HTTP, both Retry-After forms, disabled status, queued/in-flight priority,
   non-refund, recovery, exponential cap, fencing, timeout, selector/builder
   priority and transient classification, cancellation, schema overlap and index
   access. Redirect 429 tests preserve direct siblings but reject an all-deferred
   false-empty result. In-flight preemption must finish before the fake releases
   its blocked response (event-driven, not an elapsed-time guess). No optional PostgreSQL skips.
3. Runs 241 existing client/selector/degenerate-query and SQLite/PostgreSQL
   repository regression tests.

Passing run: negative control **6/6 expected failures**, health **18/18 passed**,
existing tests **241/241 passed**. The overlap fixture compiles the *unmodified*
actual v24 `PhantomDb.cs` from commit `be60223881316da24bb9b402af4b5457ddef5bb6`
(SHA256 `0802be3889e58ed726afb0f63b52c7e62cfffefc2b86f047be3458ce54bd1843`)
in a separate assembly; it writes before and after the expand, restarts and reads
new-writer values. This is repository/client integration, not a claim that two
full Jellyfin servers or production deployments were exercised.

## Rollout

1. After review/merge, use the existing image/CD path to load 0.5.7.48 on the
   inactive deployment. PostgreSQL expands on first DB use; no manual SQL/wipe.
2. Verify the loaded plugin version and `Phantom.Indexers` telemetry. A config or
   image merely applied to a retained PVC is not proof the consumer loaded it;
   use the normal rollout/restart and version check.
3. The active deployment remains subject to the existing operator cutover gate.
   No live deployment/cutover was performed by this implementation task.
