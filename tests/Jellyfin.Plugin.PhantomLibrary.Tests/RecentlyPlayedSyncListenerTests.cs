using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// Regression coverage for the recently-played-fix operator bug: a
/// materialised phantom movie/episode must persist Played + LastPlayedDate
/// (the standard-surfaces "recently played" contract) on playback stop, even
/// though the item stays <see cref="SourceType.Channel"/> forever and its
/// BaseItem snapshot cached at PlaybackStart may predate the channel's
/// runtime backfill / materialise completing.
/// </summary>
public sealed class RecentlyPlayedSyncListenerTests
{
    public RecentlyPlayedSyncListenerTests()
    {
        // Video.SourceType consults a static IRecordingsManager; stub it so
        // constructing a Movie/Episode in-test doesn't NRE.
        var recordings = new Mock<MediaBrowser.Controller.LiveTv.IRecordingsManager>(MockBehavior.Loose);
        MediaBrowser.Controller.Entities.Video.RecordingsManager = recordings.Object;
    }

    private static RecentlyPlayedSyncListener Build(
        Mock<ISessionManager> sessions,
        Mock<ILibraryManager> libraryManager,
        Mock<IUserDataManager> userData)
    {
        return new RecentlyPlayedSyncListener(
            sessions.Object,
            libraryManager.Object,
            userData.Object,
            NullLogger<RecentlyPlayedSyncListener>.Instance);
    }

    private static Movie MaterialisedMovie(Guid id, long runTimeTicks)
    {
        return new Movie
        {
            Id = id,
            ChannelId = ChannelIds.Movies,
            ExternalId = ChannelItemId.ForMovie(99000001).Encode(),
            Tags = Array.Empty<string>(),
            RunTimeTicks = runTimeTicks,
        };
    }

    [Fact]
    public void PlaybackStopped_MaterialisedMoviePlayedToCompletion_PersistsPlayedAndDatePlayed()
    {
        var sessions = new Mock<ISessionManager>(MockBehavior.Loose);
        var libraryManager = new Mock<ILibraryManager>(MockBehavior.Loose);
        var userData = new Mock<IUserDataManager>(MockBehavior.Loose);

        var itemId = Guid.NewGuid();
        // BaseItem snapshot cached at PlaybackStart time: stale/zero runtime,
        // as it would be before the channel's runtime backfill lands.
        var staleItem = MaterialisedMovie(itemId, runTimeTicks: 0);
        // Fresh re-resolve at PlaybackStopped time: the real, backfilled runtime.
        var freshItem = MaterialisedMovie(itemId, runTimeTicks: 57000000000);

        libraryManager.Setup(l => l.GetItemById(itemId)).Returns(freshItem);

        var user = new User("tester", "auth", "reset") { Id = Guid.NewGuid() };
        var existingUserData = new UserItemData { Key = "movie_99000001" };
        userData.Setup(u => u.GetUserData(user, freshItem)).Returns(existingUserData);

        var sut = Build(sessions, libraryManager, userData);
        sut.StartAsync(CancellationToken.None);

        var args = new PlaybackStopEventArgs
        {
            Item = staleItem,
            Users = new List<User> { user },
            PlaybackPositionTicks = 0,
            PlayedToCompletion = true,
        };

        sessions.Raise(s => s.PlaybackStopped += null, sessions.Object, args);

        userData.Verify(
            u => u.SaveUserData(
                user,
                freshItem,
                It.Is<UserItemData>(d => d.Played && d.PlaybackPositionTicks == 0),
                UserDataSaveReason.PlaybackFinished,
                It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.True(existingUserData.Played);
        Assert.True((DateTime.UtcNow - existingUserData.LastPlayedDate!.Value) < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void PlaybackStopped_PartialProgress_DoesNotMarkPlayed()
    {
        var sessions = new Mock<ISessionManager>(MockBehavior.Loose);
        var libraryManager = new Mock<ILibraryManager>(MockBehavior.Loose);
        var userData = new Mock<IUserDataManager>(MockBehavior.Loose);

        var itemId = Guid.NewGuid();
        var item = MaterialisedMovie(itemId, runTimeTicks: 57000000000);
        libraryManager.Setup(l => l.GetItemById(itemId)).Returns(item);

        var sut = Build(sessions, libraryManager, userData);
        sut.StartAsync(CancellationToken.None);

        var user = new User("tester", "auth", "reset") { Id = Guid.NewGuid() };
        var args = new PlaybackStopEventArgs
        {
            Item = item,
            Users = new List<User> { user },
            PlaybackPositionTicks = 12000000000, // ~20 minutes of ~95
            PlayedToCompletion = false,
        };

        sessions.Raise(s => s.PlaybackStopped += null, sessions.Object, args);

        userData.Verify(
            u => u.SaveUserData(
                It.IsAny<User>(),
                It.IsAny<BaseItem>(),
                It.IsAny<UserItemData>(),
                It.IsAny<UserDataSaveReason>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void PlaybackStopped_StillPhantomSplash_IsIgnored()
    {
        var sessions = new Mock<ISessionManager>(MockBehavior.Loose);
        var libraryManager = new Mock<ILibraryManager>(MockBehavior.Loose);
        var userData = new Mock<IUserDataManager>(MockBehavior.Loose);

        var item = new Movie
        {
            Id = Guid.NewGuid(),
            ChannelId = ChannelIds.Movies,
            ExternalId = ChannelItemId.ForMovie(99000001).Encode(),
            Tags = new[] { "phantom" },
        };

        var sut = Build(sessions, libraryManager, userData);
        sut.StartAsync(CancellationToken.None);

        var user = new User("tester", "auth", "reset") { Id = Guid.NewGuid() };
        var args = new PlaybackStopEventArgs
        {
            Item = item,
            Users = new List<User> { user },
            PlaybackPositionTicks = 0,
            PlayedToCompletion = true,
        };

        sessions.Raise(s => s.PlaybackStopped += null, sessions.Object, args);

        libraryManager.Verify(l => l.GetItemById(It.IsAny<Guid>()), Times.Never);
        userData.Verify(
            u => u.SaveUserData(
                It.IsAny<User>(),
                It.IsAny<BaseItem>(),
                It.IsAny<UserItemData>(),
                It.IsAny<UserDataSaveReason>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void PlaybackStopped_NonPhantomItem_IsIgnored()
    {
        var sessions = new Mock<ISessionManager>(MockBehavior.Loose);
        var libraryManager = new Mock<ILibraryManager>(MockBehavior.Loose);
        var userData = new Mock<IUserDataManager>(MockBehavior.Loose);

        var item = new Movie { Id = Guid.NewGuid() };

        var sut = Build(sessions, libraryManager, userData);
        sut.StartAsync(CancellationToken.None);

        var user = new User("tester", "auth", "reset") { Id = Guid.NewGuid() };
        var args = new PlaybackStopEventArgs
        {
            Item = item,
            Users = new List<User> { user },
            PlaybackPositionTicks = 0,
            PlayedToCompletion = true,
        };

        sessions.Raise(s => s.PlaybackStopped += null, sessions.Object, args);

        userData.Verify(
            u => u.SaveUserData(
                It.IsAny<User>(),
                It.IsAny<BaseItem>(),
                It.IsAny<UserItemData>(),
                It.IsAny<UserDataSaveReason>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
