using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class PhantomCollectionFolderBinderTests
{
    private sealed class TestRoot : Folder
    {
        private readonly List<BaseItem> _children;
        public TestRoot(List<BaseItem> children) { _children = children; Id = Guid.NewGuid(); }
        public override IEnumerable<BaseItem> Children => _children;
    }

    private sealed class TestPhysicalFolder : Folder { }

    private readonly Mock<ILibraryManager> _lib = new();
    private readonly PluginConfiguration _cfg = new()
    {
        PhantomStubRoot = "/tmp/phantom-binder-test",
        PhantomMoviesLibraryName = "gostream-movies",
        PhantomShowsLibraryName = "gostream-shows",
    };

    private PhantomCollectionFolderBinder Build() => new(
        _lib.Object,
        NullLogger<PhantomCollectionFolderBinder>.Instance,
        () => _cfg);

    private void SetupRoot(List<BaseItem> children)
    {
        var root = new TestRoot(children);
        _lib.Setup(l => l.GetUserRootFolder()).Returns(root);
    }

    [Fact]
    public async Task BindAsync_NoCollectionFolder_LogsAndContinues()
    {
        // Empty root; neither configured library exists.
        SetupRoot(new List<BaseItem>());

        var binder = Build();
        await binder.BindAsync(CancellationToken.None);

        _lib.Verify(l => l.AddMediaPath(It.IsAny<string>(), It.IsAny<MediaPathInfo>()), Times.Never);
        _lib.Verify(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task BindAsync_AlreadyBound_NoOps()
    {
        var phantomMoviesDir = "/tmp/phantom-binder-test/movies";
        var phantomShowsDir = "/tmp/phantom-binder-test/shows";
        var moviesCf = new CollectionFolder
        {
            Name = "gostream-movies",
            PhysicalLocationsList = new[] { "/some/existing", phantomMoviesDir },
            PhysicalFolderIds = new[] { Guid.NewGuid(), Guid.NewGuid() },
        };
        moviesCf.Id = Guid.NewGuid();
        var showsCf = new CollectionFolder
        {
            Name = "gostream-shows",
            PhysicalLocationsList = new[] { phantomShowsDir },
            PhysicalFolderIds = new[] { Guid.NewGuid() },
        };
        showsCf.Id = Guid.NewGuid();
        SetupRoot(new List<BaseItem> { moviesCf, showsCf });

        var binder = Build();
        await binder.BindAsync(CancellationToken.None);

        _lib.Verify(l => l.AddMediaPath(It.IsAny<string>(), It.IsAny<MediaPathInfo>()), Times.Never);
        _lib.Verify(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task BindAsync_MissingPath_AddsAndPatchesCollectionFolder()
    {
        var phantomMoviesDir = "/tmp/phantom-binder-test/movies";
        var phantomShowsDir = "/tmp/phantom-binder-test/shows";

        var existingId = Guid.NewGuid();
        var moviesCf = new CollectionFolder
        {
            Name = "gostream-movies",
            PhysicalLocationsList = new[] { "/some/existing" },
            PhysicalFolderIds = new[] { existingId },
        };
        moviesCf.Id = Guid.NewGuid();

        var showsCf = new CollectionFolder
        {
            Name = "gostream-shows",
            PhysicalLocationsList = new[] { phantomShowsDir },
            PhysicalFolderIds = new[] { Guid.NewGuid() },
        };
        showsCf.Id = Guid.NewGuid();

        var newPhys = new TestPhysicalFolder { Name = "movies", Path = phantomMoviesDir };
        newPhys.Id = Guid.NewGuid();

        // Root pre-binding: existing physical folder + the two CFs.
        var rootChildren = new List<BaseItem> { moviesCf, showsCf };
        SetupRoot(rootChildren);

        // ValidateTopLibraryFolders is implemented by adding the phys folder to root children.
        _lib.Setup(l => l.AddMediaPath("gostream-movies", It.IsAny<MediaPathInfo>()))
            .Callback<string, MediaPathInfo>((_, _) => rootChildren.Add(newPhys));
        _lib.Setup(l => l.ValidateTopLibraryFolders(It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Returns(Task.CompletedTask);

        _lib.Setup(l => l.GetItemById(moviesCf.Id)).Returns(moviesCf);

        var updated = new List<BaseItem>();
        _lib.Setup(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Callback<BaseItem, BaseItem, ItemUpdateType, CancellationToken>((i, _, _, _) => updated.Add(i))
            .Returns(Task.CompletedTask);

        var binder = Build();
        await binder.BindAsync(CancellationToken.None);

        _lib.Verify(l => l.AddMediaPath("gostream-movies", It.Is<MediaPathInfo>(m => m.Path == phantomMoviesDir)),
            Times.Once);
        Assert.NotEmpty(updated);
        Assert.All(updated, i => Assert.Same(moviesCf, i));
        Assert.Contains(phantomMoviesDir, moviesCf.PhysicalLocationsList);
        Assert.Contains(newPhys.Id, moviesCf.PhysicalFolderIds);
        // Pre-existing entries preserved.
        Assert.Contains("/some/existing", moviesCf.PhysicalLocationsList);
        Assert.Contains(existingId, moviesCf.PhysicalFolderIds);
    }

    [Fact]
    public async Task BindAsync_RunTwice_SecondCallIsNoOp()
    {
        var phantomMoviesDir = "/tmp/phantom-binder-test/movies";
        var phantomShowsDir = "/tmp/phantom-binder-test/shows";

        var moviesCf = new CollectionFolder
        {
            Name = "gostream-movies",
            PhysicalLocationsList = new[] { phantomMoviesDir },
            PhysicalFolderIds = new[] { Guid.NewGuid() },
        };
        moviesCf.Id = Guid.NewGuid();
        var showsCf = new CollectionFolder
        {
            Name = "gostream-shows",
            PhysicalLocationsList = new[] { phantomShowsDir },
            PhysicalFolderIds = new[] { Guid.NewGuid() },
        };
        showsCf.Id = Guid.NewGuid();
        SetupRoot(new List<BaseItem> { moviesCf, showsCf });

        var binder = Build();
        await binder.BindAsync(CancellationToken.None);
        await binder.BindAsync(CancellationToken.None);

        _lib.Verify(l => l.AddMediaPath(It.IsAny<string>(), It.IsAny<MediaPathInfo>()), Times.Never);
    }
}
