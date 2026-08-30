using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Jellyfin.Plugin.PhantomLibrary.State.Db;

/// <summary>
/// Thrown when <see cref="SchemaContractMigrator"/> refuses to run a CONTRACT
/// (drop/retire) migration because its preflight could not positively confirm
/// the prod cutover flip is recorded COMPLETED and its monitoring window has
/// elapsed. See <c>docs/tasks/p7-contract-phase-and-drift-check.md</c>.
/// </summary>
public sealed class ContractPreflightRefusedException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ContractPreflightRefusedException"/> class.
    /// </summary>
    public ContractPreflightRefusedException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContractPreflightRefusedException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    public ContractPreflightRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ContractPreflightRefusedException"/> class.
    /// </summary>
    /// <param name="message">The exception message.</param>
    /// <param name="innerException">The inner exception.</param>
    public ContractPreflightRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Records and reads the operator-owned "cutover flip" completion marker that
/// gates the CONTRACT phase. The prod flip itself — which color is live — is
/// ALWAYS an operator action (per the ROI: no task in this repo performs it);
/// this registry only records the fact + timestamp of a flip an operator has
/// already completed, e.g. via the existing NEEDS-HUMAN
/// <c>staging-migration-cutover</c> procedure, so the contract preflight below
/// has something real to gate on. Nothing in this file ever flips a color
/// itself.
/// </summary>
public static class CutoverFlipRegistry
{
    private const string FlipsTable = "phantom_cutover_flips";

    /// <summary>
    /// Records a named cutover flip as completed at the given time. Idempotent:
    /// re-recording the same name updates the timestamp rather than erroring,
    /// so an operator correcting a fat-fingered time does not need a manual
    /// DELETE first.
    /// </summary>
    /// <param name="connection">An OPEN Npgsql connection.</param>
    /// <param name="flipName">A stable name for this cutover (e.g. <c>"blue-to-green-2026-08"</c>).</param>
    /// <param name="completedAt">The wall-clock instant the flip completed.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task RecordCompletedAsync(
        NpgsqlConnection connection,
        string flipName,
        DateTimeOffset completedAt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(flipName);

        await EnsureTableAsync(connection, ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $@"
INSERT INTO {FlipsTable} (name, completed_at)
VALUES (@name, @completedAt)
ON CONFLICT (name) DO UPDATE SET completed_at = EXCLUDED.completed_at;";
        cmd.Parameters.Add(new NpgsqlParameter("name", flipName));
        cmd.Parameters.Add(new NpgsqlParameter("completedAt", completedAt.ToUnixTimeSeconds()));
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the recorded completion time for a named cutover flip, or
    /// <see langword="null"/> if no completion has been recorded.
    /// </summary>
    /// <param name="connection">An OPEN Npgsql connection.</param>
    /// <param name="flipName">The cutover flip name to look up.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The recorded completion instant, or <see langword="null"/>.</returns>
    public static async Task<DateTimeOffset?> GetCompletedAtAsync(
        NpgsqlConnection connection,
        string flipName,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(flipName);

        await EnsureTableAsync(connection, ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT completed_at FROM {FlipsTable} WHERE name = @name;";
        cmd.Parameters.Add(new NpgsqlParameter("name", flipName));
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds((long)result);
    }

    private static async Task EnsureTableAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var cmd = connection.CreateCommand();
                cmd.CommandText = $@"
CREATE TABLE IF NOT EXISTS {FlipsTable} (
    name          TEXT PRIMARY KEY,
    completed_at  BIGINT NOT NULL
);";
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                return;
            }
            catch (PostgresException ex) when (ex.SqlState == "23505" && attempt < 3)
            {
                // Lost the create race to a concurrent sibling color; retry.
            }
        }
    }
}

/// <summary>
/// Reusable CONTRACT-phase (drop/retire) migration helper for the shared-
/// Postgres blue/green topology (ROI Priority 7, item 3 —
/// <c>p7-contract-phase-and-drift-check</c>). This is the mirror-image
/// counterpart of <see cref="SchemaExpandMigrator"/>: where the expand helper
/// refuses anything that is NOT provably additive, this helper performs the
/// eventual drop of retired structure — but ONLY after a preflight proves the
/// prod cutover already completed and a monitoring window has elapsed, so a
/// contract can never run while the old color might still be live and reading
/// the structure being dropped.
/// <para>
/// This class is a TEMPLATE: it drops no live structure by itself. A future
/// concrete contract task instantiates it with the specific DROP statements
/// for the structure being retired, once the operator has performed (and
/// recorded, via <see cref="CutoverFlipRegistry"/>) the actual prod cutover —
/// itself always an operator action, never performed by this tooling or any
/// other automated task in this repo.
/// </para>
/// </summary>
public static class SchemaContractMigrator
{
    private const string MigrationsTable = "phantom_contract_migrations";

    /// <summary>
    /// Refuses to proceed unless (a) the named cutover flip is recorded
    /// COMPLETED and (b) at least <paramref name="monitoringWindow"/> has
    /// elapsed since that completion. Never mutates schema itself — pure
    /// preflight gate, safe to call repeatedly.
    /// </summary>
    /// <param name="connection">An OPEN Npgsql connection.</param>
    /// <param name="flipName">The cutover flip this contract depends on.</param>
    /// <param name="monitoringWindow">The minimum required post-flip soak time.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="timeProvider">
    /// The clock used to evaluate "now" (defaults to <see cref="TimeProvider.System"/>);
    /// overridable so tests can deterministically exercise the boundary without
    /// sleeping in real time.
    /// </param>
    /// <exception cref="ContractPreflightRefusedException">
    /// Thrown when no completed flip is recorded, or the monitoring window has
    /// not yet elapsed.
    /// </exception>
    public static async Task EnsurePreflightAsync(
        NpgsqlConnection connection,
        string flipName,
        TimeSpan monitoringWindow,
        CancellationToken ct,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(flipName);
        if (monitoringWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(monitoringWindow), monitoringWindow, "monitoring window must be non-negative");
        }

        var completedAt = await CutoverFlipRegistry.GetCompletedAtAsync(connection, flipName, ct).ConfigureAwait(false);
        if (completedAt is null)
        {
            throw new ContractPreflightRefusedException(
                $"contract refused: cutover flip '{flipName}' has no recorded COMPLETED entry in "
                + "phantom_cutover_flips — the prod flip is operator-owned and must be recorded via "
                + "CutoverFlipRegistry.RecordCompletedAsync before any contract-phase drop may run");
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var elapsed = now - completedAt.Value;
        if (elapsed < monitoringWindow)
        {
            throw new ContractPreflightRefusedException(
                $"contract refused: monitoring window not yet elapsed for cutover flip '{flipName}' — "
                + $"completed at {completedAt.Value:O}, {elapsed} elapsed, {monitoringWindow} required");
        }
    }

    /// <summary>
    /// Applies a named, preflight-gated contract (DROP/retire) migration
    /// against Postgres, serialized via a transaction-scoped advisory lock and
    /// recorded so a repeat call (from this or a peer color) is a no-op.
    /// Refuses before taking the lock or touching the DB if the preflight
    /// (see <see cref="EnsurePreflightAsync"/>) is not satisfied.
    /// </summary>
    /// <param name="connection">An OPEN Npgsql connection.</param>
    /// <param name="flipName">The cutover flip this contract depends on.</param>
    /// <param name="monitoringWindow">The minimum required post-flip soak time.</param>
    /// <param name="migrationName">A stable, unique name for this migration.</param>
    /// <param name="statements">The DROP/retire DDL statements to run.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="timeProvider">Overridable clock, see <see cref="EnsurePreflightAsync"/>.</param>
    /// <returns><see langword="true"/> if applied by THIS call; <see langword="false"/> if already applied.</returns>
    /// <exception cref="ContractPreflightRefusedException">
    /// Thrown when the preflight is not satisfied. Thrown before any lock is
    /// taken or any statement executed.
    /// </exception>
    public static async Task<bool> ApplyAsync(
        NpgsqlConnection connection,
        string flipName,
        TimeSpan monitoringWindow,
        string migrationName,
        IReadOnlyList<string> statements,
        CancellationToken ct,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationName);
        ArgumentNullException.ThrowIfNull(statements);

        // Preflight BEFORE taking any lock or opening any transaction: a
        // refused contract must never partially acquire resources — mirrors
        // SchemaExpandMigrator's "classify before locking" posture.
        await EnsurePreflightAsync(connection, flipName, monitoringWindow, ct, timeProvider).ConfigureAwait(false);

        const int maxAttempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await ApplyOnceAsync(connection, migrationName, statements, ct).ConfigureAwait(false);
            }
            catch (PostgresException ex) when (ex.SqlState == "23505" && attempt < maxAttempts)
            {
                // Lost the migrations-table create race to a concurrent
                // sibling color; retry (same rationale as SchemaExpandMigrator).
            }
        }
    }

    /// <summary>
    /// Reports whether a named contract migration has already been applied.
    /// </summary>
    /// <param name="connection">An OPEN Npgsql connection.</param>
    /// <param name="migrationName">The migration name to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><see langword="true"/> if already applied.</returns>
    public static async Task<bool> IsAppliedAsync(NpgsqlConnection connection, string migrationName, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationName);

        await EnsureMigrationsTableAsync(connection, ct).ConfigureAwait(false);

        await using var check = connection.CreateCommand();
        check.CommandText = $"SELECT 1 FROM {MigrationsTable} WHERE name = @name;";
        check.Parameters.Add(new NpgsqlParameter("name", migrationName));
        var result = await check.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is not null;
    }

    private static async Task EnsureMigrationsTableAsync(NpgsqlConnection connection, CancellationToken ct)
    {
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
                return;
            }
            catch (PostgresException ex) when (ex.SqlState == "23505" && attempt < 3)
            {
                // Lost the create race to a concurrent sibling color; retry.
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

        // Transaction-scoped advisory lock, keyed by a stable hash of the
        // migration name — identical serialization posture to
        // SchemaExpandMigrator, so an expand and a contract can never
        // interleave against the same logical migration name either.
        var lockKey = StableHash(migrationName);
        await using (var lockCmd = connection.CreateCommand())
        {
            lockCmd.Transaction = tx;
            lockCmd.CommandText = "SELECT pg_advisory_xact_lock(@key);";
            lockCmd.Parameters.Add(new NpgsqlParameter("key", lockKey));
            await lockCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var check = connection.CreateCommand())
        {
            check.Transaction = tx;
            check.CommandText = $"SELECT 1 FROM {MigrationsTable} WHERE name = @name;";
            check.Parameters.Add(new NpgsqlParameter("name", migrationName));
            var already = await check.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (already is not null)
            {
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

            return (long)(hash & 0x7FFFFFFFFFFFFFFF);
        }
    }
}
