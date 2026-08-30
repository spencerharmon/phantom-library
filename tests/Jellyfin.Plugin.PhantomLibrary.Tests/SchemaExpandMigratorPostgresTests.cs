using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.State.Db;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// Real-PostgreSQL integration coverage for <see cref="SchemaExpandMigrator"/>
/// (<c>p7-additive-idempotent-expand-migrations</c>). Gated on
/// <c>PHANTOM_TEST_POSTGRES_DSN</c> pointing at a real, disposable Postgres
/// server — never a mock — exactly like <see cref="PhantomDbPostgresIntegrationTests"/>.
/// A plain <c>dotnet test</c> run (no Postgres server available) stays green:
/// every test below returns immediately, doing nothing, when the variable is
/// unset.
///
/// To run against a real server:
/// <code>
/// podman run -d --name phantom-pg-test -p 15432:5432 \
///   -e POSTGRES_USER=phantom -e POSTGRES_PASSWORD=phantom -e POSTGRES_DB=phantom_test \
///   docker.io/library/postgres:16-alpine
/// PHANTOM_TEST_POSTGRES_DSN="Host=localhost;Port=15432;Username=phantom;Password=phantom;Database=phantom_test" \
///   MSBUILDDISABLENODEREUSE=1 dotnet test -p:UseSharedCompilation=false \
///   --filter "FullyQualifiedName~SchemaExpandMigrator"
/// </code>
/// Each test creates and drops its own Postgres SCHEMA for isolation.
/// </summary>
public sealed class SchemaExpandMigratorPostgresTests : IAsyncLifetime
{
    private static readonly string? Dsn = Environment.GetEnvironmentVariable("PHANTOM_TEST_POSTGRES_DSN");

    private readonly string _schema = "phantom_expand_it_" + Guid.NewGuid().ToString("N");
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

    // ---- (a) double-apply is a no-op the 2nd time ----

    [Fact]
    public async Task Apply_Twice_SecondCallIsNoOp()
    {
        if (!Enabled)
        {
            return;
        }

        var statements = new[]
        {
            "CREATE TABLE IF NOT EXISTS expand_probe_movie (id INTEGER PRIMARY KEY, note TEXT);",
            "CREATE INDEX IF NOT EXISTS idx_expand_probe_movie_note ON expand_probe_movie (note);",
        };

        await using var conn = await OpenAsync();

        var first = await SchemaExpandMigrator.ApplyAsync(conn, "v1_add_expand_probe_movie", statements, default);
        Assert.True(first);

        // Insert a row so a re-run of the (idempotent) DDL would be provably
        // harmless either way, then apply again: the migrator must short-circuit
        // via its own applied-migrations record, not merely rely on the DDL's
        // own IF NOT EXISTS guards.
        await using (var insert = conn.CreateCommand())
        {
            insert.CommandText = "INSERT INTO expand_probe_movie (id, note) VALUES (1, 'hello');";
            await insert.ExecuteNonQueryAsync();
        }

        var second = await SchemaExpandMigrator.ApplyAsync(conn, "v1_add_expand_probe_movie", statements, default);
        Assert.False(second);

        await using var count = conn.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM expand_probe_movie;";
        var rows = (long)(await count.ExecuteScalarAsync())!;
        Assert.Equal(1, rows);

        Assert.True(await SchemaExpandMigrator.IsAppliedAsync(conn, "v1_add_expand_probe_movie", default));
    }

    // ---- (a-parity) same coverage for the episode/series-shaped table ----

    [Fact]
    public async Task Apply_Twice_SecondCallIsNoOp_EpisodeShapedTable()
    {
        if (!Enabled)
        {
            return;
        }

        var statements = new[]
        {
            "CREATE TABLE IF NOT EXISTS expand_probe_episode ("
                + "series_tmdb_id INTEGER NOT NULL, season INTEGER NOT NULL, episode INTEGER NOT NULL, "
                + "note TEXT, PRIMARY KEY (series_tmdb_id, season, episode));",
            "CREATE INDEX IF NOT EXISTS idx_expand_probe_episode_note ON expand_probe_episode (note);",
        };

        await using var conn = await OpenAsync();

        var first = await SchemaExpandMigrator.ApplyAsync(conn, "v1_add_expand_probe_episode", statements, default);
        Assert.True(first);

        var second = await SchemaExpandMigrator.ApplyAsync(conn, "v1_add_expand_probe_episode", statements, default);
        Assert.False(second);
    }

    // ---- (b) two concurrent expands serialize via the advisory lock ----

    [Fact]
    public async Task Apply_ConcurrentFromTwoConnections_SerializesNeitherErrorsNorHalfApplies()
    {
        if (!Enabled)
        {
            return;
        }

        var statements = new[]
        {
            "CREATE TABLE IF NOT EXISTS expand_probe_concurrent (id INTEGER PRIMARY KEY, note TEXT);",
            "ALTER TABLE expand_probe_concurrent ADD COLUMN IF NOT EXISTS extra TEXT;",
        };

        await using var connA = await OpenAsync();
        await using var connB = await OpenAsync();

        var results = await Task.WhenAll(
            SchemaExpandMigrator.ApplyAsync(connA, "v1_add_expand_probe_concurrent", statements, default),
            SchemaExpandMigrator.ApplyAsync(connB, "v1_add_expand_probe_concurrent", statements, default));

        // Exactly one of the two racing colors actually applied it; the other
        // blocked on the advisory lock, then observed the record and no-opped.
        // Neither may throw (both awaited successfully to reach this line) and
        // neither may half-apply (the table + column both exist afterward,
        // asserted below).
        var appliedCount = (results[0] ? 1 : 0) + (results[1] ? 1 : 0);
        Assert.Equal(1, appliedCount);

        await using var conn = await OpenAsync();
        await using var check = conn.CreateCommand();
        check.CommandText =
            "SELECT column_name FROM information_schema.columns WHERE table_name = 'expand_probe_concurrent' ORDER BY column_name;";
        var cols = new List<string>();
        await using (var reader = await check.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                cols.Add(reader.GetString(0));
            }
        }

        Assert.Contains("id", cols);
        Assert.Contains("note", cols);
        Assert.Contains("extra", cols);
    }

    // ---- (c) a non-additive change is REFUSED by the helper ----

    [Fact]
    public void EnsureAdditiveOnly_RefusesDropColumn()
    {
        var ex = Assert.Throws<NonAdditiveSchemaChangeException>(
            () => SchemaExpandMigrator.EnsureAdditiveOnly(new[] { "ALTER TABLE foo DROP COLUMN bar;" }));
        Assert.Contains("forbidden token", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureAdditiveOnly_RefusesRenameColumn()
    {
        Assert.Throws<NonAdditiveSchemaChangeException>(
            () => SchemaExpandMigrator.EnsureAdditiveOnly(new[] { "ALTER TABLE foo RENAME COLUMN bar TO baz;" }));
    }

    [Fact]
    public void EnsureAdditiveOnly_RefusesAlterColumnType()
    {
        Assert.Throws<NonAdditiveSchemaChangeException>(
            () => SchemaExpandMigrator.EnsureAdditiveOnly(new[] { "ALTER TABLE foo ALTER COLUMN bar TYPE BIGINT;" }));
    }

    [Fact]
    public void EnsureAdditiveOnly_RefusesNotNullColumnWithoutDefault()
    {
        var ex = Assert.Throws<NonAdditiveSchemaChangeException>(
            () => SchemaExpandMigrator.EnsureAdditiveOnly(
                new[] { "ALTER TABLE foo ADD COLUMN IF NOT EXISTS bar TEXT NOT NULL;" }));
        Assert.Contains("NOT NULL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureAdditiveOnly_AllowsNotNullColumnWithDefault()
    {
        SchemaExpandMigrator.EnsureAdditiveOnly(
            new[] { "ALTER TABLE foo ADD COLUMN IF NOT EXISTS bar TEXT NOT NULL DEFAULT '';" });
    }

    [Fact]
    public void EnsureAdditiveOnly_RefusesUnguardedCreateTable()
    {
        Assert.Throws<NonAdditiveSchemaChangeException>(
            () => SchemaExpandMigrator.EnsureAdditiveOnly(new[] { "CREATE TABLE foo (id INTEGER);" }));
    }

    [Fact]
    public void EnsureAdditiveOnly_RefusesUnrecognisedStatement()
    {
        Assert.Throws<NonAdditiveSchemaChangeException>(
            () => SchemaExpandMigrator.EnsureAdditiveOnly(new[] { "CREATE VIEW foo AS SELECT 1;" }));
    }

    [Fact]
    public void EnsureAdditiveOnly_AllowsNullableAddColumnAndIndex()
    {
        SchemaExpandMigrator.EnsureAdditiveOnly(new[]
        {
            "CREATE TABLE IF NOT EXISTS foo (id INTEGER PRIMARY KEY);",
            "ALTER TABLE foo ADD COLUMN IF NOT EXISTS bar TEXT;",
            "CREATE INDEX IF NOT EXISTS idx_foo_bar ON foo (bar);",
        });
    }

    // ---- ApplyAsync refuses non-additive before touching the DB ----

    [Fact]
    public async Task ApplyAsync_RefusesNonAdditive_BeforeTakingLockOrExecuting()
    {
        if (!Enabled)
        {
            return;
        }

        await using var conn = await OpenAsync();
        var statements = new[] { "ALTER TABLE expand_probe_refuse DROP COLUMN bar;" };

        await Assert.ThrowsAsync<NonAdditiveSchemaChangeException>(
            () => SchemaExpandMigrator.ApplyAsync(conn, "v1_refused_migration", statements, default));

        Assert.False(await SchemaExpandMigrator.IsAppliedAsync(conn, "v1_refused_migration", default));
    }
}
