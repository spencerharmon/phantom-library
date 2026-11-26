# Indexed availability claims (P0)

## Contract and storage

`PhantomDb.ClaimDueAvailabilityBatchAsync` is the PostgreSQL claim path; the
single-row public API delegates to it. `AvailabilityProbeWorker` consumes real
batches (at most four concurrent probes), not a client-side queue of single-row
claims. Batch size is hard-bounded at 32 in the DB API. A transaction covers every
bucket slice in one call; cancellation rolls it back. `SKIP LOCKED`, owner and
monotonically incremented `probe_generation` fence competing old/new workers.
Cancellation releases acquired worker batches with an independent bounded token;
a killed process recovers through the same indexed lease deadline.

`AvailabilitySchedule` uses the shared closed row-projection/concurrent-index
expand executor. No general executable SQL exception or replacement migration
framework was added. It adds:

- `claim_bucket`: 0=user priority (>=100), 1=positive background promotion
  (currently stale-candidate priority 50), 2=unchecked, 3=available,
  4=unavailable, 5=other checked state. Ordering is by these finite tiers, not
  arbitrary integer priorities within a tier.
- `claim_due`: `greatest(coalesce(next_check_at,0),coalesce(lease_until,0))`.
- Index `(type,claim_bucket,claim_due,tmdb_id,season,episode)`.
- One `availability_reschedule` cursor/rate-limit row.

All old INSERT/UPDATE/upsert writers recompute the stored projection via the
owned BEFORE trigger; assigning stale new projection values cannot bypass it.
The expand's literal bucket default is **-1**, not a guessed ready state: old
rows are invisible to the new claim path until admitted, protecting old leases.
Installation writes no availability rows. Index creation is CONCURRENTLY outside
transactions; interrupted/invalid owned index recovery and definition drift
checks are the existing shared executor's responsibility. Schema v24 remains the
minimum baseline; the independent named capability receipt does not downgrade a
future schema version or change the SQLite schema.

Claims use equality on type/bucket plus one due-time range. No policy hash/NULL
OR or computed ORDER BY is in the PostgreSQL claim predicate. We deliberately
use `claim_due < now`: this retains the old strict lease-expiry boundary, at a
cost of at most one second of scheduling delay for an exactly-due item.

Episode cursors include **due time and the complete item key**, not only series
id. A series-only filter after a due range can traverse millions of rows on
sparse sets/wrap. The complete lexicographic key is an index bound; an empty
suffix wraps once to the index start. Cursors are local fairness hints, not
ownership: restart may reset them without duplication because leases persist.
Each bounded slice may contain several episodes from a series. Equal-due work
progresses through the catalogue; alternating movie/episode worker slots prevents
one kind's backlog monopolizing a tick. User activity still makes the worker yield.

## Rescheduling / admission

One transaction reads at most 256 rows by the existing primary key and updates
only unprojected or stale-policy future-due rows. Row changes and cursor commit
together; cancellation cannot advance the cursor past missing work. Maintenance
waits for row locks rather than skipping rows permanently. Only one committed
slice per second across replicas is eligible; the timer may run more slowly.
Initial 4M-row admission therefore takes at least 15,625 slices (about 4.34 hours
at one slice/second), while old workers and admitted/new writes continue working.
No policy mismatch is evaluated by the claim query.

A policy change during a sweep **does not reset its cursor**. Slices use the
latest policy; after EOF, a changed policy gets one final full sweep. Stable
policy EOF becomes idle. A restart resumes the persisted cursor. A transient
retry with a NULL/old policy hash is not repeatedly made due by every claim.

## Mandatory evidence

Run `./scripts/tests/p0-availability-claims.test.sh` (from a honeybee:
`beehive submodule worktree exec phantom-library bee-p0-indexed-batch-availability -- ./scripts/tests/p0-availability-claims.test.sh`).
It builds the real candidate and patched server in an owned scratch tree, runs
the actual PostgreSQL-backed .NET DB/worker tests and the existing SQLite worker
regressions, then invokes isolated movie/episode 35/36 native-open parity. No
optional skip, source-grep proof, forced planner setting or timing retries.

The synthetic clone uses the real v24 schema, 1M movies + 3M episodes, six
scheduling states, future/sparse due times and realistic candidate payloads.
Before/after JSON plans, PostgreSQL settings, hardware/cache assumptions, exact
TRX accounting and server receipts are retained. Every claim variant rejects
any Seq Scan node and execution >=5ms. EXPLAIN executes the actual claim command
(including UPDATE and trigger), rolled back after each measurement. Initial
seed/index writes, ordinary VACUUM/ANALYZE and a durability CHECKPOINT before
both baseline and candidate measurements are disclosed; this is not a cold
host-cache benchmark. The checkpoint finishes the synthetic millions-row fixture
write storm before measuring steady-state claims; real policy maintenance remains
capped at 256 rows. Every claim still includes its actual lease UPDATE and every
physical read/write in the strict <5ms guard, without a timing retry. PostgreSQL containers get 256MiB shared memory so normal
parallel VACUUM of the 4M-row clone can complete (64MiB Docker defaults failed).

Rehearsal computes predicted counts before availability UPDATEs. Actual bounded
scheduler slices are tested, followed by a legacy named-column writer applying
the remaining clone projection with predicted/actual counts and unchanged
legacy due/policy assertions. The separate 600-row clone runs whole sweeps under
policy churn/restart, old active leases and future schema version 999. No
production database is used or changed.

Gitea job `.gitea/workflows/availability-claims.yml` runs this entire gate and
uploads plans/logs/receipts on success **or failure**. It owns disposable
PostgreSQL containers/ports and reuses the isolated playback harness.

## Query inventory for the sibling polling task

| Path | Frequency / bounds | Remaining owner |
|---|---|---|
| `ClaimDueAvailabilityBatchAsync` | <=32 leases; <=6 buckets/type, episode suffix + one wrap; indexed UPDATE CTE | P0 claims |
| `RescheduleAvailabilityPolicyAsync` | <=256 rows, at most one committed slice/s globally; no catalogue read at stable EOF | P0 claims |
| `ReleaseAvailabilityLeaseAsync` | exact item PK + owner/generation; worker batch exit | P0 claims |
| `GetUserActivityAtAsync` | singleton metadata read per tick | polling cadence |
| `MarkStaleAvailableItemsDueAsync` | existing stale-candidate anti-join/bulk promotion per tick, unchanged | sibling polling/bulk-maintenance work |
| `ClaimDueSeriesExpansionAsync` / series expansion | existing separate series queue, opportunity per probe group | sibling polling/series work |
| metadata/external-id/air-date reads | exact probed item; up to four probes in flight | probe work, not idle polling |
| completion/transient reschedule | exact PK + lease fencing; existing relevance update | probe work |
| legacy `TryReadDueAvailabilityAsync` / episode-series cursor | SQLite only; PostgreSQL cannot reach these scans | retained SQLite backend |

No production install, image pin, config change or color flip is part of this
change. Deploy only through the existing reviewed inactive-color pipeline; no
wipe is required for the PostgreSQL additive expand.
