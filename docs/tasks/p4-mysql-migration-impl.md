# P4 Stage A — migrate Jellyfin's authoritative DB (`jellyfin.db`) to shared PostgreSQL

Task: `p4-mysql-migration-impl` (id kept for continuity; the deliverable now targets
**PostgreSQL**, see "Retarget" below). Deliverable: an offline, operator-run migration
script `scripts/phantom-migrate-jellyfindb-to-postgres.sh` that moves Jellyfin's
authoritative library/user database off the per-color SQLite `jellyfin.db` onto a
shared **PostgreSQL** instance served through the external
[`Jellyfin.Pgsql`](https://github.com/JPVenson/Jellyfin.Pgsql) (JPVenson) EF Core
provider, following the **P3 five-stage staging-validation methodology** (never a
bespoke shortcut around it). The same script also moves phantom.db's own state to its
own PostgreSQL logical DB on the SAME server (`--source phantom`).

## Retarget (2026-07-31 ROI)

This task's prior DONE deliverable targeted MySQL / `jellyfin-plugin-mysql`
(`scripts/phantom-migrate-jellyfindb-to-mysql.sh`). The 2026-07-31 ROI repointed Stage
A to the external PostgreSQL provider `Jellyfin.Pgsql` and expanded scope to phantom.db's
own PostgreSQL logical DB. The MySQL script + test are therefore **obsolete and removed**;
this Postgres script + test replace them. The task id still reads "mysql" (kept, matching
the `db-migration-script` precedent of an honest instance name over a generic pattern),
but the actual deliverable filename is `...-to-postgres.sh`.

## Why PostgreSQL, not shared SQLite

Per `docs/tasks/p4-phantomdb-multiwriter-audit.md`, a single SQLite file shared
read-write across multiple replica processes/hosts is explicitly unsafe (broken
advisory locking over network filesystems, WAL shared-memory that does not span hosts,
immediate `SQLITE_BUSY`). The only correct way for N Jellyfin replicas (blue/green
"colors") to share ONE authoritative store is a genuine multi-writer engine.
`Jellyfin.Pgsql` points Jellyfin's EF Core context at PostgreSQL, so migrating
`jellyfin.db` onto Postgres is the store-level prerequisite for the multi-replica P4
topology. The same multi-writer argument applies to phantom.db when phantom state must
be shared across replicas (audit option 2), hence the `--source phantom` path onto its
own `phantom_prod`/`phantom_dev` logical DB on the SAME Postgres server.

The provider/plugin owns the destination **schema** (`Jellyfin.Pgsql` runs the EF Core
migrations that create the `jellyfin` tables; the phantom Postgres schema is created
additively by the plugin's Postgres `EnsureSchema` path). This script owns only the
authoritative **row-data** copy from SQLite into that schema, and proves the copy.

## The five stages (mirrors P3, never bypassed)

1. **Clone** — snapshot the active color's live source SQLite DB to an offline clone
   via the SQLite backup API (folds in WAL, never a torn read). The live file is never
   written.
2. **Predicted counts** — enumerate every data table in the clone and record its exact
   row count (`predicted-counts.tsv`). This is the contract every later stage must
   reproduce byte-for-byte. `__EFMigrationsHistory` (provider-specific EF bookkeeping,
   jellyfin source only) is excluded from the copy.
3. **Staging validation on the inactive color / dev logical DB** (`--stage`) — load the
   clone's data into the INACTIVE Postgres schema and assert Postgres's actual per-table
   counts equal the stage-2 predictions. Refuses on any drift. Writes a
   `.staging-validated` receipt.
4. **Operator hand-validation** — print the predicted-vs-actual report and STOP for the
   operator to point a provider-configured Jellyfin at the inactive color and
   hand-validate (library, users, watch-state) before any prod write.
5. **Prod write** (`--commit`) — gated on a passing stage-3 receipt in the same
   `STAGING_DIR` **and** a typed `MIGRATE` confirmation; backs up the prod Postgres
   target (`pg_dump`) first, loads the SAME validated data set into prod, re-verifies
   counts, and prints the connection-string cutover next steps.

## Expand/contract compatibility

Every future schema change against the shared Postgres phantom DB must follow the
**expand/contract** discipline (flux
`docs/phantom-library-schema-change-expand-contract.md`). This migration itself lands
**additively** and is compatible with that gate: it copies row data into a FRESH logical
DB / freshly-created tables, performs NO destructive rename, NO in-place `ALTER`, and NO
rewrite of any existing table on either side (SQLite is cloned never written; the
destination load is a scoped `DELETE` + reload of the SAME logical rows inside one
transaction). See the script header's "EXPAND/CONTRACT COMPATIBILITY" block.

## Safety contract (modeled on `phantom-migrate-v11-to-v12.sh`)

- **Non-destructive to the source** — the source SQLite DB is only ever cloned; a failed
  migration leaves prod exactly as it was (just don't flip the connection string). The
  SQLite files remain a full rollback.
- **Dry-run by default** — with neither `--stage` nor `--commit`, clones, computes
  predicted counts, generates the Postgres load set, prints the plan, writes NOTHING to
  any Postgres DB.
- **Stage-gated** — a prod write is refused unless staging validation passed in the same
  `STAGING_DIR` (the `.staging-validated` receipt is required).
- **Count-verified at every load** — after every Postgres load, actual per-table counts
  must equal the stage-2 predictions or the script refuses to proceed.
- **Idempotent** — each table's load set is a scoped `DELETE` + re-`INSERT` inside one
  transaction with referential triggers suspended (`SET session_replication_role =
  replica`), so a re-run converges to the same rows without FK-ordering failures.
- **Offline** — refuses to run while any Jellyfin process is alive (every color must be
  stopped); `--skip-service-check` is sandbox/rig only.
- **Credentials never on the CLI** — the Postgres password comes from env / `*_FILE` and
  is passed to the client via `PGPASSWORD`, never argv (no `ps(1)` leak).

## Data transfer mechanism

The load set is generated from the clone with SQLite's `.mode insert <table>`, producing
`INSERT` statements per table. SQLite emits the table name UNQUOTED, so the script
rewrites each emitted `INSERT INTO <t> VALUES` prefix to a double-quoted
`INSERT INTO "<t>" VALUES` to preserve the case-sensitive identifiers the EF/plugin
schema created (Postgres folds unquoted identifiers to lower case). Value quoting (single
quotes) is standard SQL and loads into Postgres unchanged; NULLs are preserved. Each table
is prefixed with a scoped `DELETE`, and the whole set is wrapped in `BEGIN` /
`SET session_replication_role = replica` … `= DEFAULT` / `COMMIT`.

## Sources

- `--source jellyfin` (default): `jellyfin.db` → `jellyfin_inactive` (dev) /
  `jellyfin_prod`, via `Jellyfin.Pgsql`.
- `--source phantom`: `phantom.db` → `phantom_dev` / `phantom_prod` on the SAME Postgres
  server, folding the `p4-phantomdb-multiwriter-audit` multi-writer findings (the
  process-local write lock / sweepers / per-user caches are only correct once phantom
  state lives in a real multi-writer engine).

## Testing

`scripts/tests/phantom-migrate-jellyfindb-to-postgres.test.sh` proves the orchestration
on a synthetic source DB and a **SQLite-backed Postgres stand-in** (a `psql`-client shim
that applies the generated load set into a real SQLite DB and answers `COUNT` queries),
bash + sqlite3 only. It runs the full matrix for BOTH `--source jellyfin` and
`--source phantom`, asserting per source: dry-run writes nothing to Postgres (and, for
jellyfin, excludes `__EFMigrationsHistory`); the load set quotes every identifier and
suspends referential triggers; `--stage` loads the inactive color with counts == source
and writes the receipt; row data (including a quoted apostrophe value) and NULLs
round-trip faithfully; `--stage` is idempotent; `--commit` refuses without a receipt; a
wrong confirmation aborts leaving prod untouched; `--commit` with receipt + `MIGRATE`
loads prod with matching counts.

The live proof against a real PostgreSQL + a real `Jellyfin.Pgsql` cutover is the separate
operator live-rig step (mirroring how `phantom-migrate-v11-to-v12` defers its live-rig
proof to a dedicated rig task).

## Note on the "no migrations until v1.0" rule

`AGENTS.md` forbids **schema** migrations pre-v1.0 (upgrade path = wipe). This script is
**not** a schema migration: it evolves no schema and rewrites no rows in place. It is an
offline, operator-run **store relocation** (SQLite → PostgreSQL, same logical schema owned
by the provider/plugin), in the same "offline operator script, Jellyfin stopped" posture
the rule's softened carve-out and the `phantom-migrate-*` scripts already occupy.

## HELD gate (2026-07-30)

Stage A stays HELD until the host→cluster import finishes and this environment flips to
prod. The script + regression test are authored and green now, but the script must NOT be
run against real operator data while the gate holds.
