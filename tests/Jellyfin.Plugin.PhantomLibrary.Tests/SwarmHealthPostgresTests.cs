using System;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.State;
using Jellyfin.Plugin.PhantomLibrary.State.Db;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// dht-swarm-health on the production dialect: the v23->v24 EXPAND (new
/// swarm_health table) self-applies, is additive-only, and the due / upsert /
/// peers-by-title queries behave on Postgres as on SQLite. Opt-in via
/// PHANTOM_TEST_POSTGRES_DSN like the sibling Postgres suites.
/// </summary>
public sealed class SwarmHealthPostgresTests : IAsyncLifetime
{
    private static readonly string? Dsn = Environment.GetEnvironmentVariable("PHANTOM_TEST_POSTGRES_DSN");

    private readonly string _schema = "phantom_swarm_" + Guid.NewGuid().ToString("N");
    private string? _connectionString;

    private static bool Enabled => !string.IsNullOrWhiteSpace(Dsn);

    public async Task InitializeAsync()
    {
        if (!Enabled)
        {
            return;
        }

        _connectionString = new NpgsqlConnectionStringBuilder(Dsn) { SearchPath = _schema }.ToString();
        await ExecAsync(Dsn!, $"CREATE SCHEMA IF NOT EXISTS {_schema};");
    }

    public async Task DisposeAsync()
    {
        if (Enabled)
        {
            await ExecAsync(Dsn!, $"DROP SCHEMA IF EXISTS {_schema} CASCADE;");
        }
    }

    [Fact]
    public void ExpandMigration_IsAdditiveOnly()
        => SchemaExpandMigrator.EnsureAdditiveOnly(PhantomDb.GetExpandMigrationStatements("v23_v24_swarm_health"));

    [Fact]
    public async Task V23Database_SelfAppliesSwarmHealthExpand_AndQueriesWork()
    {
        if (!Enabled)
        {
            return;
        }

        // Fresh schema, then roll the stamp back to v23 and drop the new table: the
        // exact state the shared DB is in when only the older color has run.
        using (var db = PhantomDb.CreatePostgres(_connectionString!))
        {
            await db.CountCatalogueItemsAsync("movie", null, default);
        }

        await ExecAsync(_connectionString!, "DROP TABLE swarm_health; UPDATE phantom_schema_meta SET version = 23;");
        await ExecAsync(_connectionString!, "DELETE FROM phantom_schema_migrations WHERE name = 'v23_v24_swarm_health';", ignoreErrors: true);
        await ExecAsync(
            _connectionString!,
            "INSERT INTO availability_items (tmdb_id,type,season,episode,status,next_check_at,priority,candidate_info_hash) VALUES "
            + "(1,'movie',-1,-1,'available',0,0,'" + new string('A', 40) + "'),"
            + "(2,'movie',-1,-1,'available',0,0,'" + new string('b', 40) + "'),"
            + "(3,'episode',1,1,'available',0,0,'" + new string('c', 40) + "'),"
            + "(4,'movie',-1,-1,'unavailable',0,0,'" + new string('d', 40) + "');");

        using var v24 = PhantomDb.CreatePostgres(_connectionString!);
        var now = DateTimeOffset.UtcNow;
        var due = await v24.GetSwarmHealthDueItemsAsync(10, now.AddHours(-1), default);
        Assert.Equal(new[] { 1, 2, 3 }, due.Select(d => d.TmdbId).OrderBy(x => x).ToArray());
        Assert.All(due, d => Assert.Equal(d.InfoHash.ToLowerInvariant(), d.InfoHash));

        await v24.UpsertSwarmHealthAsync(
            new[]
            {
                new SwarmHealthMeasurement(new string('A', 40), 1, "movie", -1, -1, 9, now),
                new SwarmHealthMeasurement(new string('c', 40), 3, "episode", 1, 1, 4, now),
            },
            default);
        await v24.UpsertSwarmHealthAsync(new[] { new SwarmHealthMeasurement(new string('a', 40), 1, "movie", -1, -1, 11, now) }, default);

        var remaining = await v24.GetSwarmHealthDueItemsAsync(10, now.AddHours(-1), default);
        Assert.Equal(2, Assert.Single(remaining).TmdbId);
        Assert.Equal(11, (await v24.GetSwarmPeersByTmdbAsync("movie", now.AddDays(-1), default))[1]);
        Assert.Equal(4, (await v24.GetSwarmPeersByTmdbAsync("episode", now.AddDays(-1), default))[3]);
        Assert.Equal(PhantomDb.CurrentSchemaVersion, await ScalarIntAsync(_connectionString!, "SELECT version FROM phantom_schema_meta;"));
    }

    [Fact]
    public async Task PeersForTmdbIds_PerBatchIndexedLookup_MatchesFullAggregate_OnPostgres()
    {
        if (!Enabled)
        {
            return;
        }

        using var db = PhantomDb.CreatePostgres(_connectionString!);
        var now = DateTimeOffset.UtcNow;
        var rows = Enumerable.Range(1, 700)
            .Select(i => new SwarmHealthMeasurement(i.ToString("x40", System.Globalization.CultureInfo.InvariantCulture), 5000 + (i % 350), i % 2 == 0 ? "movie" : "episode", -1, -1, i % 7, i == 699 ? now.AddDays(-30) : now))
            .ToList();
        await db.UpsertSwarmHealthAsync(rows, default);

        foreach (var type in new[] { "movie", "episode" })
        {
            var full = await db.GetSwarmPeersByTmdbAsync(type, now.AddDays(-7), default);
            // > one 500-id chunk, including ids with no measurement at all.
            var ids = Enumerable.Range(4900, 600).ToList();
            var batch = await db.GetSwarmPeersForTmdbIdsAsync(type, ids, now.AddDays(-7), default);
            Assert.Equal(full.Where(kv => ids.Contains(kv.Key)).OrderBy(kv => kv.Key), batch.OrderBy(kv => kv.Key));
        }

        Assert.Empty(await db.GetSwarmPeersForTmdbIdsAsync("movie", Array.Empty<int>(), now.AddDays(-7), default));
    }

    private static async Task ExecAsync(string cs, string sql, bool ignoreErrors = false)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        try
        {
            await cmd.ExecuteNonQueryAsync();
        }
        catch (PostgresException) when (ignoreErrors)
        {
        }
    }

    private static async Task<int> ScalarIntAsync(string cs, string sql)
    {
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
