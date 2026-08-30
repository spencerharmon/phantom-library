using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.State;
using Jellyfin.Plugin.PhantomLibrary.State.Db;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// Real-PostgreSQL integration coverage for the <see cref="PostgresDbProvider"/>
/// backend (<c>p4-phantomdb-postgres-provider</c>). Gated on the
/// <c>PHANTOM_TEST_POSTGRES_DSN</c> environment variable pointing at a real,
/// disposable Postgres server — never a mock, per this task's evidence
/// requirement. A plain <c>dotnet test</c> run (no Postgres server available)
/// stays green: every test below returns immediately, doing nothing, when the
/// variable is unset.
///
/// To run against a real server:
/// <code>
/// podman run -d --name phantom-pg-test -p 15432:5432 \
///   -e POSTGRES_USER=phantom -e POSTGRES_PASSWORD=phantom -e POSTGRES_DB=phantom_test \
///   docker.io/library/postgres:16-alpine
/// PHANTOM_TEST_POSTGRES_DSN="Host=localhost;Port=15432;Username=phantom;Password=phantom;Database=phantom_test" \
///   MSBUILDDISABLENODEREUSE=1 dotnet test -p:UseSharedCompilation=false \
///   --filter "FullyQualifiedName~Postgres"
/// </code>
/// Each test creates and drops its own Postgres SCHEMA for isolation, so the suite
/// is safe to run repeatedly / in parallel against the same server.
/// </summary>
public sealed class PhantomDbPostgresIntegrationTests : IAsyncLifetime
{
    private static readonly string? Dsn = Environment.GetEnvironmentVariable("PHANTOM_TEST_POSTGRES_DSN");

    private readonly string _schema = "phantom_it_" + Guid.NewGuid().ToString("N");
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

    private PhantomDb NewDb() => PhantomDb.CreatePostgres(_connectionString!);

    // ---- (a) fresh backend reports Postgres, CRUD round-trips ----

    [Fact]
    public async Task FreshDb_ReportsPostgresBackend()
    {
        if (!Enabled)
        {
            return;
        }

        using var db = NewDb();
        Assert.Equal(PhantomDbBackend.Postgres, db.Backend);

        // Any call opens the connection and lazily ensures schema; if this
        // throws, the schema DDL (shared verbatim with SQLite) is not
        // Postgres-compatible.
        var count = await db.CountCatalogueItemsAsync("movie", null, default);
        Assert.Equal(0, count);
    }

    [Fact]
    public async Task MagnetCache_InsertOrReplace_RoundTrips()
    {
        if (!Enabled)
        {
            return;
        }

        using var db = NewDb();
        var key = new MagnetCacheKey(12345, null, "movie", null, null, "1080p");
        var entry = new MagnetCacheEntry
        {
            Magnet = "magnet:?xt=urn:btih:aaaa",
            InfoHash = "aaaa",
            Size = 123456,
            Seeders = 7,
            Indexer = "prowlarr",
            CachedAt = DateTimeOffset.UtcNow,
            Ttl = TimeSpan.FromHours(1),
            Source = "user",
        };

        await db.PutCachedMagnetAsync(key, entry, default);
        var read = await db.GetCachedMagnetAsync(key, default);
        Assert.NotNull(read);
        Assert.Equal(entry.Magnet, read!.Magnet);
        Assert.Equal(entry.InfoHash, read.InfoHash);
        Assert.Equal(entry.Seeders, read.Seeders);

        // Second write with the SAME key exercises the translated
        // "INSERT OR REPLACE" -> "ON CONFLICT ... DO UPDATE" path.
        var updated = entry with { Seeders = 99 };
        await db.PutCachedMagnetAsync(key, updated, default);
        var readAgain = await db.GetCachedMagnetAsync(key, default);
        Assert.NotNull(readAgain);
        Assert.Equal(99, readAgain!.Seeders);
    }

    [Fact]
    public async Task CatalogueItems_InsertOrIgnore_DoesNotOverwriteFirstSeenAt()
    {
        if (!Enabled)
        {
            return;
        }

        using var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        var row = new TmdbMetadataRow(
            TmdbId: 555,
            Type: "movie",
            Title: "Test Movie",
            Year: 2020,
            Overview: null,
            PosterUrl: null,
            BackdropUrl: null,
            Genres: null,
            OfficialRating: null,
            CommunityRating: null,
            OriginalTitle: null,
            RuntimeMinutes: null,
            FetchedAt: now);

        var first = await db.UpsertCatalogueHitsAsync(new[] { row }, 1, now, default);
        Assert.Equal(1, first.Inserted);

        // Second hit for the SAME (tmdb_id, type) exercises the translated
        // "INSERT OR IGNORE" -> "ON CONFLICT ... DO NOTHING" path — it must not
        // throw a duplicate-key error, and must not re-insert.
        var second = await db.UpsertCatalogueHitsAsync(new[] { row }, 2, now.AddMinutes(5), default);
        Assert.Equal(0, second.Inserted);

        var count = await db.CountCatalogueItemsAsync("movie", null, default);
        Assert.Equal(1, count);
    }

    // ---- (b) EnsureSchema hard-refuses a version mismatch against Postgres ----

    [Fact]
    public async Task EnsureSchema_HardRefusesOlderVersion()
    {
        if (!Enabled)
        {
            return;
        }

        using (var db = NewDb())
        {
            // Opens + creates the current schema.
            await db.CountCatalogueItemsAsync("movie", null, default);
        }

        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE phantom_schema_meta SET version = 1;";
            await cmd.ExecuteNonQueryAsync();
        }

        using var staleDb = NewDb();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => staleDb.CountCatalogueItemsAsync("movie", null, default));
        Assert.Contains("Pre-v1.0 has no migrations", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureSchema_ForwardTolerant_NewerVersionIsToleratedNotRefused()
    {
        if (!Enabled)
        {
            return;
        }

        using (var db = NewDb())
        {
            await db.CountCatalogueItemsAsync("movie", null, default);
        }

        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE phantom_schema_meta SET version = {PhantomDb.CurrentSchemaVersion + 1};";
            await cmd.ExecuteNonQueryAsync();
        }

        // Forward-tolerant gate (p7-forward-tolerant-schema-gate): a db_version
        // strictly GREATER than this build's CurrentSchemaVersion means a newer
        // color already ran its additive expand migration — the blue/green
        // coexistence contract requires this build to TOLERATE that, not
        // hard-refuse it, so the still-running older-schema color keeps working
        // during the shared-Postgres cutover window.
        using var futureDb = NewDb();
        var count = await futureDb.CountCatalogueItemsAsync("movie", null, default);
        Assert.Equal(0, count);

        // The recorded version must NOT be rewritten downward to this older
        // build's CurrentSchemaVersion — the newer color's stamp stands.
        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT version FROM phantom_schema_meta LIMIT 1;";
            var version = Convert.ToInt32(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
            Assert.Equal(PhantomDb.CurrentSchemaVersion + 1, version);
        }
    }

    // ---- (c) multi-writer safety against the SAME logical Postgres DB ----

    [Fact]
    public async Task MaterialiseInFlight_TwoReplicas_ExactlyOneWinnerOnSameKey()
    {
        if (!Enabled)
        {
            return;
        }

        using var replicaA = NewDb();
        using var replicaB = NewDb();
        Assert.NotEqual(replicaA.HostId, replicaB.HostId);

        const int tmdbId = 909090;
        const string type = "movie";
        const int season = -1;
        const int episode = -1;

        var tasks = new List<Task<bool>>();
        for (var i = 0; i < 8; i++)
        {
            var db = i % 2 == 0 ? replicaA : replicaB;
            tasks.Add(db.TryInsertMaterialiseInFlightAsync(tmdbId, type, season, episode, default));
        }

        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, results.Count(r => r));

        var stillInFlight = await replicaA.IsMaterialiseInFlightAsync(tmdbId, type, season, episode, default);
        Assert.True(stillInFlight);
    }

    [Fact]
    public async Task Purge_NeverStealsLiveSiblingReplicaLock_ButReclaimsLeakedForeignRow()
    {
        if (!Enabled)
        {
            return;
        }

        using var replicaA = NewDb();
        using var replicaB = NewDb();

        // replicaA holds a FRESH lock (well within any TTL): must survive a purge
        // even though it is a foreign (not-my-HostId) row from replicaB's view.
        await replicaA.TryInsertMaterialiseInFlightAsync(111, "movie", -1, -1, default);

        // replicaB holds its OWN fresh lock too — must never be touched by a
        // purge it itself runs, regardless of any foreign-row reclaim.
        await replicaB.TryInsertMaterialiseInFlightAsync(222, "movie", -1, -1, default);

        // Fresh locks survive a purge with a threshold no row is older than.
        var purgedNone = await replicaB.PurgeStaleMaterialiseInFlightAsync(TimeSpan.FromHours(1), default, TimeSpan.FromHours(1));
        Assert.Equal(0, purgedNone);
        Assert.True(await replicaA.IsMaterialiseInFlightAsync(111, "movie", -1, -1, default));
        Assert.True(await replicaB.IsMaterialiseInFlightAsync(222, "movie", -1, -1, default));

        // Simulate replicaA having crashed a long time ago: backdate ITS row
        // directly (never through PhantomDb, which has no "backdate" API) far
        // past any real crash-recovery TTL. replicaB's own row stays untouched,
        // fresh.
        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE materialise_in_flight SET started_at = @old WHERE tmdb_id = 111;";
            cmd.Parameters.AddWithValue("old", DateTimeOffset.UtcNow.AddDays(-10).ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync();
        }

        // own threshold (1 hour) is smaller than the foreign threshold (1 day) —
        // required by PurgeStaleMaterialiseInFlightAsync's invariant that a
        // foreign row NEVER gets a *tighter* reclaim window than the caller's
        // own rows. replicaB's fresh (seconds-old) own row survives the 1-hour
        // own cutoff; replicaA's now-10-day-old foreign row is past the 1-day
        // foreign cutoff and is reclaimed.
        var purgedForeign = await replicaB.PurgeStaleMaterialiseInFlightAsync(TimeSpan.FromHours(1), default, TimeSpan.FromDays(1));
        Assert.Equal(1, purgedForeign);
        Assert.False(await replicaA.IsMaterialiseInFlightAsync(111, "movie", -1, -1, default));
        Assert.True(await replicaB.IsMaterialiseInFlightAsync(222, "movie", -1, -1, default));
    }

    [Fact]
    public async Task UserPrefs_ConcurrentUpsertFromTwoReplicas_NoConstraintViolation()
    {
        if (!Enabled)
        {
            return;
        }

        using var replicaA = NewDb();
        using var replicaB = NewDb();

        var userId = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var tasks = new List<Task>();
        for (var i = 0; i < 6; i++)
        {
            var db = i % 2 == 0 ? replicaA : replicaB;
            var prefs = new UserPrefs(i % 3 != 0, i % 2 == 0, i % 4 != 0);
            tasks.Add(db.UpsertUserPrefsAsync(userId, prefs, default));
        }

        // Must complete without a constraint violation / corrupted row from the
        // concurrent same-key upsert race across two replicas' own connections.
        await Task.WhenAll(tasks);

        var final = await replicaA.GetUserPrefsAsync(userId, default);
        Assert.NotNull(final);
    }
}
