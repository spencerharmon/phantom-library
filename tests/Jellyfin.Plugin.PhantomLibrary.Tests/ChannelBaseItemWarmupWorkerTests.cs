using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Scheduled;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// home-shelves-basitem-warmup coverage: the warmup worker must reproduce the
/// channel-root browse (<see cref="IChannelManager.GetChannelItemsInternal"/>)
/// for BOTH phantom channels AS A REAL USER (a Guid.Empty user returns only the
/// bounded "latest" set, not the full flat catalogue) — the exact call that
/// repopulates the BaseItem cache the Home shelves depend on — scoped to the
/// phantom channel ids, never a per-user O(catalogue) query.
/// </summary>
public class ChannelBaseItemWarmupWorkerTests
{
    private static User MakeUser() => new User("warmup-admin", "Default", "Default");

    private static IUserManager UserManager(params User[] users)
    {
        var m = new Mock<IUserManager>(MockBehavior.Loose);
        m.Setup(x => x.GetUsers()).Returns(users);
        return m.Object;
    }

    private static ChannelBaseItemWarmupWorker Worker(IChannelManager cm, IUserManager um, PluginConfiguration cfg)
        => new ChannelBaseItemWarmupWorker(cm, um, NullLogger<ChannelBaseItemWarmupWorker>.Instance, () => cfg);

    private static QueryResult<BaseItem> Result(int count)
    {
        var items = new List<BaseItem>(count);
        for (var i = 0; i < count; i++)
        {
            items.Add(new Folder());
        }

        return new QueryResult<BaseItem>(items);
    }

    [Fact]
    public async Task WarmAsync_BrowsesBothPhantomChannelRootsAsRealUser()
    {
        var user = MakeUser();
        var seen = new List<Guid>();
        var mock = new Mock<IChannelManager>(MockBehavior.Strict);
        mock.Setup(m => m.GetChannelItemsInternal(
                It.IsAny<InternalItemsQuery>(),
                It.IsAny<IProgress<double>>(),
                It.IsAny<CancellationToken>()))
            .Callback<InternalItemsQuery, IProgress<double>, CancellationToken>((q, _, _) =>
            {
                // Root browse: scoped to exactly one phantom channel, no parent
                // folder, and carrying a REAL user (never Guid.Empty).
                Assert.Single(q.ChannelIds);
                Assert.True(q.ParentId == Guid.Empty);
                Assert.NotNull(q.User);
                Assert.Equal(user.Id, q.User!.Id);
                seen.Add(q.ChannelIds[0]);
            })
            .ReturnsAsync(Result(40));

        var wrapped = await Worker(mock.Object, UserManager(user), new PluginConfiguration())
            .WarmAsync(CancellationToken.None);

        Assert.Equal(80, wrapped); // 40 per channel × 2 channels
        Assert.Contains(ChannelIds.Movies, seen);
        Assert.Contains(ChannelIds.Shows, seen);
        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public async Task WarmAsync_NoUsers_SkipsWithoutBrowsing()
    {
        var mock = new Mock<IChannelManager>(MockBehavior.Strict); // strict: any browse call fails
        var wrapped = await Worker(mock.Object, UserManager(), new PluginConfiguration())
            .WarmAsync(CancellationToken.None);
        Assert.Equal(0, wrapped);
    }

    [Fact]
    public async Task WarmAsync_PropagatesCancellation()
    {
        var mock = new Mock<IChannelManager>(MockBehavior.Loose);
        mock.Setup(m => m.GetChannelItemsInternal(
                It.IsAny<InternalItemsQuery>(),
                It.IsAny<IProgress<double>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(Result(1));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Worker(mock.Object, UserManager(MakeUser()), new PluginConfiguration()).WarmAsync(cts.Token));
    }
}
