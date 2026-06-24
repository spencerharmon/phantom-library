using System;
using System.Diagnostics;
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
    public async Task FreshDb_CreatesSchemaV14_WithAllExpectedTables()
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

        Assert.Equal(14, version);

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
            "source_candidates",
            "bulk_materialise_requests",
            "bulk_materialise_items",
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
    public async Task FreshDb_SourceCandidates_HaveValidationColumnsAndNoAudioColumns()
    {
        using var db = await NewDbAsync();
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await conn.OpenAsync();

        async Task<bool> HasColumn(string name)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM pragma_table_info('source_candidates') WHERE name=$name;";
            cmd.Parameters.AddWithValue("$name", name);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync()) == 1;
        }

        Assert.True(await HasColumn("validation_status"));
        Assert.True(await HasColumn("validation_reason"));
        Assert.True(await HasColumn("validation_duration_ms"));
        Assert.True(await HasColumn("validation_policy_version"));
        Assert.True(await HasColumn("selected_file_path"));
        Assert.False(await HasColumn("selected_audio_index"));
        Assert.False(await HasColumn("selected_audio_language"));
        Assert.False(await HasColumn("audio_tracks_json"));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(12)]
    public async Task HardRefuse_PreV13SchemaVersion_ThrowsWithWipePointer(int oldVersion)
    {
        await CreateDbWithUserVersionAsync(oldVersion);

        using var db = new PhantomDb(_dbPath);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.SetMetaAsync("test", "1", CancellationToken.None));

        Assert.Contains("version 14", ex.Message, StringComparison.Ordinal);
        Assert.Contains("phantom-wipe.sh", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HardRefuse_V13SchemaVersion_ThrowsWithMigrationPointer()
    {
        await CreateDbWithUserVersionAsync(13);

        using var db = new PhantomDb(_dbPath);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.SetMetaAsync("test", "1", CancellationToken.None));

        Assert.Contains("version 14", ex.Message, StringComparison.Ordinal);
        Assert.Contains("migrate-source-validation-v14.sh", ex.Message, StringComparison.Ordinal);
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
    public async Task SourceCandidates_ValidationColumns_Roundtrip()
    {
        using var db = await NewDbAsync();
        await db.UpsertSourceCandidatesAsync(
            42,
            "episode",
            2,
            1,
            "preset",
            new[] { new Jellyfin.Plugin.PhantomLibrary.Sources.MagnetCandidate("magnet:?xt=urn:btih:abc", "abc", 1234, 10, "idx") { Title = "Candidate" } },
            "test",
            TimeSpan.FromHours(1),
            CancellationToken.None);

        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await db.UpdateSourceCandidateValidationAsync(new SourceCandidateValidationUpdate(
            42,
            "episode",
            2,
            1,
            "preset",
            "magnet:?xt=urn:btih:abc",
            "valid",
            null,
            now,
            now.AddHours(12),
            3456,
            "sv14-parser-audio-v1",
            7,
            "Season 02/E01.mkv",
            1234), CancellationToken.None);

        var rows = await db.ListSourceCandidatesAsync(42, "episode", 2, 1, "preset", includeExpired: true, CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Equal("valid", row.ValidationStatus);
        Assert.Null(row.ValidationReason);
        Assert.Equal(now, row.ValidatedAt);
        Assert.Equal(now.AddHours(12), row.ValidationExpiresAt);
        Assert.Equal(3456, row.ValidationDurationMs);
        Assert.Equal("sv14-parser-audio-v1", row.ValidationPolicyVersion);
        Assert.Equal(7, row.SelectedFileId);
        Assert.Equal("Season 02/E01.mkv", row.SelectedFilePath);
        Assert.Equal(1234, row.SelectedFileSize);
    }

    [Fact]
    public async Task SourceCandidates_ClearValidation_ResetsSv14StateForItemPreset()
    {
        using var db = await NewDbAsync();
        await db.UpsertSourceCandidatesAsync(
            42,
            "episode",
            2,
            1,
            "preset",
            new[]
            {
                new Jellyfin.Plugin.PhantomLibrary.Sources.MagnetCandidate("magnet:?xt=urn:btih:abc", "abc", 1234, 10, "idx") { Title = "Candidate A" },
                new Jellyfin.Plugin.PhantomLibrary.Sources.MagnetCandidate("magnet:?xt=urn:btih:def", "def", 4567, 5, "idx") { Title = "Candidate B" },
            },
            "test",
            TimeSpan.FromHours(1),
            CancellationToken.None);

        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        foreach (var magnet in new[] { "magnet:?xt=urn:btih:abc", "magnet:?xt=urn:btih:def" })
        {
            await db.UpdateSourceCandidateValidationAsync(new SourceCandidateValidationUpdate(
                42,
                "episode",
                2,
                1,
                "preset",
                magnet,
                "invalid",
                "no_english_audio",
                now,
                now.AddHours(1),
                100,
                "sv14-parser-audio-v1",
                12,
                "S02E01.mkv",
                1234), CancellationToken.None);
        }

        Assert.Equal(2, await db.ClearSourceCandidateValidationAsync(42, "episode", 2, 1, "preset", CancellationToken.None));

        var rows = await db.ListSourceCandidatesAsync(42, "episode", 2, 1, "preset", includeExpired: true, CancellationToken.None);
        Assert.All(rows, row =>
        {
            Assert.Equal("unknown", row.ValidationStatus);
            Assert.Null(row.ValidationReason);
            Assert.Null(row.ValidatedAt);
            Assert.Null(row.ValidationExpiresAt);
            Assert.Null(row.ValidationDurationMs);
            Assert.Equal("unknown", row.ValidationPolicyVersion);
            Assert.Null(row.SelectedFileId);
            Assert.Null(row.SelectedFilePath);
            Assert.Null(row.SelectedFileSize);
        });
    }

    [Fact]
    public async Task MagnetFailure_PolicySensitiveLegacyFailureIgnored_CurrentPolicyReturned_OperatorRejectionPersists()
    {
        using var db = await NewDbAsync();
        var now = DateTimeOffset.UtcNow;
        var key = new MagnetFailureKey(42, "tt0000042", "episode", 2, 1, "preset", "magnet:?xt=urn:btih:bad");
        await db.MarkMagnetFailedAsync(key, new MagnetFailureEntry
        {
            InfoHash = "bad",
            Reason = "target_episode_not_found",
            FailedAt = now,
            RetryAfter = now.AddHours(1),
            ValidationPolicyVersion = "legacy",
        }, CancellationToken.None);

        Assert.Null(await db.GetMagnetFailureAsync(key, "sv14-parser-audio-v1", CancellationToken.None));

        await db.MarkMagnetFailedAsync(key, new MagnetFailureEntry
        {
            InfoHash = "bad",
            Reason = "target_episode_not_found",
            FailedAt = now,
            RetryAfter = now.AddHours(1),
            ValidationPolicyVersion = "sv14-parser-audio-v1",
        }, CancellationToken.None);
        Assert.NotNull(await db.GetMagnetFailureAsync(key, "sv14-parser-audio-v1", CancellationToken.None));

        var op = key with { Magnet = "magnet:?xt=urn:btih:operator" };
        await db.MarkMagnetFailedAsync(op, new MagnetFailureEntry
        {
            InfoHash = "operator",
            Reason = "operator_rejected",
            FailedAt = now,
            RetryAfter = now.AddHours(1),
            ValidationPolicyVersion = "legacy",
        }, CancellationToken.None);
        Assert.NotNull(await db.GetMagnetFailureAsync(op, "sv14-parser-audio-v1", CancellationToken.None));
    }

    [Fact]
    public void BulkMaterialiseRequestId_IsDeterministicSha256LowerHex()
    {
        var a = PhantomDb.ComputeBulkMaterialiseRequestId("user1", "season_42_s02");
        var b = PhantomDb.ComputeBulkMaterialiseRequestId("user1", "season_42_s02");
        var c = PhantomDb.ComputeBulkMaterialiseRequestId("user1", "series_42");

        Assert.Equal(64, a.Length);
        Assert.Equal(a.ToLowerInvariant(), a);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public async Task BulkMaterialiseQueue_CRUD_Claim_Complete_StaleReset()
    {
        using var db = await NewDbAsync();
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var request = new BulkMaterialiseRequestRow(
            "req1", "user1", "season_42_s02", "season", 42, 2, "favourite", "pending", now, now, null, null, 0);
        await db.UpsertBulkMaterialiseRequestAsync(request, CancellationToken.None);
        Assert.Equal("season_42_s02", (await db.GetBulkMaterialiseRequestAsync("req1", CancellationToken.None))!.ParentExternalId);

        await db.UpsertBulkMaterialiseItemAsync(new BulkMaterialiseItemRow(
            "req1", 42, "episode", 2, 1, "pending", 0, null, 0, now, now, null), CancellationToken.None);
        await db.UpsertBulkMaterialiseItemAsync(new BulkMaterialiseItemRow(
            "req1", 42, "episode", 2, 2, "pending", 0, null, 0, now.AddHours(1), now, null), CancellationToken.None);

        var due = await db.PeekDueBulkMaterialiseItemsAsync(now, 10, CancellationToken.None);
        var onlyDue = Assert.Single(due);
        Assert.Equal(1, onlyDue.Episode);

        Assert.True(await db.TryClaimBulkMaterialiseItemAsync("req1", 42, "episode", 2, 1, 0, "claim-a", now, CancellationToken.None));
        Assert.False(await db.TryClaimBulkMaterialiseItemAsync("req1", 42, "episode", 2, 1, 0, "claim-b", now, CancellationToken.None));
        var running = Assert.Single(await db.ListBulkMaterialiseItemsAsync("req1", CancellationToken.None), i => i.Episode == 1);
        Assert.Equal("running", running.Status);
        Assert.Equal("claim-a", running.ClaimToken);
        Assert.Equal(1, running.Attempts);

        Assert.False(await db.CompleteBulkMaterialiseItemAsync("req1", 42, "episode", 2, 1, 0, "wrong", "done", now, null, now, CancellationToken.None));
        Assert.True(await db.CompleteBulkMaterialiseItemAsync("req1", 42, "episode", 2, 1, 0, "claim-a", "done", now, null, now, CancellationToken.None));

        await db.UpsertBulkMaterialiseItemAsync(new BulkMaterialiseItemRow(
            "req1", 42, "episode", 2, 3, "running", 0, "stale", 1, now, now.AddHours(-2), "old"), CancellationToken.None);
        var reset = await db.ResetStaleBulkMaterialiseItemsAsync(TimeSpan.FromMinutes(30), now, CancellationToken.None);
        Assert.Equal(1, reset);
        var stale = Assert.Single(await db.ListBulkMaterialiseItemsAsync("req1", CancellationToken.None), i => i.Episode == 3);
        Assert.Equal("retry", stale.Status);
        Assert.Null(stale.ClaimToken);
        Assert.Equal("stale_running_reset", stale.LastError);
    }

    [Fact]
    public async Task MigrationScript_V13ToV14_PreservesCandidates_DeletesSensitiveEpisodeFailures_Idempotent()
    {
        await CreateMinimalV13DbAsync();
        var script = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../scripts/migrate-source-validation-v14.sh"));

        var first = await RunProcessAsync(script, _dbPath);
        Assert.Equal(0, first.ExitCode);
        var second = await RunProcessAsync(script, _dbPath);
        Assert.Equal(0, second.ExitCode);

        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await conn.OpenAsync();
        Assert.Equal(14, Convert.ToInt32(await ScalarAsync(conn, "PRAGMA user_version;")));
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(conn, "SELECT COUNT(*) FROM source_candidates;")));
        Assert.Equal(0, Convert.ToInt32(await ScalarAsync(conn, "SELECT COUNT(*) FROM magnet_failure_cache WHERE reason='target_episode_not_found';")));
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(conn, "SELECT COUNT(*) FROM magnet_failure_cache WHERE reason='operator_rejected' AND validation_policy_version='legacy';")));
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='bulk_materialise_requests';")));
        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(conn, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='bulk_materialise_items';")));
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
            FetchedAt: fetched,
            RuntimeMinutes: 136);

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
        Assert.Equal(136, got.RuntimeMinutes);
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
        Assert.Null(got.RuntimeMinutes);
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
    private async Task CreateDbWithUserVersionAsync(int version)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString();
        await using (var conn = new SqliteConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"PRAGMA user_version = {version};";
            await cmd.ExecuteNonQueryAsync();
        }

        SqliteConnection.ClearAllPools();
    }

    private async Task CreateMinimalV13DbAsync()
    {
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE source_candidates (
    tmdb_id INTEGER NOT NULL, type TEXT NOT NULL, season INTEGER NOT NULL DEFAULT -1, episode INTEGER NOT NULL DEFAULT -1,
    preset TEXT NOT NULL DEFAULT '', magnet TEXT NOT NULL, info_hash TEXT NOT NULL, indexer TEXT NOT NULL, title TEXT NOT NULL,
    seeders INTEGER, size INTEGER, rank INTEGER NOT NULL, source TEXT NOT NULL, fetched_at INTEGER NOT NULL, expires_at INTEGER NOT NULL,
    PRIMARY KEY (tmdb_id,type,season,episode,preset,magnet)
);
CREATE TABLE magnet_failure_cache (
    tmdb_id INTEGER NOT NULL DEFAULT 0, imdb_id TEXT NOT NULL DEFAULT '', type TEXT NOT NULL, season INTEGER NOT NULL DEFAULT 0,
    episode INTEGER NOT NULL DEFAULT 0, preset TEXT NOT NULL DEFAULT '', magnet TEXT NOT NULL, info_hash TEXT NOT NULL, reason TEXT NOT NULL,
    failed_at INTEGER NOT NULL, retry_after INTEGER NOT NULL, PRIMARY KEY (tmdb_id,imdb_id,type,season,episode,preset,magnet)
);
INSERT INTO source_candidates VALUES (42,'episode',2,1,'preset','magnet:?xt=urn:btih:abc','abc','idx','title',10,1234,1,'test',100,200);
INSERT INTO magnet_failure_cache VALUES (42,'tt42','episode',2,1,'preset','magnet:?xt=urn:btih:bad','bad','target_episode_not_found',100,9999999999);
INSERT INTO magnet_failure_cache VALUES (42,'tt42','episode',2,1,'preset','magnet:?xt=urn:btih:op','op','operator_rejected',100,9999999999);
PRAGMA user_version=13;";
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunProcessAsync(string fileName, string argument)
    {
        var psi = new ProcessStartInfo(fileName, argument)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("failed to start migration script");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, stdout, stderr);
    }
}
