using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.PhantomLibrary.Library;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.State;
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

public class PlaybackTriggerListenerTests
{
    private sealed class FakeQueue : IMaterialisationQueue
    {
        public readonly List<(Guid id, MaterialiseTrigger trigger)> User = new();
        public readonly List<(Guid id, MaterialiseTrigger trigger)> Eager = new();

        public event EventHandler<MaterialisationLifecycleEvent>? LifecycleChanged
        {
            add { _ = value; }
            remove { _ = value; }
        }

        public int PendingUserCount => User.Count;
        public int PendingEagerCount => Eager.Count;

        public void EnqueueUser(Guid id, MaterialiseTrigger trigger) => User.Add((id, trigger));
        public void EnqueueEager(Guid id) => Eager.Add((id, MaterialiseTrigger.PreResolve));
    }

    private sealed class FakeUserDataManager : IUserDataManager
    {
        public readonly List<(Guid userId, Guid itemId, UserItemData data, UserDataSaveReason reason)> Saved = new();
        private readonly Dictionary<(Guid userId, Guid itemId), UserItemData> _store = new();

        public FakeUserDataManager(params (Guid userId, Guid itemId, UserItemData data)[] seed)
        {
            foreach (var (u, i, d) in seed) _store[(u, i)] = d;
        }

        public event EventHandler<UserDataSaveEventArgs>? UserDataSaved
        {
            add { _ = value; }
            remove { _ = value; }
        }

        public void SaveUserData(User user, BaseItem item, UserItemData userData, UserDataSaveReason reason, CancellationToken cancellationToken)
        {
            Saved.Add((user.Id, item.Id, userData, reason));
            _store[(user.Id, item.Id)] = userData;
        }

        public void SaveUserData(User user, BaseItem item, MediaBrowser.Model.Dto.UpdateUserItemDataDto userDataDto, UserDataSaveReason reason)
            => throw new NotImplementedException();

        public UserItemData GetUserData(User user, BaseItem item)
            => _store.TryGetValue((user.Id, item.Id), out var d) ? d : null!;

        public UserItemData GetUserData(User user, List<string> keys) => throw new NotImplementedException();
        public UserItemData GetUserData(Guid userId, BaseItem item) => throw new NotImplementedException();
        public UserItemData GetUserData(Guid userId, List<string> keys) => throw new NotImplementedException();
        public MediaBrowser.Model.Dto.UserItemDataDto GetUserDataDto(BaseItem item, User user) => throw new NotImplementedException();
        public MediaBrowser.Model.Dto.UserItemDataDto GetUserDataDto(BaseItem item, MediaBrowser.Model.Dto.BaseItemDto? itemDto, User user, MediaBrowser.Controller.Dto.DtoOptions options) => throw new NotImplementedException();
        public bool UpdatePlayState(BaseItem item, UserItemData data, long? reportedPositionTicks) => throw new NotImplementedException();
    }

    private static User NewUser(Guid id) => new User("test", "InternalAuth", "Reset") { Id = id };

    private static Movie NewPhantomMovie(Guid id, string name)
        => new Movie { Id = id, Name = name, Path = $"/var/lib/jellyfin/phantom-library/movies/{name}__phantom_tmdb123.mp4" };

    private static Movie NewRealMovie(Guid id, string name)
        => new Movie { Id = id, Name = name, Path = "/var/gostream/gostream-mkv-virtual/movies/Real_2025_1080p_abc.mkv" };

    private static PlaybackTriggerListener Build(FakeQueue q, IUserDataManager ud)
        => new(Mock.Of<ISessionManager>(), q, ud, null!,
               NullLogger<PlaybackTriggerListener>.Instance);

    [Fact]
    public void PlaybackStart_OnPhantomMovie_EnqueuesPlayTrigger()
    {
        var q = new FakeQueue();
        var ud = new FakeUserDataManager();
        var sut = Build(q, ud);
        var movie = NewPhantomMovie(Guid.NewGuid(), "Backrooms");

        sut.HandlePlaybackStart(new PlaybackProgressEventArgs { Item = movie });

        Assert.Single(q.User);
        Assert.Equal(movie.Id, q.User[0].id);
        Assert.Equal(MaterialiseTrigger.Play, q.User[0].trigger);
    }

    [Fact]
    public void PlaybackStart_OnRealMovie_DoesNotEnqueue()
    {
        var q = new FakeQueue();
        var ud = new FakeUserDataManager();
        var sut = Build(q, ud);
        var movie = NewRealMovie(Guid.NewGuid(), "Real");

        sut.HandlePlaybackStart(new PlaybackProgressEventArgs { Item = movie });

        Assert.Empty(q.User);
    }

    [Fact]
    public void PlaybackStart_OnPhantomSeries_DoesNotEnqueue()
    {
        var q = new FakeQueue();
        var ud = new FakeUserDataManager();
        var sut = Build(q, ud);
        var series = new Series
        {
            Id = Guid.NewGuid(),
            Name = "Severance",
            Path = "/var/lib/jellyfin/phantom-library/shows/Severance__phantom_tmdb456.mp4",
        };

        sut.HandlePlaybackStart(new PlaybackProgressEventArgs { Item = series });

        // Series-level Play does not enqueue — autopilot handles
        // episode-by-episode resolution; Series is a container.
        Assert.Empty(q.User);
    }

    [Fact]
    public void PlaybackStart_NullItem_DoesNotThrow()
    {
        var q = new FakeQueue();
        var ud = new FakeUserDataManager();
        var sut = Build(q, ud);

        sut.HandlePlaybackStart(new PlaybackProgressEventArgs { Item = null! });

        Assert.Empty(q.User);
    }

    [Fact]
    public async Task PlaybackStopped_OnPhantom_ResetsUserData()
    {
        var q = new FakeQueue();
        var user = NewUser(Guid.NewGuid());
        var movie = NewPhantomMovie(Guid.NewGuid(), "Backrooms");
        var existing = new UserItemData
        {
            Key = "k",
            PlayCount = 1,
            Played = true,
            PlaybackPositionTicks = 10_000_000L,
            LastPlayedDate = DateTime.UtcNow,
        };
        var ud = new FakeUserDataManager((user.Id, movie.Id, existing));
        var sut = Build(q, ud);

        sut.HandlePlaybackStopped(new PlaybackStopEventArgs
        {
            Item = movie,
            Users = new List<User> { user },
        });

        // SaveUserData fires on a Task.Run background; wait briefly.
        for (var i = 0; i < 50 && ud.Saved.Count == 0; i++) await Task.Delay(50);

        Assert.Single(ud.Saved);
        var saved = ud.Saved[0];
        Assert.Equal(user.Id, saved.userId);
        Assert.Equal(movie.Id, saved.itemId);
        Assert.Equal(0, saved.data.PlayCount);
        Assert.False(saved.data.Played);
        Assert.Equal(0, saved.data.PlaybackPositionTicks);
        Assert.Null(saved.data.LastPlayedDate);
    }

    [Fact]
    public async Task PlaybackStopped_OnRealMovie_DoesNotTouchUserData()
    {
        var q = new FakeQueue();
        var user = NewUser(Guid.NewGuid());
        var movie = NewRealMovie(Guid.NewGuid(), "Real");
        var existing = new UserItemData { Key = "k", PlayCount = 1, Played = true };
        var ud = new FakeUserDataManager((user.Id, movie.Id, existing));
        var sut = Build(q, ud);

        sut.HandlePlaybackStopped(new PlaybackStopEventArgs
        {
            Item = movie,
            Users = new List<User> { user },
        });

        await Task.Delay(200);
        Assert.Empty(ud.Saved);
    }

    [Fact]
    public async Task PlaybackStopped_OnPhantomWithCleanUserData_DoesNotSave()
    {
        var q = new FakeQueue();
        var user = NewUser(Guid.NewGuid());
        var movie = NewPhantomMovie(Guid.NewGuid(), "Backrooms");
        var existing = new UserItemData { Key = "k", PlayCount = 0, Played = false };
        var ud = new FakeUserDataManager((user.Id, movie.Id, existing));
        var sut = Build(q, ud);

        sut.HandlePlaybackStopped(new PlaybackStopEventArgs
        {
            Item = movie,
            Users = new List<User> { user },
        });

        await Task.Delay(200);
        Assert.Empty(ud.Saved);
    }
}
