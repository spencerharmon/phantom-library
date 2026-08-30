using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.State.Db;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// Real-PostgreSQL integration coverage for <see cref="SchemaDriftChecker"/>
/// (<c>p7-contract-phase-and-drift-check</c>). Gated on
/// <c>PHANTOM_TEST_POSTGRES_DSN</c> pointing at a real, disposable Postgres
/// server — never a mock — exactly like <see cref="SchemaExpandMigratorPostgresTests"/>.
/// A plain <c>dotnet test</c> run (no Postgres server available) stays green:
/// every test below returns immediately, doing nothing, when the variable is
/// unset.
/// </summary>
public sealed class SchemaDriftCheckerPostgresTests : IAsyncLifetime
{
    private static readonly string? Dsn = Environment.GetEnvironmentVariable("PHANTOM_TEST_POSTGRES_DSN");

    private readonly string _schema = "phantom_drift_it_" + Guid.NewGuid().ToString("N");
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

    // ---- (b) DETECTS a would-break-a-running-color drift and refuses — movie-shaped table ----

    [Fact]
    public async Task EnsureNoBreakingDrift_RefusesWhenActiveColorColumnMissing_MovieShapedTable()
    {
        if (!Enabled)
        {
            return;
        }

        await using var conn = await OpenAsync();
        await using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TABLE drift_probe_movie (id INTEGER PRIMARY KEY, title TEXT);";
            await create.ExecuteNonQueryAsync();
        }

        // The still-active old color's code explicitly names 'legacy_rating'
        // in a read, but the actual (already-contracted) schema never had it,
        // or it was already dropped — either way this must refuse.
        var ex = await Assert.ThrowsAsync<SchemaDriftDetectedException>(
            () => SchemaDriftChecker.EnsureNoBreakingDriftAsync(
                conn, "drift_probe_movie", new[] { "id", "title", "legacy_rating" }, default));

        Assert.Equal("drift_probe_movie", ex.TableName);
        Assert.Contains("legacy_rating", ex.MissingColumns);
    }

    // ---- (b) PASSES a genuinely-safe drift — movie-shaped table ----

    [Fact]
    public async Task EnsureNoBreakingDrift_PassesWhenAllRequiredColumnsPresent_MovieShapedTable()
    {
        if (!Enabled)
        {
            return;
        }

        await using var conn = await OpenAsync();
        await using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TABLE drift_probe_movie_ok (id INTEGER PRIMARY KEY, title TEXT, extra_new_col TEXT);";
            await create.ExecuteNonQueryAsync();
        }

        // The still-active color only reads id/title; the presence of an
        // extra column added by a peer's already-applied expand is safe and
        // must not trip the check (this is a positive/subset check, not
        // schema equality).
        await SchemaDriftChecker.EnsureNoBreakingDriftAsync(
            conn, "drift_probe_movie_ok", new[] { "id", "title" }, default);
    }

    // ---- (b-parity) same coverage for the episode/series-shaped table ----

    [Fact]
    public async Task EnsureNoBreakingDrift_RefusesWhenActiveColorColumnMissing_EpisodeShapedTable()
    {
        if (!Enabled)
        {
            return;
        }

        await using var conn = await OpenAsync();
        await using (var create = conn.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE drift_probe_episode (series_tmdb_id INTEGER NOT NULL, season INTEGER NOT NULL, "
                + "episode INTEGER NOT NULL, title TEXT, PRIMARY KEY (series_tmdb_id, season, episode));";
            await create.ExecuteNonQueryAsync();
        }

        var ex = await Assert.ThrowsAsync<SchemaDriftDetectedException>(
            () => SchemaDriftChecker.EnsureNoBreakingDriftAsync(
                conn,
                "drift_probe_episode",
                new[] { "series_tmdb_id", "season", "episode", "title", "legacy_episode_rating" },
                default));

        Assert.Equal("drift_probe_episode", ex.TableName);
        Assert.Contains("legacy_episode_rating", ex.MissingColumns);
    }

    [Fact]
    public async Task EnsureNoBreakingDrift_PassesWhenAllRequiredColumnsPresent_EpisodeShapedTable()
    {
        if (!Enabled)
        {
            return;
        }

        await using var conn = await OpenAsync();
        await using (var create = conn.CreateCommand())
        {
            create.CommandText =
                "CREATE TABLE drift_probe_episode_ok (series_tmdb_id INTEGER NOT NULL, season INTEGER NOT NULL, "
                + "episode INTEGER NOT NULL, title TEXT, extra_new_col TEXT, "
                + "PRIMARY KEY (series_tmdb_id, season, episode));";
            await create.ExecuteNonQueryAsync();
        }

        await SchemaDriftChecker.EnsureNoBreakingDriftAsync(
            conn, "drift_probe_episode_ok", new[] { "series_tmdb_id", "season", "episode", "title" }, default);
    }

    // ---- GetActualColumnsAsync reports the real live schema, not an assumption ----

    [Fact]
    public async Task GetActualColumnsAsync_ReturnsRealSchemaColumns()
    {
        if (!Enabled)
        {
            return;
        }

        await using var conn = await OpenAsync();
        await using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TABLE drift_probe_columns (id INTEGER PRIMARY KEY, note TEXT);";
            await create.ExecuteNonQueryAsync();
        }

        var columns = await SchemaDriftChecker.GetActualColumnsAsync(conn, "drift_probe_columns", default);

        Assert.Contains("id", columns, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("note", columns, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TableExistsAsync_ReportsAbsenceAndPresence()
    {
        if (!Enabled)
        {
            return;
        }

        await using var conn = await OpenAsync();

        Assert.False(await SchemaDriftChecker.TableExistsAsync(conn, "drift_probe_absent_table", default));

        await using (var create = conn.CreateCommand())
        {
            create.CommandText = "CREATE TABLE drift_probe_present_table (id INTEGER PRIMARY KEY);";
            await create.ExecuteNonQueryAsync();
        }

        Assert.True(await SchemaDriftChecker.TableExistsAsync(conn, "drift_probe_present_table", default));
    }

    // ---- No required columns named => trivially passes without querying drift ----

    [Fact]
    public async Task EnsureNoBreakingDrift_NoOpWhenNoColumnsRequired()
    {
        if (!Enabled)
        {
            return;
        }

        await using var conn = await OpenAsync();

        // Table does not even exist — must still pass, since nothing was
        // required of it.
        await SchemaDriftChecker.EnsureNoBreakingDriftAsync(
            conn, "drift_probe_nonexistent", Array.Empty<string>(), default);
    }
}
