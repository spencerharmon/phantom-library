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
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
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
    private readonly GostreamFilesystemEnumerator _enumerator;
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
        _enumerator = new GostreamFilesystemEnumerator(_db, NullLogger<GostreamFilesystemEnumerator>.Instance);
        _enumerator.ShowsRootOverride = Path.Combine(Path.GetTempPath(), "phantom-sc-shows-" + stamp);
        Directory.CreateDirectory(_enumerator.ShowsRootOverride);
        _tmdb = new Mock<ITmdbClient>(MockBehavior.Strict);
        _channel = new PhantomShowsChannel(
            _db, _tmdb.Object, _splash, _state, _enumerator,
            NullLogger<PhantomShowsChannel>.Instance,
            () => null);
    }

    public void Dispose()
    {
        GostreamFilesystemEnumerator.ResetForTests();
        _db.Dispose();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_splashHome)) Directory.Delete(_splashHome, true); } catch { }
        try { if (_enumerator.ShowsRootOverride is not null && Directory.Exists(_enumerator.ShowsRootOverride)) Directory.Delete(_enumerator.ShowsRootOverride, true); } catch { }
    }

    private static void AssertOpeningSource(MediaSourceInfo src, string externalId)
    {
        Assert.Equal(string.Empty, src.Path);
        Assert.True(src.RequiresOpening);
        Assert.StartsWith(PhantomMaterialisingMediaSourceProvider.ProviderPrefix + "phantom:" + externalId, src.OpenToken, StringComparison.Ordinal);
        Assert.True(Guid.TryParse(src.Id, out _));
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

    private Task SeedAvailableEpisodeAsync(int seriesTmdb, int season, int episode)
        => SeedAvailabilityEpisodeAsync(seriesTmdb, season, episode, "available");

    private async Task SeedAvailabilityEpisodeAsync(int seriesTmdb, int season, int episode, string status)
    {
        await _db.SetMetaAsync("__test_schema__", "1", CancellationToken.None);
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO availability_items
            (tmdb_id,type,season,episode,status,checked_at,next_check_at)
            VALUES ($tmdb,'episode',$season,$episode,$status,$now,$next);";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        cmd.Parameters.AddWithValue("$tmdb", seriesTmdb);
        cmd.Parameters.AddWithValue("$season", season);
        cmd.Parameters.AddWithValue("$episode", episode);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$next", DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
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
            Name = $"Season {season}",
            Overview = $"Season {season} overview.",
            PosterPath = $"/season_s{season}.jpg",
            AirDate = $"2011-04-{season:D2}",
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
        await SeedAvailableEpisodeAsync(1399, 1, 1);

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
        await SeedAvailableEpisodeAsync(1399, 1, 1);
        await SeedAvailableEpisodeAsync(1399, 2, 1);
        await SeedAvailableEpisodeAsync(1399, 3, 1);

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
    public async Task GetChannelItems_SeriesFolder_EnrichesSeasonTilesFromTmdbSeasonDetails()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        await SeedAvailableEpisodeAsync(1399, 1, 1);
        await SeedAvailabilityEpisodeAsync(1399, 1, 3, "unavailable");
        _tmdb.Setup(t => t.GetSeriesAsync(1399, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSeriesDetails(1399, 1));
        _tmdb.Setup(t => t.GetSeasonAsync(1399, 1, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSeasonDetails(1399, 1, 3));

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery { FolderId = "series_1399" }, CancellationToken.None);

        var season = Assert.Single(result.Items);
        Assert.Equal("Season 1", season.Name);
        Assert.Equal("Game of Thrones", season.SeriesName);
        Assert.Equal("https://image.tmdb.org/t/p/w500/season_s1.jpg", season.ImageUrl);
        Assert.Contains("Season 1 overview.", season.Overview, StringComparison.Ordinal);
        Assert.Contains("3 episodes", season.Overview, StringComparison.Ordinal);
        Assert.Contains("1 available/materialised", season.Overview, StringComparison.Ordinal);
        Assert.Contains("1 unknown", season.Overview, StringComparison.Ordinal);
        Assert.Contains("1 unavailable", season.Overview, StringComparison.Ordinal);
        Assert.Equal(2011, season.ProductionYear);
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
    public async Task GetChannelItems_SeasonFolder_EmitsPhantomEpisodesWithOpeningSources()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        await SeedAvailableEpisodeAsync(1399, 1, 1);
        await SeedAvailableEpisodeAsync(1399, 1, 2);
        await SeedAvailableEpisodeAsync(1399, 1, 3);
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
            AssertOpeningSource(i.MediaSources[0], i.Id);
            Assert.Equal("Game of Thrones", i.SeriesName);
            Assert.Equal(1, i.ParentIndexNumber);
        });

        // tmdb_episode_cache should have been populated for all 3.
        var cached = await _db.ListEpisodesForSeasonAsync(1399, 1, CancellationToken.None);
        Assert.Equal(3, cached.Count);
        Assert.Equal("Episode 1 of Season 1", cached[0].Title);
    }

    [Fact]
    public async Task GetChannelItems_SeasonFolder_OneAvailableEpisode_EmitsUnknownAndUnavailableSiblings()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        await SeedAvailableEpisodeAsync(1399, 1, 1);
        await SeedAvailabilityEpisodeAsync(1399, 1, 3, "unavailable");
        _tmdb.Setup(t => t.GetSeasonAsync(1399, 1, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSeasonDetails(1399, 1, 3));

        var result = await _channel.GetChannelItems(
            new InternalChannelItemQuery { FolderId = "season_1399_s01" },
            CancellationToken.None);

        Assert.Equal(new int?[] { 1, 2, 3 }, result.Items.Select(i => i.IndexNumber).ToArray());
        Assert.All(result.Items, i => Assert.Contains("phantom", i.Tags));
        Assert.All(result.Items, i => AssertOpeningSource(i.MediaSources[0], i.Id));
    }

    [Fact]
    public async Task GetChannelItems_SeriesFolder_OneAvailableEpisode_EmitsAllTmdbSeasons()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        await SeedAvailableEpisodeAsync(1399, 1, 1);
        _tmdb.Setup(t => t.GetSeriesAsync(1399, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSeriesDetails(1399, 3));

        var result = await _channel.GetChannelItems(
            new InternalChannelItemQuery { FolderId = "series_1399" },
            CancellationToken.None);

        Assert.Equal(new[] { "season_1399_s01", "season_1399_s02", "season_1399_s03" }, result.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task GetChannelItems_SeasonFolder_MaterialisedEpisode_EmitsFuseAndNoPhantomTag()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        await SeedAvailableEpisodeAsync(1399, 1, 1);
        _tmdb.Setup(t => t.GetSeasonAsync(1399, 1, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSeasonDetails(1399, 1, 2));
        var fusePath = Path.Combine(_splashHome, "got_s01e02.mkv");
        await File.WriteAllTextAsync(fusePath, "x", CancellationToken.None);
        await _db.InsertMaterialisedStateAsync(1399, "episode", 1, 2, "/stub/e2", fusePath, CancellationToken.None);

        var q = new InternalChannelItemQuery { FolderId = "season_1399_s01" };
        var result = await _channel.GetChannelItems(q, CancellationToken.None);

        var e1 = result.Items.First(i => i.Id == "episode_1399_s01e01");
        var e2 = result.Items.First(i => i.Id == "episode_1399_s01e02");
        Assert.Contains("phantom", e1.Tags);
        Assert.DoesNotContain("phantom", e2.Tags);
        Assert.Equal(fusePath, e2.MediaSources[0].Path);
    }

    [Fact]
    public async Task GetChannelItems_SeasonFolder_DoesNotProbeMaterialisedEpisodeFilesDuringBrowse()
    {
        // Regression guard for 2026-06-25 root/list timeout: browse must
        // not FFprobe every materialised episode while building channel
        // lists. Audio stream probing belongs to playback/media-info for
        // the selected item only.
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        _tmdb.Setup(t => t.GetSeasonAsync(1399, 1, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSeasonDetails(1399, 1, 3));
        var expectedPaths = new List<string>();
        for (var episode = 1; episode <= 3; episode++)
        {
            var fusePath = Path.Combine(_splashHome, $"browse_s01e{episode:D2}.mkv");
            await File.WriteAllTextAsync(fusePath, "x", CancellationToken.None);
            expectedPaths.Add(fusePath);
            await _db.InsertMaterialisedStateAsync(1399, "episode", 1, episode, $"/stub/e{episode}", fusePath, CancellationToken.None);
        }

        var encoder = new Mock<IMediaEncoder>(MockBehavior.Strict);
        var channel = new PhantomShowsChannel(
            _db, _tmdb.Object, _splash, _state, _enumerator, encoder.Object,
            NullLogger<PhantomShowsChannel>.Instance);

        var result = await channel.GetChannelItems(new InternalChannelItemQuery { FolderId = "season_1399_s01" }, CancellationToken.None);

        Assert.Equal(3, result.Items.Count);
        Assert.Equal(
            expectedPaths.OrderBy(p => p, StringComparer.Ordinal).ToArray(),
            result.Items.Select(i => i.MediaSources[0].Path).OrderBy(p => p, StringComparer.Ordinal).ToArray());
        encoder.Verify(e => e.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetChannelItems_SeasonFolder_MaterialisedEpisodeWithMissingFile_EmitsOpeningSource()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        _tmdb.Setup(t => t.GetSeasonAsync(1399, 1, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSeasonDetails(1399, 1, 1));
        await _db.InsertMaterialisedStateAsync(1399, "episode", 1, 1, "/stub/e1", Path.Combine(_splashHome, "missing_s01e01.mkv"), CancellationToken.None);

        var q = new InternalChannelItemQuery { FolderId = "season_1399_s01" };
        var result = await _channel.GetChannelItems(q, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Contains("phantom", item.Tags);
        AssertOpeningSource(item.MediaSources[0], "episode_1399_s01e01");
    }

    [Fact]
    public async Task GetChannelItems_SeasonFolder_TmdbReturnsNull_FallsBackToEpisodeCache()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        await SeedAvailableEpisodeAsync(1399, 1, 1);
        await _db.UpsertTmdbEpisodeAsync(
            new TmdbEpisodeRow(1399, 1, 1, "Cached Pilot", "Cached synopsis", null, null, 50, DateTimeOffset.UtcNow),
            CancellationToken.None);
        _tmdb.Setup(t => t.GetSeasonAsync(1399, 1, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TmdbSeasonDetails?)null);

        var q = new InternalChannelItemQuery { FolderId = "season_1399_s01" };
        var result = await _channel.GetChannelItems(q, CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("episode_1399_s01e01", item.Id);
        Assert.Equal("Cached Pilot", item.Name);
        Assert.Contains("phantom", item.Tags);
    }

    [Fact]
    public async Task GetChannelItems_SeasonFolder_TmdbReturnsNullAndCacheCold_ReturnsEmpty()
    {
        _tmdb.Setup(t => t.GetSeasonAsync(1399, 1, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TmdbSeasonDetails?)null);

        var q = new InternalChannelItemQuery { FolderId = "season_1399_s01" };
        var result = await _channel.GetChannelItems(q, CancellationToken.None);

        Assert.Empty(result.Items);
    }

    // ----------------------------------------------------------------
    // Per-user visibility (REQ-M14-PER-USER Surface 3)
    // ----------------------------------------------------------------

    [Fact]
    public async Task GetChannelItems_TopLevel_HiddenSeriesForUser_OmittedForHider_ButVisibleForOtherUserAndAnonymous()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        await SeedAvailableEpisodeAsync(1399, 1, 1);
        var hider = Guid.NewGuid();
        var other = Guid.NewGuid();
        await _db.AddHiddenItemAsync(hider, 1399, "series", CancellationToken.None);

        var hiderResult = await _channel.GetChannelItems(new InternalChannelItemQuery { UserId = hider }, CancellationToken.None);
        var otherResult = await _channel.GetChannelItems(new InternalChannelItemQuery { UserId = other }, CancellationToken.None);
        var anonymousResult = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        Assert.Empty(hiderResult.Items);
        Assert.Single(otherResult.Items);
        Assert.Equal("series_1399", otherResult.Items[0].Id);
        Assert.Single(anonymousResult.Items);
    }

    [Fact]
    public async Task GetChannelItems_SeriesFolder_HiddenForUser_ReturnsEmpty_BeforeAnyTmdbCall()
    {
        // _tmdb is a MockBehavior.Strict mock with NO setups in this test: if
        // the hidden short-circuit did not run before SafeGetSeriesAsync /
        // FindExternalSeriesByTmdbAsync's TMDB calls, this would throw
        // MockException instead of returning an empty result.
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        await SeedAvailableEpisodeAsync(1399, 1, 1);
        await SeedAvailableEpisodeAsync(1399, 2, 1);
        var hider = Guid.NewGuid();
        await _db.AddHiddenItemAsync(hider, 1399, "series", CancellationToken.None);

        var q = new InternalChannelItemQuery { FolderId = "series_1399", UserId = hider };
        var result = await _channel.GetChannelItems(q, CancellationToken.None);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetChannelItems_SeriesFolder_HiddenForHider_ButOtherUserStillSeesSeasons()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        await SeedAvailableEpisodeAsync(1399, 1, 1);
        _tmdb.Setup(t => t.GetSeriesAsync(1399, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSeriesDetails(1399, 1));
        var hider = Guid.NewGuid();
        var other = Guid.NewGuid();
        await _db.AddHiddenItemAsync(hider, 1399, "series", CancellationToken.None);

        var hiderResult = await _channel.GetChannelItems(
            new InternalChannelItemQuery { FolderId = "series_1399", UserId = hider }, CancellationToken.None);
        var otherResult = await _channel.GetChannelItems(
            new InternalChannelItemQuery { FolderId = "series_1399", UserId = other }, CancellationToken.None);

        Assert.Empty(hiderResult.Items);
        Assert.Single(otherResult.Items);
        Assert.Equal("season_1399_s01", otherResult.Items[0].Id);
    }

    [Fact]
    public async Task GetChannelItems_SeasonFolder_HiddenForUser_ReturnsEmpty_BeforeAnyTmdbCall()
    {
        // Same Strict-mock-with-no-setups proof as the series-folder case,
        // for the season → episodes path.
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        await SeedAvailableEpisodeAsync(1399, 1, 1);
        var hider = Guid.NewGuid();
        await _db.AddHiddenItemAsync(hider, 1399, "series", CancellationToken.None);

        var q = new InternalChannelItemQuery { FolderId = "season_1399_s01", UserId = hider };
        var result = await _channel.GetChannelItems(q, CancellationToken.None);

        Assert.Empty(result.Items);
    }

    [Fact]
    public async Task GetChannelItems_SeasonFolder_HiddenForHider_ButOtherUserStillSeesEpisodes()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        await SeedAvailableEpisodeAsync(1399, 1, 1);
        _tmdb.Setup(t => t.GetSeasonAsync(1399, 1, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSeasonDetails(1399, 1, 1));
        var hider = Guid.NewGuid();
        var other = Guid.NewGuid();
        await _db.AddHiddenItemAsync(hider, 1399, "series", CancellationToken.None);

        var hiderResult = await _channel.GetChannelItems(
            new InternalChannelItemQuery { FolderId = "season_1399_s01", UserId = hider }, CancellationToken.None);
        var otherResult = await _channel.GetChannelItems(
            new InternalChannelItemQuery { FolderId = "season_1399_s01", UserId = other }, CancellationToken.None);

        Assert.Empty(hiderResult.Items);
        Assert.Single(otherResult.Items);
        Assert.Equal("episode_1399_s01e01", otherResult.Items[0].Id);
    }

    [Fact]
    public async Task GetChannelItems_HiddenForUser_ThenUnhidden_ReappearsForThatUser()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        await SeedAvailableEpisodeAsync(1399, 1, 1);
        var userId = Guid.NewGuid();
        await _db.AddHiddenItemAsync(userId, 1399, "series", CancellationToken.None);
        Assert.Empty((await _channel.GetChannelItems(new InternalChannelItemQuery { UserId = userId }, CancellationToken.None)).Items);

        await _db.RemoveHiddenItemAsync(userId, 1399, "series", CancellationToken.None);

        var result = await _channel.GetChannelItems(new InternalChannelItemQuery { UserId = userId }, CancellationToken.None);
        Assert.Single(result.Items);
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

    // ----------------------------------------------------------------
    // ID stability across phantom → materialised transition
    // ----------------------------------------------------------------

    [Fact]
    public async Task EpisodeId_StableAcrossPhantomToMaterialiseTransition()
    {
        await SeedSeriesMetaAsync(1399, "Game of Thrones");
        await SeedAvailableEpisodeAsync(1399, 1, 1);
        _tmdb.Setup(t => t.GetSeasonAsync(1399, 1, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSeasonDetails(1399, 1, 1));

        var q = new InternalChannelItemQuery { FolderId = "season_1399_s01" };
        var before = await _channel.GetChannelItems(q, CancellationToken.None);
        var beforeId = before.Items.Single().Id;
        Assert.Contains("phantom", before.Items.Single().Tags);

        var fusePath = Path.Combine(_splashHome, "stable_s01e01.mkv");
        await File.WriteAllTextAsync(fusePath, "x", CancellationToken.None);
        await _db.InsertMaterialisedStateAsync(1399, "episode", 1, 1, "/stub", fusePath, CancellationToken.None);

        var after = await _channel.GetChannelItems(q, CancellationToken.None);
        var afterEpisode = after.Items.Single();
        Assert.Equal(beforeId, afterEpisode.Id);
        Assert.Equal("episode_1399_s01e01", afterEpisode.Id);
        Assert.DoesNotContain("phantom", afterEpisode.Tags);
        Assert.Equal(fusePath, afterEpisode.MediaSources[0].Path);
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
    public async Task GetChannelItemAsync_Episode_BeforeMaterialise_ReturnsOpeningSourceWithPhantomTag()
    {
        // Cold tmdb_episode_cache → channel falls back to TMDB warm.
        _tmdb.Setup(t => t.GetSeasonAsync(1399, 1, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSeasonDetails(1399, 1, 1));

        var got = await _channel.GetChannelItemAsync("episode_1399_s01e01", CancellationToken.None);
        Assert.NotNull(got);
        Assert.Equal("episode_1399_s01e01", got.Id);
        Assert.Contains("phantom", got.Tags);
        AssertOpeningSource(got.MediaSources[0], "episode_1399_s01e01");
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
        var fusePath = Path.Combine(_splashHome, "refresh_s01e01.mkv");
        await File.WriteAllTextAsync(fusePath, "x", CancellationToken.None);
        await _db.InsertMaterialisedStateAsync(1399, "episode", 1, 1, "/stub", fusePath, CancellationToken.None);

        var got = await _channel.GetChannelItemAsync("episode_1399_s01e01", CancellationToken.None);
        Assert.NotNull(got);
        Assert.Equal("episode_1399_s01e01", got.Id);
        Assert.DoesNotContain("phantom", got.Tags);
        Assert.Equal(fusePath, got.MediaSources[0].Path);
    }

    [Fact]
    public async Task GetChannelItemAsync_Episode_AfterMaterialiseWithMissingFile_ReturnsOpeningSource()
    {
        _tmdb.Setup(t => t.GetSeasonAsync(1399, 1, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeSeasonDetails(1399, 1, 1));
        await _db.InsertMaterialisedStateAsync(1399, "episode", 1, 1, "/stub", Path.Combine(_splashHome, "missing_refresh_s01e01.mkv"), CancellationToken.None);

        var got = await _channel.GetChannelItemAsync("episode_1399_s01e01", CancellationToken.None);

        Assert.NotNull(got);
        Assert.Contains("phantom", got.Tags);
        AssertOpeningSource(got.MediaSources[0], "episode_1399_s01e01");
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
    public async Task GetChannelItemMediaInfo_PhantomEpisode_ReturnsEmpty()
    {
        var got = await _channel.GetChannelItemMediaInfo("episode_1399_s01e01", CancellationToken.None);
        Assert.Empty(got);
    }

    [Fact]
    public async Task GetChannelItemMediaInfo_MaterialisedEpisode_ReturnsFusePath()
    {
        var fusePath = Path.Combine(_splashHome, "media-info.mkv");
        await File.WriteAllTextAsync(fusePath, "x", CancellationToken.None);
        await _db.InsertMaterialisedStateAsync(1399, "episode", 1, 1, "/stub", fusePath, CancellationToken.None);
        var got = await _channel.GetChannelItemMediaInfo("episode_1399_s01e01", CancellationToken.None);
        var src = Assert.Single(got);
        Assert.Equal(fusePath, src.Path);
    }

    [Fact]
    public async Task GetChannelItemMediaInfo_MaterialisedEpisode_ProbesAudioStreams()
    {
        var fusePath = Path.Combine(_splashHome, "media-info-audio.mkv");
        await File.WriteAllTextAsync(fusePath, "x", CancellationToken.None);
        await _db.InsertMaterialisedStateAsync(1399, "episode", 1, 2, "/stub", fusePath, CancellationToken.None);
        var encoder = new Mock<IMediaEncoder>(MockBehavior.Loose);
        encoder.Setup(e => e.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaInfo
            {
                MediaStreams = new List<MediaStream>
                {
                    new() { Index = 0, Type = MediaStreamType.Video, IsDefault = true },
                    new() { Index = 1, Type = MediaStreamType.Audio, Language = "pol", IsDefault = true },
                    new() { Index = 2, Type = MediaStreamType.Audio, Language = "eng" },
                },
            });
        var channel = new PhantomShowsChannel(
            _db, _tmdb.Object, _splash, _state, _enumerator, encoder.Object,
            NullLogger<PhantomShowsChannel>.Instance);

        var got = await channel.GetChannelItemMediaInfo("episode_1399_s01e02", CancellationToken.None);

        var src = Assert.Single(got);
        Assert.Equal(new[] { 1, 2 }, src.MediaStreams.Where(s => s.Type == MediaStreamType.Audio).Select(s => s.Index));
        Assert.Equal(1, src.DefaultAudioStreamIndex);
    }

    [Fact]
    public async Task GetChannelItemMediaInfo_MaterialisedEpisodeWithMissingFile_ReturnsEmpty()
    {
        await _db.InsertMaterialisedStateAsync(1399, "episode", 1, 1, "/stub", Path.Combine(_splashHome, "missing-media-info.mkv"), CancellationToken.None);

        var got = await _channel.GetChannelItemMediaInfo("episode_1399_s01e01", CancellationToken.None);

        Assert.Empty(got);
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
    // Latest media suppression (Option 3, operator decision 2026-06-28)
    // ----------------------------------------------------------------

    [Fact]
    public void Channel_DoesNotImplementISupportsLatestMedia()
    {
        // Implementing ISupportsLatestMedia makes Jellyfin core's
        // RefreshLatestChannelItems deep-enumerate the whole channel
        // (series -> season -> build) on every Home load to populate the
        // "Latest in Phantom Shows" row, hanging the Home screen on every
        // client. The interface must stay off until the O(latest) Option 2
        // fast-path exists.
        Assert.DoesNotContain(
            typeof(MediaBrowser.Controller.Channels.ISupportsLatestMedia),
            _channel.GetType().GetInterfaces());
    }

    [Fact]
    public async Task GostreamOnlyTvFiles_GroupVariantsAndKeepFilesWithoutEpisodeTokenVisible()
    {
        var seasonDir = Path.Combine(_enumerator.ShowsRootOverride!, "Variant_Show (2026)", "Season.01");
        Directory.CreateDirectory(seasonDir);
        await File.WriteAllTextAsync(Path.Combine(seasonDir, "Variant_Show_S01E01_720p_aaaaaaaa.mkv"), "x", CancellationToken.None);
        var best = Path.Combine(seasonDir, "Variant_Show_S01E01_2160p_bbbbbbbb.mkv");
        await File.WriteAllTextAsync(best, "x", CancellationToken.None);
        var noToken = Path.Combine(seasonDir, "Special Feature cccccccc.mkv");
        await File.WriteAllTextAsync(noToken, "x", CancellationToken.None);

        var top = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
        var series = Assert.Single(top.Items, i => string.Equals(i.Name, "Variant Show", StringComparison.Ordinal));
        var seasons = await _channel.GetChannelItems(new InternalChannelItemQuery { FolderId = series.Id }, CancellationToken.None);
        var season = Assert.Single(seasons.Items);
        var episodes = await _channel.GetChannelItems(new InternalChannelItemQuery { FolderId = season.Id }, CancellationToken.None);

        Assert.Equal(2, episodes.Items.Count);
        var parsed = Assert.Single(episodes.Items, i => i.IndexNumber == 1);
        Assert.Equal(best, Assert.Single(parsed.MediaSources).Path);
        var unparsed = Assert.Single(episodes.Items, i => i.IndexNumber is null);
        Assert.Equal(noToken, Assert.Single(unparsed.MediaSources).Path);
        Assert.Equal(1, unparsed.ParentIndexNumber);
    }

    [Fact]
    public async Task GostreamOnlyTvFiles_WithTmdbHit_UseSeriesMetadata()
    {
        var seasonDir = Path.Combine(_enumerator.ShowsRootOverride!, "56_Days (2026)", "Season.01");
        Directory.CreateDirectory(seasonDir);
        await File.WriteAllTextAsync(Path.Combine(seasonDir, "56_Days_S01E01_72a275d4.mkv"), "x", CancellationToken.None);
        await _db.UpsertTmdbMetadataAsync(
            new TmdbMetadataRow(
                99056001,
                "series",
                "56 Days From TMDB",
                2026,
                "Full overview",
                "https://image.tmdb.org/t/p/w500/poster.jpg",
                "https://image.tmdb.org/t/p/w500/backdrop.jpg",
                new[] { "Drama" },
                null,
                8.1,
                "56 Days",
                DateTimeOffset.UtcNow),
            CancellationToken.None);

        var top = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);

        var series = Assert.Single(top.Items, i => string.Equals(i.Name, "56 Days From TMDB", StringComparison.Ordinal));
        Assert.Equal("series_99056001", series.Id);
        Assert.Equal("Full overview", series.Overview);
        Assert.Equal("https://image.tmdb.org/t/p/w500/poster.jpg", series.ImageUrl);
        Assert.Equal("99056001", series.ProviderIds["Tmdb"]);
        Assert.Contains("external", series.Tags);
    }

    [Fact]
    public async Task GostreamOnlyTvFiles_PathTmdbPersisted_SecondColdChannel_UsesCacheNotTitleSearch()
    {
        var seasonDir = Path.Combine(_enumerator.ShowsRootOverride!, "56_Days (2026)", "Season.01");
        Directory.CreateDirectory(seasonDir);
        await File.WriteAllTextAsync(Path.Combine(seasonDir, "56_Days_S01E01_72a275d4.mkv"), "x", CancellationToken.None);
        await _db.UpsertTmdbMetadataAsync(
            new TmdbMetadataRow(99056001, "series", "56 Days From TMDB", 2026, "ov",
                "https://img/p.jpg", "https://img/b.jpg", new[] { "Drama" }, null, 8.1, "56 Days", DateTimeOffset.UtcNow),
            CancellationToken.None);

        var first = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
        Assert.Single(first.Items, i => i.Id == "series_99056001");
        Assert.Equal(99056001, await _db.GetGostreamPathTmdbAsync(Path.Combine(_enumerator.ShowsRootOverride!, "56_Days (2026)"), "series", CancellationToken.None));

        // Rename metadata so a title/year search would no longer match. Only the
        // persisted path->tmdb cache + GetTmdbMetadataAsync(id) can still resolve.
        await _db.UpsertTmdbMetadataAsync(
            new TmdbMetadataRow(99056001, "series", "Renamed", null, "ov",
                null, null, null, null, null, "Renamed", DateTimeOffset.UtcNow),
            CancellationToken.None);
        GostreamFilesystemEnumerator.ResetForTests();
        var cold = new PhantomShowsChannel(_db, _tmdb.Object, _splash, _state, _enumerator,
            NullLogger<PhantomShowsChannel>.Instance, () => null);
        var second = await cold.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
        var series = Assert.Single(second.Items, i => i.Id == "series_99056001");
        Assert.Equal("Renamed", series.Name);
    }

    [Fact]
    public async Task GostreamOnlyTvFiles_AppearAsOrphanSeriesSeasonsAndEpisodes()
    {
        var seasonDir = Path.Combine(_enumerator.ShowsRootOverride!, "56_Days (2026)", "Season.01");
        Directory.CreateDirectory(seasonDir);
        var episodePath = Path.Combine(seasonDir, "56_Days_S01E01_72a275d4.mkv");
        await File.WriteAllTextAsync(episodePath, "x", CancellationToken.None);

        var top = await _channel.GetChannelItems(new InternalChannelItemQuery(), CancellationToken.None);
        var series = Assert.Single(top.Items, i => string.Equals(i.Name, "56 Days", StringComparison.Ordinal));
        Assert.StartsWith("orphanseries_", series.Id, StringComparison.Ordinal);
        Assert.Equal(2026, series.ProductionYear);
        Assert.Contains("external", series.Tags);

        var seasons = await _channel.GetChannelItems(new InternalChannelItemQuery { FolderId = series.Id }, CancellationToken.None);
        var season = Assert.Single(seasons.Items);
        Assert.Equal("Season 1", season.Name);
        Assert.StartsWith("orphanseason_", season.Id, StringComparison.Ordinal);
        Assert.Contains("external", season.Tags);

        var episodes = await _channel.GetChannelItems(new InternalChannelItemQuery { FolderId = season.Id }, CancellationToken.None);
        var episode = Assert.Single(episodes.Items);
        Assert.StartsWith("orphanepisode_", episode.Id, StringComparison.Ordinal);
        Assert.Contains("external", episode.Tags);
        Assert.Equal(1, episode.ParentIndexNumber);
        Assert.Equal(1, episode.IndexNumber);
        Assert.Equal(episodePath, Assert.Single(episode.MediaSources).Path);

        var media = await _channel.GetChannelItemMediaInfo(episode.Id, CancellationToken.None);
        Assert.Equal(episodePath, Assert.Single(media).Path);
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
