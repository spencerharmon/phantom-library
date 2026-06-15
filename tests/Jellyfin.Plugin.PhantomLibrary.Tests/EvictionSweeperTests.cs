using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class EvictionSweeperTests : IDisposable
{
    private readonly string _dbPath;

    public EvictionSweeperTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "phantom-evict-" + Guid.NewGuid().ToString("N") + ".db");
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
        catch
        {
            // best-effort
        }
    }

    private async Task<PhantomDb> NewDbAsync()
    {
        var db = new PhantomDb(_dbPath);
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        return db;
    }

    /// <summary>Backdate a materialised_state row's <c>materialised_at</c> column for "young row" testing.</summary>
    private async Task BackdateMaterialisedAtAsync(int tmdbId, string type, int season, int episode, DateTimeOffset newWhen)
    {
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE materialised_state SET materialised_at=$t WHERE tmdb_id=$id AND type=$type AND season=$s AND episode=$e;";
        cmd.Parameters.AddWithValue("$t", newWhen.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$id", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$s", season);
        cmd.Parameters.AddWithValue("$e", episode);
        await cmd.ExecuteNonQueryAsync();
    }

    private static User MakeUser(string name = "alice")
    {
        // Two-arg ctor variant isn't public; the three-arg one is.
        return new User(name, "InternalProvider", "InternalReset")
        {
            Id = Guid.NewGuid(),
        };
    }

    private (EvictionSweeper sut,
             Mock<IGostreamClient> gostream,
             Mock<ILibraryManager> lib,
             Mock<IUserManager> userMgr,
             Mock<IUserDataManager> userData,
             Mock<IChannelItemRefreshManager> refresh,
             ChannelStateProvider state,
             PluginConfiguration cfg) BuildSut(PhantomDb db, IEnumerable<User>? users = null)
    {
        var gostream = new Mock<IGostreamClient>(MockBehavior.Loose);
        gostream.Setup(g => g.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var lib = new Mock<ILibraryManager>(MockBehavior.Loose);
        var userMgr = new Mock<IUserManager>(MockBehavior.Loose);
        var userList = (users ?? new[] { MakeUser() }).ToArray();
        userMgr.Setup(u => u.GetUsers()).Returns(userList);

        var userData = new Mock<IUserDataManager>(MockBehavior.Loose);
        userData.Setup(u => u.GetUserData(It.IsAny<User>(), It.IsAny<BaseItem>())).Returns((UserItemData?)null);

        var refresh = new Mock<IChannelItemRefreshManager>(MockBehavior.Loose);
        refresh.Setup(r => r.RefreshChannelItemAsync(
                It.IsAny<Guid>(), It.IsAny<string>(),
                It.IsAny<ChannelItemRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var state = new ChannelStateProvider(db);

        var cfg = new PluginConfiguration
        {
            EvictionEnabled = true,
            EvictionIdleDays = 30,
            ProtectFavourites = true,
            EvictionScheduleCron = "0 4 * * *",
        };

        var sut = new EvictionSweeper(
            db, gostream.Object, lib.Object,
            userMgr.Object, userData.Object,
            refresh.Object, state,
            NullLogger<EvictionSweeper>.Instance,
            () => cfg);

        return (sut, gostream, lib, userMgr, userData, refresh, state, cfg);
    }

    private static void SetupExternalIdLookup(Mock<ILibraryManager> lib, string externalId, BaseItem item)
    {
        lib.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ExternalId == externalId)))
            .Returns(new[] { item });
    }

    private static Movie MakeChannelMovie(int tmdb)
    {
        return new Movie
        {
            Id = Guid.NewGuid(),
            ExternalId = ChannelItemId.ForMovie(tmdb).Encode(),
            ChannelId = ChannelIds.Movies,
            Name = "Test Movie",
        };
    }

    private static Episode MakeChannelEpisode(int tmdb, int season, int episode)
    {
        return new Episode
        {
            Id = Guid.NewGuid(),
            ExternalId = ChannelItemId.ForEpisode(tmdb, season, episode).Encode(),
            ChannelId = ChannelIds.Shows,
            Name = "Test Episode",
        };
    }

    // ---- happy path ----

    [Fact]
    public async Task IdleMovie_NeverPlayed_OldEnough_EvictsCleanly()
    {
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(42, "movie", -1, -1, "/stub/a.mkv", "/fuse/a.mkv", CancellationToken.None);
        await BackdateMaterialisedAtAsync(42, "movie", -1, -1, DateTimeOffset.UtcNow.AddDays(-45));

        var (sut, gostream, lib, _, _, refresh, _, _) = BuildSut(db);
        var item = MakeChannelMovie(42);
        SetupExternalIdLookup(lib, item.ExternalId, item);

        await sut.RunOnceAsync(CancellationToken.None);

        gostream.Verify(g => g.RemoveAsync("/stub/a.mkv", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(await db.GetMaterialisedStateAsync(42, "movie", -1, -1, CancellationToken.None));
        refresh.Verify(r => r.RefreshChannelItemAsync(
            ChannelIds.Movies, item.ExternalId,
            It.Is<ChannelItemRefreshOptions>(o => o.ForceUpdate && !o.ForceProbe && o.InvalidateMediaInfoCache),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task IdleMovie_LastPlayedLongAgo_Evicts()
    {
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(43, "movie", -1, -1, "/stub/b.mkv", "/fuse/b.mkv", CancellationToken.None);

        var (sut, gostream, lib, _, userData, _, _, _) = BuildSut(db);
        var item = MakeChannelMovie(43);
        SetupExternalIdLookup(lib, item.ExternalId, item);

        userData.Setup(u => u.GetUserData(It.IsAny<User>(), item)).Returns(new UserItemData
        {
            Key = "k",
            LastPlayedDate = DateTime.UtcNow.AddDays(-45),
            IsFavorite = false,
        });

        await sut.RunOnceAsync(CancellationToken.None);

        gostream.Verify(g => g.RemoveAsync("/stub/b.mkv", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(await db.GetMaterialisedStateAsync(43, "movie", -1, -1, CancellationToken.None));
    }

    // ---- protection cases ----

    [Fact]
    public async Task FavouriteProtected_NoEviction()
    {
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(44, "movie", -1, -1, "/stub/c.mkv", "/fuse/c.mkv", CancellationToken.None);
        await BackdateMaterialisedAtAsync(44, "movie", -1, -1, DateTimeOffset.UtcNow.AddDays(-90));

        var (sut, gostream, lib, _, userData, refresh, _, _) = BuildSut(db);
        var item = MakeChannelMovie(44);
        SetupExternalIdLookup(lib, item.ExternalId, item);

        userData.Setup(u => u.GetUserData(It.IsAny<User>(), item)).Returns(new UserItemData
        {
            Key = "k",
            IsFavorite = true,
        });

        await sut.RunOnceAsync(CancellationToken.None);

        gostream.Verify(g => g.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        refresh.Verify(r => r.RefreshChannelItemAsync(
            It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<ChannelItemRefreshOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.NotNull(await db.GetMaterialisedStateAsync(44, "movie", -1, -1, CancellationToken.None));
    }

    [Fact]
    public async Task RecentlyPlayed_NoEviction()
    {
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(45, "movie", -1, -1, "/stub/d.mkv", "/fuse/d.mkv", CancellationToken.None);
        await BackdateMaterialisedAtAsync(45, "movie", -1, -1, DateTimeOffset.UtcNow.AddDays(-90));

        var (sut, gostream, lib, _, userData, _, _, _) = BuildSut(db);
        var item = MakeChannelMovie(45);
        SetupExternalIdLookup(lib, item.ExternalId, item);

        userData.Setup(u => u.GetUserData(It.IsAny<User>(), item)).Returns(new UserItemData
        {
            Key = "k",
            LastPlayedDate = DateTime.UtcNow.AddDays(-3),
            IsFavorite = false,
        });

        await sut.RunOnceAsync(CancellationToken.None);

        gostream.Verify(g => g.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.NotNull(await db.GetMaterialisedStateAsync(45, "movie", -1, -1, CancellationToken.None));
    }

    [Fact]
    public async Task RecentlyMaterialised_NeverPlayed_NoEviction()
    {
        using var db = await NewDbAsync();
        // Default materialised_at = now → well within the 30-day idle window.
        await db.InsertMaterialisedStateAsync(46, "movie", -1, -1, "/stub/e.mkv", "/fuse/e.mkv", CancellationToken.None);

        var (sut, gostream, lib, _, _, _, _, _) = BuildSut(db);
        var item = MakeChannelMovie(46);
        SetupExternalIdLookup(lib, item.ExternalId, item);

        await sut.RunOnceAsync(CancellationToken.None);

        gostream.Verify(g => g.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.NotNull(await db.GetMaterialisedStateAsync(46, "movie", -1, -1, CancellationToken.None));
    }

    // ---- failure mode: gostream.RemoveAsync throws → state row stays, no refresh ----

    [Fact]
    public async Task GostreamRemoveFails_StateRowStays_NoRefresh()
    {
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(47, "movie", -1, -1, "/stub/f.mkv", "/fuse/f.mkv", CancellationToken.None);
        await BackdateMaterialisedAtAsync(47, "movie", -1, -1, DateTimeOffset.UtcNow.AddDays(-60));

        var (sut, gostream, lib, _, _, refresh, _, _) = BuildSut(db);
        gostream.Reset();
        gostream.Setup(g => g.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("gostream down"));

        var item = MakeChannelMovie(47);
        SetupExternalIdLookup(lib, item.ExternalId, item);

        await sut.RunOnceAsync(CancellationToken.None);

        Assert.NotNull(await db.GetMaterialisedStateAsync(47, "movie", -1, -1, CancellationToken.None));
        refresh.Verify(r => r.RefreshChannelItemAsync(
            It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<ChannelItemRefreshOptions>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- orphan: ILibraryManager returns nothing ----

    [Fact]
    public async Task OrphanStateRow_NoBaseItem_LogsAndSkips()
    {
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(48, "movie", -1, -1, "/stub/g.mkv", "/fuse/g.mkv", CancellationToken.None);
        await BackdateMaterialisedAtAsync(48, "movie", -1, -1, DateTimeOffset.UtcNow.AddDays(-60));

        var (sut, gostream, lib, _, _, refresh, _, _) = BuildSut(db);
        // No SetupExternalIdLookup → default returns null/empty.
        lib.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(Array.Empty<BaseItem>());

        await sut.RunOnceAsync(CancellationToken.None);

        gostream.Verify(g => g.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        refresh.Verify(r => r.RefreshChannelItemAsync(
            It.IsAny<Guid>(), It.IsAny<string>(),
            It.IsAny<ChannelItemRefreshOptions>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.NotNull(await db.GetMaterialisedStateAsync(48, "movie", -1, -1, CancellationToken.None));
    }

    // ---- episodes: same patterns + shows-channel id ----

    [Fact]
    public async Task IdleEpisode_Evicts_RefreshesShowsChannel()
    {
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(1399, "episode", 1, 2, "/stub/ep.mkv", "/fuse/ep.mkv", CancellationToken.None);
        await BackdateMaterialisedAtAsync(1399, "episode", 1, 2, DateTimeOffset.UtcNow.AddDays(-90));

        var (sut, gostream, lib, _, _, refresh, _, _) = BuildSut(db);
        var item = MakeChannelEpisode(1399, 1, 2);
        SetupExternalIdLookup(lib, item.ExternalId, item);

        await sut.RunOnceAsync(CancellationToken.None);

        gostream.Verify(g => g.RemoveAsync("/stub/ep.mkv", It.IsAny<CancellationToken>()), Times.Once);
        Assert.Null(await db.GetMaterialisedStateAsync(1399, "episode", 1, 2, CancellationToken.None));
        refresh.Verify(r => r.RefreshChannelItemAsync(
            ChannelIds.Shows, item.ExternalId,
            It.Is<ChannelItemRefreshOptions>(o => o.ForceUpdate && !o.ForceProbe && o.InvalidateMediaInfoCache),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
