using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Dto;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class PhantomMoviesChannelTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _moviesRoot;
    private readonly string _showsRoot;
    private readonly string _splashHome;
    private readonly PhantomDb _db;
    private readonly ChannelStateProvider _state;
    private readonly GostreamFilesystemEnumerator _enumerator;
    private readonly SplashSourceProvider _splash;
    private readonly Mock<ITmdbClient> _tmdb;
    private readonly PhantomMoviesChannel _channel;

    public PhantomMoviesChannelTests()
    {
        var stamp = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), "phantom-mc-tests-" + stamp + ".db");
        _moviesRoot = Path.Combine(Path.GetTempPath(), "phantom-mc-movies-" + stamp);
        _showsRoot = Path.Combine(Path.GetTempPath(), "phantom-mc-shows-" + stamp);
        _splashHome = Path.Combine(Path.GetTempPath(), "phantom-mc-splash-" + stamp);
        Directory.CreateDirectory(_moviesRoot);
        Directory.CreateDirectory(_showsRoot);
        Directory.CreateDirectory(_splashHome);

        _db = new PhantomDb(_dbPath);
        _state = new ChannelStateProvider(_db);
        _enumerator = new GostreamFilesystemEnumerator(_db, NullLogger<GostreamFilesystemEnumerator>.Instance)
        {
            MoviesRootOverride = _moviesRoot,
            ShowsRootOverride = _showsRoot,
        };
        _splash = new SplashSourceProvider(MockPaths(_splashHome));
        _tmdb = new Mock<ITmdbClient>(MockBehavior.Loose);
        _channel = new PhantomMoviesChannel(
            _db, _enumerator, _splash, _state, _tmdb.Object,
            NullLogger<PhantomMoviesChannel>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_moviesRoot)) Directory.Delete(_moviesRoot, true); } catch { }
        try { if (Directory.Exists(_showsRoot)) Directory.Delete(_showsRoot, true); } catch { }
        try { if (Directory.Exists(_splashHome)) Directory.Delete(_splashHome, true); } catch { }
    }

    private static void AssertOpeningSource(MediaSourceInfo src, string externalId)
    {
        Assert.Equal(string.Empty, src.Path);
        Assert.True(src.RequiresOpening);
        Assert.StartsWith(PhantomMaterialisingMediaSourceProvider.ProviderPrefix + "phantom:" + externalId, src.OpenToken, StringComparison.Ordinal);
        Assert.True(Guid.TryParse(src.Id, out _));
    }

    private async Task SeedMetaAsync(int tmdb, string title)
    {
        await _db.UpsertTmdbMetadataAsync(
            new TmdbMetadataRow(tmdb, "movie", title, 2020, "Overview " + tmdb,
                "https://image.tmdb.org/t/p/w500/p.jpg",
                "https://image.tmdb.org/t/p/w500/b.jpg",
                new[] { "Drama" }, null, 7.5, title, DateTimeOffset.UtcNow),
            CancellationToken.None);
    }

    private async Task SeedAvailableMovieAsync(int tmdb)
    {
        await _db.SetMetaAsync("__test_schema__", "1", CancellationToken.None);
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO availability_items
            (tmdb_id,type,season,episode,status,checked_at,next_check_at)
            VALUES ($tmdb,'movie',-1,-1,'available',$now,$next);";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        cmd.Parameters.AddWithValue("$tmdb", tmdb);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$next", DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    [Fact]
    public async Task GetChannelItems_AllEmpty_ReturnsEmpty()
    {
        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalRecordCount);
    }

    [Fact]
    public async Task GetChannelItems_DiscoveryOnly_EmitsAsPhantomWithOpeningSource()
    {
        await SeedMetaAsync(101, "Discovery Movie");
        await SeedAvailableMovieAsync(101);

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        Assert.Single(result.Items);
        var item = result.Items[0];
        Assert.Equal("movie_101", item.Id);
        Assert.Equal("Discovery Movie", item.Name);
        Assert.Contains("phantom", item.Tags);
        var src = Assert.Single(item.MediaSources);
        AssertOpeningSource(src, "movie_101");
    }

    [Fact]
    public async Task GetChannelItems_MaterialisedOnly_EmitsAsRealWithFusePath()
    {
        await SeedMetaAsync(202, "Materialised Movie");
        var fusePath = Path.Combine(_moviesRoot, "x.mkv");
        File.WriteAllText(fusePath, string.Empty);
        await _db.InsertMaterialisedStateAsync(202, "movie", -1, -1, "/stub/x.mkv", fusePath, CancellationToken.None);

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        Assert.Single(result.Items);
        var item = result.Items[0];
        Assert.Equal("movie_202", item.Id);
        Assert.DoesNotContain("phantom", item.Tags);
        Assert.Equal(fusePath, item.MediaSources[0].Path);
        Assert.True(Guid.TryParse(item.MediaSources[0].Id, out _));
        Assert.Equal("202", item.ProviderIds["Tmdb"]);
    }

    [Fact]
    public async Task GetChannelItems_MaterialisedOnlyWithMissingFile_EmitsOpeningSource()
    {
        await SeedMetaAsync(202, "Materialised Movie");
        await _db.InsertMaterialisedStateAsync(202, "movie", -1, -1, "/stub/x.mkv", Path.Combine(_moviesRoot, "missing-x.mkv"), CancellationToken.None);

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("movie_202", item.Id);
        Assert.Contains("phantom", item.Tags);
        AssertOpeningSource(item.MediaSources[0], "movie_202");
    }

    [Fact]
    public async Task GetChannelItems_MaterialisedAndDiscoveryForSameTmdb_EmitsOnce_MaterialisedWins()
    {
        // Critical regression test for plan §3.3 + critic round 3 BLOCKER 1:
        // when a tmdb appears in BOTH materialised_state and discovery_cache,
        // the channel must emit a single item (materialised wins) and the
        // id must remain "movie_<tmdb>" (no phantom_/real_ prefix).
        await SeedMetaAsync(42, "Forty Two");
        await _db.UpsertDiscoveryCacheAsync(42, "movie", CancellationToken.None);
        var fusePath = Path.Combine(_moviesRoot, "42.mkv");
        File.WriteAllText(fusePath, string.Empty);
        await _db.InsertMaterialisedStateAsync(42, "movie", -1, -1, "/stub", fusePath, CancellationToken.None);

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("movie_42", item.Id);
        Assert.Equal(fusePath, item.MediaSources[0].Path);
        Assert.DoesNotContain("phantom", item.Tags);
    }

    [Fact]
    public async Task GetChannelItems_IdStableAcrossPhantomToMaterialiseTransition()
    {
        // Plan §3.3 + critic round 3 BLOCKER 1: emit tmdb=99 as phantom,
        // then materialise it, then re-query. The id stays "movie_99"
        // both times so Jellyfin's BaseItem hash and any UserData
        // (favourites, watched) is preserved across the transition.
        await SeedMetaAsync(99, "Stable Id Movie");
        await SeedAvailableMovieAsync(99);

        var before = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
        var beforeId = before.Items.Single().Id;

        var fusePath = Path.Combine(_moviesRoot, "99.mkv");
        File.WriteAllText(fusePath, string.Empty);
        await _db.InsertMaterialisedStateAsync(99, "movie", -1, -1, "/stub", fusePath, CancellationToken.None);

        var after = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
        var afterId = after.Items.Single().Id;

        Assert.Equal("movie_99", beforeId);
        Assert.Equal("movie_99", afterId);
    }

    [Fact]
    public async Task GetChannelItems_GostreamFileWithTmdbSearchHit_EmitsAsMaterialisedMovieWithMetadata()
    {
        var path = Path.Combine(_moviesRoot, "Some_Movie_2026_1080p_abcd1234.mkv");
        File.WriteAllText(path, string.Empty);
        _tmdb.Setup(t => t.SearchMoviesAsync("Some Movie", 2026, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new TmdbSearchHit(4242, "Some Movie", "Some Movie", "hit", null, null, "2026-01-01", 8.1, 10),
            });
        _tmdb.Setup(t => t.GetMovieAsync(4242, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TmdbMovieDetails(4242, "Some Movie", "Some Movie", "overview", "/poster.jpg", null,
                "2026-01-01", 8.1, 10, 100, new[] { "Drama" }, "Released", null, "tt4242", null, null));

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("movie_4242", item.Id);
        Assert.Equal("Some Movie", item.Name);
        Assert.Contains("external", item.Tags);
        Assert.DoesNotContain("phantom", item.Tags);
        Assert.Equal("4242", item.ProviderIds["Tmdb"]);
        Assert.Equal(path, item.MediaSources[0].Path);
        Assert.True(Guid.TryParse(item.MediaSources[0].Id, out _));
        Assert.Equal(2026, item.ProductionYear);
    }

    [Fact]
    public async Task GetChannelItems_GostreamVariantsWithSameTmdb_EmitOneMovieWithBestSource_NoOrphans()
    {
        var dv = Path.Combine(_moviesRoot, "Apex_2026_2160p_DV_Atmos_7cf0a865.mkv");
        var hdr = Path.Combine(_moviesRoot, "Apex_2026_2160p_HDR_26bcf71f.mkv");
        File.WriteAllText(dv, string.Empty);
        File.WriteAllText(hdr, string.Empty);
        _tmdb.Setup(t => t.SearchMoviesAsync("Apex", 2026, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new TmdbSearchHit(1318447, "Apex", "Apex", "hit", null, null, "2026-01-01", 8.1, 10),
            });
        _tmdb.Setup(t => t.GetMovieAsync(1318447, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TmdbMovieDetails(1318447, "Apex", "Apex", "overview", "/poster.jpg", null,
                "2026-01-01", 8.1, 10, 100, new[] { "Action" }, "Released", null, "tt1318447", null, null));

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("movie_1318447", item.Id);
        Assert.Equal("Apex", item.Name);
        Assert.Contains("external", item.Tags);
        var source = Assert.Single(item.MediaSources);
        Assert.Equal(dv, source.Path);
    }

    [Fact]
    public async Task GetChannelItems_MaterialisedAndGostreamVariantsForSameTmdb_EmitsOneMovie_MaterialisedSourceWins()
    {
        await SeedMetaAsync(1318447, "Apex");
        var materialised = Path.Combine(_moviesRoot, "Apex_2026_2160p_DV_Atmos_7cf0a865.mkv");
        var hdr = Path.Combine(_moviesRoot, "Apex_2026_2160p_HDR_26bcf71f.mkv");
        File.WriteAllText(materialised, string.Empty);
        File.WriteAllText(hdr, string.Empty);
        await _db.InsertMaterialisedStateAsync(1318447, "movie", -1, -1, "/stub", materialised, CancellationToken.None);
        _tmdb.Setup(t => t.SearchMoviesAsync("Apex", 2026, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new TmdbSearchHit(1318447, "Apex", "Apex", "hit", null, null, "2026-01-01", 8.1, 10),
            });

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("movie_1318447", item.Id);
        var source = Assert.Single(item.MediaSources);
        Assert.Equal(materialised, source.Path);
        Assert.DoesNotContain("external", item.Tags);
        Assert.DoesNotContain("phantom", item.Tags);
    }

    [Fact]
    public async Task GetChannelItems_GostreamFileWithSameTmdbAsDiscovery_GostreamRealSourceWins()
    {
        await SeedMetaAsync(4243, "Discovery Copy");
        await _db.UpsertDiscoveryCacheAsync(4243, "movie", CancellationToken.None);
        var path = Path.Combine(_moviesRoot, "Discovery_Copy_2020_1080p_abcd1234.mkv");
        File.WriteAllText(path, string.Empty);
        _tmdb.Setup(t => t.SearchMoviesAsync("Discovery Copy", 2020, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new TmdbSearchHit(4243, "Discovery Copy", "Discovery Copy", "hit", null, null, "2020-01-01", 8.1, 10),
            });

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items, i => i.Id == "movie_4243");
        Assert.Equal(path, item.MediaSources[0].Path);
        Assert.True(Guid.TryParse(item.MediaSources[0].Id, out _));
        Assert.DoesNotContain("phantom", item.Tags);
        Assert.Contains("external", item.Tags);
    }

    [Fact]
    public async Task GetChannelItems_UnresolvableGostreamFile_FallsBackToOrphanWithRawFilename()
    {
        var orphanPath = Path.Combine(_moviesRoot, "Some Unknown Movie.mkv");
        File.WriteAllText(orphanPath, string.Empty);
        _tmdb.Setup(t => t.SearchMoviesAsync("Some Unknown Movie", null, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TmdbSearchHit>());

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.StartsWith("orphan_", item.Id, StringComparison.Ordinal);
        Assert.Equal("Some Unknown Movie", item.Name);
        Assert.Contains("external", item.Tags);
        Assert.Equal(orphanPath, item.MediaSources[0].Path);
        Assert.True(Guid.TryParse(item.MediaSources[0].Id, out _));
    }

    [Fact]
    public async Task GetChannelItems_FolderIdSet_ReturnsEmpty()
    {
        await SeedMetaAsync(1, "A");
        await _db.UpsertDiscoveryCacheAsync(1, "movie", CancellationToken.None);

        var q = new InternalChannelItemQuery { FolderId = "some-folder-id" };
        var result = await _channel.GetChannelItems(q, CancellationToken.None);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetChannelItems_DiscoveryWithNoMetadata_Skipped()
    {
        // Cold-cache miss: discovery row exists but tmdb_metadata doesn't yet.
        // Channel should skip the item silently (next DiscoveryRefreshTask
        // warms it).
        await SeedAvailableMovieAsync(777);

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        Assert.Empty(result.Items);
    }

    // ----------------------------------------------------------------
    // Per-user visibility (REQ-M14-PER-USER Surface 3)
    // ----------------------------------------------------------------

    [Fact]
    public async Task GetChannelItems_HiddenForUser_OmittedForHider_ButVisibleForOtherUserAndAnonymous()
    {
        await SeedMetaAsync(550, "Fight Club");
        await SeedAvailableMovieAsync(550);
        var hider = Guid.NewGuid();
        var other = Guid.NewGuid();
        await _db.AddHiddenItemAsync(hider, 550, "movie", CancellationToken.None);

        var hiderResult = await _channel.GetChannelItems(new InternalChannelItemQuery { UserId = hider }, CancellationToken.None);
        var otherResult = await _channel.GetChannelItems(new InternalChannelItemQuery { UserId = other }, CancellationToken.None);
        var anonymousResult = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        Assert.Empty(hiderResult.Items);
        Assert.Single(otherResult.Items);
        Assert.Equal("movie_550", otherResult.Items[0].Id);
        Assert.Single(anonymousResult.Items);
    }

    [Fact]
    public async Task GetChannelItems_HiddenForUser_ThenUnhidden_ReappearsForThatUser()
    {
        await SeedMetaAsync(550, "Fight Club");
        await SeedAvailableMovieAsync(550);
        var userId = Guid.NewGuid();
        await _db.AddHiddenItemAsync(userId, 550, "movie", CancellationToken.None);
        Assert.Empty((await _channel.GetChannelItems(new InternalChannelItemQuery { UserId = userId }, CancellationToken.None)).Items);

        await _db.RemoveHiddenItemAsync(userId, 550, "movie", CancellationToken.None);

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery { UserId = userId }, CancellationToken.None);
        Assert.Single(result.Items);
    }

    [Fact]
    public async Task GetChannelItems_HiddenForUser_AlsoOmitsRealGostreamVariant_NoLeak()
    {
        // Regression coverage for the arbitration-required cache/visibility fix:
        // a hidden movie must disappear even when a real on-disk gostream file
        // also resolves to the same tmdb id (step 2 of GetChannelItems, which
        // runs independently of the `visible` catalogue step 1 that the hidden
        // filter was originally applied to).
        await SeedMetaAsync(4242, "Some Movie");
        await SeedAvailableMovieAsync(4242);
        var path = Path.Combine(_moviesRoot, "Some_Movie_2026_1080p_abcd1234.mkv");
        File.WriteAllText(path, string.Empty);
        _tmdb.Setup(t => t.SearchMoviesAsync("Some Movie", 2026, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new TmdbSearchHit(4242, "Some Movie", "Some Movie", "hit", null, null, "2026-01-01", 8.1, 10),
            });
        var hider = Guid.NewGuid();
        await _db.AddHiddenItemAsync(hider, 4242, "movie", CancellationToken.None);

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery { UserId = hider }, CancellationToken.None);

        Assert.Empty(result.Items);
    }

    [Fact]
    public void GetCacheKey_EchoesUserId_ForPerUserCachePartitioning()
    {
        // REQ-M14-PER-USER Surface 3: Jellyfin's on-disk channel-item cache is
        // otherwise channel+folder+DataVersion keyed with no user component
        // (ChannelManager.GetChannelDataCachePath), so without this, one
        // user's hidden-filtered result could be served to another.
        Assert.Equal("abc-user-id", _channel.GetCacheKey("abc-user-id"));
        Assert.Null(_channel.GetCacheKey(null));
    }

    [Fact]
    public async Task GetChannelItemMediaInfo_Materialised_ReturnsFusePath()
    {
        await SeedMetaAsync(50, "Fifty");
        var fusePath = Path.Combine(_moviesRoot, "50.mkv");
        File.WriteAllText(fusePath, string.Empty);
        await _db.InsertMaterialisedStateAsync(50, "movie", -1, -1, "/stub", fusePath, CancellationToken.None);

        var got = await _channel.GetChannelItemMediaInfo("movie_50", CancellationToken.None);
        var src = Assert.Single(got);
        Assert.Equal(fusePath, src.Path);
        Assert.True(Guid.TryParse(src.Id, out _));
    }

    [Fact]
    public async Task GetChannelItemMediaInfo_MaterialisedWithMissingFile_ReturnsEmpty()
    {
        await SeedMetaAsync(50, "Fifty");
        await _db.InsertMaterialisedStateAsync(50, "movie", -1, -1, "/stub", Path.Combine(_moviesRoot, "missing-50.mkv"), CancellationToken.None);

        var got = await _channel.GetChannelItemMediaInfo("movie_50", CancellationToken.None);

        Assert.Empty(got);
    }

    [Fact]
    public async Task GetChannelItemMediaInfo_Phantom_ReturnsEmpty()
    {
        var got = await _channel.GetChannelItemMediaInfo("movie_60", CancellationToken.None);
        Assert.Empty(got);
    }

    [Fact]
    public async Task GetChannelItemMediaInfo_UnknownId_ReturnsEmpty()
    {
        var got = await _channel.GetChannelItemMediaInfo("garbage_id", CancellationToken.None);
        Assert.Empty(got);
    }

    [Fact]
    public async Task GetChannelItemAsync_Materialised_ReturnsItemNotNull()
    {
        await SeedMetaAsync(70, "Seventy");
        var fusePath = Path.Combine(_moviesRoot, "70.mkv");
        File.WriteAllText(fusePath, string.Empty);
        await _db.InsertMaterialisedStateAsync(70, "movie", -1, -1, "/s", fusePath, CancellationToken.None);

        var item = await _channel.GetChannelItemAsync("movie_70", CancellationToken.None);
        Assert.NotNull(item);
        Assert.Equal("movie_70", item.Id);
        Assert.DoesNotContain("phantom", item.Tags);
    }

    [Fact]
    public async Task GetChannelItemAsync_MaterialisedWithMissingFile_ReturnsOpeningSource()
    {
        await SeedMetaAsync(70, "Seventy");
        await _db.InsertMaterialisedStateAsync(70, "movie", -1, -1, "/s", Path.Combine(_moviesRoot, "missing-70.mkv"), CancellationToken.None);

        var item = await _channel.GetChannelItemAsync("movie_70", CancellationToken.None);

        Assert.NotNull(item);
        Assert.Equal("movie_70", item.Id);
        Assert.Contains("phantom", item.Tags);
        AssertOpeningSource(item.MediaSources[0], "movie_70");
    }

    [Fact]
    public async Task GetLatestMedia_ReturnsMaterialisedSortedByMaterialisedAtDesc()
    {
        await SeedMetaAsync(1, "First");
        var f1 = Path.Combine(_moviesRoot, "latest-1.mkv");
        File.WriteAllText(f1, string.Empty);
        await _db.InsertMaterialisedStateAsync(1, "movie", -1, -1, "/s1", f1, CancellationToken.None);
        await Task.Delay(1100); // unix-seconds resolution
        await SeedMetaAsync(2, "Second");
        var f2 = Path.Combine(_moviesRoot, "latest-2.mkv");
        File.WriteAllText(f2, string.Empty);
        await _db.InsertMaterialisedStateAsync(2, "movie", -1, -1, "/s2", f2, CancellationToken.None);

        var got = (await _channel.GetLatestMedia(new ChannelLatestMediaSearch(), CancellationToken.None)).ToList();

        Assert.Equal(2, got.Count);
        Assert.Equal("movie_2", got[0].Id); // most-recent first
        Assert.Equal("movie_1", got[1].Id);
    }

    private static IApplicationPaths MockPaths(string root)
    {
        var mock = new Mock<IApplicationPaths>();
        mock.SetupGet(p => p.PluginConfigurationsPath).Returns(root);
        mock.SetupGet(p => p.DataPath).Returns(root);
        mock.SetupGet(p => p.CachePath).Returns(root);
        mock.SetupGet(p => p.TempDirectory).Returns(root);
        mock.SetupGet(p => p.ProgramDataPath).Returns(root);
        return mock.Object;
    }
}
