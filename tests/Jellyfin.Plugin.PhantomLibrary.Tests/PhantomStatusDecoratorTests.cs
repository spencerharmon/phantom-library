using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.Playback;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class PhantomStatusDecoratorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PhantomDb _db;
    private readonly Mock<ILibraryManager> _libMock = new();
    private readonly StubMaterialiser _materialiser = new();
    private readonly StubQueue _queue = new();

    public PhantomStatusDecoratorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "psd_" + Guid.NewGuid().ToString("N") + ".db");
        _db = new PhantomDb(_dbPath);
        _libMock.Setup(l => l.UpdateItemAsync(
            It.IsAny<BaseItem>(), It.IsAny<BaseItem>(),
            It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); File.Delete(_dbPath + "-wal"); File.Delete(_dbPath + "-shm"); } catch { }
    }

    private PhantomStatusDecorator Build() => new(
        _materialiser, _queue, _libMock.Object, _db,
        NullLogger<PhantomStatusDecorator>.Instance);

    private Movie BuildMovie(Guid id, string? overview)
    {
        var m = new Movie { Name = "M", Overview = overview };
        m.Id = id;
        _libMock.Setup(l => l.GetItemById(id)).Returns(m);
        return m;
    }

    [Fact]
    public async Task Started_Prepends_Materialising_Prefix()
    {
        var id = Guid.NewGuid();
        var m = BuildMovie(id, "Original synopsis.");
        var d = Build();

        await d.HandleAsync(new MaterialisationLifecycleEvent(
            id, MaterialisationLifecyclePhase.Started, null), default);

        Assert.Equal(PhantomStatusDecorator.MaterialisingPrefix + "Original synopsis.", m.Overview);
    }

    [Fact]
    public async Task Finished_Success_Replaces_Prefix_With_Ready()
    {
        var id = Guid.NewGuid();
        var m = BuildMovie(id, "Original synopsis.");
        var d = Build();

        await d.HandleAsync(new MaterialisationLifecycleEvent(
            id, MaterialisationLifecyclePhase.Started, null), default);

        await d.HandleAsync(new MaterialisationLifecycleEvent(
            id, MaterialisationLifecyclePhase.Finished,
            new MaterialisationOutcome { Status = MaterialisationStatus.Success }), default);

        Assert.Equal(PhantomStatusDecorator.ReadyPrefix + "Original synopsis.", m.Overview);
    }

    [Fact]
    public async Task Finished_Failure_Restores_Original_Overview_Exactly()
    {
        var id = Guid.NewGuid();
        var m = BuildMovie(id, "Original synopsis.");
        var d = Build();

        await d.HandleAsync(new MaterialisationLifecycleEvent(
            id, MaterialisationLifecyclePhase.Started, null), default);

        await d.HandleAsync(new MaterialisationLifecycleEvent(
            id, MaterialisationLifecyclePhase.Finished,
            new MaterialisationOutcome { Status = MaterialisationStatus.Error, Error = "boom" }), default);

        Assert.Equal("Original synopsis.", m.Overview);
    }

    [Fact]
    public async Task Original_Overview_Preserved_Across_Repeated_Started_Events()
    {
        var id = Guid.NewGuid();
        var m = BuildMovie(id, "Original synopsis.");
        var d = Build();

        await d.HandleAsync(new MaterialisationLifecycleEvent(
            id, MaterialisationLifecyclePhase.Started, null), default);
        // Now overview is decorated; a second Started must not stomp the
        // stored original copy with the already-decorated string.
        await d.HandleAsync(new MaterialisationLifecycleEvent(
            id, MaterialisationLifecyclePhase.Started, null), default);

        await d.HandleAsync(new MaterialisationLifecycleEvent(
            id, MaterialisationLifecyclePhase.Finished,
            new MaterialisationOutcome { Status = MaterialisationStatus.Error }), default);

        Assert.Equal("Original synopsis.", m.Overview);
    }

    [Fact]
    public async Task Null_Overview_Handled_Gracefully()
    {
        var id = Guid.NewGuid();
        var m = BuildMovie(id, null);
        var d = Build();

        await d.HandleAsync(new MaterialisationLifecycleEvent(
            id, MaterialisationLifecyclePhase.Started, null), default);

        Assert.Equal(PhantomStatusDecorator.MaterialisingPrefix, m.Overview);

        await d.HandleAsync(new MaterialisationLifecycleEvent(
            id, MaterialisationLifecyclePhase.Finished,
            new MaterialisationOutcome { Status = MaterialisationStatus.Success }), default);

        Assert.Equal(PhantomStatusDecorator.ReadyPrefix, m.Overview);
    }

    // --- stubs ---

    private sealed class StubMaterialiser : IMaterialiser
    {
        public event EventHandler<MaterialisationLifecycleEvent>? LifecycleChanged;
        public Task<MaterialisationOutcome> MaterialiseAsync(Guid id, MaterialiseTrigger t, CancellationToken ct)
            => Task.FromResult(new MaterialisationOutcome { Status = MaterialisationStatus.Success });
        public void Raise(MaterialisationLifecycleEvent e) => LifecycleChanged?.Invoke(this, e);
    }

    private sealed class StubQueue : IMaterialisationQueue
    {
        public event EventHandler<MaterialisationLifecycleEvent>? LifecycleChanged;
        public void EnqueueUser(Guid id, MaterialiseTrigger t) { }
        public void EnqueueEager(Guid id) { }
        public int PendingUserCount => 0;
        public int PendingEagerCount => 0;
        public void Raise(MaterialisationLifecycleEvent e) => LifecycleChanged?.Invoke(this, e);
    }
}
