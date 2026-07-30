# P4 Stage A — migrate Jellyfin's authoritative DB (`jellyfin.db`) to shared MySQL

Task: `p4-mysql-migration-impl`. Deliverable: an offline, operator-run migration
script `scripts/phantom-migrate-jellyfindb-to-mysql.sh` that moves Jellyfin's
authoritative library/user database off the per-color SQLite `jellyfin.db` onto a
shared **MySQL / MariaDB** instance served through the `jellyfin-plugin-mysql` EF
Core provider, following the **P3 five-stage staging-validation methodology**
(never a bespoke shortcut around it).

## Why MySQL, not shared SQLite

Per `docs/tasks/p4-phantomdb-multiwriter-audit.md`, a single SQLite file shared
read-write across multiple replica processes/hosts is explicitly unsafe (broken
advisory locking over network filesystems, WAL shared-memory that does not span
hosts, immediate `SQLITE_BUSY`). The only correct way for N Jellyfin replicas
(blue/green "colors") to share ONE authoritative store is a genuine multi-writer
engine. `jellyfin-plugin-mysql` points Jellyfin's EF Core context at MySQL/MariaDB,
so migrating `jellyfin.db` onto MySQL is the store-level prerequisite for the
multi-replica P4 topology.

The plugin owns the MySQL **schema** (it runs the EF Core migrations that create the
tables). This script owns only the authoritative **row data** copy from SQLite into
that schema, and proves the copy.

## The five stages (mirrors P3, never bypassed)

1. **Clone** — snapshot the active color's live `jellyfin.db` to an offline clone
   via the SQLite backup API (folds in WAL, never a torn read). The live file is
   never written.
2. **Predicted counts** — enumerate every data table in the clone and record its
   exact row count (`predicted-counts.tsv`). This is the contract every later stage
   must reproduce byte-for-byte. `__EFMigrationsHistory` (provider-specific EF
   bookkeeping) is excluded from the copy.
3. **Staging validation on the inactive color** (`--stage`) — load the clone's data
   into the INACTIVE color's MySQL schema and assert MySQL's actual per-table counts
   equal the stage-2 predictions. Refuses on any drift. Writes a
   `.staging-validated` receipt.
4. **Operator hand-validation** — print the predicted-vs-actual report and STOP for
   the operator to point a plugin-configured Jellyfin at the inactive color and
   hand-validate (library, users, watch-state) before any prod write.
5. **Prod write** (`--commit`) — gated on a passing stage-3 receipt in the same
   `STAGING_DIR` **and** a typed `MIGRATE` confirmation; backs up the prod MySQL
   target (mysqldump) first, loads the SAME validated data set into prod, re-verifies
   counts, and prints the connection-string cutover next steps.

## Safety contract (modeled on `phantom-migrate-v11-to-v12.sh`)

- **Non-destructive to the source** — `jellyfin.db` is only ever cloned; a failed
  migration leaves prod exactly as it was (just don't flip the connection string).
  The SQLite files remain a full rollback.
- **Dry-run by default** — with neither `--stage` nor `--commit`, clones, computes
  predicted counts, generates the MySQL load set, prints the plan, writes NOTHING to
  any MySQL DB.
- **Stage-gated** — a prod write is refused unless staging validation passed in the
  same `STAGING_DIR` (the `.staging-validated` receipt is required).
- **Count-verified at every load** — after every MySQL load, actual per-table counts
  must equal the stage-2 predictions or the script refuses to proceed.
- **Idempotent** — each table's load set is a scoped `DELETE` + re-`INSERT`, so a
  re-run converges to the same rows (FK/unique checks disabled around the load).
- **Offline** — refuses to run while any Jellyfin process is alive (every color must
  be stopped); `--skip-service-check` is sandbox/rig only.
- **Credentials never on the CLI** — MySQL passwords come from env / `*_FILE` and are
  passed to the client via `MYSQL_PWD`, never argv (no `ps(1)` leak).

## Data transfer mechanism

The load set is generated from the clone with SQLite's `.mode insert <table>` (bare
identifier — a backtick-quoted arg would be emitted literally and corrupt the target
name), producing MySQL-loadable `INSERT` statements per table, prefixed with a scoped
`DELETE` and wrapped in `SET FOREIGN_KEY_CHECKS=0/1`. Column order follows the schema.

## Testing

`scripts/tests/phantom-migrate-jellyfindb-to-mysql.test.sh` proves the orchestration
on a synthetic `jellyfin.db` and a **SQLite-backed MySQL stand-in** (a `mysql`-client
shim that applies the generated load set into a real SQLite DB and answers `COUNT`
queries), bash + sqlite3 only. It asserts: dry-run writes nothing to MySQL and
excludes `__EFMigrationsHistory`; `--stage` loads the inactive color with counts ==
source and writes the receipt; row data (including a quoted apostrophe value)
round-trips faithfully; `--stage` is idempotent; `--commit` refuses without a receipt;
a wrong confirmation aborts leaving prod untouched; `--commit` with receipt + `MIGRATE`
loads prod with matching counts.

The live proof against a real MySQL/MariaDB + a real `jellyfin-plugin-mysql` cutover is
the separate operator live-rig step (mirroring how `phantom-migrate-v11-to-v12` defers
its live-rig proof to a dedicated rig task).

## Note on the "no migrations until v1.0" rule

`AGENTS.md` forbids **schema** migrations pre-v1.0 (upgrade path = wipe). This script
is **not** a schema migration: it evolves no schema and rewrites no rows in place. It
is an offline, operator-run **store relocation** (SQLite → MySQL, same logical schema
owned by the plugin), in the same "offline operator script, Jellyfin stopped" posture
the rule's softened carve-out and the `phantom-migrate-*` scripts already occupy.
