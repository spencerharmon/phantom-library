using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Jellyfin.Plugin.PhantomLibrary.State.Db;

/// <summary>A statement cannot be positively classified as additive-safe.</summary>
public sealed class NonAdditiveSchemaChangeException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="NonAdditiveSchemaChangeException"/> class.</summary>
    public NonAdditiveSchemaChangeException()
    {
        Statement = string.Empty;
        Reason = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="NonAdditiveSchemaChangeException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    public NonAdditiveSchemaChangeException(string message)
        : base(message)
    {
        Statement = string.Empty;
        Reason = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="NonAdditiveSchemaChangeException"/> class.</summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public NonAdditiveSchemaChangeException(string message, Exception innerException)
        : base(message, innerException)
    {
        Statement = string.Empty;
        Reason = string.Empty;
    }

    /// <summary>Initializes a new instance of the <see cref="NonAdditiveSchemaChangeException"/> class.</summary>
    /// <param name="statement">The offending DDL.</param>
    /// <param name="reason">Why the statement was refused.</param>
    public NonAdditiveSchemaChangeException(string statement, string reason)
        : base($"Refusing non-additive schema statement (belongs to the CONTRACT phase, not expand): {reason}" + Environment.NewLine + statement)
    {
        Statement = statement;
        Reason = reason;
    }

    /// <summary>Gets the offending statement.</summary>
    public string Statement { get; }

    /// <summary>Gets the reason the statement was refused.</summary>
    public string Reason { get; }
}

/// <summary>
/// Shared PostgreSQL expand executor. Tables/columns/ordinary indexes form an atomic
/// transactional prefix; concurrent indexes form an explicitly ordered, resumable
/// nontransactional suffix. A receipt is written only after catalogue verification.
/// See docs/tasks/p7-concurrent-index-expand.md for grammar and ownership boundaries.
/// </summary>
public static class SchemaExpandMigrator
{
    private static readonly string[] ForbiddenTokens =
    {
        "DROP ", "RENAME ", "TRUNCATE", "ALTER COLUMN", "DELETE FROM", "UPDATE ",
    };

    /// <summary>Classifies the entire batch before any connection/lock/DDL mutation. Unknown SQL fails closed.</summary>
    /// <param name="statements">Candidate DDL, one statement per element.</param>
    public static void EnsureAdditiveOnly(IEnumerable<string> statements)
    {
        ArgumentNullException.ThrowIfNull(statements);
        var concurrentPhase = false;
        foreach (var raw in statements)
        {
            var stmt = ExpandIndex.Clean(raw ?? string.Empty);
            if (stmt.Length == 0)
            {
                continue;
            }

            var upper = stmt.ToUpperInvariant();
            foreach (var forbidden in ForbiddenTokens)
            {
                if (upper.Contains(forbidden, StringComparison.Ordinal))
                {
                    throw new NonAdditiveSchemaChangeException(stmt, $"contains forbidden token '{forbidden.Trim()}'");
                }
            }

            var index = ExpandIndex.Parse(stmt);
            if (index?.Concurrent == true)
            {
                concurrentPhase = true;
            }
            else if (concurrentPhase)
            {
                throw new NonAdditiveSchemaChangeException(stmt, "transactional statements must precede the concurrent index phase");
            }

            if (index is null)
            {
                ExpandIndex.ValidateTransactional(stmt);
            }
        }
    }

    /// <summary>
    /// Applies a named expand on a dedicated unpooled connection, preserving the
    /// caller's credentials and current schema, not its transaction/session settings.
    /// All applies in that schema serialize without retaining a waiting snapshot.
    /// Even with a receipt, every requested index is verified and, when owned, repaired.
    /// </summary>
    /// <param name="connection">An open connection; call outside caller transactions.</param>
    /// <param name="migrationName">A stable unique name. Do not change an already published migration.</param>
    /// <param name="statements">Additive transactional prefix then concurrent index suffix.</param>
    /// <param name="ct">Cancellation token; also bounds advisory-lock waits and builds.</param>
    /// <returns>True for a new apply or repair; false for an already valid replay.</returns>
    public static Task<bool> ApplyAsync(NpgsqlConnection connection, string migrationName, IReadOnlyList<string> statements, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationName);
        ArgumentNullException.ThrowIfNull(statements);
        // Classification and execution must see identical SQL, even for mutable inputs.
        var snapshot = statements.ToArray();
        EnsureAdditiveOnly(snapshot);
        return ConcurrentIndexExpand.ApplyAsync(connection, migrationName, snapshot, Array.Empty<AdditiveRowProjection>(), ct);
    }

    /// <summary>
    /// Applies closed row projections in the same transactional prefix as literal
    /// columns, before any index build. Existing raw-SQL grammar is unchanged.
    /// </summary>
    /// <param name="connection">Open caller connection outside a transaction.</param>
    /// <param name="migrationName">Stable, immutable expand identity.</param>
    /// <param name="statements">Existing additive SQL prefix and concurrent suffix.</param>
    /// <param name="projections">Closed, versioned definitions introducing only new columns.</param>
    /// <param name="ct">Cancellation bounds locks, DDL and builds.</param>
    /// <returns>True on new apply/repair, false on verified replay.</returns>
    public static Task<bool> ApplyAsync(NpgsqlConnection connection, string migrationName, IReadOnlyList<string> statements, IReadOnlyList<AdditiveRowProjection> projections, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationName);
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(projections);
        var snapshot = statements.ToArray();
        var rows = projections.ToArray();
        EnsureAdditiveOnly(snapshot);
        if (rows.Any(p => p is null) || rows.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != rows.Length)
        {
            throw new ArgumentException("Projection names must be distinct and definitions non-null", nameof(projections));
        }

        foreach (var row in rows)
        {
            if (rows.Where(p => p.Table == row.Table && p != row).SelectMany(p => p.Columns)
                .Any(c => row.Columns.Any(o => o.Name == c.Name) || row.Sources.Any(s => s.Name == c.Name)))
            {
                throw new ArgumentException("Projections must not share outputs or read another projection's outputs", nameof(projections));
            }
        }

        return ConcurrentIndexExpand.ApplyAsync(connection, migrationName, snapshot, rows, ct);
    }

    /// <summary>
    /// Reads the completion receipt (creating the metadata table if needed).
    /// Diagnostics only: this does NOT verify index state. Use ApplyAsync to reconcile
    /// after a restart; never use this method to bypass that validation.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="migrationName">Migration name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Whether a completion receipt exists.</returns>
    public static async Task<bool> IsAppliedAsync(NpgsqlConnection connection, string migrationName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationName);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var ensure = connection.CreateCommand();
                ensure.CommandText = "CREATE TABLE IF NOT EXISTS phantom_expand_migrations (name TEXT PRIMARY KEY, applied_at BIGINT NOT NULL)";
                await ensure.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                break;
            }
            catch (PostgresException ex) when (ex.SqlState == "23505" && attempt < 3)
            {
                // A first-use diagnostics reader can race metadata creation.
            }
        }

        await using var check = connection.CreateCommand();
        check.CommandText = "SELECT 1 FROM phantom_expand_migrations WHERE name=@name";
        check.Parameters.AddWithValue("name", migrationName);
        return await check.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    // Keep the historical name hash: it also interlocks with SchemaContractMigrator.
    internal static long StableHash(string name)
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

            return (long)(hash & 0x7FFFFFFFFFFFFFFF);
        }
    }
}
