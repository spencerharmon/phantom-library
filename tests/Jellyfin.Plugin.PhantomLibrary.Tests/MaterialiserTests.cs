using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class MaterialiserTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PhantomDb _db;
    private readonly Mock<ILibraryManager> _libMock = new();
    private readonly Mock<IProviderManager> _provMock = new();
    private readonly Mock<IGostreamClient> _gsMock = new();
    private readonly Mock<IIndexerClient> _idxMock = new();

    public MaterialiserTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "matt_" + Guid.NewGuid().ToString("N") + ".db");
        _db = new PhantomDb(_dbPath);

        _idxMock.SetupGet(i => i.Name).Returns("MockIdx");
        _idxMock.SetupGet(i => i.IsEnabled).Returns(true);

        _libMock.Setup(l => l.UpdateItemAsync(
            It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); File.Delete(_dbPath + "-wal"); File.Delete(_dbPath + "-shm"); } catch { }
    }

    private Materialiser BuildMaterialiser(PluginConfiguration? cfg = null)
    {
        cfg ??= new PluginConfiguration { MinSeeders = 1, MinSizeGb1080p = 0, MinSizeGb4K = 0 };
        return new Materialiser(
            _libMock.Object,
            _provMock.Object,
            new[] { _idxMock.Object },
            _gsMock.Object,
            new QualityScorer(NullLogger<QualityScorer>.Instance),
            _db,
            NullLogger<Materialiser>.Instance,
            () => cfg);
    }

    private static Movie BuildMovie(Guid id, int tmdb = 1, string? imdb = "tt1")
    {
        var m = new Movie { Name = "Test Movie", ProductionYear = 2020 };
        m.Id = id;
        m.ProviderIds["Tmdb"] = tmdb.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (imdb is not null) m.ProviderIds["Imdb"] = imdb;
        return m;
    }

    private static IndexerCandidate Cand() => new()
    {
        Title = "Test 1080p",
        Magnet = "magnet:?xt=urn:btih:DEAD",
        InfoHash = "DEAD",
        Size = 5L * 1024 * 1024 * 1024,
        Seeders = 100,
        IndexerName = "MockIdx",
    };

    [Fact]
    public async Task Cache_Miss_Indexer_Scorer_Gostream_Success()
    {
        var id = Guid.NewGuid();
        _libMock.Setup(l => l.GetItemById(id)).Returns(BuildMovie(id));
        _idxMock.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Cand() });
        _gsMock.Setup(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GostreamAddResult { StubPath = "/r/x.mkv", FusePath = "/f/x.mkv", Hash = "DEAD", Size = 1 });

        var m = BuildMaterialiser();
        var r = await m.MaterialiseAsync(id, MaterialiseTrigger.Favourite, CancellationToken.None);
        Assert.Equal(MaterialisationStatus.Success, r.Status);
        Assert.Equal("/f/x.mkv", r.FusePath);
        _gsMock.Verify(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Cache_Hit_Skips_Indexer()
    {
        var id = Guid.NewGuid();
        _libMock.Setup(l => l.GetItemById(id)).Returns(BuildMovie(id));
        _gsMock.Setup(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GostreamAddResult { StubPath = "/r/x.mkv", FusePath = "/f/x.mkv", Hash = "DEAD", Size = 1 });

        // Seed cache with key that matches default GostreamDefault preset, movie type.
        await _db.PutCachedMagnetAsync(
            new MagnetCacheKey(1, "tt1", "movie", null, null, "GostreamDefault"),
            new MagnetCacheEntry
            {
                Magnet = "magnet:?xt=urn:btih:CACHED",
                InfoHash = "CACHED",
                Size = 1,
                Seeders = 1,
                Indexer = "Prowlarr",
                CachedAt = DateTimeOffset.UtcNow,
                Ttl = TimeSpan.FromDays(7),
                Source = "user",
            }, default);

        var m = BuildMaterialiser();
        var r = await m.MaterialiseAsync(id, MaterialiseTrigger.Favourite, CancellationToken.None);
        Assert.Equal(MaterialisationStatus.Success, r.Status);
        _idxMock.Verify(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Unavailable_Marker_Hit_Returns_Unavailable()
    {
        var id = Guid.NewGuid();
        _libMock.Setup(l => l.GetItemById(id)).Returns(BuildMovie(id));
        await _db.MarkUnavailableAsync(new UnavailableKey(1, "tt1", "movie", null, null), TimeSpan.FromHours(1), default);

        var m = BuildMaterialiser();
        var r = await m.MaterialiseAsync(id, MaterialiseTrigger.Favourite, CancellationToken.None);
        Assert.Equal(MaterialisationStatus.Unavailable, r.Status);
        _gsMock.Verify(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PreResolve_Stops_After_Cache_Write_Never_Calls_Gostream()
    {
        var id = Guid.NewGuid();
        _libMock.Setup(l => l.GetItemById(id)).Returns(BuildMovie(id));
        _idxMock.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Cand() });

        var m = BuildMaterialiser();
        var r = await m.MaterialiseAsync(id, MaterialiseTrigger.PreResolve, CancellationToken.None);
        Assert.Equal(MaterialisationStatus.Success, r.Status);
        _gsMock.Verify(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()), Times.Never);

        // Verify the cache was written with source=eager.
        var cached = await _db.GetCachedMagnetAsync(
            new MagnetCacheKey(1, "tt1", "movie", null, null, "GostreamDefault"), default);
        Assert.NotNull(cached);
        Assert.Equal("eager", cached!.Source);
    }

    [Fact]
    public async Task Gostream_Timeout_Does_Not_Mark_Unavailable()
    {
        var id = Guid.NewGuid();
        _libMock.Setup(l => l.GetItemById(id)).Returns(BuildMovie(id));
        _idxMock.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Cand() });
        _gsMock.Setup(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GostreamTimeoutException("timeout"));

        var m = BuildMaterialiser();
        var r = await m.MaterialiseAsync(id, MaterialiseTrigger.Favourite, CancellationToken.None);
        Assert.Equal(MaterialisationStatus.Error, r.Status);
        Assert.False(await _db.IsMarkedUnavailableAsync(new UnavailableKey(1, "tt1", "movie", null, null), default));
    }

    [Fact]
    public async Task Gostream_NoValidFiles_Marks_Unavailable()
    {
        var id = Guid.NewGuid();
        _libMock.Setup(l => l.GetItemById(id)).Returns(BuildMovie(id));
        _idxMock.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Cand() });
        _gsMock.Setup(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GostreamNoValidFilesException("no files"));

        var m = BuildMaterialiser();
        var r = await m.MaterialiseAsync(id, MaterialiseTrigger.Favourite, CancellationToken.None);
        Assert.Equal(MaterialisationStatus.Unavailable, r.Status);
        Assert.True(await _db.IsMarkedUnavailableAsync(new UnavailableKey(1, "tt1", "movie", null, null), default));
    }

    [Fact]
    public async Task No_Candidates_Marks_Unavailable()
    {
        var id = Guid.NewGuid();
        _libMock.Setup(l => l.GetItemById(id)).Returns(BuildMovie(id));
        _idxMock.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IndexerCandidate>());

        var m = BuildMaterialiser();
        var r = await m.MaterialiseAsync(id, MaterialiseTrigger.Favourite, CancellationToken.None);
        Assert.Equal(MaterialisationStatus.Unavailable, r.Status);
    }
}
