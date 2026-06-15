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
using MediaBrowser.Model.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class PhantomShowsChannelTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _splashHome;
    private readonly PhantomDb _db;
    private readonly ChannelStateProvider _state;
    private readonly SplashSourceProvider _splash;
    private readonly Mock<ITmdbClient> _tmdb;
    private readonly PhantomShowsChannel _channel;

    public PhantomShowsChannelTests()
    {
        var stamp = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), "phantom-sc-tests-" + stamp + ".db");
        _splashHome = Path.Combine(Path.GetTempPath(), "phantom-sc-splash-" + stamp);
        Directory.CreateDirectory(_splashHome);

        _db = new PhantomDb(_dbPath);
        _state = new ChannelStateProvider(_db);
        _splash = new SplashSourceProvider(MockPaths(_splashHome));
        _tmdb = new Mock<ITmdbClient>(MockBehavior.Strict);
        _channel = new PhantomShowsChannel(
            _db, _tmdb.Object, _splash, _state,
            NullLogger<PhantomShowsChannel>.Instance,
            () => null);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_splashHome)) Directory.Delete(_splashHome, true); } catch { }
    }

    private async Task SeedSeriesMetaAsync(int tmdb, string title, int year = 2011, int? communityRating = null)
    {
        await _db.UpsertTmdbMetadataAsync(
            new TmdbMetadataRow(tmdb, "series", title, year, "Overview " + tmdb,
                "https://image.tmdb.org/t/p/w500/p.jpg",
                "https://image.tmdb.org/t/p/w500/b.jpg",
                new[] { "Drama" }, null,
                communityRating.HasValue ? (double)communityRating.Value : (double?)null,
                title, DateTimeOffset.UtcNow),
            CancellationToken.None);
    }

    private static TmdbSeriesDetails MakeSeriesDetails(int tmdb, int seasons)
    {
        return new TmdbSeriesDetails(
            Id: tmdb,
            Name: "Test Series",
            OriginalName: "Test Series",
            Overview: "Overview",
            PosterPath: "/p.jpg",
            BackdropPath: "/b.jpg",
            FirstAirDate: "2011-04-17",
            VoteAverage: 9.0,
            VoteCount: 1000,
            Genres: new[] { "Drama" },
            Status: "Returning Series",
            NumberOfSeasons: seasons,
            NumberOfEpisodes: seasons * 10,
            OriginCountry: new[] { "US" },
            ImdbId: "tt0000000");
    }

    private static TmdbSeasonDetails MakeSeasonDetails(int seriesTmdb, int season, int episodeCount)
    {
        var eps = Enumerable.Range(1, episodeCount).Select(n => new TmdbEpisodeSummary
        {
            Id = season * 1000 + n,
            EpisodeNumber = n,
            SeasonNumber = season,
            Name = $"Episode {n} of Season {season}",
            Overview = $"Synopsis {season}x{n}",
            AirDate = $"2011-04-{17 + (n - 1):D2}",
            StillPath = $"/still_s{season}e{n}.jpg",
            Runtime = 60,
            VoteAverage = 8.5,
        }).ToList();
        return new TmdbSeasonDetails
        {
            SeriesTmdbId = seriesTmdb,
            SeasonNumber = season,
            Episodes = eps,
        };
    }

    // ----------------------------------------------------------------
    // Top-level series listing
    // ----------------------------------------------------------------

    [Fact]
    public async Task GetChannelItems_AllEmpty_ReturnsEmpty()
    {
        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalRecordCount);
    }

    [Fact]
    public async Task GetChannelItems_DiscoverySeriesOnly_EmitsSeriesFolder()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        await _db.UpsertDiscoveryCacheAsync(1399, "series", CancellationToken.None);

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("series_1399", item.Id);
        Assert.Equal("Game of Thrones", item.Name);
        Assert.Equal(ChannelItemType.Folder, item.Type);
        Assert.Equal(ChannelFolderType.Series, item.FolderType);
        Assert.Equal("1399", item.ProviderIds["Tmdb"]);
    }

    [Fact]
    public async Task GetChannelItems_MaterialisedEpisodeOnly_EmitsSeriesFolder()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        // Discovery row absent; only a materialised episode for the series.
        await _db.InsertMaterialisedStateAsync(1399, "episode", 1, 1, "/stub", "/fuse/ep.mkv", CancellationToken.None);

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("series_1399", item.Id);
    }

    [Fact]
    public async Task GetChannelItems_DiscoveryAndMaterialisedSameSeries_DedupesToOneTile()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        await _db.UpsertDiscoveryCacheAsync(1399, "series", CancellationToken.None);
        await _db.InsertMaterialisedStateAsync(1399, "episode", 1, 1, "/stub", "/fuse/ep.mkv", CancellationToken.None);

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("series_1399", item.Id);
    }

    [Fact]
    public async Task GetChannelItems_DiscoveryWithoutMetadata_Skipped()
    {
        // Cold-cache miss: discovery row exists, tmdb_metadata absent.
        await _db.UpsertDiscoveryCacheAsync(777, "series", CancellationToken.None);

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetChannelItems_FolderIdMalformed_ReturnsEmpty()
    {
        var q = new InternalChannelItemQuery { FolderId = "garbage" };
        var result = await _channel.GetChannelItems(q, CancellationToken.None);
        Assert.Empty(result.Items);
    }

    // ----------------------------------------------------------------
    // Series → Seasons folder navigation
    // ----------------------------------------------------------------

    [Fact]
    public async Task GetChannelItems_SeriesFolder_EmitsNSeasons()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        _tmdb.Setup(t => t.GetSeriesAsync(1399, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSeriesDetails(1399, seasons: 3));

        var q = new InternalChannelItemQuery { FolderId = "series_1399" };
        var result = await _channel.GetChannelItems(q, CancellationToken.None);

        Assert.Equal(3, result.Items.Count);
        Assert.Equal("season_1399_s01", result.Items[0].Id);
        Assert.Equal("season_1399_s02", result.Items[1].Id);
        Assert.Equal("season_1399_s03", result.Items[2].Id);
        Assert.All(result.Items, i =>
        {
            Assert.Equal(ChannelItemType.Folder, i.Type);
            Assert.Equal(ChannelFolderType.Season, i.FolderType);
            Assert.Equal("Game of Thrones", i.SeriesName);
        });
        Assert.Equal(1, result.Items[0].IndexNumber);
    }

    [Fact]
    public async Task GetChannelItems_SeriesFolder_TmdbReturnsNull_ReturnsEmpty()
    {
        _tmdb.Setup(t => t.GetSeriesAsync(1399, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TmdbSeriesDetails?)null);

        var q = new InternalChannelItemQuery { FolderId = "series_1399" };
        var result = await _channel.GetChannelItems(q, CancellationToken.None);

        Assert.Empty(result.Items);
    }

    // ----------------------------------------------------------------
    // Season → Episodes
    // ----------------------------------------------------------------

    [Fact]
    public async Task GetChannelItems_SeasonFolder_EmitsPhantomEpisodesWithSplash()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        _tmdb.Setup(t => t.GetSeasonAsync(1399, 1, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSeasonDetails(1399, 1, 3));

        var q = new InternalChannelItemQuery { FolderId = "season_1399_s01" };
        var result = await _channel.GetChannelItems(q, CancellationToken.None);

        Assert.Equal(3, result.Items.Count);
        Assert.Equal("episode_1399_s01e01", result.Items[0].Id);
        Assert.Equal("episode_1399_s01e02", result.Items[1].Id);
        Assert.Equal("episode_1399_s01e03", result.Items[2].Id);
        Assert.All(result.Items, i =>
        {
            Assert.Equal(ChannelItemType.Media, i.Type);
            Assert.Equal(ChannelMediaContentType.Episode, i.ContentType);
            Assert.Contains("phantom", i.Tags);
            Assert.Equal(_splash.ResolveSplashPath(), i.MediaSources[0].Path);
            Assert.Equal("Game of Thrones", i.SeriesName);
            Assert.Equal(1, i.ParentIndexNumber);
        });

        // tmdb_episode_cache should have been populated for all 3.
        var cached = await _db.ListEpisodesForSeasonAsync(1399, 1, CancellationToken.None);
        Assert.Equal(3, cached.Count);
        Assert.Equal("Episode 1 of Season 1", cached[0].Title);
    }

    [Fact]
    public async Task GetChannelItems_SeasonFolder_MaterialisedEpisode_EmitsFuseAndNoPhantomTag()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        _tmdb.Setup(t => t.GetSeasonAsync(1399, 1, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSeasonDetails(1399, 1, 2));
        await _db.InsertMaterialisedStateAsync(1399, "episode", 1, 2, "/stub/e2", "/fuse/got_s01e02.mkv", CancellationToken.None);

        var q = new InternalChannelItemQuery { FolderId = "season_1399_s01" };
        var result = await _channel.GetChannelItems(q, CancellationToken.None);

        var e1 = result.Items.First(i => i.Id == "episode_1399_s01e01");
        var e2 = result.Items.First(i => i.Id == "episode_1399_s01e02");
        Assert.Contains("phantom", e1.Tags);
        Assert.DoesNotContain("phantom", e2.Tags);
        Assert.Equal("/fuse/got_s01e02.mkv", e2.MediaSources[0].Path);
    }

    [Fact]
    public async Task GetChannelItems_SeasonFolder_TmdbReturnsNull_ReturnsEmpty()
    {
        _tmdb.Setup(t => t.GetSeasonAsync(1399, 1, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TmdbSeasonDetails?)null);

        var q = new InternalChannelItemQuery { FolderId = "season_1399_s01" };
        var result = await _channel.GetChannelItems(q, CancellationToken.None);

        Assert.Empty(result.Items);
    }

    // ----------------------------------------------------------------
    // ID stability across phantom → materialised transition
    // ----------------------------------------------------------------

    [Fact]
    public async Task EpisodeId_StableAcrossPhantomToMaterialiseTransition()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        _tmdb.Setup(t => t.GetSeasonAsync(1399, 1, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSeasonDetails(1399, 1, 1));

        var q = new InternalChannelItemQuery { FolderId = "season_1399_s01" };
        var before = await _channel.GetChannelItems(q, CancellationToken.None);
        var beforeId = before.Items.Single().Id;
        Assert.Contains("phantom", before.Items.Single().Tags);

        await _db.InsertMaterialisedStateAsync(1399, "episode", 1, 1, "/stub", "/fuse/got_s01e01.mkv", CancellationToken.None);

        var after = await _channel.GetChannelItems(q, CancellationToken.None);
        var afterEpisode = after.Items.Single();
        Assert.Equal(beforeId, afterEpisode.Id);
        Assert.Equal("episode_1399_s01e01", afterEpisode.Id);
        Assert.DoesNotContain("phantom", afterEpisode.Tags);
        Assert.Equal("/fuse/got_s01e01.mkv", afterEpisode.MediaSources[0].Path);
    }

    // ----------------------------------------------------------------
    // GetChannelItemAsync (IChannelItemRefresh) — critic IMPORTANT 5
    // ----------------------------------------------------------------

    [Fact]
    public async Task GetChannelItemAsync_Series_Resolves()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        var got = await _channel.GetChannelItemAsync("series_1399", CancellationToken.None);
        Assert.NotNull(got);
        Assert.Equal("series_1399", got.Id);
        Assert.Equal(ChannelItemType.Folder, got.Type);
    }

    [Fact]
    public async Task GetChannelItemAsync_Season_Resolves()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        var got = await _channel.GetChannelItemAsync("season_1399_s02", CancellationToken.None);
        Assert.NotNull(got);
        Assert.Equal("season_1399_s02", got.Id);
        Assert.Equal(ChannelFolderType.Season, got.FolderType);
        Assert.Equal(2, got.IndexNumber);
    }

    [Fact]
    public async Task GetChannelItemAsync_Episode_BeforeMaterialise_ReturnsSplashWithPhantomTag()
    {
        // Cold tmdb_episode_cache → channel falls back to TMDB warm.
        _tmdb.Setup(t => t.GetSeasonAsync(1399, 1, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSeasonDetails(1399, 1, 1));

        var got = await _channel.GetChannelItemAsync("episode_1399_s01e01", CancellationToken.None);
        Assert.NotNull(got);
        Assert.Equal("episode_1399_s01e01", got.Id);
        Assert.Contains("phantom", got.Tags);
        Assert.Equal(_splash.ResolveSplashPath(), got.MediaSources[0].Path);
    }

    [Fact]
    public async Task GetChannelItemAsync_Episode_AfterMaterialise_ReturnsFusePath()
    {
        // Critical regression for critic IMPORTANT 5 fix: the post-flight
        // RefreshChannelItem path drives single-item refresh through
        // GetChannelItemAsync. Without explicit episode-kind handling
        // the refresh silently no-ops and BaseItem.Path stays at splash.
        _tmdb.Setup(t => t.GetSeasonAsync(1399, 1, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSeasonDetails(1399, 1, 1));
        await _db.InsertMaterialisedStateAsync(1399, "episode", 1, 1, "/stub", "/fuse/got_s01e01.mkv", CancellationToken.None);

        var got = await _channel.GetChannelItemAsync("episode_1399_s01e01", CancellationToken.None);
        Assert.NotNull(got);
        Assert.Equal("episode_1399_s01e01", got.Id);
        Assert.DoesNotContain("phantom", got.Tags);
        Assert.Equal("/fuse/got_s01e01.mkv", got.MediaSources[0].Path);
    }

    [Fact]
    public async Task GetChannelItemAsync_Episode_UsesCacheWhenPresent_NoTmdbCall()
    {
        // Pre-warm tmdb_episode_cache; assert we serve from cache (no TMDB call expected).
        await _db.UpsertTmdbEpisodeAsync(
            new TmdbEpisodeRow(1399, 1, 1, "Cached Title", "Cached overview", null, null, 50, DateTimeOffset.UtcNow),
            CancellationToken.None);

        var got = await _channel.GetChannelItemAsync("episode_1399_s01e01", CancellationToken.None);
        Assert.NotNull(got);
        Assert.Equal("Cached Title", got.Name);
        _tmdb.Verify(t => t.GetSeasonAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task GetChannelItemAsync_Malformed_ReturnsNull()
    {
        var got = await _channel.GetChannelItemAsync("totally-not-an-id", CancellationToken.None);
        Assert.Null(got);
    }

    [Fact]
    public async Task GetChannelItemAsync_Movie_ReturnsNull()
    {
        // Wrong-channel id; the movies channel handles this kind.
        var got = await _channel.GetChannelItemAsync("movie_42", CancellationToken.None);
        Assert.Null(got);
    }

    // ----------------------------------------------------------------
    // GetChannelItemMediaInfo
    // ----------------------------------------------------------------

    [Fact]
    public async Task GetChannelItemMediaInfo_PhantomEpisode_ReturnsSplash()
    {
        var got = await _channel.GetChannelItemMediaInfo("episode_1399_s01e01", CancellationToken.None);
        var src = Assert.Single(got);
        Assert.Equal(_splash.ResolveSplashPath(), src.Path);
    }

    [Fact]
    public async Task GetChannelItemMediaInfo_MaterialisedEpisode_ReturnsFusePath()
    {
        await _db.InsertMaterialisedStateAsync(1399, "episode", 1, 1, "/stub", "/fuse/ep.mkv", CancellationToken.None);
        var got = await _channel.GetChannelItemMediaInfo("episode_1399_s01e01", CancellationToken.None);
        var src = Assert.Single(got);
        Assert.Equal("/fuse/ep.mkv", src.Path);
    }

    [Fact]
    public async Task GetChannelItemMediaInfo_Series_ReturnsEmpty()
    {
        var got = await _channel.GetChannelItemMediaInfo("series_1399", CancellationToken.None);
        Assert.Empty(got);
    }

    [Fact]
    public async Task GetChannelItemMediaInfo_Season_ReturnsEmpty()
    {
        var got = await _channel.GetChannelItemMediaInfo("season_1399_s01", CancellationToken.None);
        Assert.Empty(got);
    }

    [Fact]
    public async Task GetChannelItemMediaInfo_Garbage_ReturnsEmpty()
    {
        var got = await _channel.GetChannelItemMediaInfo("garbage", CancellationToken.None);
        Assert.Empty(got);
    }

    // ----------------------------------------------------------------
    // GetLatestMedia
    // ----------------------------------------------------------------

    [Fact]
    public async Task GetLatestMedia_ReturnsMaterialisedEpisodesNewestFirst()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        await _db.UpsertTmdbEpisodeAsync(
            new TmdbEpisodeRow(1399, 1, 1, "Pilot", null, null, null, null, DateTimeOffset.UtcNow),
            CancellationToken.None);
        await _db.InsertMaterialisedStateAsync(1399, "episode", 1, 1, "/s1", "/f1", CancellationToken.None);
        await Task.Delay(1100);
        await _db.UpsertTmdbEpisodeAsync(
            new TmdbEpisodeRow(1399, 1, 2, "Second", null, null, null, null, DateTimeOffset.UtcNow),
            CancellationToken.None);
        await _db.InsertMaterialisedStateAsync(1399, "episode", 1, 2, "/s2", "/f2", CancellationToken.None);

        var got = (await _channel.GetLatestMedia(new ChannelLatestMediaSearch(), CancellationToken.None)).ToList();
        Assert.Equal(2, got.Count);
        Assert.Equal("episode_1399_s01e02", got[0].Id);
        Assert.Equal("episode_1399_s01e01", got[1].Id);
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
