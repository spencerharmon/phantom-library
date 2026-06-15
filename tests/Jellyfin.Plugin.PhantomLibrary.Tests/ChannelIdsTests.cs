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

    /// <summary>
    /// Regression test for the algorithm match against Jellyfin's
    /// <c>LibraryManager.GetNewItemId</c>. Computed independently here
    /// against the exact expected MD5 of the canonical concatenation:
    /// type FullName + lowercased "channel phantom movies".
    /// </summary>
    [Fact]
    public void Movies_Id_MatchesExpectedMd5()
    {
        // ChannelManager calls GetInternalChannelId("Phantom Movies")
        //   → LibraryManager.GetNewItemId("Channel Phantom Movies", typeof(Channel))
        //   → lowercased: "channel phantom movies"
        //   → prepended with "MediaBrowser.Controller.Channels.Channel"
        //   → final string: "MediaBrowser.Controller.Channels.Channelchannel phantom movies"
        //   → MD5 of UTF-16 encoded bytes → Guid
        var input = "MediaBrowser.Controller.Channels.Channelchannel phantom movies";
#pragma warning disable CA5351
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.Unicode.GetBytes(input));
#pragma warning restore CA5351
        var expected = new Guid(bytes);
        Assert.Equal(expected, ChannelIds.Movies);
    }

    [Fact]
    public void Shows_Id_MatchesExpectedMd5()
    {
        var input = "MediaBrowser.Controller.Channels.Channelchannel phantom shows";
#pragma warning disable CA5351
        var bytes = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.Unicode.GetBytes(input));
#pragma warning restore CA5351
        var expected = new Guid(bytes);
        Assert.Equal(expected, ChannelIds.Shows);
    }
}
