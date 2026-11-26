using System;
using System.Collections.Generic;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Swarm;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>The custom metadata provider re-applies the Peers score after core's refresh merge.</summary>
[Collection("SwarmHealthCache")]
public sealed class SwarmHealthProviderTests : IDisposable
{
    public SwarmHealthProviderTests()
        => SwarmHealthCache.Set(true, new Dictionary<int, int> { [10] = 42, [11] = 0 }, new Dictionary<int, int> { [20] = 7 });

    public void Dispose() => SwarmHealthCache.Set(false, new Dictionary<int, int>(), new Dictionary<int, int>());

    [Fact]
    public void RestoresScoreWipedByRefresh_ForPhantomMoviesAndSeries()
    {
        var movie = new Movie { ChannelId = ChannelIds.Movies, ExternalId = "movie_10", CriticRating = null };
        var dead = new Movie { ChannelId = ChannelIds.Movies, ExternalId = "movie_11" };
        var unmeasured = new Movie { ChannelId = ChannelIds.Movies, ExternalId = "movie_12" };
        var series = new Series { ChannelId = ChannelIds.Shows, ExternalId = "series_20" };

        Assert.Equal(ItemUpdateType.MetadataEdit, SwarmHealthMetadataProvider.Apply(movie, series: false));
        SwarmHealthMetadataProvider.Apply(dead, series: false);
        SwarmHealthMetadataProvider.Apply(unmeasured, series: false);
        SwarmHealthMetadataProvider.Apply(series, series: true);

        Assert.Equal(42f, movie.CriticRating);
        Assert.Equal(0f, dead.CriticRating);
        Assert.Equal(SwarmHealthScore.UnmeasuredScore, unmeasured.CriticRating);
        Assert.Equal(7f, series.CriticRating);
        Assert.Equal(ItemUpdateType.None, SwarmHealthMetadataProvider.Apply(movie, series: false));
    }

    [Fact]
    public void LeavesNonPhantomItemsAndDisabledStateAlone()
    {
        var library = new Movie { ChannelId = Guid.Empty, ExternalId = "movie_10", CriticRating = 88f };
        Assert.Equal(ItemUpdateType.None, SwarmHealthMetadataProvider.Apply(library, series: false));
        Assert.Equal(88f, library.CriticRating);

        SwarmHealthCache.Set(false, new Dictionary<int, int>(), new Dictionary<int, int>());
        var phantom = new Movie { ChannelId = ChannelIds.Movies, ExternalId = "movie_10" };
        Assert.Equal(ItemUpdateType.None, SwarmHealthMetadataProvider.Apply(phantom, series: false));
        Assert.Null(phantom.CriticRating);
    }
}
