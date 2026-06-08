using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Library;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests.Library;

public class StubLayoutMigrationTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _cacheRoot;
    private readonly string _dbPath;
    private readonly PhantomDb _db;
    private readonly PluginConfiguration _cfg = new();
    private readonly Mock<IApplicationPaths> _paths = new();
    private readonly Mock<ILibraryManager> _lib = new();
    private readonly PhantomStubManager _stubs;

    public StubLayoutMigrationTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "phantom_mig_" + Guid.NewGuid().ToString("N"));
        _cacheRoot = Path.Combine(Path.GetTempPath(), "phantom_mig_cache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheRoot);
        _cfg.PhantomStubRoot = _tempRoot;
        _paths.SetupGet(p => p.CachePath).Returns(_cacheRoot);

        _dbPath = Path.Combine(_tempRoot, "phantom.db");
        Directory.CreateDirectory(_tempRoot);
        _db = new PhantomDb(_dbPath);

        _stubs = new PhantomStubManager(_paths.Object, NullLogger<PhantomStubManager>.Instance, () => _cfg);
        _stubs.BootstrapAsync(default).GetAwaiter().GetResult();

        _lib.Setup(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(),
            It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        try { Directory.Delete(_cacheRoot, recursive: true); } catch { }
    }

    private StubLayoutMigration Build()
        => new(_lib.Object, _stubs, _db, NullLogger<StubLayoutMigration>.Instance);

    private static Movie MakeMovie(Guid id, string name, int? year)
    {
        var m = new Movie { Name = name, ProductionYear = year };
        m.Id = id;
        return m;
    }

    private static Series MakeSeries(Guid id, string name, int? year)
    {
        var s = (Series)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Series));
        typeof(BaseItem).GetProperty("Id")!.SetValue(s, id);
        s.Name = name;
        s.ProductionYear = year;
        return s;
    }

    [Fact]
    public async Task NoRows_NoOp_MarkerSet()
    {
        var s = await Build().RunAsync(default);
        Assert.Equal(0, s.Scanned);
        Assert.True(s.MarkerSet);
        Assert.NotNull(await _db.GetMetaAsync(StubLayoutMigration.MarkerKey, default));
    }

    [Fact]
    public async Task LegacyMovieRow_MovedAndDbUpdated()
    {
        var id = Guid.NewGuid();
        var moviesDir = Path.Combine(_tempRoot, "movies");
        var oldName = "The_Boys__phantom_tmdb1234.mp4";
        var oldPath = Path.Combine(moviesDir, oldName);
        // Use the real splash via the stub manager to create a symlink.
        await _stubs.CreateAsync("The_Boys__phantom_tmdb1234", 999000, PhantomMediaKind.Movie, default);
        // Manually plant a legacy-named symlink pointing at the splash.
        File.CreateSymbolicLink(oldPath, Path.Combine(_cacheRoot, "splash.mp4"));

        var movie = MakeMovie(id, "The Boys", 2019);
        _lib.Setup(l => l.GetItemById(id)).Returns(movie);

        await _db.UpsertPhantomItemAsync(id, new PhantomItemRow
        {
            ItemGuid = id, TmdbId = 1234, Type = "movie",
            State = PhantomItemState.Virtual,
            FirstSeen = DateTimeOffset.UtcNow, LastTouched = DateTimeOffset.UtcNow,
            StubPath = oldPath,
        }, default);

        var s = await Build().RunAsync(default);
        Assert.Equal(1, s.Migrated);

        var expectedNew = Path.Combine(moviesDir, "The Boys (2019) [tmdbid-1234].mp4");
        Assert.True(File.Exists(expectedNew) || new FileInfo(expectedNew).Exists,
            $"Expected new path {expectedNew}");
        Assert.False(File.Exists(oldPath) || new FileInfo(oldPath).Exists,
            "Old path should be gone");

        var row = await _db.GetPhantomItemAsync(id, default);
        Assert.Equal(expectedNew, row!.StubPath);
        Assert.Equal(expectedNew, movie.Path);
        Assert.True(movie.IsLocked);
    }

    [Fact]
    public async Task LegacySeriesRow_DirectoryRenamedRecursively()
    {
        var id = Guid.NewGuid();
        // Plant a legacy series stub tree.
        var oldDir = Path.Combine(_tempRoot, "shows", "Severance__phantom_tmdb95396");
        var oldSeason = Path.Combine(oldDir, "Season 01");
        Directory.CreateDirectory(oldSeason);
        var oldEp = Path.Combine(oldSeason, "Severance__phantom_tmdb95396 S01E01.mp4");
        File.WriteAllText(oldEp, "stub");

        var series = MakeSeries(id, "Severance", 2022);
        _lib.Setup(l => l.GetItemById(id)).Returns(series);

        await _db.UpsertPhantomItemAsync(id, new PhantomItemRow
        {
            ItemGuid = id, TmdbId = 95396, Type = "series",
            State = PhantomItemState.Virtual,
            FirstSeen = DateTimeOffset.UtcNow, LastTouched = DateTimeOffset.UtcNow,
            StubPath = oldDir,
        }, default);

        var s = await Build().RunAsync(default);
        Assert.Equal(1, s.Migrated);

        var expectedDir = Path.Combine(_tempRoot, "shows", "Severance (2022) [tmdbid-95396]");
        Assert.True(Directory.Exists(expectedDir));
        Assert.False(Directory.Exists(oldDir));
        // Inner files moved along with the dir.
        Assert.True(File.Exists(Path.Combine(expectedDir, "Season 01",
            "Severance__phantom_tmdb95396 S01E01.mp4")));

        var row = await _db.GetPhantomItemAsync(id, default);
        Assert.Equal(expectedDir, row!.StubPath);
        Assert.Equal(expectedDir, series.Path);
    }

    [Fact]
    public async Task AlreadyNewFormat_Skipped()
    {
        var id = Guid.NewGuid();
        var newPath = Path.Combine(_tempRoot, "movies", "The Boys (2019) [tmdbid-1234].mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        File.WriteAllText(newPath, "stub");

        await _db.UpsertPhantomItemAsync(id, new PhantomItemRow
        {
            ItemGuid = id, TmdbId = 1234, Type = "movie",
            State = PhantomItemState.Virtual,
            FirstSeen = DateTimeOffset.UtcNow, LastTouched = DateTimeOffset.UtcNow,
            StubPath = newPath,
        }, default);

        var s = await Build().RunAsync(default);
        Assert.Equal(0, s.Migrated);
        Assert.Equal(1, s.AlreadyNewFormat);
        Assert.True(s.MarkerSet);
        Assert.True(File.Exists(newPath));
    }

    [Fact]
    public async Task MissingBaseItem_OrphanRowSkipped_MarkerStillSet()
    {
        var id = Guid.NewGuid();
        var oldPath = Path.Combine(_tempRoot, "movies", "X__phantom_tmdb777.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
        File.WriteAllText(oldPath, "stub");

        _lib.Setup(l => l.GetItemById(id)).Returns((BaseItem?)null);
        await _db.UpsertPhantomItemAsync(id, new PhantomItemRow
        {
            ItemGuid = id, TmdbId = 777, Type = "movie",
            State = PhantomItemState.Virtual,
            FirstSeen = DateTimeOffset.UtcNow, LastTouched = DateTimeOffset.UtcNow,
            StubPath = oldPath,
        }, default);

        var s = await Build().RunAsync(default);
        Assert.Equal(0, s.Migrated);
        Assert.Equal(1, s.SkippedNoBaseItem);
        Assert.Equal(0, s.Failed);
        Assert.True(s.MarkerSet);
    }

    [Fact]
    public async Task Idempotent_SecondRunIsNoOp()
    {
        var id = Guid.NewGuid();
        var oldPath = Path.Combine(_tempRoot, "movies", "Foo__phantom_tmdb55.mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
        File.WriteAllText(oldPath, "stub");

        var movie = MakeMovie(id, "Foo", 2020);
        _lib.Setup(l => l.GetItemById(id)).Returns(movie);
        await _db.UpsertPhantomItemAsync(id, new PhantomItemRow
        {
            ItemGuid = id, TmdbId = 55, Type = "movie",
            State = PhantomItemState.Virtual,
            FirstSeen = DateTimeOffset.UtcNow, LastTouched = DateTimeOffset.UtcNow,
            StubPath = oldPath,
        }, default);

        var s1 = await Build().RunAsync(default);
        Assert.Equal(1, s1.Migrated);

        var s2 = await Build().RunAsync(default);
        // Second run: marker already set, early-out.
        Assert.True(s2.AlreadyComplete);
        Assert.Equal(0, s2.Migrated);
    }

    [Fact]
    public async Task DestinationConflict_SkippedWithoutClobber()
    {
        var id = Guid.NewGuid();
        var oldPath = Path.Combine(_tempRoot, "movies", "Foo__phantom_tmdb99.mp4");
        var dest = Path.Combine(_tempRoot, "movies", "Foo (2021) [tmdbid-99].mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(oldPath)!);
        File.WriteAllText(oldPath, "stub-old");
        File.WriteAllText(dest, "pre-existing-do-not-clobber");

        var movie = MakeMovie(id, "Foo", 2021);
        _lib.Setup(l => l.GetItemById(id)).Returns(movie);
        await _db.UpsertPhantomItemAsync(id, new PhantomItemRow
        {
            ItemGuid = id, TmdbId = 99, Type = "movie",
            State = PhantomItemState.Virtual,
            FirstSeen = DateTimeOffset.UtcNow, LastTouched = DateTimeOffset.UtcNow,
            StubPath = oldPath,
        }, default);

        var s = await Build().RunAsync(default);
        Assert.Equal(0, s.Migrated);
        Assert.Equal(1, s.SkippedConflict);
        Assert.True(File.Exists(oldPath));
        Assert.Equal("pre-existing-do-not-clobber", File.ReadAllText(dest));
    }
}
