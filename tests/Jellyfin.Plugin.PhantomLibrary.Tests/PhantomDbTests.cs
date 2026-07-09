using System;
using System.Collections.Generic;
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
    public async Task FreshDb_CreatesSchemaV12_WithAllExpectedTables()
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

        Assert.Equal(12, version);

        var expectedTables = new[]
        {
            "discovery_cache",
            "catalogue_items",
            "series_expansion_state",
            "series_episode_catalogue",
            "availability_items",
            "materialised_state",
            "materialise_in_flight",
            "tmdb_external_ids",
            "tmdb_cache",
            "tmdb_metadata",
            "tmdb_episode_cache",
            "magnet_cache",
            "magnet_failure_cache",
            "unavailable_marker",
            "plugin_meta",
            // v12 additive per-user tables (REQ-M14-PER-USER branch B).
            "user_prefs",
            "user_hidden_items",
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

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    public async Task HardRefuse_OldSchemaVersion_ThrowsWithWipePointer(int oldVersion)
    {
        // Pre-create a DB with an older user_version (v5 = pre-channel-arch,
        // v7/v8 = intermediate channel-arch schemas without tmdb_episode_cache,
        // v10/v11 = pre per-user-schema channel-arch versions). v12 adds the
        // additive per-user tables but ships no runtime migration, so every
        // pre-v12 version is still HARD-REFUSED with the wipe pointer.
        var cs = new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        await using (var conn = new SqliteConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA user_version = {oldVersion};";
            await cmd.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();

        using var db = new PhantomDb(_dbPath);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.SetMetaAsync("test", "1", CancellationToken.None));

        Assert.Contains("version 12", ex.Message, StringComparison.Ordinal);
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
    // v12 per-user schema (REQ-M14-PER-USER branch B) — additive tables
    // created on a fresh DB. These are schema-shape assertions; the
    // read/write accessors land with the dependent per-user backend task.
    // ----------------------------------------------------------------

    [Fact]
    public async Task FreshDb_UserPrefsTable_HasTogglesKeyedByUserId()
    {
        using var db = await NewDbAsync();
        var cols = await ReadColumnsAsync("user_prefs");

        Assert.Equal(
            new[] { "allow_eager", "protect_favourites", "show_phantoms", "updated_at", "user_id" },
            cols.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        // user_id is the sole primary key (one prefs row per Jellyfin user).
        Assert.Equal(1, cols["user_id"].Pk);
        Assert.Equal(0, cols["updated_at"].Pk);

        // The three toggles default ON so an absent explicit choice reads as
        // enabled, and are NOT NULL. Favourites are intentionally absent —
        // favourite state comes from Jellyfin UserData, not this table.
        Assert.Equal("1", cols["protect_favourites"].Default);
        Assert.Equal("1", cols["show_phantoms"].Default);
        Assert.Equal("1", cols["allow_eager"].Default);
        Assert.Equal(1, cols["protect_favourites"].NotNull);
        Assert.Equal(1, cols["show_phantoms"].NotNull);
        Assert.Equal(1, cols["allow_eager"].NotNull);
    }

    [Fact]
    public async Task FreshDb_UserHiddenItemsTable_HasCompositePrimaryKey()
    {
        using var db = await NewDbAsync();
        var cols = await ReadColumnsAsync("user_hidden_items");

        Assert.Equal(
            new[] { "hidden_at", "tmdb_id", "type", "user_id" },
            cols.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());

        // Composite PK (user_id, tmdb_id, type): the hidden set is per
        // (user, catalogue title), so one user hiding a title never collides
        // with another user's identical hide.
        Assert.Equal(1, cols["user_id"].Pk);
        Assert.Equal(2, cols["tmdb_id"].Pk);
        Assert.Equal(3, cols["type"].Pk);
        Assert.Equal(0, cols["hidden_at"].Pk);
    }

    /// <summary>
    /// Reads <c>PRAGMA table_info</c> for <paramref name="table"/> from a
    /// read-only connection, returning per-column (NotNull, Default, Pk).
    /// <c>Pk</c> is the 1-based position of the column within the primary
    /// key (0 = not part of the PK), matching SQLite's table_info contract.
    /// </summary>
    private async Task<Dictionary<string, (int NotNull, string? Default, int Pk)>> ReadColumnsAsync(string table)
    {
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();
        await using var conn = new SqliteConnection(cs);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // table is a test-local constant, not user input: PRAGMA does not
        // accept a bound parameter for the table name.
        cmd.CommandText = $"PRAGMA table_info({table});";
        var result = new Dictionary<string, (int, string?, int)>(StringComparer.Ordinal);
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            // cid=0, name=1, type=2, notnull=3, dflt_value=4, pk=5
            var name = r.GetString(1);
            var notnull = r.GetInt32(3);
            var dflt = await r.IsDBNullAsync(4) ? null : r.GetValue(4).ToString();
            var pk = r.GetInt32(5);
            result[name] = (notnull, dflt, pk);
        }

        return result;
    }

    // ----------------------------------------------------------------
    // v12 per-user accessors (REQ-M14-PER-USER branch B, backend task):
    // user_prefs read/write, user_hidden_items set ops, and the
    // userId-aware visibility overloads (composition over the server-wide
    // queries).
    // ----------------------------------------------------------------

    [Fact]
    public async Task UserPrefs_MissingRow_ReturnsDefaultsAllOn()
    {
        using var db = await NewDbAsync();
        var prefs = await db.GetUserPrefsAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Equal(UserPrefs.Defaults, prefs);
        Assert.True(prefs.ProtectFavourites);
        Assert.True(prefs.ShowPhantoms);
        Assert.True(prefs.AllowEager);
    }

    [Fact]
    public async Task UserPrefs_UpsertThenGet_RoundtripsEachToggle()
    {
        using var db = await NewDbAsync();
        var user = Guid.NewGuid();
        await db.UpsertUserPrefsAsync(user, new UserPrefs(ProtectFavourites: false, ShowPhantoms: true, AllowEager: false), CancellationToken.None);

        var got = await db.GetUserPrefsAsync(user, CancellationToken.None);
        Assert.False(got.ProtectFavourites);
        Assert.True(got.ShowPhantoms);
        Assert.False(got.AllowEager);
    }

    [Fact]
    public async Task UserPrefs_UpsertTwice_OverwritesPrevious()
    {
        using var db = await NewDbAsync();
        var user = Guid.NewGuid();
        await db.UpsertUserPrefsAsync(user, new UserPrefs(false, false, false), CancellationToken.None);
        await db.UpsertUserPrefsAsync(user, new UserPrefs(true, false, true), CancellationToken.None);

        var got = await db.GetUserPrefsAsync(user, CancellationToken.None);
        Assert.True(got.ProtectFavourites);
        Assert.False(got.ShowPhantoms);
        Assert.True(got.AllowEager);
    }

    [Fact]
    public async Task UserPrefs_IsolatedPerUser()
    {
        using var db = await NewDbAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await db.UpsertUserPrefsAsync(a, new UserPrefs(false, false, false), CancellationToken.None);

        var pa = await db.GetUserPrefsAsync(a, CancellationToken.None);
        var pb = await db.GetUserPrefsAsync(b, CancellationToken.None);

        Assert.Equal(new UserPrefs(false, false, false), pa);
        // b never wrote a row -> defaults, unaffected by a's write.
        Assert.Equal(UserPrefs.Defaults, pb);
    }

    [Fact]
    public async Task HiddenItems_AddIsListRemove_Cycle()
    {
        using var db = await NewDbAsync();
        var user = Guid.NewGuid();

        Assert.False(await db.IsItemHiddenAsync(user, 42, "movie", CancellationToken.None));

        await db.AddHiddenItemAsync(user, 42, "movie", CancellationToken.None);
        Assert.True(await db.IsItemHiddenAsync(user, 42, "movie", CancellationToken.None));

        var list = await db.ListHiddenItemsAsync(user, CancellationToken.None);
        Assert.Single(list);
        Assert.Equal(42, list[0].TmdbId);
        Assert.Equal("movie", list[0].Type);

        await db.RemoveHiddenItemAsync(user, 42, "movie", CancellationToken.None);
        Assert.False(await db.IsItemHiddenAsync(user, 42, "movie", CancellationToken.None));
        Assert.Empty(await db.ListHiddenItemsAsync(user, CancellationToken.None));
    }

    [Fact]
    public async Task HiddenItems_AddIdempotent_SingleRow()
    {
        using var db = await NewDbAsync();
        var user = Guid.NewGuid();
        await db.AddHiddenItemAsync(user, 42, "series", CancellationToken.None);
        await db.AddHiddenItemAsync(user, 42, "series", CancellationToken.None);

        var list = await db.ListHiddenItemsAsync(user, CancellationToken.None);
        Assert.Single(list);
    }

    [Fact]
    public async Task HiddenItems_RemoveMissing_IsNoOp()
    {
        using var db = await NewDbAsync();
        var user = Guid.NewGuid();
        // Must not throw and must leave the set empty.
        await db.RemoveHiddenItemAsync(user, 404, "movie", CancellationToken.None);
        Assert.Empty(await db.ListHiddenItemsAsync(user, CancellationToken.None));
    }

    [Fact]
    public async Task HiddenItems_IsolatedPerUser()
    {
        using var db = await NewDbAsync();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await db.AddHiddenItemAsync(a, 42, "movie", CancellationToken.None);

        Assert.True(await db.IsItemHiddenAsync(a, 42, "movie", CancellationToken.None));
        Assert.False(await db.IsItemHiddenAsync(b, 42, "movie", CancellationToken.None));
    }

    [Fact]
    public async Task HiddenItems_TypeIsolation_MovieAndSeriesIndependent()
    {
        using var db = await NewDbAsync();
        var user = Guid.NewGuid();
        await db.AddHiddenItemAsync(user, 42, "movie", CancellationToken.None);

        Assert.True(await db.IsItemHiddenAsync(user, 42, "movie", CancellationToken.None));
        Assert.False(await db.IsItemHiddenAsync(user, 42, "series", CancellationToken.None));
    }

    [Fact]
    public async Task HiddenItems_InvalidType_Throws()
    {
        using var db = await NewDbAsync();
        var user = Guid.NewGuid();
        // 'episode' is not a valid title-level hide type (movie/series only).
        await Assert.ThrowsAsync<ArgumentException>(
            () => db.AddHiddenItemAsync(user, 42, "episode", CancellationToken.None));
    }

    [Fact]
    public async Task ListVisibleMovieRows_UserId_ExcludesOnlyThatUsersHidden()
    {
        using var db = await NewDbAsync();
        await SeedVisibleMovieAsync(db, 42);
        await SeedVisibleMovieAsync(db, 43);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await db.AddHiddenItemAsync(a, 42, "movie", CancellationToken.None);

        var serverWide = await db.ListVisibleMovieRowsAsync(CancellationToken.None);
        Assert.Equal(2, serverWide.Count);

        var forA = await db.ListVisibleMovieRowsAsync(a, CancellationToken.None);
        Assert.Single(forA);
        Assert.Equal(43, forA[0].Metadata.TmdbId);

        // b hid nothing -> sees the full server-wide set.
        var forB = await db.ListVisibleMovieRowsAsync(b, CancellationToken.None);
        Assert.Equal(2, forB.Count);
    }

    [Fact]
    public async Task ListVisibleSeriesRows_UserId_ExcludesOnlyThatUsersHidden()
    {
        using var db = await NewDbAsync();
        await SeedVisibleSeriesAsync(db, 98);
        await SeedVisibleSeriesAsync(db, 99);
        var a = Guid.NewGuid();
        await db.AddHiddenItemAsync(a, 98, "series", CancellationToken.None);

        var serverWide = await db.ListVisibleSeriesRowsAsync(CancellationToken.None);
        Assert.Equal(2, serverWide.Count);

        var forA = await db.ListVisibleSeriesRowsAsync(a, CancellationToken.None);
        Assert.Single(forA);
        Assert.Equal(99, forA[0].Metadata.TmdbId);
    }

    [Fact]
    public async Task IsSeriesVisible_UserId_FalseWhenHiddenByUser()
    {
        using var db = await NewDbAsync();
        await SeedVisibleSeriesAsync(db, 99);
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await db.AddHiddenItemAsync(a, 99, "series", CancellationToken.None);

        Assert.True(await db.IsSeriesVisibleAsync(99, 1, CancellationToken.None));
        Assert.False(await db.IsSeriesVisibleAsync(a, 99, 1, CancellationToken.None));
        Assert.True(await db.IsSeriesVisibleAsync(b, 99, 1, CancellationToken.None));
    }

    [Fact]
    public async Task IsEpisodeVisible_UserId_FalseWhenParentSeriesHiddenByUser()
    {
        using var db = await NewDbAsync();
        await SeedVisibleSeriesAsync(db, 99); // seeds episode (1,1)
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        await db.AddHiddenItemAsync(a, 99, "series", CancellationToken.None);

        Assert.True(await db.IsEpisodeVisibleAsync(99, 1, 1, CancellationToken.None));
        // Hiding is title-level: hiding the series hides its episodes for a.
        Assert.False(await db.IsEpisodeVisibleAsync(a, 99, 1, 1, CancellationToken.None));
        Assert.True(await db.IsEpisodeVisibleAsync(b, 99, 1, 1, CancellationToken.None));
    }

    /// <summary>
    /// Make a movie server-wide-visible: a tmdb_metadata row plus a
    /// materialised_state row (the movie visibility query surfaces a movie
    /// when it is materialised OR marked available).
    /// </summary>
    private static async Task SeedVisibleMovieAsync(PhantomDb db, int tmdbId)
    {
        await db.UpsertTmdbMetadataAsync(
            new TmdbMetadataRow(tmdbId, "movie", "Movie " + tmdbId, null, null, null, null, null, null, null, null, DateTimeOffset.UtcNow),
            CancellationToken.None);
        await db.InsertMaterialisedStateAsync(tmdbId, "movie", -1, -1, "/s/m" + tmdbId, "/f/m" + tmdbId, CancellationToken.None);
    }

    /// <summary>
    /// Make a series server-wide-visible: a tmdb_metadata row plus one
    /// materialised episode (season 1, episode 1) so the series clears the
    /// min-available-episode display gate.
    /// </summary>
    private static async Task SeedVisibleSeriesAsync(PhantomDb db, int tmdbId)
    {
        await db.UpsertTmdbMetadataAsync(
            new TmdbMetadataRow(tmdbId, "series", "Series " + tmdbId, null, null, null, null, null, null, null, null, DateTimeOffset.UtcNow),
            CancellationToken.None);
        await db.InsertMaterialisedStateAsync(tmdbId, "episode", 1, 1, "/s/e" + tmdbId, "/f/e" + tmdbId, CancellationToken.None);
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
    public async Task MaterialiseInFlight_TryInsert_IsAtomicClaim()
    {
        using var db = await NewDbAsync();

        var first = await db.TryInsertMaterialiseInFlightAsync(42, "movie", -1, -1, CancellationToken.None);
        var second = await db.TryInsertMaterialiseInFlightAsync(42, "movie", -1, -1, CancellationToken.None);

        Assert.True(first);
        Assert.False(second);
        Assert.True(await db.IsMaterialiseInFlightAsync(42, "movie", -1, -1, CancellationToken.None));
    }

    [Fact]
    public async Task MaterialiseInFlight_ConcurrentTryInsert_OnlyOneWins()
    {
        using var db = await NewDbAsync();

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            db.TryInsertMaterialiseInFlightAsync(42, "movie", -1, -1, CancellationToken.None)));

        Assert.Equal(1, claims.Count(x => x));
        Assert.Equal(7, claims.Count(x => !x));
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
    public async Task MagnetFailure_MarkGetPurge_Roundtrips()
    {
        using var db = await NewDbAsync();
        var key = new MagnetFailureKey(42, "tt0000042", "episode", 1, 2, "preset-A", "magnet:?xt=urn:btih:bad");
        var now = DateTimeOffset.UtcNow;
        await db.MarkMagnetFailedAsync(key, new MagnetFailureEntry
        {
            InfoHash = "bad",
            Reason = "target_episode_not_found",
            FailedAt = now,
            RetryAfter = now.AddHours(1),
        }, CancellationToken.None);

        var got = await db.GetMagnetFailureAsync(key, CancellationToken.None);
        Assert.NotNull(got);
        Assert.Equal("target_episode_not_found", got!.Reason);

        var purged = await db.PurgeExpiredMagnetFailuresAsync(CancellationToken.None);
        Assert.Equal(0, purged);
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

    // ----------------------------------------------------------------
    // tmdb_metadata
    // ----------------------------------------------------------------

    [Fact]
    public async Task TmdbMetadata_UpsertGet_RoundtripsAllFields()
    {
        using var db = await NewDbAsync();
        var fetched = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var row = new TmdbMetadataRow(
            TmdbId: 42,
            Type: "movie",
            Title: "The Answer",
            Year: 1979,
            Overview: "A meditation on 42.",
            PosterUrl: "https://image.tmdb.org/t/p/w500/poster.jpg",
            BackdropUrl: "https://image.tmdb.org/t/p/w500/backdrop.jpg",
            Genres: new[] { "Drama", "Sci-Fi" },
            OfficialRating: "PG",
            CommunityRating: 7.5,
            OriginalTitle: "La Réponse",
            FetchedAt: fetched);

        await db.UpsertTmdbMetadataAsync(row, CancellationToken.None);
        var got = await db.GetTmdbMetadataAsync(42, "movie", CancellationToken.None);
        Assert.NotNull(got);
        Assert.Equal(42, got!.TmdbId);
        Assert.Equal("movie", got.Type);
        Assert.Equal("The Answer", got.Title);
        Assert.Equal(1979, got.Year);
        Assert.Equal("A meditation on 42.", got.Overview);
        Assert.Equal("https://image.tmdb.org/t/p/w500/poster.jpg", got.PosterUrl);
        Assert.Equal("https://image.tmdb.org/t/p/w500/backdrop.jpg", got.BackdropUrl);
        Assert.NotNull(got.Genres);
        Assert.Equal(new[] { "Drama", "Sci-Fi" }, got.Genres);
        Assert.Equal("PG", got.OfficialRating);
        Assert.Equal(7.5, got.CommunityRating);
        Assert.Equal("La Réponse", got.OriginalTitle);
        Assert.Equal(fetched, got.FetchedAt);
    }

    [Fact]
    public async Task TmdbMetadata_UpsertWithNullables_RoundtripsAsNull()
    {
        using var db = await NewDbAsync();
        var row = new TmdbMetadataRow(
            TmdbId: 99,
            Type: "series",
            Title: "Title Only",
            Year: null,
            Overview: null,
            PosterUrl: null,
            BackdropUrl: null,
            Genres: null,
            OfficialRating: null,
            CommunityRating: null,
            OriginalTitle: null,
            FetchedAt: DateTimeOffset.UtcNow);

        await db.UpsertTmdbMetadataAsync(row, CancellationToken.None);
        var got = await db.GetTmdbMetadataAsync(99, "series", CancellationToken.None);
        Assert.NotNull(got);
        Assert.Equal("Title Only", got!.Title);
        Assert.Null(got.Year);
        Assert.Null(got.Overview);
        Assert.Null(got.PosterUrl);
        Assert.Null(got.BackdropUrl);
        Assert.Null(got.Genres);
        Assert.Null(got.OfficialRating);
        Assert.Null(got.CommunityRating);
        Assert.Null(got.OriginalTitle);
    }

    [Fact]
    public async Task TmdbMetadata_TypeIsolation_MovieAndSeriesIndependent()
    {
        using var db = await NewDbAsync();
        await db.UpsertTmdbMetadataAsync(
            new TmdbMetadataRow(42, "movie", "Movie", null, null, null, null, null, null, null, null, DateTimeOffset.UtcNow),
            CancellationToken.None);
        await db.UpsertTmdbMetadataAsync(
            new TmdbMetadataRow(42, "series", "Series", null, null, null, null, null, null, null, null, DateTimeOffset.UtcNow),
            CancellationToken.None);

        var m = await db.GetTmdbMetadataAsync(42, "movie", CancellationToken.None);
        var s = await db.GetTmdbMetadataAsync(42, "series", CancellationToken.None);

        Assert.Equal("Movie", m!.Title);
        Assert.Equal("Series", s!.Title);
    }

    [Fact]
    public async Task TmdbMetadata_UpsertOverwritesPrevious()
    {
        using var db = await NewDbAsync();
        await db.UpsertTmdbMetadataAsync(
            new TmdbMetadataRow(42, "movie", "Old", 1970, null, null, null, null, null, null, null, DateTimeOffset.UtcNow),
            CancellationToken.None);
        await db.UpsertTmdbMetadataAsync(
            new TmdbMetadataRow(42, "movie", "New", 1979, null, null, null, null, null, null, null, DateTimeOffset.UtcNow),
            CancellationToken.None);
        var got = await db.GetTmdbMetadataAsync(42, "movie", CancellationToken.None);
        Assert.Equal("New", got!.Title);
        Assert.Equal(1979, got.Year);
    }

    [Fact]
    public async Task TmdbMetadata_GetMissing_ReturnsNull()
    {
        using var db = await NewDbAsync();
        var got = await db.GetTmdbMetadataAsync(404, "movie", CancellationToken.None);
        Assert.Null(got);
    }

    // ----------------------------------------------------------------
    // tmdb_episode_cache
    // ----------------------------------------------------------------

    [Fact]
    public async Task TmdbEpisodeCache_UpsertGet_RoundtripsAllFields()
    {
        using var db = await NewDbAsync();
        var fetched = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var row = new TmdbEpisodeRow(
            SeriesTmdbId: 1399,
            Season: 1,
            Episode: 1,
            Title: "Winter Is Coming",
            Overview: "Pilot.",
            StillUrl: "https://image.tmdb.org/t/p/w500/still.jpg",
            AirDate: "2011-04-17",
            RuntimeMinutes: 62,
            FetchedAt: fetched);

        await db.UpsertTmdbEpisodeAsync(row, CancellationToken.None);
        var got = await db.GetTmdbEpisodeAsync(1399, 1, 1, CancellationToken.None);
        Assert.NotNull(got);
        Assert.Equal(1399, got!.SeriesTmdbId);
        Assert.Equal(1, got.Season);
        Assert.Equal(1, got.Episode);
        Assert.Equal("Winter Is Coming", got.Title);
        Assert.Equal("Pilot.", got.Overview);
        Assert.Equal("https://image.tmdb.org/t/p/w500/still.jpg", got.StillUrl);
        Assert.Equal("2011-04-17", got.AirDate);
        Assert.Equal(62, got.RuntimeMinutes);
        Assert.Equal(fetched, got.FetchedAt);
    }

    [Fact]
    public async Task TmdbEpisodeCache_UpsertWithNullables_RoundtripsAsNull()
    {
        using var db = await NewDbAsync();
        var row = new TmdbEpisodeRow(
            SeriesTmdbId: 1,
            Season: 1,
            Episode: 1,
            Title: "Bare",
            Overview: null,
            StillUrl: null,
            AirDate: null,
            RuntimeMinutes: null,
            FetchedAt: DateTimeOffset.UtcNow);
        await db.UpsertTmdbEpisodeAsync(row, CancellationToken.None);
        var got = await db.GetTmdbEpisodeAsync(1, 1, 1, CancellationToken.None);
        Assert.NotNull(got);
        Assert.Equal("Bare", got!.Title);
        Assert.Null(got.Overview);
        Assert.Null(got.StillUrl);
        Assert.Null(got.AirDate);
        Assert.Null(got.RuntimeMinutes);
    }

    [Fact]
    public async Task TmdbEpisodeCache_UpsertOverwritesPrevious()
    {
        using var db = await NewDbAsync();
        await db.UpsertTmdbEpisodeAsync(new TmdbEpisodeRow(1, 1, 1, "Old", null, null, null, null, DateTimeOffset.UtcNow), CancellationToken.None);
        await db.UpsertTmdbEpisodeAsync(new TmdbEpisodeRow(1, 1, 1, "New", "better", null, null, 45, DateTimeOffset.UtcNow), CancellationToken.None);
        var got = await db.GetTmdbEpisodeAsync(1, 1, 1, CancellationToken.None);
        Assert.Equal("New", got!.Title);
        Assert.Equal("better", got.Overview);
        Assert.Equal(45, got.RuntimeMinutes);
    }

    [Fact]
    public async Task ClaimDueAvailability_PreferredEpisodeSpreadsAcrossSeriesBeforeDeepeningOneSeries()
    {
        using var db = await NewDbAsync();
        await db.SetMetaAsync("availability.cursor.episode_series", "100", CancellationToken.None);
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await conn.OpenAsync();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO availability_items
                (tmdb_id,type,season,episode,status,checked_at,next_check_at)
                VALUES
                (100,'episode',1,1,'available',1000,900),
                (100,'episode',1,2,'unknown',NULL,900),
                (200,'episode',1,1,'unknown',NULL,950);";
            await cmd.ExecuteNonQueryAsync(CancellationToken.None);
        }

        var lease = await db.ClaimDueAvailabilityAsync(
            "test-owner",
            TimeSpan.FromMinutes(5),
            DateTimeOffset.FromUnixTimeSeconds(1000),
            "policy",
            CancellationToken.None,
            preferredType: "episode");

        Assert.NotNull(lease);
        Assert.Equal(200, lease!.TmdbId);
        Assert.Equal(1, lease.Season);
        Assert.Equal(1, lease.Episode);
    }

    [Fact]
    public async Task TmdbEpisodeCache_GetMissing_ReturnsNull()
    {
        using var db = await NewDbAsync();
        var got = await db.GetTmdbEpisodeAsync(1, 1, 1, CancellationToken.None);
        Assert.Null(got);
    }

    [Fact]
    public async Task TmdbEpisodeCache_ListForSeason_OrdersByEpisodeAscending()
    {
        using var db = await NewDbAsync();
        // Insert out of order.
        await db.UpsertTmdbEpisodeAsync(new TmdbEpisodeRow(1399, 1, 3, "E3", null, null, null, null, DateTimeOffset.UtcNow), CancellationToken.None);
        await db.UpsertTmdbEpisodeAsync(new TmdbEpisodeRow(1399, 1, 1, "E1", null, null, null, null, DateTimeOffset.UtcNow), CancellationToken.None);
        await db.UpsertTmdbEpisodeAsync(new TmdbEpisodeRow(1399, 1, 2, "E2", null, null, null, null, DateTimeOffset.UtcNow), CancellationToken.None);
        // Different season — must not appear.
        await db.UpsertTmdbEpisodeAsync(new TmdbEpisodeRow(1399, 2, 1, "S2E1", null, null, null, null, DateTimeOffset.UtcNow), CancellationToken.None);

        var got = await db.ListEpisodesForSeasonAsync(1399, 1, CancellationToken.None);
        Assert.Equal(3, got.Count);
        Assert.Equal(1, got[0].Episode);
        Assert.Equal(2, got[1].Episode);
        Assert.Equal(3, got[2].Episode);
        Assert.All(got, r => Assert.Equal(1, r.Season));
    }

    [Fact]
    public async Task TmdbEpisodeCache_ListForMissingSeason_ReturnsEmpty()
    {
        using var db = await NewDbAsync();
        var got = await db.ListEpisodesForSeasonAsync(999, 1, CancellationToken.None);
        Assert.Empty(got);
    }
}
