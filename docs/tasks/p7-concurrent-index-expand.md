# Shared concurrent index expands

`SchemaExpandMigrator.ApplyAsync` is the only apply entrypoint. No migration is
registered or deployed by this change. P0 supplies its availability indexes in a
separate reviewed migration; this helper does not backfill rows, change workers,
benchmark availability or replace playback scenarios 35/36.

## Phases and receipts

Pass one statement per element, with a stable migration name. Put transactional
`CREATE TABLE IF NOT EXISTS`, `ALTER TABLE ... ADD COLUMN IF NOT EXISTS`, and
ordinary indexes first; put `CREATE [UNIQUE] INDEX CONCURRENTLY IF NOT EXISTS`
statements last. A transactional statement after a concurrent index is refused
before mutation, not silently reordered. The entire input is copied and classified
before opening the executor connection.

The transactional prefix is atomic. For mixed batches its commit includes durable
index reservations, but **not** completion. Concurrent builds/drops run outside
transaction blocks. Failure/cancellation leaves the prefix and reservations for a
retry; every requested index must have the expected definition and
`indisvalid && indisready && indislive` before recording completion. `ApplyAsync`
returns true for a new apply/repair and false for a valid replay (index OIDs stay
unchanged). It revalidates even with an existing receipt, including ordinary
indexes. The receipt is removed during validation and restored only on success;
a conflicting definition cannot keep an old false-positive receipt. `IsAppliedAsync`
is a diagnostics-only receipt read, **not** a replacement for `ApplyAsync`.

## Locks, connections and restart

Call with an open connection **outside a caller transaction**. The executor clones
its credentials and current schema onto a dedicated `Pooling=false`,
`Multiplexing=false`, `Enlist=false` physical connection. Other caller session
settings/temporary objects are not inherited. Socket disposal owns session-lock
release, including cancellation racing acquisition; a locked session never enters
a connection pool. The caller's connection remains usable.

Schema-wide advisory serialization protects metadata creation and index ownership
across migration names; the historical migration-name key also interlocks with
`SchemaContractMigrator`. Acquisition polls `pg_try_advisory_lock`, ending each
statement before the cancellable wait. **Do not replace this with a blocking
`SELECT pg_advisory_lock`: its waiting snapshot can deadlock the concurrent index
builder's old-snapshot phase (observed SQLSTATE 40P01 in the two-color test).**
All expand batches use this one executor; there is no legacy parallel apply path.
Readers/writers of application rows do not take these migration locks.

## Ownership and catalogue validation

`phantom_expand_index_builds` reserves an absent index name with the migration,
original table OID and server-normalized definition **before** starting a build.
The reservation is the crash witness when PostgreSQL commits an invalid index
before CREATE returns to the client. When an index is successfully observed, its
OID is retained too. An externally replaced invalid OID is not owned by the old
receipt. Missing indexes may be reserved again; valid conflicting indexes, invalid
unreserved indexes, another migration's reservation, and changed definitions are
refused, never dropped. Only an owned invalid index with a matching definition is
dropped/rebuilt (without CASCADE). Earlier valid indexes survive a later failure.

Like any advisory-lock protocol, migration DDL must cooperate: do not have an
operator/another DDL engine claim an absent reserved name during an in-flight or
crashed build. A reservation with no returned OID is intentionally recoverable;
PostgreSQL offers no atomic COMMENT/ownership write inside CREATE INDEX CONCURRENTLY.
Out-of-band row writes are supported; out-of-band replacement DDL is not a second
concurrent migration engine. Reservations are kept for restart provenance.

Expected definitions are parsed by PostgreSQL using indexes on empty temporary
`LIKE` tables, never duplicate indexes on the application's rows. Catalogue checks
include namespace, owning table OID, btree method, key/include count, ordered column
definitions, sort/null ordering, collation/opclass OIDs, expression/predicate,
uniqueness/nulls-not-distinct and validity flags. Name existence and IF NOT EXISTS
notices alone never establish correctness.

## Deliberately narrow grammar

Unquoted ASCII identifiers (maximum 63 bytes), ordinary tables in the current
schema, plain btree column lists with ASC/DESC/NULLS FIRST/LAST, optional predicates
made of column-to-literal comparisons or IS [NOT] NULL joined by AND. An explicit
table schema is supported and must equal the current schema. Index names themselves
are unqualified, as required by PostgreSQL CREATE INDEX syntax.

Transactional columns support the project's current primitive types, optional
literal defaults, and nullable/non-null-with-non-null-default additions. New table
primary keys are supported; adding a primary key to an existing table is not.
Comments, multi-statement elements, multi-action ALTER, quoted/dollar/escape syntax,
function/expression indexes/defaults, CREATE TABLE AS, INCLUDE/custom opclasses,
partitioned tables and cross-schema batches fail closed. Extend the grammar and
its real-PostgreSQL tests together rather than adding an unchecked escape hatch.

## Reproduce

```
./scripts/tests/postgres-concurrent-index-expand.test.sh
```

Requires .NET 9, Python 3 and either rootless Podman (automatically provisions the
pinned isolated PostgreSQL 16.16 image) or `PHANTOM_TEST_POSTGRES_DSN` pointing to a
**disposable** server with CREATE SCHEMA and test-backend termination privileges.
The xUnit fixture requires the DSN; missing/unreachable DB is a failure, never skip.
The harness compiles the actual helper sources and unchanged transactional tests
without needing the Jellyfin server. Exactly **46** tests must execute and pass:
34 concurrent/phase/ownership cases plus 12 existing helper tests. TRX, SQLSTATE,
OID/validity, legacy row counts/checksums, and PostgreSQL logs are retained at the
printed evidence path (`PHANTOM_TEST_RESULTS` can select it).

`.gitea/workflows/postgres-concurrent-index-expand.yml` runs this exact harness
against a disposable service container and uploads its evidence, with no shared
or production database credentials. No operator deployment or production flip is
part of this helper task.
