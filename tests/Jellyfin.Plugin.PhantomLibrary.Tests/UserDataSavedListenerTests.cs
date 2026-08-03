using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Library;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public sealed class UserDataSavedListenerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "phantom-udsl-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly Mock<ISeriesAutopilot> _autopilot = new(MockBehavior.Loose);
    private PhantomDb? _db;

    public void Dispose()
    {
        try
        {
            _db?.Dispose();
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

    [Fact]
    public async Task FavouriteMovie_TriggersMaterialiseByExternalId()
    {
        using var db = await NewDbAsync();
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var sut = Build(materialiser, db);
        var item = new Movie
        {
            ChannelId = ChannelIds.Movies,
            ExternalId = ChannelItemId.ForMovie(42).Encode(),
        };
        var userData = new UserItemData { Key = "movie_42", IsFavorite = true };

        sut.HandleSavedUserData(item, userData, Guid.NewGuid());

        materialiser.Verify(m => m.MaterialiseAsync(
            42,
            "movie",
            null,
            null,
            MaterialiseTrigger.Favourite,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FavouriteEpisode_TriggersMaterialiseByExternalId()
    {
        using var db = await NewDbAsync();
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var sut = Build(materialiser, db);
        var item = new Episode
        {
            ChannelId = ChannelIds.Shows,
            ExternalId = ChannelItemId.ForEpisode(200, 1, 2).Encode(),
        };
        var userData = new UserItemData { Key = "episode_200_s01e02", IsFavorite = true };

        sut.HandleSavedUserData(item, userData, Guid.NewGuid());

        materialiser.Verify(m => m.MaterialiseAsync(
            200,
            "episode",
            1,
            2,
            MaterialiseTrigger.Favourite,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FavouriteSeason_MaterialisesEveryCachedEpisodeInSeason()
    {
        using var db = await NewDbAsync();
        await SeedEpisodeAsync(db, 200, 1, 1);
        await SeedEpisodeAsync(db, 200, 1, 2);
        var calls = new ConcurrentBag<(int Tmdb, string Type, int? Season, int? Episode, MaterialiseTrigger Trigger)>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var materialiser = RecordingMaterialiser(calls, completed, expectedCalls: 2);
        var sut = Build(materialiser, db);
        var item = new Season
        {
            ChannelId = ChannelIds.Shows,
            ExternalId = ChannelItemId.ForSeason(200, 1).Encode(),
        };
        var userData = new UserItemData { Key = "season_200_s01", IsFavorite = true };

        sut.HandleSavedUserData(item, userData, Guid.NewGuid());
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains(calls, c => c.Tmdb == 200 && c.Type == "episode" && c.Season == 1 && c.Episode == 1 && c.Trigger == MaterialiseTrigger.Favourite);
        Assert.Contains(calls, c => c.Tmdb == 200 && c.Type == "episode" && c.Season == 1 && c.Episode == 2 && c.Trigger == MaterialiseTrigger.Favourite);
    }

    [Fact]
    public async Task FavouriteSeries_FetchesSeasonsAndMaterialisesEveryEpisode()
    {
        using var db = await NewDbAsync();
        var tmdb = new Mock<ITmdbClient>(MockBehavior.Loose);
        tmdb.Setup(t => t.GetSeriesAsync(200, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TmdbSeriesDetails(200, "Show", null, null, null, null, "2020-01-01", null, null, Array.Empty<string>(), "Ended", 2, 3, Array.Empty<string>(), "tt200"));
        tmdb.Setup(t => t.GetSeasonAsync(200, 1, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Season(200, 1, 1, 2));
        tmdb.Setup(t => t.GetSeasonAsync(200, 2, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Season(200, 2, 1));
        var calls = new ConcurrentBag<(int Tmdb, string Type, int? Season, int? Episode, MaterialiseTrigger Trigger)>();
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var materialiser = RecordingMaterialiser(calls, completed, expectedCalls: 3);
        var sut = Build(materialiser, db, tmdb.Object);
        var item = new Series
        {
            ChannelId = ChannelIds.Shows,
            ExternalId = ChannelItemId.ForSeries(200).Encode(),
        };
        var userData = new UserItemData { Key = "series_200", IsFavorite = true };

        sut.HandleSavedUserData(item, userData, Guid.NewGuid());
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Contains(calls, c => c.Season == 1 && c.Episode == 1);
        Assert.Contains(calls, c => c.Season == 1 && c.Episode == 2);
        Assert.Contains(calls, c => c.Season == 2 && c.Episode == 1);
    }

    [Fact]
    public void FavouriteMovie_TriggersRecommendationIngest()
    {
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var sut = Build(materialiser, out var ingestor);
        var item = new Movie
        {
            ChannelId = ChannelIds.Movies,
            ExternalId = ChannelItemId.ForMovie(42).Encode(),
        };
        var userData = new UserItemData { Key = "movie_42", IsFavorite = true };

        sut.HandleSavedUserData(item, userData, Guid.NewGuid());

        ingestor.Verify(i => i.IngestForFavouriteAsync(42, "movie", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void FavouriteSeries_TriggersRecommendationIngestForSeries()
    {
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var sut = Build(materialiser, out var ingestor);
        var item = new Series
        {
            ChannelId = ChannelIds.Shows,
            ExternalId = ChannelItemId.ForSeries(200).Encode(),
        };
        var userData = new UserItemData { Key = "series_200", IsFavorite = true };

        sut.HandleSavedUserData(item, userData, Guid.NewGuid());

        ingestor.Verify(i => i.IngestForFavouriteAsync(200, "series", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void FavouriteEpisode_TriggersRecommendationIngestForParentSeries()
    {
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var sut = Build(materialiser, out var ingestor);
        var item = new Episode
        {
            ChannelId = ChannelIds.Shows,
            ExternalId = ChannelItemId.ForEpisode(200, 1, 2).Encode(),
        };
        var userData = new UserItemData { Key = "episode_200_s01e02", IsFavorite = true };

        sut.HandleSavedUserData(item, userData, Guid.NewGuid());

        // Episode's ChannelItemId carries the series id; recommendations seed off it.
        ingestor.Verify(i => i.IngestForFavouriteAsync(200, "series", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void NonFavourite_DoesNotTriggerRecommendationIngest()
    {
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var sut = Build(materialiser, out var ingestor);
        var item = new Movie
        {
            ChannelId = ChannelIds.Movies,
            ExternalId = ChannelItemId.ForMovie(42).Encode(),
        };
        var userData = new UserItemData { Key = "movie_42", IsFavorite = false };

        sut.HandleSavedUserData(item, userData, Guid.NewGuid());

        ingestor.Verify(
            i => i.IngestForFavouriteAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ---- per-user allow_eager gate (REQ-M14-PER-USER, Surface 4) ----
    //
    // A user's own interactions drive eager source probing / materialise only
    // when their allow_eager toggle is on. Two probe entry points are gated:
    // favourite -> materialise (movie + episode) and episode-playback ->
    // autopilot prefetch. Recommendation ingest is catalogue expansion off an
    // explicit taste signal, NOT this user's probe budget, so it stays ungated.

    [Fact]
    public void FavouriteMovie_AllowEagerOff_SuppressesMaterialise_StillRecommends()
    {
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var sut = Build(materialiser, out var ingestor);
        var userId = Guid.NewGuid();
        SetAllowEager(userId, false);

        var item = new Movie
        {
            ChannelId = ChannelIds.Movies,
            ExternalId = ChannelItemId.ForMovie(42).Encode(),
        };
        var userData = new UserItemData { Key = "movie_42", IsFavorite = true };

        sut.HandleSavedUserData(item, userData, userId);

        materialiser.Verify(m => m.MaterialiseAsync(
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<int?>(),
            It.IsAny<int?>(),
            It.IsAny<MaterialiseTrigger>(),
            It.IsAny<CancellationToken>()), Times.Never);

        // The taste signal still expands the catalogue even with probing off.
        ingestor.Verify(i => i.IngestForFavouriteAsync(42, "movie", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void FavouriteEpisode_AllowEagerOff_SuppressesMaterialise_StillRecommends()
    {
        // Movie/TV parity for the favourite-materialise gate.
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var sut = Build(materialiser, out var ingestor);
        var userId = Guid.NewGuid();
        SetAllowEager(userId, false);

        var item = new Episode
        {
            ChannelId = ChannelIds.Shows,
            ExternalId = ChannelItemId.ForEpisode(200, 1, 2).Encode(),
        };
        var userData = new UserItemData { Key = "episode_200_s01e02", IsFavorite = true };

        sut.HandleSavedUserData(item, userData, userId);

        materialiser.Verify(m => m.MaterialiseAsync(
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<int?>(),
            It.IsAny<int?>(),
            It.IsAny<MaterialiseTrigger>(),
            It.IsAny<CancellationToken>()), Times.Never);

        ingestor.Verify(i => i.IngestForFavouriteAsync(200, "series", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void FavouriteMovie_AllowEagerOn_TriggersMaterialise()
    {
        // Explicit allow_eager=on row still permits the probe (complements the
        // missing-row default-on path covered above).
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var sut = Build(materialiser);
        var userId = Guid.NewGuid();
        SetAllowEager(userId, true);

        var item = new Movie
        {
            ChannelId = ChannelIds.Movies,
            ExternalId = ChannelItemId.ForMovie(42).Encode(),
        };
        var userData = new UserItemData { Key = "movie_42", IsFavorite = true };

        sut.HandleSavedUserData(item, userData, userId);

        materialiser.Verify(m => m.MaterialiseAsync(
            42,
            "movie",
            null,
            null,
            MaterialiseTrigger.Favourite,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void EpisodePlayback_AllowEagerOff_SuppressesAutopilot()
    {
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var sut = Build(materialiser);
        var userId = Guid.NewGuid();
        SetAllowEager(userId, false);

        var item = new Episode
        {
            ChannelId = ChannelIds.Shows,
            ExternalId = ChannelItemId.ForEpisode(200, 1, 2).Encode(),
        };
        // Played -> 100% -> clears the autopilot playback threshold.
        var userData = new UserItemData { Key = "episode_200_s01e02", Played = true };

        sut.HandleSavedUserData(item, userData, userId);

        _autopilot.Verify(a => a.OnEpisodePlaybackProgressAsync(
            It.IsAny<Guid>(),
            It.IsAny<Episode>(),
            It.IsAny<double>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void EpisodePlayback_AllowEagerOn_TriggersAutopilot()
    {
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var sut = Build(materialiser);
        var userId = Guid.NewGuid();
        SetAllowEager(userId, true);

        var item = new Episode
        {
            ChannelId = ChannelIds.Shows,
            ExternalId = ChannelItemId.ForEpisode(200, 1, 2).Encode(),
        };
        var userData = new UserItemData { Key = "episode_200_s01e02", Played = true };

        sut.HandleSavedUserData(item, userData, userId);

        _autopilot.Verify(a => a.OnEpisodePlaybackProgressAsync(
            userId,
            It.IsAny<Episode>(),
            It.IsAny<double>(),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private UserDataSavedListener Build(Mock<IMaterialiser> materialiser)
        => Build(materialiser, out _);

    private async Task<PhantomDb> NewDbAsync()
    {
        var db = new PhantomDb(_dbPath);
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        return db;
    }

    private static async Task SeedEpisodeAsync(PhantomDb db, int seriesTmdb, int season, int episode)
    {
        await db.UpsertTmdbEpisodeAsync(new TmdbEpisodeRow(
            seriesTmdb,
            season,
            episode,
            $"Episode {episode}",
            null,
            null,
            "2024-01-01",
            30,
            DateTimeOffset.UtcNow), CancellationToken.None);
    }

    private static TmdbSeasonDetails Season(int seriesTmdb, int season, params int[] episodes)
        => new()
        {
            SeriesTmdbId = seriesTmdb,
            SeasonNumber = season,
            Episodes = episodes.Select(ep => new TmdbEpisodeSummary
            {
                Id = season * 1000 + ep,
                EpisodeNumber = ep,
                SeasonNumber = season,
                Name = $"Episode {ep}",
                AirDate = "2024-01-01",
            }).ToArray(),
        };

    private static Mock<IMaterialiser> RecordingMaterialiser(
        ConcurrentBag<(int Tmdb, string Type, int? Season, int? Episode, MaterialiseTrigger Trigger)> calls,
        TaskCompletionSource completed,
        int expectedCalls)
    {
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        materialiser.Setup(m => m.MaterialiseAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<MaterialiseTrigger>(),
                It.IsAny<CancellationToken>()))
            .Callback<int, string, int?, int?, MaterialiseTrigger, CancellationToken>((tmdb, type, season, episode, trigger, _) =>
            {
                calls.Add((tmdb, type, season, episode, trigger));
                if (calls.Count >= expectedCalls)
                {
                    completed.TrySetResult();
                }
            })
            .ReturnsAsync(MaterialisationOutcome.Duplicate);
        return materialiser;
    }

    private UserDataSavedListener Build(Mock<IMaterialiser> materialiser, out Mock<IFavouriteRecommendationIngestor> ingestor)
    {
        materialiser.Setup(m => m.MaterialiseAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<MaterialiseTrigger>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(MaterialisationOutcome.Duplicate);

        ingestor = new Mock<IFavouriteRecommendationIngestor>(MockBehavior.Loose);
        ingestor.Setup(i => i.IngestForFavouriteAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, string type, CancellationToken _) =>
                new FavouriteRecommendationResult(id, type, true, 0, 0, 0, 0, 0, 0));

        var userData = new Mock<IUserDataManager>(MockBehavior.Loose);
        _db ??= CreateDb();
        var tmdb = new Mock<ITmdbClient>(MockBehavior.Loose).Object;
        return new UserDataSavedListener(
            userData.Object,
            _autopilot.Object,
            materialiser.Object,
            ingestor.Object,
            _db,
            tmdb,
            NullLogger<UserDataSavedListener>.Instance,
            () => new PluginConfiguration { DiscoveryLanguage = "en-US" });
    }

    private UserDataSavedListener Build(Mock<IMaterialiser> materialiser, PhantomDb db, ITmdbClient? tmdbClient = null)
    {
        var userData = new Mock<IUserDataManager>(MockBehavior.Loose);
        var tmdb = tmdbClient ?? new Mock<ITmdbClient>(MockBehavior.Loose).Object;
        var ingestor = new Mock<IFavouriteRecommendationIngestor>(MockBehavior.Loose);
        ingestor.Setup(i => i.IngestForFavouriteAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((int id, string type, CancellationToken _) =>
                new FavouriteRecommendationResult(id, type, true, 0, 0, 0, 0, 0, 0));
        return new UserDataSavedListener(
            userData.Object,
            _autopilot.Object,
            materialiser.Object,
            ingestor.Object,
            db,
            tmdb,
            NullLogger<UserDataSavedListener>.Instance,
            () => new PluginConfiguration { DiscoveryLanguage = "en-US" });
    }

    private PhantomDb CreateDb()
    {
        var db = new PhantomDb(_dbPath);
        // Force schema creation, mirroring the other DB-backed test fixtures.
        // Microsoft.Data.Sqlite's async API runs synchronously, so bridging the
        // one-time init here does not deadlock.
        db.SetMetaAsync("__init__", "1", CancellationToken.None).GetAwaiter().GetResult();
        return db;
    }

    /// <summary>Seed the acting user's <c>allow_eager</c> toggle (other toggles left on).</summary>
    private void SetAllowEager(Guid userId, bool allow)
    {
        _db ??= CreateDb();
        _db.UpsertUserPrefsAsync(
                userId,
                new UserPrefs(ProtectFavourites: true, ShowPhantoms: true, AllowEager: allow),
                CancellationToken.None)
            .GetAwaiter().GetResult();
    }
}
