using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Library;
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
    private readonly string _dbPath;
    private readonly Mock<ISeriesAutopilot> _autopilot = new(MockBehavior.Loose);
    private PhantomDb? _db;

    public UserDataSavedListenerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "phantom-udsl-" + Guid.NewGuid().ToString("N") + ".db");
    }

    public void Dispose()
    {
        try
        {
            _db?.Dispose();
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

    [Fact]
    public void FavouriteMovie_TriggersMaterialiseByExternalId()
    {
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var sut = Build(materialiser);
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
    public void FavouriteEpisode_TriggersMaterialiseByExternalId()
    {
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var sut = Build(materialiser);
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
    public void FavouriteSeries_DoesNotMaterialiseContainer()
    {
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var sut = Build(materialiser);
        var item = new Series
        {
            ChannelId = ChannelIds.Shows,
            ExternalId = ChannelItemId.ForSeries(200).Encode(),
        };
        var userData = new UserItemData { Key = "series_200", IsFavorite = true };

        sut.HandleSavedUserData(item, userData, Guid.NewGuid());

        materialiser.Verify(m => m.MaterialiseAsync(
            It.IsAny<int>(),
            It.IsAny<string>(),
            It.IsAny<int?>(),
            It.IsAny<int?>(),
            It.IsAny<MaterialiseTrigger>(),
            It.IsAny<CancellationToken>()), Times.Never);
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
        return new UserDataSavedListener(
            userData.Object,
            _autopilot.Object,
            materialiser.Object,
            ingestor.Object,
            _db,
            NullLogger<UserDataSavedListener>.Instance);
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
