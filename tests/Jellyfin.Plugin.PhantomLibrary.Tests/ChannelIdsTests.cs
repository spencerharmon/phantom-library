using System;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class ChannelIdsTests
{
    [Fact]
    public void Movies_Id_IsStableAcrossCalls()
    {
        var a = ChannelIds.Movies;
        var b = ChannelIds.Movies;
        Assert.Equal(a, b);
        Assert.NotEqual(Guid.Empty, a);
    }

    [Fact]
    public void Shows_Id_IsStableAcrossCalls()
    {
        var a = ChannelIds.Shows;
        var b = ChannelIds.Shows;
        Assert.Equal(a, b);
        Assert.NotEqual(Guid.Empty, a);
    }

    [Fact]
    public void MoviesAndShows_AreDistinct()
    {
        Assert.NotEqual(ChannelIds.Movies, ChannelIds.Shows);
    }

    [Fact]
    public void For_Movies_ReturnsMoviesId()
        => Assert.Equal(ChannelIds.Movies, ChannelIds.For("movies"));

    [Fact]
    public void For_Shows_ReturnsShowsId()
        => Assert.Equal(ChannelIds.Shows, ChannelIds.For("shows"));

    [Fact]
    public void For_UnknownKind_Throws()
        => Assert.Throws<ArgumentException>(() => ChannelIds.For("podcasts"));

    [Fact]
    public void IsPhantom_MoviesId_True()
        => Assert.True(ChannelIds.IsPhantom(ChannelIds.Movies));

    [Fact]
    public void IsPhantom_ShowsId_True()
        => Assert.True(ChannelIds.IsPhantom(ChannelIds.Shows));

    [Fact]
    public void IsPhantom_RandomGuid_False()
        => Assert.False(ChannelIds.IsPhantom(Guid.NewGuid()));

    [Fact]
    public void IsPhantom_EmptyGuid_False()
        => Assert.False(ChannelIds.IsPhantom(Guid.Empty));

    [Fact]
    public void Movies_Id_MatchesJellyfinRuntimeChannelId()
        => Assert.Equal(Guid.Parse("80089d10-394f-b545-b5e4-d7d56a872393"), ChannelIds.Movies);

    [Fact]
    public void Shows_Id_MatchesJellyfinRuntimeChannelId()
        => Assert.Equal(Guid.Parse("40ab6e9a-f516-a84f-46dc-ea7140855d88"), ChannelIds.Shows);
}
