using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public sealed class UserDataSavedListenerTests
{
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

    // ---- Vault Mode: prestage on favourite, unprestage on de-favourite,
    //      gated on the discrete user-metadata save reasons (movie + episode parity) ----

    [Fact]
    public void UpdateUserRating_FavouriteMovie_PrestagesVault()
    {
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var vault = new Mock<IVaultManager>(MockBehavior.Loose);
        var sut = Build(materialiser, vault);
        var item = new Movie
        {
            ChannelId = ChannelIds.Movies,
            ExternalId = ChannelItemId.ForMovie(42).Encode(),
        };
        var userData = new UserItemData { Key = "movie_42", IsFavorite = true };

        sut.HandleSavedUserData(item, userData, Guid.NewGuid(), UserDataSaveReason.UpdateUserRating);

        vault.Verify(v => v.PrestageAsync(42, "movie", null, null, It.IsAny<CancellationToken>()), Times.Once);
        vault.Verify(v => v.UnprestageAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void UpdateUserRating_FavouriteEpisode_PrestagesVault()
    {
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var vault = new Mock<IVaultManager>(MockBehavior.Loose);
        var sut = Build(materialiser, vault);
        var item = new Episode
        {
            ChannelId = ChannelIds.Shows,
            ExternalId = ChannelItemId.ForEpisode(200, 1, 2).Encode(),
        };
        var userData = new UserItemData { Key = "episode_200_s01e02", IsFavorite = true };

        sut.HandleSavedUserData(item, userData, Guid.NewGuid(), UserDataSaveReason.UpdateUserRating);

        vault.Verify(v => v.PrestageAsync(200, "episode", 1, 2, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void UpdateUserRating_UnfavouriteMovie_UnprestagesVault()
    {
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var vault = new Mock<IVaultManager>(MockBehavior.Loose);
        var sut = Build(materialiser, vault);
        var item = new Movie
        {
            ChannelId = ChannelIds.Movies,
            ExternalId = ChannelItemId.ForMovie(42).Encode(),
        };
        var userData = new UserItemData { Key = "movie_42", IsFavorite = false };

        sut.HandleSavedUserData(item, userData, Guid.NewGuid(), UserDataSaveReason.UpdateUserRating);

        vault.Verify(v => v.UnprestageAsync(42, "movie", null, null, It.IsAny<CancellationToken>()), Times.Once);
        vault.Verify(v => v.PrestageAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void UpdateUserRating_UnfavouriteEpisode_UnprestagesVault()
    {
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var vault = new Mock<IVaultManager>(MockBehavior.Loose);
        var sut = Build(materialiser, vault);
        var item = new Episode
        {
            ChannelId = ChannelIds.Shows,
            ExternalId = ChannelItemId.ForEpisode(200, 1, 2).Encode(),
        };
        var userData = new UserItemData { Key = "episode_200_s01e02", IsFavorite = false };

        sut.HandleSavedUserData(item, userData, Guid.NewGuid(), UserDataSaveReason.UpdateUserRating);

        vault.Verify(v => v.UnprestageAsync(200, "episode", 1, 2, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void UpdateUserData_FavouriteMovie_PrestagesVault()
    {
        // The bulk user-data API (ItemsController UpdateUserItemData) emits
        // UpdateUserData rather than UpdateUserRating; it must also drive the vault.
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var vault = new Mock<IVaultManager>(MockBehavior.Loose);
        var sut = Build(materialiser, vault);
        var item = new Movie
        {
            ChannelId = ChannelIds.Movies,
            ExternalId = ChannelItemId.ForMovie(42).Encode(),
        };
        var userData = new UserItemData { Key = "movie_42", IsFavorite = true };

        sut.HandleSavedUserData(item, userData, Guid.NewGuid(), UserDataSaveReason.UpdateUserData);

        vault.Verify(v => v.PrestageAsync(42, "movie", null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void PlaybackProgress_FavouriteMovie_DoesNotTouchVault()
    {
        // A single watch fires many PlaybackProgress saves; the vault must not
        // react to them or it would spam gostream once per progress tick.
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var vault = new Mock<IVaultManager>(MockBehavior.Loose);
        var sut = Build(materialiser, vault);
        var item = new Movie
        {
            ChannelId = ChannelIds.Movies,
            ExternalId = ChannelItemId.ForMovie(42).Encode(),
        };
        var userData = new UserItemData { Key = "movie_42", IsFavorite = true };

        sut.HandleSavedUserData(item, userData, Guid.NewGuid(), UserDataSaveReason.PlaybackProgress);

        vault.Verify(v => v.PrestageAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
        vault.Verify(v => v.UnprestageAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void NullReason_FavouriteMovie_DoesNotTouchVault()
    {
        // The 3-arg overload (unknown reason) still materialises but must not
        // drive the vault, since it cannot distinguish a metadata edit from a
        // playback tick.
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var vault = new Mock<IVaultManager>(MockBehavior.Loose);
        var sut = Build(materialiser, vault);
        var item = new Movie
        {
            ChannelId = ChannelIds.Movies,
            ExternalId = ChannelItemId.ForMovie(42).Encode(),
        };
        var userData = new UserItemData { Key = "movie_42", IsFavorite = true };

        sut.HandleSavedUserData(item, userData, Guid.NewGuid());

        vault.Verify(v => v.PrestageAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void UpdateUserRating_FavouriteSeries_DoesNotTouchVault()
    {
        // Series/season containers have no vault footprint (only movies and
        // episodes are prestaged); the container save must be inert.
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        var vault = new Mock<IVaultManager>(MockBehavior.Loose);
        var sut = Build(materialiser, vault);
        var item = new Series
        {
            ChannelId = ChannelIds.Shows,
            ExternalId = ChannelItemId.ForSeries(200).Encode(),
        };
        var userData = new UserItemData { Key = "series_200", IsFavorite = true };

        sut.HandleSavedUserData(item, userData, Guid.NewGuid(), UserDataSaveReason.UpdateUserRating);

        vault.Verify(v => v.PrestageAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
        vault.Verify(v => v.UnprestageAsync(
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static UserDataSavedListener Build(Mock<IMaterialiser> materialiser, Mock<IVaultManager>? vault = null)
    {
        materialiser.Setup(m => m.MaterialiseAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<MaterialiseTrigger>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(MaterialisationOutcome.Duplicate);

        vault ??= new Mock<IVaultManager>(MockBehavior.Loose);
        vault.Setup(v => v.PrestageAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        vault.Setup(v => v.UnprestageAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var userData = new Mock<IUserDataManager>(MockBehavior.Loose);
        var autopilot = new Mock<ISeriesAutopilot>(MockBehavior.Loose);
        return new UserDataSavedListener(
            userData.Object,
            autopilot.Object,
            materialiser.Object,
            vault.Object,
            NullLogger<UserDataSavedListener>.Instance);
    }
}
