using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Jellyfin.Plugin.PhantomLibrary.State.Db;

/// <summary>
/// Thrown when <see cref="SchemaExpandMigrator"/> is asked to apply a statement it
/// cannot positively prove is additive-safe (the EXPAND half of expand/contract —
/// see <c>docs/phantom-library-schema-change-expand-contract.md</c> in the flux
/// repo, and this project's own <c>docs/tasks/p7-additive-idempotent-expand-migrations.md</c>).
/// A non-additive statement (drop/rename/retype/NOT-NULL-without-default) belongs
/// to the CONTRACT phase, never here — see <c>p7-contract-phase-and-drift-check</c>.
/// </summary>
public sealed class NonAdditiveSchemaChangeException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="NonAdditiveSchemaChangeException"/> class.
    /// </summary>
    public NonAdditiveSchemaChangeException()
    {
        Statement = string.Empty;
        Reason = string.Empty;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NonAdditiveSchemaChangeException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    public NonAdditiveSchemaChangeException(string message)
        : base(message)
    {
        Statement = string.Empty;
        Reason = string.Empty;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NonAdditiveSchemaChangeException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public NonAdditiveSchemaChangeException(string message, Exception innerException)
        : base(message, innerException)
    {
        Statement = string.Empty;
        Reason = string.Empty;
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="NonAdditiveSchemaChangeException"/> class.
    /// </summary>
    /// <param name="statement">The offending DDL statement.</param>
    /// <param name="reason">Why it was classified as non-additive.</param>
    public NonAdditiveSchemaChangeException(string statement, string reason)
        : base(
            $"Refusing non-additive schema statement (belongs to the CONTRACT phase, not expand): {reason}"
            + Environment.NewLine + statement)
    {
        Statement = statement;
        Reason = reason;
    }

    /// <summary>Gets the offending statement.</summary>
    public string Statement { get; }

    /// <summary>Gets the human-readable reason the statement was refused.</summary>
    public string Reason { get; }
}

/// <summary>
/// Reusable EXPAND-migration helper for the shared-Postgres blue/green topology
/// (ROI Priority 7, item 2 — <c>p7-additive-idempotent-expand-migrations</c>).
/// Every future additive schema change (new table / nullable-or-defaulted column /
/// index) routes through this class rather than a bespoke one-off, so the three
/// invariants required for two colors to share one logical Postgres DB are
/// enforced in exactly one place:
/// <list type="bullet">
/// <item><b>Additive-only</b> — <see cref="EnsureAdditiveOnly"/> statically
/// classifies every statement, conservatively (anything not positively provable
/// additive is refused — the same posture as
/// <c>scripts/phantom-library-schema-gate.sh</c> in the flux repo).</item>
/// <item><b>Idempotent</b> — every statement must itself be guard-clause safe
/// (<c>CREATE TABLE/INDEX IF NOT EXISTS</c>, <c>ADD COLUMN IF NOT EXISTS</c>), AND
/// the migrator additionally records each named migration in
/// <c>phantom_expand_migrations</c> so a double-apply from the peer color is a
/// pure no-op without even re-running the (already-idempotent) DDL.</item>
/// <item><b>Concurrency-safe</b> — the whole apply runs under a Postgres
/// transaction-scoped advisory lock (<c>pg_advisory_xact_lock</c>) keyed by the
/// migration name, so two racing colors serialize; the loser blocks, then observes
/// the already-applied record and no-ops instead of racing the DDL.</item>
/// </list>
/// SQLite has no advisory-lock primitive and (per this project's
/// <c>AGENTS.md</c> "No database migrations until v1.0") is not part of the
/// shared blue/green topology this class exists for — it is Postgres-only by
/// design, not an oversight.
/// </summary>
public static class SchemaExpandMigrator
{
    private const string MigrationsTable = "phantom_expand_migrations";

    // Conservative allow-list classifier. Anything not matched by one of these
    // shapes — or matched but failing its own additive guard — is refused. This
    // mirrors the rules table in
    // docs/phantom-library-schema-change-expand-contract.md: add nullable
    // column/add column with default/add index/create table are safe; rename,
    // drop, retype, and NOT NULL without a default are not.
    private static readonly Regex CreateTablePattern = new(
        @"^\s*CREATE\s+TABLE\s+(IF\s+NOT\s+EXISTS\s+)?(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex CreateIndexPattern = new(
        @"^\s*CREATE\s+(UNIQUE\s+)?INDEX\s+(CONCURRENTLY\s+)?(IF\s+NOT\s+EXISTS\s+)?(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex AddColumnPattern = new(
        @"^\s*ALTER\s+TABLE\s+(\w+)\s+ADD\s+COLUMN\s+(IF\s+NOT\s+EXISTS\s+)?(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly string[] ForbiddenTokens =
    {
        "DROP ", "RENAME ", "TRUNCATE", "ALTER COLUMN", "DELETE FROM", "UPDATE ",
    };

    /// <summary>
    /// Statically classifies every statement as additive-safe or refuses the
    /// whole batch. Conservative: a statement that does not positively match a
    /// known-safe shape is refused, never assumed safe.
    /// </summary>
    /// <param name="statements">The candidate expand-phase DDL statements.</param>
    /// <exception cref="NonAdditiveSchemaChangeException">
    /// Thrown for the first statement that is not provably additive.
    /// </exception>
    public static void EnsureAdditiveOnly(IEnumerable<string> statements)
    {
        ArgumentNullException.ThrowIfNull(statements);
        foreach (var raw in statements)
        {
            var stmt = raw?.Trim().TrimEnd(';') ?? string.Empty;
            if (stmt.Length == 0)
            {
                continue;
            }

            var upper = stmt.ToUpperInvariant();

            foreach (var forbidden in ForbiddenTokens)
            {
                if (upper.Contains(forbidden, StringComparison.Ordinal))
                {
                    throw new NonAdditiveSchemaChangeException(
                        stmt, $"contains forbidden token '{forbidden.Trim()}'");
                }
            }

            if (CreateTablePattern.IsMatch(stmt))
            {
                if (!upper.Contains("IF NOT EXISTS", StringComparison.Ordinal))
                {
                    throw new NonAdditiveSchemaChangeException(
                        stmt, "CREATE TABLE must be guarded with IF NOT EXISTS to stay idempotent");
                }

                continue;
            }

            if (CreateIndexPattern.IsMatch(stmt))
            {
                if (!upper.Contains("IF NOT EXISTS", StringComparison.Ordinal))
                {
                    throw new NonAdditiveSchemaChangeException(
                        stmt, "CREATE INDEX must be guarded with IF NOT EXISTS to stay idempotent");
                }

                continue;
            }

            var addColumn = AddColumnPattern.Match(stmt);
            if (addColumn.Success)
            {
                if (!upper.Contains("ADD COLUMN IF NOT EXISTS", StringComparison.Ordinal))
                {
                    throw new NonAdditiveSchemaChangeException(
                        stmt, "ADD COLUMN must be guarded with IF NOT EXISTS to stay idempotent");
                }

                var hasNotNull = upper.Contains("NOT NULL", StringComparison.Ordinal);
                var hasDefault = upper.Contains("DEFAULT", StringComparison.Ordinal);
                if (hasNotNull && !hasDefault)
                {
                    throw new NonAdditiveSchemaChangeException(
                        stmt,
                        "a NOT NULL column added without a DEFAULT breaks the still-running old color's "
                        + "unaware INSERTs; add nullable or with a DEFAULT (expand), backfill, then a later "
                        + "contract-phase migration may tighten it");
                }

                continue;
            }

            // Not one of the known-safe shapes at all — fail closed, exactly the
            // "cannot positively prove additive" posture of
            // scripts/phantom-library-schema-gate.sh.
            throw new NonAdditiveSchemaChangeException(
                stmt, "statement does not match a recognised additive shape (CREATE TABLE/INDEX, ADD COLUMN)");
        }
    }

    /// <summary>
    /// Applies a named, additive-only migration against Postgres, serialized via a
    /// transaction-scoped advisory lock and recorded so a repeat call (from this
    /// or a peer color) is a no-op.
    /// </summary>
    /// <param name="connection">An OPEN Npgsql connection.</param>
    /// <param name="migrationName">
    /// A stable, unique name for this migration (e.g. <c>"v17_add_foo_table"</c>).
    /// Used both as the advisory-lock key and the idempotency record key.
    /// </param>
    /// <param name="statements">The additive DDL statements to run.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> if the migration was applied by THIS call; <see langword="false"/> if it was already applied (no-op).</returns>
    /// <exception cref="NonAdditiveSchemaChangeException">
    /// Thrown if any statement is not provably additive-safe. Thrown before any
    /// lock is taken or any statement executed.
    /// </exception>
    public static async Task<bool> ApplyAsync(
        NpgsqlConnection connection,
        string migrationName,
        IReadOnlyList<string> statements,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationName);
        ArgumentNullException.ThrowIfNull(statements);

        // Classify BEFORE taking any lock or opening any transaction: a refused
        // migration must never partially acquire resources.
        EnsureAdditiveOnly(statements);

        // Two racing colors can both observe the migrations bookkeeping table
        // absent and both attempt "CREATE TABLE IF NOT EXISTS" for the first
        // time — Postgres is documented to raise a duplicate-key error against
        // its own system catalog even though the DDL itself says IF NOT EXISTS
        // (the same race PhantomDb.EnsureSchemaAsync retries around). This race
        // is BEFORE the advisory lock is held (the lock itself is what the
        // table's own creation would otherwise need to be guarded by — a
        // chicken-and-egg the retry sidesteps), so retry the whole attempt with
        // a fresh transaction; by the second attempt the table is guaranteed to
        // exist under any interleaving of two concurrent creators.
        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ApplyOnceAsync(connection, migrationName, statements, ct).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505" && attempt < maxAttempts)
            {
                // Lost the migrations-table create race to a concurrent sibling
                // color; retry.
            }
        }
    }

    private static async Task<bool> ApplyOnceAsync(
        NpgsqlConnection connection,
        string migrationName,
        IReadOnlyList<string> statements,
        CancellationToken ct)
    {
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

        await using (var ensure = connection.CreateCommand())
        {
            ensure.Transaction = tx;
            ensure.CommandText = $@"
CREATE TABLE IF NOT EXISTS {MigrationsTable} (
    name        TEXT PRIMARY KEY,
    applied_at  BIGINT NOT NULL
);";
            await ensure.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Transaction-scoped advisory lock: automatically released at COMMIT or
        // ROLLBACK, so a crashed/cancelled apply can never leak a held lock — the
        // next attempt (this or a peer color) simply blocks until this
        // transaction ends, then proceeds normally. Keyed by a stable hash of the
        // migration name so unrelated migrations never contend with each other.
        var lockKey = StableHash(migrationName);
        await using (var lockCmd = connection.CreateCommand())
        {
            lockCmd.Transaction = tx;
            lockCmd.CommandText = "SELECT pg_advisory_xact_lock(@key);";
            lockCmd.Parameters.Add(new NpgsqlParameter("key", lockKey));
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Now holding the lock: check whether a peer (or an earlier call from
        // this same process) already applied this migration while we waited.
        await using (var check = connection.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = $"SELECT 1 FROM {MigrationsTable} WHERE name = @name;";
            check.Parameters.Add(new NpgsqlParameter("name", migrationName));
            var already = await check.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (already is not null)
            {
                // No-op: releases the advisory lock via commit of an otherwise
                // empty transaction.
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return false;
            }
        }

        foreach (var stmt in statements)
        {
            var trimmed = stmt?.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            await using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = trimmed;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var record = connection.CreateCommand())
        {
            record.Transaction = tx;
            record.CommandText = $"INSERT INTO {MigrationsTable} (name, applied_at) VALUES (@name, @at);";
            record.Parameters.Add(new NpgsqlParameter("name", migrationName));
            record.Parameters.Add(new NpgsqlParameter("at", DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            await record.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>
    /// Reports whether a named migration has already been applied, without
    /// taking the advisory lock (read-only fast path for callers that only need
    /// to check, e.g. diagnostics/health).
    /// </summary>
    /// <param name="connection">An OPEN Npgsql connection.</param>
    /// <param name="migrationName">The migration name to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> if already applied.</returns>
    public static async Task<bool> IsAppliedAsync(NpgsqlConnection connection, string migrationName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationName);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var ensure = connection.CreateCommand();
                ensure.CommandText = $@"
CREATE TABLE IF NOT EXISTS {MigrationsTable} (
    name        TEXT PRIMARY KEY,
    applied_at  BIGINT NOT NULL
);";
                await ensure.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                break;
            }
            catch (PostgresException ex) when (ex.SqlState == "23505" && attempt < 3)
            {
                // Lost the create race to a concurrent sibling color; retry.
            }
        }

        await using var check = connection.CreateCommand();
        check.CommandText = $"SELECT 1 FROM {MigrationsTable} WHERE name = @name;";
        check.Parameters.Add(new NpgsqlParameter("name", migrationName));
        var result = await check.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null;
    }

    /// <summary>
    /// Deterministic 63-bit (fits a signed Postgres BIGINT) FNV-1a hash of the
    /// migration name, used as the <c>pg_advisory_xact_lock</c> key. Deliberately
    /// NOT <c>string.GetHashCode()</c> (randomised per-process in .NET, which
    /// would let two colors pick different lock keys for the SAME migration name
    /// and defeat the whole point of the lock).
    /// </summary>
    private static long StableHash(string name)
    {
        unchecked
        {
            const ulong fnvOffset = 14695981039346656037;
            const ulong fnvPrime = 1099511628211;
            var hash = fnvOffset;
            foreach (var b in Encoding.UTF8.GetBytes(name))
            {
                hash ^= b;
                hash *= fnvPrime;
            }

            // Mask to 63 bits so the value is always representable as a
            // non-negative signed long (Postgres bigint is signed 64-bit;
            // pg_advisory_xact_lock accepts any bigint, but keeping it
            // non-negative avoids surprising callers who log the key).
            return (long)(hash & 0x7FFFFFFFFFFFFFFF);
        }
    }
}
