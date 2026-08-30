using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.State.Db;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// Real-PostgreSQL integration coverage for <see cref="SchemaContractMigrator"/>
/// and <see cref="CutoverFlipRegistry"/> (<c>p7-contract-phase-and-drift-check</c>).
/// Gated on <c>PHANTOM_TEST_POSTGRES_DSN</c> pointing at a real, disposable
/// Postgres server — never a mock — exactly like
/// <see cref="SchemaExpandMigratorPostgresTests"/>. A plain <c>dotnet test</c>
/// run (no Postgres server available) stays green: every test below returns
/// immediately, doing nothing, when the variable is unset.
///
/// To run against a real server:
/// <code>
/// podman run -d --name phantom-pg-test -p 15432:5432 \
///   -e POSTGRES_USER=phantom -e POSTGRES_PASSWORD=phantom -e POSTGRES_DB=phantom_test \
///   docker.io/library/postgres:16-alpine
/// PHANTOM_TEST_POSTGRES_DSN="Host=localhost;Port=15432;Username=phantom;Password=phantom;Database=phantom_test" \
///   MSBUILDDISABLENODEREUSE=1 dotnet test -p:UseSharedCompilation=false \
///   --filter "FullyQualifiedName~SchemaContractMigrator"
/// </code>
/// Each test creates and drops its own Postgres SCHEMA for isolation.
/// </summary>
public sealed class SchemaContractMigratorPostgresTests : IAsyncLifetime
{
    private static readonly string? Dsn = Environment.GetEnvironmentVariable("PHANTOM_TEST_POSTGRES_DSN");

    private readonly string _schema = "phantom_contract_it_" + Guid.NewGuid().ToString("N");
    private string? _connectionString;

    private static bool Enabled => !string.IsNullOrWhiteSpace(Dsn);

    public async Task InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        var csb = new NpgsqlConnectionStringBuilder(Dsn)
        {
            SearchPath = _schema,
        };
        _connectionString = csb.ToString();

        await using var conn = new NpgsqlConnection(Dsn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE SCHEMA IF NOT EXISTS {_schema};";
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        await using var conn = new NpgsqlConnection(Dsn);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP SCHEMA IF EXISTS {_schema} CASCADE;";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        return conn;
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now)
        {
            _now = now;
        }

        public override DateTimeOffset GetUtcNow() => _now;
    }

    // ---- (a) preflight REFUSES with no completed-flip record ----

    [Fact]
    public async Task Preflight_RefusesWithNoRecordedFlip()
    {
        if (!Enabled)
        {
            return;
        }

        await using var conn = await OpenAsync();

        var ex = await Assert.ThrowsAsync<ContractPreflightRefusedException>(
            () => SchemaContractMigrator.EnsurePreflightAsync(
                conn, "flip-movie-never-recorded", TimeSpan.FromHours(24), default));

        Assert.Contains("no recorded COMPLETED entry", ex.Message, StringComparison.Ordinal);
    }

    // ---- (a) preflight REFUSES with an unelapsed monitoring window ----

    [Fact]
    public async Task Preflight_RefusesWithUnelapsedMonitoringWindow()
    {
        if (!Enabled)
        {
            return;
        }

        await using var conn = await OpenAsync();

        var flipName = "flip-movie-recent";
        var flipCompletedAt = DateTimeOffset.UtcNow;
        await CutoverFlipRegistry.RecordCompletedAsync(conn, flipName, flipCompletedAt, default);

        var clock = new FixedTimeProvider(flipCompletedAt.AddHours(1));

        var ex = await Assert.ThrowsAsync<ContractPreflightRefusedException>(
            () => SchemaContractMigrator.EnsurePreflightAsync(
                conn, flipName, TimeSpan.FromHours(24), default, clock));

        Assert.Contains("monitoring window not yet elapsed", ex.Message, StringComparison.Ordinal);
    }

    // ---- (a) preflight PROCEEDS only when both a completed flip AND an elapsed window hold ----

    [Fact]
    public async Task Preflight_ProceedsWhenFlipCompletedAndWindowElapsed()
    {
        if (!Enabled)
        {
            return;
        }

        await using var conn = await OpenAsync();

        var flipName = "flip-movie-soaked";
        var flipCompletedAt = DateTimeOffset.UtcNow;
        await CutoverFlipRegistry.RecordCompletedAsync(conn, flipName, flipCompletedAt, default);

        var clock = new FixedTimeProvider(flipCompletedAt.AddHours(25));

        // Must not throw.
        await SchemaContractMigrator.EnsurePreflightAsync(conn, flipName, TimeSpan.FromHours(24), default, clock);
    }

    // ---- ApplyAsync refuses the drop entirely when preflight fails ----

    [Fact]
    public async Task ApplyAsync_RefusesDrop_BeforeTakingLockOrExecuting_WhenPreflightFails()
    {
        if (!Enabled)
        {
            return;
        }

        await using var conn = await OpenAsync();

        await using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TABLE contract_probe_movie (id INTEGER PRIMARY KEY, retired_col TEXT);";
            await create.ExecuteNonQueryAsync();
        }

        var statements = new[] { "ALTER TABLE contract_probe_movie DROP COLUMN retired_col;" };

        await Assert.ThrowsAsync<ContractPreflightRefusedException>(
            () => SchemaContractMigrator.ApplyAsync(
                conn, "flip-movie-drop-refused", TimeSpan.FromHours(24), "v1_drop_retired_col_movie", statements, default));

        Assert.False(await SchemaContractMigrator.IsAppliedAsync(conn, "v1_drop_retired_col_movie", default));

        // The column must still be present — the refusal happened before any
        // statement executed.
        await using var check = conn.CreateCommand();
        check.CommandText =
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'contract_probe_movie' AND column_name = 'retired_col';";
        var stillThere = await check.ExecuteScalarAsync();
        Assert.NotNull(stillThere);
    }

    // ---- ApplyAsync PROCEEDS and drops once preflight is satisfied; second call is a no-op ----

    [Fact]
    public async Task ApplyAsync_ProceedsAndIsIdempotent_OnceCutoverRecordedAndWindowElapsed()
    {
        if (!Enabled)
        {
            return;
        }

        await using var conn = await OpenAsync();

        await using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TABLE contract_probe_movie_ok (id INTEGER PRIMARY KEY, retired_col TEXT);";
            await create.ExecuteNonQueryAsync();
        }

        var flipName = "flip-movie-drop-ok";
        var flipCompletedAt = DateTimeOffset.UtcNow;
        await CutoverFlipRegistry.RecordCompletedAsync(conn, flipName, flipCompletedAt, default);
        var clock = new FixedTimeProvider(flipCompletedAt.AddHours(25));

        var statements = new[] { "ALTER TABLE contract_probe_movie_ok DROP COLUMN retired_col;" };

        var first = await SchemaContractMigrator.ApplyAsync(
            conn, flipName, TimeSpan.FromHours(24), "v1_drop_retired_col_movie_ok", statements, default, clock);
        Assert.True(first);

        var second = await SchemaContractMigrator.ApplyAsync(
            conn, flipName, TimeSpan.FromHours(24), "v1_drop_retired_col_movie_ok", statements, default, clock);
        Assert.False(second);

        await using var check = conn.CreateCommand();
        check.CommandText =
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'contract_probe_movie_ok' AND column_name = 'retired_col';";
        var gone = await check.ExecuteScalarAsync();
        Assert.Null(gone);
    }

    // ---- (a-parity) same preflight + drop coverage for the episode/series-shaped table ----

    [Fact]
    public async Task ApplyAsync_ProceedsAndIsIdempotent_EpisodeShapedTable()
    {
        if (!Enabled)
        {
            return;
        }

        await using var conn = await OpenAsync();

        await using (var create = conn.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE contract_probe_episode (series_tmdb_id INTEGER NOT NULL, season INTEGER NOT NULL, "
                + "episode INTEGER NOT NULL, retired_col TEXT, PRIMARY KEY (series_tmdb_id, season, episode));";
            await create.ExecuteNonQueryAsync();
        }

        var flipName = "flip-episode-drop-ok";
        var flipCompletedAt = DateTimeOffset.UtcNow;
        await CutoverFlipRegistry.RecordCompletedAsync(conn, flipName, flipCompletedAt, default);
        var clock = new FixedTimeProvider(flipCompletedAt.AddHours(25));

        var statements = new[] { "ALTER TABLE contract_probe_episode DROP COLUMN retired_col;" };

        var first = await SchemaContractMigrator.ApplyAsync(
            conn, flipName, TimeSpan.FromHours(24), "v1_drop_retired_col_episode", statements, default, clock);
        Assert.True(first);

        var second = await SchemaContractMigrator.ApplyAsync(
            conn, flipName, TimeSpan.FromHours(24), "v1_drop_retired_col_episode", statements, default, clock);
        Assert.False(second);
    }

    // ---- CutoverFlipRegistry re-recording updates the timestamp (operator-correction path) ----

    [Fact]
    public async Task CutoverFlipRegistry_ReRecording_UpdatesTimestamp()
    {
        if (!Enabled)
        {
            return;
        }

        await using var conn = await OpenAsync();

        var flipName = "flip-rerecorded";
        var first = DateTimeOffset.UtcNow.AddDays(-2);
        var second = DateTimeOffset.UtcNow;

        await CutoverFlipRegistry.RecordCompletedAsync(conn, flipName, first, default);
        await CutoverFlipRegistry.RecordCompletedAsync(conn, flipName, second, default);

        var stored = await CutoverFlipRegistry.GetCompletedAtAsync(conn, flipName, default);
        Assert.NotNull(stored);
        Assert.Equal(second.ToUnixTimeSeconds(), stored!.Value.ToUnixTimeSeconds());
    }
}
