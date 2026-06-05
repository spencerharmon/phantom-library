using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class VirtualLibraryRootTests
{
    private static Folder MakeFolder(string name, Guid id)
    {
        var f = new TestFolder { Name = name };
        f.Id = id;
        return f;
    }

    private sealed class TestFolder : Folder
    {
        public override IEnumerable<BaseItem> Children { get; set; } = Array.Empty<BaseItem>();
    }

    private sealed class TestRootFolder : Folder
    {
        private readonly List<BaseItem> _children = new();
        public TestRootFolder(IEnumerable<BaseItem> children) { _children.AddRange(children); }
        public override IEnumerable<BaseItem> Children => _children;
    }

    [Fact]
    public void AutoPick_PrefersMoviesFolder_ForMovies()
    {
        var moviesId = Guid.NewGuid();
        var tvId = Guid.NewGuid();
        var movies = MakeFolder("Films", moviesId);
        var tv = MakeFolder("Shows", tvId);
        var root = new TestRootFolder(new BaseItem[] { tv, movies });

        var lib = new Mock<ILibraryManager>();
        lib.Setup(l => l.GetUserRootFolder()).Returns(root);
        lib.Setup(l => l.GetContentType(movies)).Returns(CollectionType.movies);
        lib.Setup(l => l.GetContentType(tv)).Returns(CollectionType.tvshows);

        var r = new VirtualLibraryRoot(lib.Object, NullLogger<VirtualLibraryRoot>.Instance,
            () => new PluginConfiguration());

        Assert.Equal(moviesId, r.ResolveMoviesParent()!.Id);
        Assert.Equal(tvId, r.ResolveSeriesParent()!.Id);
    }

    [Fact]
    public void AutoPick_CachesResultUntilInvalidate()
    {
        var moviesId = Guid.NewGuid();
        var movies = MakeFolder("Films", moviesId);
        var root = new TestRootFolder(new BaseItem[] { movies });

        var lib = new Mock<ILibraryManager>();
        lib.Setup(l => l.GetUserRootFolder()).Returns(root);
        lib.Setup(l => l.GetContentType(movies)).Returns(CollectionType.movies);

        var r = new VirtualLibraryRoot(lib.Object, NullLogger<VirtualLibraryRoot>.Instance,
            () => new PluginConfiguration());

        r.ResolveMoviesParent();
        r.ResolveMoviesParent();
        r.ResolveMoviesParent();
        lib.Verify(l => l.GetUserRootFolder(), Times.Once);

        r.Invalidate();
        r.ResolveMoviesParent();
        lib.Verify(l => l.GetUserRootFolder(), Times.Exactly(2));
    }

    [Fact]
    public void ConfiguredGuid_OverridesAutoPick()
    {
        var pinnedId = Guid.NewGuid();
        var pinned = MakeFolder("Custom", pinnedId);
        var root = new TestRootFolder(Array.Empty<BaseItem>());

        var lib = new Mock<ILibraryManager>();
        lib.Setup(l => l.GetItemById(pinnedId)).Returns(pinned);
        lib.Setup(l => l.GetUserRootFolder()).Returns(root);

        var cfg = new PluginConfiguration { PhantomTargetLibraryId = pinnedId.ToString("D") };
        var r = new VirtualLibraryRoot(lib.Object, NullLogger<VirtualLibraryRoot>.Instance, () => cfg);

        Assert.Equal(pinnedId, r.ResolveMoviesParent()!.Id);
    }

    [Fact]
    public void StaleConfiguredGuid_FallsBackToAutoPick()
    {
        var staleId = Guid.NewGuid();
        var moviesId = Guid.NewGuid();
        var movies = MakeFolder("Films", moviesId);
        var root = new TestRootFolder(new BaseItem[] { movies });

        var lib = new Mock<ILibraryManager>();
        lib.Setup(l => l.GetItemById(staleId)).Returns((BaseItem?)null);
        lib.Setup(l => l.GetUserRootFolder()).Returns(root);
        lib.Setup(l => l.GetContentType(movies)).Returns(CollectionType.movies);

        var cfg = new PluginConfiguration { PhantomTargetLibraryId = staleId.ToString("D") };
        var r = new VirtualLibraryRoot(lib.Object, NullLogger<VirtualLibraryRoot>.Instance, () => cfg);

        Assert.Equal(moviesId, r.ResolveMoviesParent()!.Id);
    }
}
