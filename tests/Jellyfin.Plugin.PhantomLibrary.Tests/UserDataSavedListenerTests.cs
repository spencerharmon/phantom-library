using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class UserDataSavedListenerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PhantomDb _db;
    private readonly Mock<IUserDataManager> _ud = new();
    private readonly Mock<IMaterialisationQueue> _queue = new();
    private readonly Mock<ISeriesAutopilot> _autopilot = new();
    private readonly Mock<IGostreamClient> _gostream = new();

    public UserDataSavedListenerTests()
    {
        _dbPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "udl_" + Guid.NewGuid().ToString("N") + ".db");
        _db = new PhantomDb(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { System.IO.File.Delete(_dbPath); System.IO.File.Delete(_dbPath + "-wal"); System.IO.File.Delete(_dbPath + "-shm"); } catch { }
    }

    private UserDataSavedListener Build() => new(
        _ud.Object, _queue.Object, _autopilot.Object, _gostream.Object, _db,
        NullLogger<UserDataSavedListener>.Instance);

    [Fact]
    public async Task FavouriteToTrue_OnMaterialisedItem_VaultPresent_Prestages()
    {
        var movie = new Movie { Name = "X", Path = "/fake/p.mkv" };
        movie.Id = Guid.NewGuid();
        await _db.UpsertPhantomItemAsync(movie.Id, new PhantomItemRow
        {
            Type = "movie", State = PhantomItemState.Materialised,
            FirstSeen = DateTimeOffset.UtcNow, LastTouched = DateTimeOffset.UtcNow,
            StubPath = "/r/x.mkv", FusePath = "/f/x.mkv",
        }, default);
        _gostream.Setup(g => g.IsVaultModePresentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var listener = Build();
        await listener.StartAsync(CancellationToken.None);

        _ud.Raise(u => u.UserDataSaved += null, new UserDataSaveEventArgs
        {
            UserId = Guid.NewGuid(),
            Item = movie,
            UserData = new UserItemData { Key = "k", IsFavorite = true },
            SaveReason = UserDataSaveReason.UpdateUserData,
        });

        for (var i = 0; i < 50; i++) { await Task.Delay(20); }
        _gostream.Verify(g => g.PrestageAsync("/r/x.mkv", 50, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FavouriteToFalse_OnMaterialisedItem_VaultPresent_Unprestages_NoEnqueueEviction()
    {
        var movie = new Movie { Name = "X", Path = "/fake/p.mkv" };
        movie.Id = Guid.NewGuid();
        await _db.UpsertPhantomItemAsync(movie.Id, new PhantomItemRow
        {
            Type = "movie", State = PhantomItemState.Materialised,
            FirstSeen = DateTimeOffset.UtcNow, LastTouched = DateTimeOffset.UtcNow,
            StubPath = "/r/x.mkv",
        }, default);
        _gostream.Setup(g => g.IsVaultModePresentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var listener = Build();
        await listener.StartAsync(CancellationToken.None);

        var userId = Guid.NewGuid();
        // First raise favourite=true so the listener records "prev = true".
        _ud.Raise(u => u.UserDataSaved += null, new UserDataSaveEventArgs
        {
            UserId = userId, Item = movie,
            UserData = new UserItemData { Key = "k", IsFavorite = true },
            SaveReason = UserDataSaveReason.UpdateUserData,
        });
        for (var i = 0; i < 25; i++) { await Task.Delay(20); }

        // Then favourite=false: transition triggers unprestage.
        _ud.Raise(u => u.UserDataSaved += null, new UserDataSaveEventArgs
        {
            UserId = userId, Item = movie,
            UserData = new UserItemData { Key = "k", IsFavorite = false },
            SaveReason = UserDataSaveReason.UpdateUserData,
        });
        for (var i = 0; i < 50; i++) { await Task.Delay(20); }

        _gostream.Verify(g => g.UnprestageAsync("/r/x.mkv", It.IsAny<CancellationToken>()), Times.Once);
        // No immediate eviction enqueue on un-favourite.
        _queue.Verify(q => q.EnqueueUser(It.IsAny<Guid>(), MaterialiseTrigger.Favourite), Times.Never);
    }

    [Fact]
    public async Task FavouriteToTrue_VaultAbsent_NoPrestage()
    {
        var movie = new Movie { Name = "X", Path = "/fake/p.mkv" };
        movie.Id = Guid.NewGuid();
        await _db.UpsertPhantomItemAsync(movie.Id, new PhantomItemRow
        {
            Type = "movie", State = PhantomItemState.Materialised,
            FirstSeen = DateTimeOffset.UtcNow, LastTouched = DateTimeOffset.UtcNow,
            StubPath = "/r/x.mkv",
        }, default);
        _gostream.Setup(g => g.IsVaultModePresentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var listener = Build();
        await listener.StartAsync(CancellationToken.None);

        _ud.Raise(u => u.UserDataSaved += null, new UserDataSaveEventArgs
        {
            UserId = Guid.NewGuid(), Item = movie,
            UserData = new UserItemData { Key = "k", IsFavorite = true },
            SaveReason = UserDataSaveReason.UpdateUserData,
        });
        for (var i = 0; i < 50; i++) { await Task.Delay(20); }

        _gostream.Verify(g => g.PrestageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _gostream.Verify(g => g.UnprestageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FavouriteToTrue_PhantomPath_StillEnqueuesMaterialise()
    {
        // M11 issue: M10 set Path to the symlink stub, which made
        // IsMaterialisable return false because items "with a path"
        // were treated as already-materialised. Fix in
        // UserDataSavedListener: items whose Path matches the
        // PhantomStubManager sentinel are still phantoms and must
        // enqueue when favourited.
        var movie = new Movie
        {
            Name = "Backrooms",
            Path = "/var/lib/jellyfin/phantom-library/movies/Backrooms__phantom_tmdb1083381.mp4",
        };
        movie.Id = Guid.NewGuid();
        // No PhantomDb row; Vault hand-off path no-ops; this isolates
        // the enqueue-on-favourite-transition behaviour.
        _gostream.Setup(g => g.IsVaultModePresentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var listener = Build();
        await listener.StartAsync(CancellationToken.None);

        _ud.Raise(u => u.UserDataSaved += null, new UserDataSaveEventArgs
        {
            UserId = Guid.NewGuid(), Item = movie,
            UserData = new UserItemData { Key = "k", IsFavorite = true },
            SaveReason = UserDataSaveReason.UpdateUserData,
        });
        for (var i = 0; i < 50 && _queue.Invocations.Count == 0; i++) await Task.Delay(20);

        _queue.Verify(q => q.EnqueueUser(movie.Id, MaterialiseTrigger.Favourite), Times.Once);
    }

    [Fact]
    public async Task FavouriteToTrue_RealGostreamPath_DoesNotEnqueue()
    {
        // Materialised items (real gostream fuse path, no phantom
        // sentinel) must NOT be re-enqueued on favourite; they are
        // already real. Vault prestage still fires via the separate
        // hand-off branch and is covered above.
        var movie = new Movie
        {
            Name = "Real",
            Path = "/var/gostream/gostream-mkv-virtual/movies/Real_2025_1080p_abc.mkv",
        };
        movie.Id = Guid.NewGuid();
        _gostream.Setup(g => g.IsVaultModePresentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var listener = Build();
        await listener.StartAsync(CancellationToken.None);

        _ud.Raise(u => u.UserDataSaved += null, new UserDataSaveEventArgs
        {
            UserId = Guid.NewGuid(), Item = movie,
            UserData = new UserItemData { Key = "k", IsFavorite = true },
            SaveReason = UserDataSaveReason.UpdateUserData,
        });
        await Task.Delay(200);

        _queue.Verify(q => q.EnqueueUser(It.IsAny<Guid>(), MaterialiseTrigger.Favourite), Times.Never);
    }
}
