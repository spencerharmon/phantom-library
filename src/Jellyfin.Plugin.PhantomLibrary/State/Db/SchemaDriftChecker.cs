using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Jellyfin.Plugin.PhantomLibrary.State.Db;

/// <summary>
/// Thrown when <see cref="SchemaDriftChecker"/> detects that the shared
/// Postgres database's actual schema has drifted away from what a still-active
/// color explicitly reads — i.e. applying the phase under evaluation would
/// break a running color. See
/// <c>docs/tasks/p7-contract-phase-and-drift-check.md</c>.
/// </summary>
public sealed class SchemaDriftDetectedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="SchemaDriftDetectedException"/> class.
    /// </summary>
    public SchemaDriftDetectedException()
    {
        TableName = string.Empty;
        MissingColumns = Array.Empty<string>();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SchemaDriftDetectedException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    public SchemaDriftDetectedException(string message)
        : base(message)
    {
        TableName = string.Empty;
        MissingColumns = Array.Empty<string>();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SchemaDriftDetectedException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public SchemaDriftDetectedException(string message, Exception innerException)
        : base(message, innerException)
    {
        TableName = string.Empty;
        MissingColumns = Array.Empty<string>();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="SchemaDriftDetectedException"/> class.
    /// </summary>
    /// <param name="tableName">The table on which drift was detected.</param>
    /// <param name="missingColumns">The columns a still-active color explicitly reads but which the actual schema no longer has.</param>
    public SchemaDriftDetectedException(string tableName, IReadOnlyCollection<string> missingColumns)
        : base(
            $"schema drift would break a running color: table '{tableName}' is missing column(s) "
            + $"[{string.Join(", ", missingColumns)}] that an active color's explicit reads name — "
            + "refusing this phase")
    {
        TableName = tableName;
        MissingColumns = missingColumns;
    }

    /// <summary>Gets the table on which drift was detected.</summary>
    public string TableName { get; }

    /// <summary>Gets the columns a still-active color needs but which are absent from the actual schema.</summary>
    public IReadOnlyCollection<string> MissingColumns { get; }
}

/// <summary>
/// Cross-color schema DRIFT check for the shared-Postgres blue/green topology
/// (ROI Priority 7, item 4 — <c>p7-contract-phase-and-drift-check</c>).
/// Compares what a color's code EXPECTS/explicitly reads against the shared
/// database's ACTUAL schema (via <c>information_schema.columns</c>), so that
/// an expand or contract phase run by one color can be refused if it would
/// break the OTHER, still-active color — e.g. a contract dropping a column
/// the still-running old color's explicit `SELECT`/`INSERT` names, or an
/// expand somehow landing without a column a new color's read already assumes.
/// <para>
/// This is deliberately a POSITIVE check (actual schema has every column the
/// caller names as required), not a schema-equality check — the two colors
/// are expected to differ during the overlap window (that is the whole point
/// of expand/contract), so only the intersection that both colors' code
/// explicitly reads is asserted present.
/// </para>
/// </summary>
public static class SchemaDriftChecker
{
    /// <summary>
    /// Reads the actual column names Postgres currently has for the given
    /// table, via <c>information_schema.columns</c> (the single source of
    /// truth for "what does the shared DB actually look like right now" — never
    /// assumed from either color's own migration history).
    /// </summary>
    /// <param name="connection">An OPEN Npgsql connection.</param>
    /// <param name="tableName">The table to inspect.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The actual column names, case-preserved as Postgres reports them.</returns>
    public static async Task<IReadOnlyList<string>> GetActualColumnsAsync(
        NpgsqlConnection connection,
        string tableName,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        var columns = new List<string>();
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT column_name FROM information_schema.columns WHERE table_name = @table;";
        cmd.Parameters.Add(new NpgsqlParameter("table", tableName));
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    /// <summary>
    /// Reports whether the given table currently exists in the shared
    /// database (per <c>information_schema.tables</c>).
    /// </summary>
    /// <param name="connection">An OPEN Npgsql connection.</param>
    /// <param name="tableName">The table to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> if the table exists.</returns>
    public static async Task<bool> TableExistsAsync(NpgsqlConnection connection, string tableName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM information_schema.tables WHERE table_name = @table;";
        cmd.Parameters.Add(new NpgsqlParameter("table", tableName));
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null;
    }

    /// <summary>
    /// Refuses (throws <see cref="SchemaDriftDetectedException"/>) if the
    /// actual schema for <paramref name="tableName"/> is missing any column a
    /// still-active color's explicit reads name. Call this immediately before
    /// running EITHER an expand or a contract phase against a table another
    /// color may still be reading — this is the "check cross-color schema
    /// drift before each phase" requirement.
    /// </summary>
    /// <param name="connection">An OPEN Npgsql connection.</param>
    /// <param name="tableName">The table about to be touched by the phase.</param>
    /// <param name="requiredColumnsForActiveColor">
    /// The columns the OTHER, still-active color's code explicitly reads
    /// (e.g. named in a hand-authored projection/DTO) — never the whole
    /// current schema, since the two colors are expected to differ during
    /// the overlap window.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="SchemaDriftDetectedException">
    /// Thrown when one or more required columns are absent from the actual
    /// schema — the phase would break the still-active color.
    /// </exception>
    public static async Task EnsureNoBreakingDriftAsync(
        NpgsqlConnection connection,
        string tableName,
        IReadOnlyCollection<string> requiredColumnsForActiveColor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);
        ArgumentNullException.ThrowIfNull(requiredColumnsForActiveColor);

        if (requiredColumnsForActiveColor.Count == 0)
        {
            return;
        }

        var actual = await GetActualColumnsAsync(connection, tableName, ct).ConfigureAwait(false);
        var actualSet = new HashSet<string>(actual, StringComparer.OrdinalIgnoreCase);

        var missing = requiredColumnsForActiveColor
            .Where(required => !actualSet.Contains(required))
            .ToList();

        if (missing.Count > 0)
        {
            throw new SchemaDriftDetectedException(tableName, missing);
        }
    }
}
