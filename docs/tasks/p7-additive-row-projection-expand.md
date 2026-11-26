# Closed additive row projections (shared PostgreSQL expand substrate)

This adds an overload of `SchemaExpandMigrator.ApplyAsync`; it does **not** register
or deploy an availability migration. P0 still owns its scheduling columns/defaults,
strict due-boundary semantics, bounded/resumable backfill and policy cursor, claim
SQL/workers, 4M-row EXPLAIN <5ms/no-Seq-Scan acceptance, inventory and 35/36 parity.
No existing application schema version, production data or deployment is changed.

## Closed API, not SQL permission

Supply the existing SQL statement list plus `IReadOnlyList<AdditiveRowProjection>`.
Each definition has an immutable logical name, positive version, an unqualified
lowercase target table and 1..16 `RowProjectionColumn` records. Each output is a
**new nullable bigint column with a literal long default**. The facility itself
introduces these columns: do not put their ADD COLUMN in the SQL list, create them
in advance, or ask it to adopt a pre-existing column. Fresh builders use the same
facility after creating their base tables. Do not hand-maintain a second function.

`RowExpression` is sealed with a private constructor. Its only constructors are
numeric/text legacy column reads, long/text literals, matching-type equality and
greater-than, `IS NULL`, boolean `AND`, `CASE`, `COALESCE` and numeric `GREATEST`.
Outputs must be numeric. Number inputs must be built-in int2/int4/int8, text inputs
built-in text, not domains, generated expressions or user-defined casts/operators.
The compiler schema-qualifies built-in casts/operators and fixes text collation to
`pg_catalog."C"`. No arbitrary function identifier, body, SQL expression, clock,
subquery, arithmetic overflow or external row access can enter the generated body.
Source fields may be nullable; SQL NULL semantics apply and outputs stay nullable,
so unchecked legacy rows cannot fail NOT NULL constraints. The owner chooses safe
literal defaults and uses Coalesce/Choose where a total numeric result is required.
Identifiers are validated before connection/DDL; literal text is quoted as data.

A fixture example (not the P0 policy) combines priority/status/checked_at into a
bucket and `GREATEST(COALESCE(next_check_at,0), COALESCE(lease_until,0))` into a due
bound. This contains no `now()`: expiry is evaluated by the owner's query against
stored time. It does not encode the difference between `next <= now` and
`lease < now`; retain the exact residual lease predicate when using such a bound.
No backfill happens here. Unrelated UPDATEs intentionally retain safe defaults;
P0's bounded backfill can touch a watched input or output to project one batch.

## One executor, atomic prefix, existing concurrent suffix

The overload snapshots/validates all inputs, then calls the **existing**
`ConcurrentIndexExpand` executor. `RowProjectionExpand` is just its prefix component:
it opens no connection, takes no advisory lock and writes no completion receipt.
Schema-wide and migration-name locks still belong to the dedicated unpooled
connection; the same disposal/cancellation/restart protocol applies to every expand.
Raw `EnsureAdditiveOnly`, `ExpandIndex.Clean` and `ValidateTransactional` are unchanged:
CREATE FUNCTION / CREATE TRIGGER / DO / generated columns / executable defaults
remain refused. There is no raw SQL escape hatch or alternative migration executor.

Put base table/column SQL before indexes. Immediately before the first index (or at
prefix end with no indexes), the executor adds literal-default columns and creates
`<name>_v<version>_fn` + `<name>_v<version>_tr` and their ownership record in the SAME
transaction. A brief ACCESS EXCLUSIVE metadata lock is the one ADD COLUMN already
needs; no rows are scanned, updated or rewritten. PostgreSQL's fast literal default
preserves original tuples. Readers/writers see either the old shape or all new
columns plus trigger, never columns with an absent trigger during normal execution.

The generated function is IMMUTABLE, SECURITY INVOKER, with fixed
`search_path=pg_catalog`; its entire body assigns only `NEW.<new output>` then returns
NEW. The row trigger runs BEFORE INSERT and BEFORE UPDATE OF all referenced inputs
**and outputs**. Legacy explicit-column INSERT/update/upsert immediately maintains
projections; new writers cannot spoof a stale output. No legacy field is assigned.
Existing rows retain defaults until written or deliberately backfilled by the owner.

Index reservations and ordinary indexes remain in the prefix transaction;
concurrent indexes remain the existing nontransactional suffix. Cancellation inside
the prefix rolls everything back. A suffix failure keeps the complete projection,
not a receipt, so even old writers remain correct before restart. Invalid-index
recovery remains exclusively the existing owned-build protocol.

## Ownership, versioning and verification

`phantom_expand_row_projections` binds a logical name to migration, immutable compiled
definition and catalog fingerprint. Versioned function/trigger names do **not**
authorize upgrades in place: another version, owner or table is a conflict. Omission
of a previously published projection from the same migration is refused too.
Existing outputs/functions/triggers are never adopted, even when apparently equal.
There is no DROP, REPLACE, automatic repair of missing projection objects or
whole-table rewrite. A separate reviewed repair/contract is needed for external drift.

Catalog fingerprints include table OID/namespace/owner/kind; input and output column
attnums, types, typmods, nullability, identity/generated/collation and server-parsed
defaults; complete pg_proc records (OID/body/language/return type/arguments/owner/ACL/
security/volatility/config etc.) and pg_trigger records (OID/target/function/events/
watched columns/enabled mode/constraint flags/condition etc.). Even an identical
external drop/recreate has different OIDs and is refused. New unrelated columns,
row changes, ANALYZE and concurrent indexes do not change the fingerprint.

Every replay checks catalog state, not just the stored receipt. The executor clears
the receipt before potentially failing verification; the suffix re-verifies all
projections as well as all indexes before restoring it. A changed/disabled trigger,
wrong function, source/output drift or future definition cannot retain a false
completed receipt. `IsAppliedAsync` is still **diagnostics only**; never use it to
bypass ApplyAsync. Migration DDL must cooperate with the existing advisory lock
protocol; malicious changes to ownership metadata are not a supported second engine.

## Evidence / reproduction

```
./scripts/tests/postgres-row-projection-expand.test.sh
```

Requires .NET 9 + Python 3 and rootless Podman (provisions pinned disposable
PostgreSQL 16.16) or `PHANTOM_TEST_POSTGRES_DSN` for an isolated fixture server.
It compiles the actual production executor files, not a mock. Missing database,
skipped test, unexpected count or nonzero exit fails. **84 executed / 84 passed**
is required: 37 projection cases, all 35 existing concurrent-index/refusal cases,
and all 12 unchanged legacy helper tests. The old harness still runs its original
47 cases separately, with unchanged assertions and exact accounting.

Coverage: movie/episode old/new INSERT, upsert, lease acquisition/completion,
priority/status/checked_at transitions, NULL/future/expired leases, output spoofing,
unrelated updates, defaults/relfilenode/legacy counts/checksums/ctid/xmin unchanged,
fresh-vs-expanded shape, replay/OIDs, raw-helper rejection of legitimate compiled
projection, literal SQL-like data safety, conflicting/external/future definitions,
missing/disabled/moved triggers and function/default drift, atomic prefix rollback,
cancellation inside prefix and suffix, backend loss, racing colors/cancelled waiter,
final-suffix drift detection and owned invalid concurrent-index recovery.

Unrelated test-class DDL teardown is isolated from projection fault injection
(PostgreSQL CIC waits on database-wide snapshots); explicit two-color race tests
remain genuinely concurrent. TRX, SQLSTATE, OID/shape, row checksum and PostgreSQL
logs are retained at the printed evidence directory. Gitea's
`.gitea/workflows/postgres-row-projection-expand.yml` invokes this exact script with
a disposable PostgreSQL 16 cluster inside the SDK job container and uploads artifacts.
It disables package auto-start, initializes a private data directory, listens ONLY
on a private Unix socket (no TCP), and stops that cluster on exit. This works on
both bridge- and host-network runners: Gitea host networking does not provide the
usual `postgres` service DNS alias. No optional skip or shared application DSN.
No operator action, plugin rollout or production flip is needed.
