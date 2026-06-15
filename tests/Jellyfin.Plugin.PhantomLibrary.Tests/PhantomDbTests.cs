using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class PhantomDbTests : IDisposable
{
    private readonly string _dbPath;

    public PhantomDbTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "phantom-db-tests-" + Guid.NewGuid().ToString("N") + ".db");
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private async Task<PhantomDb> NewDbAsync()
    {
        var db = new PhantomDb(_dbPath);
        // Force schema creation via any helper that opens a connection.
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        return db;
    }

    // ----------------------------------------------------------------
    // Schema / HARD-REFUSE
    // ----------------------------------------------------------------

    [Fact]
    public async Task FreshDb_CreatesSchemaV7_WithAllExpectedTables()
    {
        using var db = await NewDbAsync();

        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
        await using var conn = new SqliteConnection(cs);
        await conn.OpenAsync();

        int version;
        await using (var v = conn.CreateCommand())
        {
            v.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt32(await v.ExecuteScalarAsync());
        }

        Assert.Equal(7, version);

        var expectedTables = new[]
        {
            "discovery_cache",
            "materialised_state",
            "materialise_in_flight",
            "tmdb_external_ids",
            "tmdb_cache",
            "magnet_cache",
            "unavailable_marker",
            "plugin_meta",
        };

        foreach (var tbl in expectedTables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name=$n";
            cmd.Parameters.AddWithValue("$n", tbl);
            var got = await cmd.ExecuteScalarAsync();
            Assert.Equal(tbl, got as string);
        }
    }

    [Fact]
    public async Task HardRefuse_OldSchemaVersion_ThrowsWithWipePointer()
    {
        // Pre-create a DB with user_version=5 (the pre-channel-arch schema).
        var cs = new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        await using (var conn = new SqliteConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 5;";
            await cmd.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();

        using var db = new PhantomDb(_dbPath);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.SetMetaAsync("test", "1", CancellationToken.None));

        Assert.Contains("version 7", ex.Message, StringComparison.Ordinal);
        Assert.Contains("wipe", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HardRefuse_NewerSchemaVersion_Throws()
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        await using (var conn = new SqliteConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version = 99;";
            await cmd.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();

        using var db = new PhantomDb(_dbPath);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.SetMetaAsync("test", "1", CancellationToken.None));
    }

    [Fact]
    public async Task FreshDb_UserVersionZero_InitialisesCleanly()
    {
        // user_version is 0 on a brand-new DB; this is the supported path.
        using var db = await NewDbAsync();
        var v = await db.GetMetaAsync("__init__", CancellationToken.None);
        Assert.Equal("1", v);
    }

    // ----------------------------------------------------------------
    // discovery_cache
    // ----------------------------------------------------------------

    [Fact]
    public async Task DiscoveryCache_Upsert_List_Roundtrips()
    {
        using var db = await NewDbAsync();
        await db.UpsertDiscoveryCacheAsync(42, "movie", CancellationToken.None);
        await db.UpsertDiscoveryCacheAsync(43, "movie", CancellationToken.None);
        await db.UpsertDiscoveryCacheAsync(99, "series", CancellationToken.None);

        var movies = await db.ListDiscoveryCacheAsync("movie", CancellationToken.None);
        var series = await db.ListDiscoveryCacheAsync("series", CancellationToken.None);

        Assert.Equal(2, movies.Count);
        Assert.Contains(movies, r => r.TmdbId == 42);
        Assert.Contains(movies, r => r.TmdbId == 43);
        Assert.Single(series);
        Assert.Equal(99, series[0].TmdbId);
    }

    [Fact]
    public async Task DiscoveryCache_UpsertSameKey_UpdatesLastRefreshed()
    {
        using var db = await NewDbAsync();
        await db.UpsertDiscoveryCacheAsync(42, "movie", CancellationToken.None);
        var first = await db.ListDiscoveryCacheAsync("movie", CancellationToken.None);
        await Task.Delay(1100); // ensure unix-ts increments
        await db.UpsertDiscoveryCacheAsync(42, "movie", CancellationToken.None);
        var second = await db.ListDiscoveryCacheAsync("movie", CancellationToken.None);

        Assert.Single(first);
        Assert.Single(second);
        Assert.True(second[0].LastRefreshed >= first[0].LastRefreshed);
        // discovered_at preserved
        Assert.Equal(first[0].DiscoveredAt, second[0].DiscoveredAt);
    }

    [Fact]
    public async Task DiscoveryCache_PurgeStale_RemovesOnlyOlderThanTtl()
    {
        using var db = await NewDbAsync();
        await db.UpsertDiscoveryCacheAsync(42, "movie", CancellationToken.None);
        await Task.Delay(2500);
        await db.UpsertDiscoveryCacheAsync(43, "movie", CancellationToken.None);

        // Purge anything older than 1 second
        var purged = await db.PurgeStaleDiscoveryAsync(TimeSpan.FromSeconds(1), protectFavourited: false, CancellationToken.None);

        Assert.Equal(1, purged);
        var remaining = await db.ListDiscoveryCacheAsync("movie", CancellationToken.None);
        Assert.Single(remaining);
        Assert.Equal(43, remaining[0].TmdbId);
    }

    [Fact]
    public async Task DiscoveryCache_DeleteRow_RemovesIt()
    {
        using var db = await NewDbAsync();
        await db.UpsertDiscoveryCacheAsync(42, "movie", CancellationToken.None);
        await db.DeleteDiscoveryCacheRowAsync(42, "movie", CancellationToken.None);
        var rows = await db.ListDiscoveryCacheAsync("movie", CancellationToken.None);
        Assert.Empty(rows);
    }

    // ----------------------------------------------------------------
    // materialised_state — sentinel PK isolation
    // ----------------------------------------------------------------

    [Fact]
    public async Task MaterialisedState_MovieAndEpisodeSameTmdb_IndependentRows()
    {
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(42, "movie", -1, -1, "/stub/movie.mkv", "/fuse/movie.mkv", CancellationToken.None);
        await db.InsertMaterialisedStateAsync(42, "episode", 1, 1, "/stub/ep.mkv", "/fuse/ep.mkv", CancellationToken.None);

        var movie = await db.GetMaterialisedStateAsync(42, "movie", -1, -1, CancellationToken.None);
        var episode = await db.GetMaterialisedStateAsync(42, "episode", 1, 1, CancellationToken.None);

        Assert.NotNull(movie);
        Assert.Equal("/fuse/movie.mkv", movie!.FusePath);
        Assert.NotNull(episode);
        Assert.Equal("/fuse/ep.mkv", episode!.FusePath);
    }

    [Fact]
    public async Task MaterialisedState_ListByType_ReturnsOnlyMatchingType()
    {
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(42, "movie", -1, -1, "/s/a", "/f/a", CancellationToken.None);
        await db.InsertMaterialisedStateAsync(43, "movie", -1, -1, "/s/b", "/f/b", CancellationToken.None);
        await db.InsertMaterialisedStateAsync(99, "episode", 1, 1, "/s/e", "/f/e", CancellationToken.None);

        var movies = await db.ListMaterialisedStateAsync("movie", CancellationToken.None);
        var eps = await db.ListMaterialisedStateAsync("episode", CancellationToken.None);

        Assert.Equal(2, movies.Count);
        Assert.Single(eps);
    }

    [Fact]
    public async Task MaterialisedState_Delete_RemovesRow()
    {
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(42, "movie", -1, -1, "/s", "/f", CancellationToken.None);
        await db.DeleteMaterialisedStateAsync(42, "movie", -1, -1, CancellationToken.None);
        var got = await db.GetMaterialisedStateAsync(42, "movie", -1, -1, CancellationToken.None);
        Assert.Null(got);
    }

    [Fact]
    public async Task MaterialisedState_GetMissing_ReturnsNull()
    {
        using var db = await NewDbAsync();
        var got = await db.GetMaterialisedStateAsync(404, "movie", -1, -1, CancellationToken.None);
        Assert.Null(got);
    }

    // ----------------------------------------------------------------
    // materialise_in_flight
    // ----------------------------------------------------------------

    [Fact]
    public async Task MaterialiseInFlight_Upsert_IsInFlight_Delete_Cycle()
    {
        using var db = await NewDbAsync();
        Assert.False(await db.IsMaterialiseInFlightAsync(42, "movie", -1, -1, CancellationToken.None));

        await db.UpsertMaterialiseInFlightAsync(42, "movie", -1, -1, CancellationToken.None);
        Assert.True(await db.IsMaterialiseInFlightAsync(42, "movie", -1, -1, CancellationToken.None));

        await db.DeleteMaterialiseInFlightAsync(42, "movie", -1, -1, CancellationToken.None);
        Assert.False(await db.IsMaterialiseInFlightAsync(42, "movie", -1, -1, CancellationToken.None));
    }

    [Fact]
    public async Task MaterialiseInFlight_PurgeStale_RemovesOldRows_KeepsFresh()
    {
        using var db = await NewDbAsync();
        await db.UpsertMaterialiseInFlightAsync(42, "movie", -1, -1, CancellationToken.None);
        await Task.Delay(2500);
        await db.UpsertMaterialiseInFlightAsync(43, "movie", -1, -1, CancellationToken.None);

        var purged = await db.PurgeStaleMaterialiseInFlightAsync(TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.Equal(1, purged);
        Assert.False(await db.IsMaterialiseInFlightAsync(42, "movie", -1, -1, CancellationToken.None));
        Assert.True(await db.IsMaterialiseInFlightAsync(43, "movie", -1, -1, CancellationToken.None));
    }

    [Fact]
    public async Task MaterialiseInFlight_PurgeWhenEmpty_ReturnsZero()
    {
        using var db = await NewDbAsync();
        var purged = await db.PurgeStaleMaterialiseInFlightAsync(TimeSpan.FromMinutes(10), CancellationToken.None);
        Assert.Equal(0, purged);
    }

    // ----------------------------------------------------------------
    // tmdb_external_ids — positive + negative cache
    // ----------------------------------------------------------------

    [Fact]
    public async Task TmdbExternalIds_SetPositive_GetReturnsImdbId()
    {
        using var db = await NewDbAsync();
        await db.SetImdbIdAsync(42, "movie", "tt0000042", CancellationToken.None);
        var got = await db.GetImdbIdAsync(42, "movie", CancellationToken.None);
        Assert.NotNull(got);
        Assert.Equal("tt0000042", got!.ImdbId);
    }

    [Fact]
    public async Task TmdbExternalIds_SetNegative_GetReturnsRowWithNullImdb()
    {
        using var db = await NewDbAsync();
        await db.SetImdbIdAsync(42, "movie", null, CancellationToken.None);
        var got = await db.GetImdbIdAsync(42, "movie", CancellationToken.None);
        Assert.NotNull(got);
        Assert.Null(got!.ImdbId);
    }

    [Fact]
    public async Task TmdbExternalIds_GetMissing_ReturnsNull()
    {
        using var db = await NewDbAsync();
        var got = await db.GetImdbIdAsync(404, "movie", CancellationToken.None);
        Assert.Null(got);
    }

    [Fact]
    public async Task TmdbExternalIds_UpsertOverwritesPrevious()
    {
        using var db = await NewDbAsync();
        await db.SetImdbIdAsync(42, "movie", null, CancellationToken.None);
        await db.SetImdbIdAsync(42, "movie", "tt0000042", CancellationToken.None);
        var got = await db.GetImdbIdAsync(42, "movie", CancellationToken.None);
        Assert.Equal("tt0000042", got!.ImdbId);
    }

    [Fact]
    public async Task TmdbExternalIds_TypeIsolation_MovieAndSeriesIndependent()
    {
        using var db = await NewDbAsync();
        await db.SetImdbIdAsync(42, "movie", "tt0000042", CancellationToken.None);
        await db.SetImdbIdAsync(42, "series", "tt9999042", CancellationToken.None);

        var m = await db.GetImdbIdAsync(42, "movie", CancellationToken.None);
        var s = await db.GetImdbIdAsync(42, "series", CancellationToken.None);

        Assert.Equal("tt0000042", m!.ImdbId);
        Assert.Equal("tt9999042", s!.ImdbId);
    }

    // ----------------------------------------------------------------
    // Surviving helpers: magnet_cache, unavailable_marker, plugin_meta
    // (smoke tests — these tables and helpers carry over from v5).
    // ----------------------------------------------------------------

    [Fact]
    public async Task MagnetCache_PutGet_Roundtrips()
    {
        using var db = await NewDbAsync();
        var key = new MagnetCacheKey(42, "tt0000042", "movie", null, null, "preset-A");
        var entry = new MagnetCacheEntry
        {
            Magnet = "magnet:?xt=urn:btih:abc",
            InfoHash = "abc",
            Size = 1234567890,
            Seeders = 99,
            Indexer = "test-indexer",
            CachedAt = DateTimeOffset.UtcNow,
            Ttl = TimeSpan.FromHours(24),
            Source = "user",
        };
        await db.PutCachedMagnetAsync(key, entry, CancellationToken.None);
        var got = await db.GetCachedMagnetAsync(key, CancellationToken.None);
        Assert.NotNull(got);
        Assert.Equal("magnet:?xt=urn:btih:abc", got!.Magnet);
        Assert.Equal(99, got.Seeders);
    }

    [Fact]
    public async Task UnavailableMarker_MarkAndCheck_Roundtrips()
    {
        using var db = await NewDbAsync();
        var key = new UnavailableKey(42, "tt0000042", "movie", null, null);
        Assert.Null(await db.IsMarkedUnavailableAsync(key, CancellationToken.None));

        await db.MarkUnavailableAsync(key, TimeSpan.FromHours(1), CancellationToken.None);
        var until = await db.IsMarkedUnavailableAsync(key, CancellationToken.None);
        Assert.NotNull(until);
        Assert.True(until > DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task PluginMeta_SetGet_Roundtrips()
    {
        using var db = await NewDbAsync();
        Assert.Null(await db.GetMetaAsync("missing", CancellationToken.None));
        await db.SetMetaAsync("key1", "value1", CancellationToken.None);
        Assert.Equal("value1", await db.GetMetaAsync("key1", CancellationToken.None));
        await db.SetMetaAsync("key1", "value2", CancellationToken.None);
        Assert.Equal("value2", await db.GetMetaAsync("key1", CancellationToken.None));
    }
}
