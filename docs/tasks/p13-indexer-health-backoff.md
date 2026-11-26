# P13: shared per-indexer health/admission

Plugin **0.5.7.47**, schema **25**. Production `ProwlarrClient` and
`TorrentioClient` require the singleton `IndexerHealthGate`; it uses their
existing `PhantomDb`, not a process-local quota. There is no colour label/key.
Both instances must point to the same PostgreSQL database and use the same
canonical gateway URL. Torrentio's authority is its quota identity (filter paths
share a budget); Prowlarr uses gateway authority/base path + discovered indexer ID.
Keys are SHA-256-scoped, exclude API keys, and are never metric labels.

## Admission and failure policy

- One in-flight request per upstream, minimum start spacing **1 s Prowlarr /
  2 s Torrentio**. Atomic conditional update of a primary-key row; DB wall clock
  governs deadlines. Each IMDB/title subquery and each download-to-magnet lookup
  spends a slot. Different upstreams still run concurrently through P13 fan-out.
- Interactive admission reserves the next slot for **3.25 s**. Queued background
  work yields (100 ms initial admission grace, then 50 ms polling, at most 3 s).
  It cannot borrow an extra quota or refund a sent request. This preempts pending
  work, **not already-sent upstream I/O**. Sustained interactive traffic may starve
  background, intentionally; skipped work retains transient retry semantics.
- The availability oracle and async magnet-cache builder use Background priority;
  ordinary source selection and synchronous materialise/cache builds use
  Interactive. Identical movie/episode handling. Collector timeout is distinguished
  from caller/shutdown cancellation; timeout penalizes the indexer, user cancellation
  releases the lease without counting an upstream error.
- HTTP errors, malformed responses and transport/collector timeouts back off at
  5/10/20/40/80/160/**300 s cap**. `Retry-After` delta and HTTP-date are floors,
  including values beyond the cap. Successful recovery resets the failure streak.
  No immediate retry or empty-success substitution during backoff.
- Prowlarr `/api/v1/indexerstatus` (`indexerId`, `disabledTill`) refreshes every
  **30 s** per directory instance, single-flighted; expiry is persisted as a
  monotonic backoff floor. Failure is cached for 5 s and fails searches closed
  instead of hammering known-disabled indexers. Last disabled-until is not cleared
  merely because a later status list omits the row. Direct HTTP 429 acts immediately.
- After backoff, normal demand admits a single shared recovery probe. HTTP work
  has a 55 s deadline, less than the 60 s owner-fenced crash lease. Cancellation
  cleanup uses a separate bounded token. No permanent lock after a crashed colour.
  DB failures fail closed as transient, never bypass admission.

## Schema / P0 / P7

`PhantomDb.IndexerHealth.cs` defines the single new `indexer_health` table.
The same DDL feeds fresh installs and the registered `v24_v25_indexer_health`
advisory-locked additive expand. No catalogue backfill, renamed columns, drops,
or destructive conversion. Concurrent calls on one new `PhantomDb` now also wait
for that instance's schema initialization to finish before issuing queries.

The per-request SQL uses only `WHERE indexer_key = @key` / PK upsert. No catalogue
scan, row enumeration or unbounded cleanup. State size scales with configured
upstreams, not titles/queries. Counters and lease timestamps persist across restarts.
The ordinary minimum-version gate still tolerates future additive schemas. The
real previous v24 plugin is tested before/during/after expansion, including restart.
**Schema compatibility is not retroactive quota enforcement:** a v24 binary does
not implement this gate; both colours need the new code for shared admission.
SQLite keeps the project's pre-v1.0 wipe/rebuild upgrade policy; a fresh SQLite
installation uses the same row-based admission locally, not a cross-host budget.

## Metrics

Existing `Phantom.Indexers` meter/exporter, no new exporter instance:

- `phantom_indexer_health_total{client,indexer,status,priority}` records actual
  admission outcomes: `ok`, `error`, `auth`, `429`, `timeout`, `cancelled`,
  `skipped_backoff`, `skipped_budget`, `skipped_preempted`.
- `phantom_indexer_upstream_duration_ms`: admitted upstream latency only.
- `phantom_indexer_retry_after_ms`: explicit upstream requested delay.
- Existing `phantom_indexer_requests_total` also distinguishes health skip and 429
  statuses at collection. No title, item ID, URL or credential labels.

## Executable proof

```sh
./scripts/tests/p13-indexer-health-backoff.test.sh
```

From the hive, use:

```sh
beehive submodule worktree exec phantom-library bee-p13-indexer-health-backoff -- ./scripts/tests/p13-indexer-health-backoff.test.sh
```

Runs **22 mandatory xUnit cases** in `tests/IndexerHealth.Tests`, referencing the
real plugin (not copied health/SQL code). Real loopback HTTP and two independent
plugin client/directory/PhantomDb instances share an isolated PostgreSQL schema.
It verifies 429 skip/recovery for movie+episode and both clients, disabled status,
healthy sibling, shared spacing, interactive preemption, redirect quota/429,
error cap/date floor, owner cancellation, real selector priority/timeout and DI.
The overlap test builds the actual old plugin at
`9e0555b330659afe2e0b3414c5fed171335cf075`, runs its old schema/reader/writer and
restart against the new schema, compares fresh/expanded table shape, reapplies DDL,
and checks an actual indexed UPDATE plan with 20,000 unrelated indexer rows.

An unset DSN provisions/removes a disposable pinned PostgreSQL container using
Podman. CI can supply `PHANTOM_TEST_POSTGRES_DSN` to an isolated service instead.
The script initializes the recorded nested Jellyfin source if missing, builds
old/new code, refuses missing/skipped cases and retains logs/TRX under
`PHANTOM_TEST_RESULTS` (otherwise a fresh TMPDIR directory). Run separately from
other builds of this worktree: shared build outputs and overload invalidate timing
measurements. `.gitea/workflows/p13-indexer-health-backoff.yml` runs this separate
mandatory integration job; the general offline test suite needs no PG/old binary.

Observed final run: **22 executed, 22 passed, 0 skipped**. A temporary mutation
bypassing `ExecuteCoreAsync` with `action(ct)` compiled, then **20/22 failed**
(spacing, skip, preemption, health, timeout and redirect assertions); restored before
final validation. Existing parallel-search regression: **103 passed**. Release
Torrentio/cache-builder subset: **14 passed**. Broader local runs exceeded their
120/240 s invocation bounds; no full-suite pass is claimed.

## Delivery

This is a code/test change, not a production rollout receipt. No deployed image,
service, PVC, production data or routing changed. PostgreSQL expands automatically
on first DB use after normal plugin upgrade/restart; no offline SQL or wipe there.
Delivery owners should build this source and install/restart the inactive colour
through the existing pipeline, verify loaded version, and observe health metrics.
Production cutover remains separately operator-gated. No extra operator action
beyond the normal PostgreSQL plugin upgrade is required.
