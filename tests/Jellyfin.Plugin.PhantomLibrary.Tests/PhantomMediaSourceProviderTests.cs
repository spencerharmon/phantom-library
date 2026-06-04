using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.Playback;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class PhantomMediaSourceProviderTests : IDisposable
{
    private readonly string _cacheRoot;
    private readonly IApplicationPaths _paths;
    private readonly Mock<IMaterialisationQueue> _queue = new();

    public PhantomMediaSourceProviderTests()
    {
        _cacheRoot = Path.Combine(Path.GetTempPath(), "msp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheRoot);
        var mock = new Mock<IApplicationPaths>();
        mock.SetupGet(p => p.CachePath).Returns(_cacheRoot);
        _paths = mock.Object;
    }

    public void Dispose()
    {
        try { Directory.Delete(_cacheRoot, true); } catch { }
    }

    private PhantomMediaSourceProvider Build()
        => new(_paths, _queue.Object, NullLogger<PhantomMediaSourceProvider>.Instance);

    [Fact]
    public async Task Virtual_Movie_Returns_Splash_And_Enqueues()
    {
        var item = new Movie { Name = "X", IsVirtualItem = true };
        item.Id = Guid.NewGuid();

        var sources = (await Build().GetMediaSources(item, default)).ToList();

        Assert.Single(sources);
        var s = sources[0];
        Assert.Equal(MediaProtocol.File, s.Protocol);
        Assert.True(File.Exists(s.Path), "splash path should exist on disk");
        Assert.Equal("mp4", s.Container);
        Assert.False(s.IsRemote);
        Assert.True(s.SupportsDirectPlay);
        Assert.Equal(TimeSpan.FromSeconds(10).Ticks, s.RunTimeTicks);
        Assert.Equal(2, s.MediaStreams.Count);
        Assert.StartsWith("phantom-splash-", s.Id, StringComparison.Ordinal);

        _queue.Verify(q => q.EnqueueUser(item.Id, MaterialiseTrigger.Play), Times.Once);
    }

    [Fact]
    public async Task Materialised_Item_Returns_Empty_And_Does_Not_Enqueue()
    {
        var item = new Movie { Name = "Y", IsVirtualItem = false, Path = "/somewhere/real.mkv" };
        item.Id = Guid.NewGuid();

        var sources = (await Build().GetMediaSources(item, default)).ToList();
        Assert.Empty(sources);
        _queue.Verify(q => q.EnqueueUser(It.IsAny<Guid>(), It.IsAny<MaterialiseTrigger>()), Times.Never);
    }

    [Fact]
    public async Task Non_Video_Item_Returns_Empty_And_Does_Not_Enqueue()
    {
        var item = new MusicAlbum { Name = "Album" };
        item.Id = Guid.NewGuid();

        var sources = (await Build().GetMediaSources(item, default)).ToList();
        Assert.Empty(sources);
        _queue.Verify(q => q.EnqueueUser(It.IsAny<Guid>(), It.IsAny<MaterialiseTrigger>()), Times.Never);
    }

    [Fact]
    public async Task Virtual_Episode_Returns_Splash()
    {
        var item = new Episode { Name = "S01E01", IsVirtualItem = true };
        item.Id = Guid.NewGuid();

        var sources = (await Build().GetMediaSources(item, default)).ToList();
        Assert.Single(sources);
        _queue.Verify(q => q.EnqueueUser(item.Id, MaterialiseTrigger.Play), Times.Once);
    }

    [Fact]
    public async Task Queue_Throw_Does_Not_Fail_Provider()
    {
        var item = new Movie { Name = "X", IsVirtualItem = true };
        item.Id = Guid.NewGuid();
        _queue.Setup(q => q.EnqueueUser(It.IsAny<Guid>(), It.IsAny<MaterialiseTrigger>()))
            .Throws(new InvalidOperationException("queue down"));

        var sources = (await Build().GetMediaSources(item, default)).ToList();
        Assert.Single(sources);
    }
}
