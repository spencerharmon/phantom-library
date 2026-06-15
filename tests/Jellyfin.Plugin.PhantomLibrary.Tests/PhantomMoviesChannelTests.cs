using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Channels;
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
        _channel = new PhantomMoviesChannel(
            _db, _enumerator, _splash, _state,
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

    private async Task SeedMetaAsync(int tmdb, string title)
    {
        await _db.UpsertTmdbMetadataAsync(
            new TmdbMetadataRow(tmdb, "movie", title, 2020, "Overview " + tmdb,
                "https://image.tmdb.org/t/p/w500/p.jpg",
                "https://image.tmdb.org/t/p/w500/b.jpg",
                new[] { "Drama" }, null, 7.5, title, DateTimeOffset.UtcNow),
            CancellationToken.None);
    }

    [Fact]
    public async Task GetChannelItems_AllEmpty_ReturnsEmpty()
    {
        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalRecordCount);
    }

    [Fact]
    public async Task GetChannelItems_DiscoveryOnly_EmitsAsPhantomWithSplash()
    {
        await SeedMetaAsync(101, "Discovery Movie");
        await _db.UpsertDiscoveryCacheAsync(101, "movie", CancellationToken.None);

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        Assert.Single(result.Items);
        var item = result.Items[0];
        Assert.Equal("movie_101", item.Id);
        Assert.Equal("Discovery Movie", item.Name);
        Assert.Contains("phantom", item.Tags);
        var src = Assert.Single(item.MediaSources);
        Assert.Equal(_splash.ResolveSplashPath(), src.Path);
    }

    [Fact]
    public async Task GetChannelItems_MaterialisedOnly_EmitsAsRealWithFusePath()
    {
        await SeedMetaAsync(202, "Materialised Movie");
        await _db.InsertMaterialisedStateAsync(202, "movie", -1, -1, "/stub/x.mkv", "/fuse/x.mkv", CancellationToken.None);

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        Assert.Single(result.Items);
        var item = result.Items[0];
        Assert.Equal("movie_202", item.Id);
        Assert.DoesNotContain("phantom", item.Tags);
        Assert.Equal("/fuse/x.mkv", item.MediaSources[0].Path);
        Assert.Equal("202", item.ProviderIds["Tmdb"]);
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
        await _db.InsertMaterialisedStateAsync(42, "movie", -1, -1, "/stub", "/fuse/42.mkv", CancellationToken.None);

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("movie_42", item.Id);
        Assert.Equal("/fuse/42.mkv", item.MediaSources[0].Path);
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
        await _db.UpsertDiscoveryCacheAsync(99, "movie", CancellationToken.None);

        var before = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
        var beforeId = before.Items.Single().Id;

        await _db.InsertMaterialisedStateAsync(99, "movie", -1, -1, "/stub", "/fuse/99.mkv", CancellationToken.None);

        var after = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
        var afterId = after.Items.Single().Id;

        Assert.Equal("movie_99", beforeId);
        Assert.Equal("movie_99", afterId);
    }

    [Fact]
    public async Task GetChannelItems_OrphanFile_EmitsAsOrphanWithRawFilename()
    {
        var orphanPath = Path.Combine(_moviesRoot, "Some Unknown Movie.mkv");
        File.WriteAllText(orphanPath, string.Empty);

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.StartsWith("orphan_", item.Id, StringComparison.Ordinal);
        Assert.Equal("Some Unknown Movie", item.Name);
        Assert.Contains("orphan", item.Tags);
        Assert.Equal(orphanPath, item.MediaSources[0].Path);
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
        await _db.UpsertDiscoveryCacheAsync(777, "movie", CancellationToken.None);

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetChannelItemMediaInfo_Materialised_ReturnsFusePath()
    {
        await SeedMetaAsync(50, "Fifty");
        await _db.InsertMaterialisedStateAsync(50, "movie", -1, -1, "/stub", "/fuse/50.mkv", CancellationToken.None);

        var got = await _channel.GetChannelItemMediaInfo("movie_50", CancellationToken.None);
        var src = Assert.Single(got);
        Assert.Equal("/fuse/50.mkv", src.Path);
    }

    [Fact]
    public async Task GetChannelItemMediaInfo_Phantom_ReturnsSplash()
    {
        var got = await _channel.GetChannelItemMediaInfo("movie_60", CancellationToken.None);
        var src = Assert.Single(got);
        Assert.Equal(_splash.ResolveSplashPath(), src.Path);
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
        await _db.InsertMaterialisedStateAsync(70, "movie", -1, -1, "/s", "/f/70.mkv", CancellationToken.None);

        var item = await _channel.GetChannelItemAsync("movie_70", CancellationToken.None);
        Assert.NotNull(item);
        Assert.Equal("movie_70", item.Id);
        Assert.DoesNotContain("phantom", item.Tags);
    }

    [Fact]
    public async Task GetLatestMedia_ReturnsMaterialisedSortedByMaterialisedAtDesc()
    {
        await SeedMetaAsync(1, "First");
        await _db.InsertMaterialisedStateAsync(1, "movie", -1, -1, "/s1", "/f1", CancellationToken.None);
        await Task.Delay(1100); // unix-seconds resolution
        await SeedMetaAsync(2, "Second");
        await _db.InsertMaterialisedStateAsync(2, "movie", -1, -1, "/s2", "/f2", CancellationToken.None);

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
