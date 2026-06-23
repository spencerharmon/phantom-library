using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
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

    private static UserDataSavedListener Build(Mock<IMaterialiser> materialiser)
    {
        materialiser.Setup(m => m.MaterialiseAsync(
                It.IsAny<int>(),
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<int?>(),
                It.IsAny<MaterialiseTrigger>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(MaterialisationOutcome.Duplicate);

        var userData = new Mock<IUserDataManager>(MockBehavior.Loose);
        var autopilot = new Mock<ISeriesAutopilot>(MockBehavior.Loose);
        return new UserDataSavedListener(
            userData.Object,
            autopilot.Object,
            materialiser.Object,
            NullLogger<UserDataSavedListener>.Instance);
    }
}
