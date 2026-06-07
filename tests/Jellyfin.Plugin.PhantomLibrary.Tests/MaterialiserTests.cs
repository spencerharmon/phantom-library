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
    private readonly NullPhantomStubManager _stubs = new();

    public MaterialiserTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "matt_" + Guid.NewGuid().ToString("N") + ".db");
        _db = new PhantomDb(_dbPath);

        _idxMock.SetupGet(i => i.Name).Returns("MockIdx");
        _idxMock.SetupGet(i => i.IsEnabled).Returns(true);

        _libMock.Setup(l => l.UpdateItemAsync(
            It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // BaseItem.GetParent() reads from a static LibraryManager. Wire
        // it so any test that uses parent traversal (ResolveHostPath
        // tests, the materialise-loop tests that touch item.GetParent)
        // sees our mock.
        BaseItem.LibraryManager = _libMock.Object;
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
            _stubs,
            NullLogger<Materialiser>.Instance,
            () => cfg);
    }

    private Materialiser BuildMaterialiserWithTmdb(
        Jellyfin.Plugin.PhantomLibrary.Clients.ITmdbClient tmdb,
        PluginConfiguration? cfg = null)
    {
        cfg ??= new PluginConfiguration { MinSeeders = 1, MinSizeGb1080p = 0, MinSizeGb4K = 0 };
        return new Materialiser(
            _libMock.Object,
            _provMock.Object,
            new[] { _idxMock.Object },
            _gsMock.Object,
            new QualityScorer(NullLogger<QualityScorer>.Instance),
            _db,
            _stubs,
            NullLogger<Materialiser>.Instance,
            () => cfg,
            tmdb);
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

    [Fact]
    public async Task Series_Materialisation_Returns_Error_With_Documented_Reason()
    {
        var id = Guid.NewGuid();
        var s = new MediaBrowser.Controller.Entities.TV.Series { Name = "X" };
        s.Id = id;
        s.ProviderIds["Tmdb"] = "1";
        s.ProviderIds["Imdb"] = "tt1";
        _libMock.Setup(l => l.GetItemById(id)).Returns(s);

        var m = BuildMaterialiser();
        var r = await m.MaterialiseAsync(id, MaterialiseTrigger.Favourite, CancellationToken.None);
        Assert.Equal(MaterialisationStatus.Error, r.Status);
        Assert.Contains("Series", r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("not supported", r.Error!, StringComparison.OrdinalIgnoreCase);
        _gsMock.Verify(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Episode_Materialisation_Passes_SeriesImdb_Season_Episode_To_Gostream()
    {
        var id = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var seriesItem = new MediaBrowser.Controller.Entities.TV.Series { Name = "Show" };
        seriesItem.Id = seriesId;
        seriesItem.ProviderIds["Tmdb"] = "99";
        seriesItem.ProviderIds["Imdb"] = "tt0944947";

        var ep = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "E1",
            ParentIndexNumber = 1,
            IndexNumber = 4,
            SeriesId = seriesId,
        };
        ep.Id = id;
        ep.ProviderIds["Tmdb"] = "500";

        _libMock.Setup(l => l.GetItemById(id)).Returns(ep);
        _libMock.Setup(l => l.GetItemById(seriesId)).Returns(seriesItem);

        IndexerQuery? observedQuery = null;
        _idxMock.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IndexerQuery, CancellationToken>((q, _) => observedQuery = q)
            .ReturnsAsync(new[] { Cand() });
        GostreamAddRequest? observedAdd = null;
        _gsMock.Setup(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
            .Callback<GostreamAddRequest, CancellationToken>((r, _) => observedAdd = r)
            .ReturnsAsync(new GostreamAddResult { StubPath = "/r/x.mkv", FusePath = "/f/x.mkv", Hash = "DEAD", Size = 1 });

        var m = BuildMaterialiser();
        var r = await m.MaterialiseAsync(id, MaterialiseTrigger.Favourite, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.Success, r.Status);
        Assert.NotNull(observedQuery);
        Assert.Equal("episode", observedQuery!.Type);
        Assert.Equal(1, observedQuery.Season);
        Assert.Equal(4, observedQuery.Episode);
        Assert.Equal("tt0944947", observedQuery.Imdb);
        Assert.Equal("tt0944947", observedQuery.SeriesImdb);
        Assert.NotNull(observedAdd);
        Assert.Equal("episode", observedAdd!.Type);
        Assert.Equal("tt0944947", observedAdd.SeriesImdb);
        Assert.Equal(1, observedAdd.Season);
        Assert.Equal(4, observedAdd.Episode);
    }

    [Fact]
    public async Task Episode_Without_Series_Imdb_Refuses()
    {
        var id = Guid.NewGuid();
        var seriesId = Guid.NewGuid();
        var seriesItem = new MediaBrowser.Controller.Entities.TV.Series { Name = "Show" };
        seriesItem.Id = seriesId;
        seriesItem.ProviderIds["Tmdb"] = "99";
        // No Imdb on series.

        var ep = new MediaBrowser.Controller.Entities.TV.Episode
        {
            Name = "E1", ParentIndexNumber = 1, IndexNumber = 4, SeriesId = seriesId,
        };
        ep.Id = id;
        ep.ProviderIds["Tmdb"] = "500";

        _libMock.Setup(l => l.GetItemById(id)).Returns(ep);
        _libMock.Setup(l => l.GetItemById(seriesId)).Returns(seriesItem);
        var m = BuildMaterialiser();
        var r = await m.MaterialiseAsync(id, MaterialiseTrigger.Favourite, CancellationToken.None);
        Assert.Equal(MaterialisationStatus.Error, r.Status);
        _gsMock.Verify(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Promote_Success_DeletesPhantomStub()
    {
        var id = Guid.NewGuid();
        var movie = BuildMovie(id);
        movie.Path = "/var/lib/jellyfin/phantom-library/movies/Test_Movie__phantom_tmdb1.mp4";
        _libMock.Setup(l => l.GetItemById(id)).Returns(movie);
        _idxMock.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Cand() });
        _gsMock.Setup(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GostreamAddResult { StubPath = "/r/x.mkv", FusePath = "/f/x.mkv", Hash = "DEAD", Size = 1 });

        var m = BuildMaterialiser();
        var r = await m.MaterialiseAsync(id, MaterialiseTrigger.Favourite, CancellationToken.None);
        Assert.Equal(MaterialisationStatus.Success, r.Status);
        Assert.Single(_stubs.Deleted);
        Assert.Contains("__phantom_tmdb", _stubs.Deleted[0]);
    }

    [Fact]
    public async Task Promote_Success_DoesNotDelete_NonPhantomPath()
    {
        var id = Guid.NewGuid();
        var movie = BuildMovie(id);
        movie.Path = "/some/real/file.mkv"; // no sentinel
        _libMock.Setup(l => l.GetItemById(id)).Returns(movie);
        _idxMock.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Cand() });
        _gsMock.Setup(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GostreamAddResult { StubPath = "/r/x.mkv", FusePath = "/f/x.mkv", Hash = "DEAD", Size = 1 });

        var m = BuildMaterialiser();
        var r = await m.MaterialiseAsync(id, MaterialiseTrigger.Favourite, CancellationToken.None);
        Assert.Equal(MaterialisationStatus.Success, r.Status);
        Assert.Empty(_stubs.Deleted);
    }

    // ----- M12: IMDB enrichment from TMDB when item has no Imdb -----

    [Fact]
    public async Task Materialise_EnrichesImdbFromTmdb_WhenItemHasNoImdb()
    {
        // Operator-observed failure: phantom rows have Tmdb but no
        // Imdb. Torrentio refuses queries without Imdb -> indexer
        // returns 0 candidates -> Materialiser bails. Fix: fetch the
        // Imdb via ITmdbClient.GetImdbIdForMovieAsync.
        var id = Guid.NewGuid();
        var movie = BuildMovie(id, tmdb: 1083381, imdb: null);
        _libMock.Setup(l => l.GetItemById(id)).Returns(movie);

        var tmdbMock = new Mock<Jellyfin.Plugin.PhantomLibrary.Clients.ITmdbClient>();
        tmdbMock.Setup(t => t.GetImdbIdForMovieAsync(1083381, It.IsAny<CancellationToken>()))
            .ReturnsAsync("tt9999999");

        _idxMock.Setup(i => i.SearchAsync(
                It.Is<IndexerQuery>(q => q.Imdb == "tt9999999"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Cand() });
        _gsMock.Setup(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GostreamAddResult
            { StubPath = "/s.mkv", FusePath = "/f.mkv", Hash = "DEAD", Size = 1 });

        var m = BuildMaterialiserWithTmdb(tmdbMock.Object);
        var r = await m.MaterialiseAsync(id, MaterialiseTrigger.Play, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.Success, r.Status);
        tmdbMock.Verify(t => t.GetImdbIdForMovieAsync(1083381, It.IsAny<CancellationToken>()),
            Times.Once);
        _idxMock.Verify(i => i.SearchAsync(
                It.Is<IndexerQuery>(q => q.Imdb == "tt9999999"),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
        Assert.Equal("tt9999999", movie.ProviderIds["Imdb"]);
    }

    [Fact]
    public async Task Materialise_SkipsImdbEnrichment_WhenItemAlreadyHasImdb()
    {
        var id = Guid.NewGuid();
        var movie = BuildMovie(id, tmdb: 1083381, imdb: "tt0000001");
        _libMock.Setup(l => l.GetItemById(id)).Returns(movie);

        var tmdbMock = new Mock<Jellyfin.Plugin.PhantomLibrary.Clients.ITmdbClient>();
        _idxMock.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Cand() });
        _gsMock.Setup(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GostreamAddResult
            { StubPath = "/s.mkv", FusePath = "/f.mkv", Hash = "DEAD", Size = 1 });

        var m = BuildMaterialiserWithTmdb(tmdbMock.Object);
        var r = await m.MaterialiseAsync(id, MaterialiseTrigger.Play, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.Success, r.Status);
        tmdbMock.Verify(t => t.GetImdbIdForMovieAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Materialise_TmdbEnrichmentReturnsNull_ProceedsWithoutImdb()
    {
        var id = Guid.NewGuid();
        var movie = BuildMovie(id, tmdb: 99999999, imdb: null);
        _libMock.Setup(l => l.GetItemById(id)).Returns(movie);

        var tmdbMock = new Mock<Jellyfin.Plugin.PhantomLibrary.Clients.ITmdbClient>();
        tmdbMock.Setup(t => t.GetImdbIdForMovieAsync(99999999, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        _idxMock.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Cand() });
        _gsMock.Setup(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GostreamAddResult
            { StubPath = "/s.mkv", FusePath = "/f.mkv", Hash = "DEAD", Size = 1 });

        var m = BuildMaterialiserWithTmdb(tmdbMock.Object);
        var r = await m.MaterialiseAsync(id, MaterialiseTrigger.Play, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.Success, r.Status);
        Assert.False(movie.ProviderIds.ContainsKey("Imdb"));
    }

    // ----- M12: gostream path translation via library locations -----

    private sealed class TestCollectionFolder : MediaBrowser.Controller.Entities.CollectionFolder
    {
        public TestCollectionFolder(string containerPath, string[] locations)
        {
            Path = containerPath;
            PhysicalLocationsList = locations;
        }
    }

    [Fact]
    public void ResolveHostPath_OneNonPhantomLocation_ConcatsFilename()
    {
        var cf = new TestCollectionFolder(
            "/var/lib/jellyfin/root/default/gostream-movies",
            new[]
            {
                "/var/lib/jellyfin/root/default/gostream-movies",
                "/var/lib/jellyfin/phantom-library/movies",
                "/var/gostream/gostream-mkv-virtual/movies",
            });
        cf.Id = Guid.NewGuid();
        var movie = new Movie { Name = "X" };
        movie.Id = Guid.NewGuid();
        movie.SetParent(cf);
        _libMock.Setup(l => l.GetItemById(cf.Id)).Returns(cf);

        var cfg = new PluginConfiguration
        {
            PhantomStubRoot = "/var/lib/jellyfin/phantom-library",
        };

        var m = BuildMaterialiser(cfg);
        var result = m.ResolveHostPath(
            "/mnt/gostream-mkv-virtual/movies/Backrooms_2026_1080p_fdf0872d.mkv",
            movie,
            cfg);

        Assert.Equal(
            "/var/gostream/gostream-mkv-virtual/movies/Backrooms_2026_1080p_fdf0872d.mkv",
            result);
    }

    [Fact]
    public void ResolveHostPath_NoCollectionFolderParent_ReturnsOriginal()
    {
        var movie = new Movie { Name = "X" };
        movie.Id = Guid.NewGuid();
        // No parent set.

        var cfg = new PluginConfiguration
        {
            PhantomStubRoot = "/var/lib/jellyfin/phantom-library",
        };

        var m = BuildMaterialiser(cfg);
        var input = "/mnt/gostream-mkv-virtual/movies/X.mkv";
        var result = m.ResolveHostPath(input, movie, cfg);
        Assert.Same(input, result);
    }

    [Fact]
    public void ResolveHostPath_OnlyPhantomLocation_ReturnsOriginal()
    {
        var cf = new TestCollectionFolder(
            "/var/lib/jellyfin/root/default/gostream-movies",
            new[]
            {
                "/var/lib/jellyfin/root/default/gostream-movies",
                "/var/lib/jellyfin/phantom-library/movies",
            });
        cf.Id = Guid.NewGuid();
        var movie = new Movie { Name = "X" };
        movie.Id = Guid.NewGuid();
        movie.SetParent(cf);
        _libMock.Setup(l => l.GetItemById(cf.Id)).Returns(cf);

        var cfg = new PluginConfiguration
        {
            PhantomStubRoot = "/var/lib/jellyfin/phantom-library",
        };

        var m = BuildMaterialiser(cfg);
        var input = "/mnt/gostream-mkv-virtual/movies/X.mkv";
        var result = m.ResolveHostPath(input, movie, cfg);
        Assert.Same(input, result);
    }

    [Fact]
    public void ResolveHostPath_EmptyInput_PassThrough()
    {
        var cfg = new PluginConfiguration();
        var m = BuildMaterialiser(cfg);
        Assert.Equal(string.Empty, m.ResolveHostPath(string.Empty, new Movie(), cfg));
    }

    [Fact]
    public void ResolveHostPath_ItemUnderPhantomPhysFolder_UsesGetCollectionFolders()
    {
        // Repro of the operator-observed prod state: phantom items
        // are persisted with ParentId = phantom-library physical
        // Folder (not the CollectionFolder directly). Walking ParentId
        // never reaches a CollectionFolder. The fix uses
        // LibraryManager.GetCollectionFolders to find the CF via
        // PhysicalLocations lookup.
        var cf = new TestCollectionFolder(
            "/var/lib/jellyfin/root/default/gostream-movies",
            new[]
            {
                "/var/lib/jellyfin/root/default/gostream-movies",
                "/var/lib/jellyfin/phantom-library/movies",
                "/var/gostream/gostream-mkv-virtual/movies",
            });
        cf.Id = Guid.NewGuid();

        // Phantom physical Folder under AggregateFolder root.
        var phantomPhys = new TestPhysicalFolder { Name = "movies", Path = "/var/lib/jellyfin/phantom-library/movies" };
        phantomPhys.Id = Guid.NewGuid();
        var aggRoot = new TestPhysicalFolder { Name = "root", Path = "/var/lib/jellyfin/root" };
        aggRoot.Id = Guid.NewGuid();
        phantomPhys.SetParent(aggRoot);

        var movie = new Movie { Name = "X" };
        movie.Id = Guid.NewGuid();
        movie.SetParent(phantomPhys);
        _libMock.Setup(l => l.GetItemById(phantomPhys.Id)).Returns(phantomPhys);
        _libMock.Setup(l => l.GetItemById(aggRoot.Id)).Returns(aggRoot);

        // GetCollectionFolders returns our cf for this item.
        _libMock.Setup(l => l.GetCollectionFolders(It.IsAny<BaseItem>()))
            .Returns(new List<Folder> { cf });

        var cfg = new PluginConfiguration
        {
            PhantomStubRoot = "/var/lib/jellyfin/phantom-library",
        };

        var m = BuildMaterialiser(cfg);
        var result = m.ResolveHostPath(
            "/mnt/gostream-mkv-virtual/movies/28_Years_Later_2025_2160p_511a90f7.mkv",
            movie,
            cfg);

        Assert.Equal(
            "/var/gostream/gostream-mkv-virtual/movies/28_Years_Later_2025_2160p_511a90f7.mkv",
            result);
    }

    private sealed class TestPhysicalFolder : Folder
    {
    }
}
