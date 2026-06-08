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

    private static Movie MakeMovie(Guid id, string name, int? year, string? path = null)
    {
        var m = new Movie { Name = name, ProductionYear = year };
        m.Id = id;
        if (path is not null) m.Path = path;
        return m;
    }

    private static Series MakeSeries(Guid id, string name, int? year, string? path = null)
    {
        var s = (Series)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(Series));
        typeof(BaseItem).GetProperty("Id")!.SetValue(s, id);
        s.Name = name;
        s.ProductionYear = year;
        if (path is not null) s.Path = path;
        return s;
    }

    /// <summary>
    /// Helper: insert a Virtual phantom_items row mirroring what
    /// SuggestionsContributor / SeriesIngestor produce — i.e. with
    /// stub_path = NULL. The migration must source the on-disk path
    /// from BaseItem.Path, not from this row.
    /// </summary>
    private Task InsertVirtualRow(Guid id, int tmdbId, string type, string? stubPath = null)
        => _db.UpsertPhantomItemAsync(id, new PhantomItemRow
        {
            ItemGuid = id, TmdbId = tmdbId, Type = type,
            State = PhantomItemState.Virtual,
            FirstSeen = DateTimeOffset.UtcNow, LastTouched = DateTimeOffset.UtcNow,
            StubPath = stubPath,
        }, default);

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
        await _stubs.CreateAsync("The_Boys__phantom_tmdb1234", 999000, PhantomMediaKind.Movie, default);
        File.CreateSymbolicLink(oldPath, Path.Combine(_cacheRoot, "splash.mp4"));

        // Production reality: phantom_items.stub_path is NULL for
        // Suggestions-created Virtual rows; the legacy path lives on
        // BaseItem.Path.
        var movie = MakeMovie(id, "The Boys", 2019, path: oldPath);
        _lib.Setup(l => l.GetItemById(id)).Returns(movie);

        await InsertVirtualRow(id, 1234, "movie", stubPath: null);

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
        var oldDir = Path.Combine(_tempRoot, "shows", "Severance__phantom_tmdb95396");
        var oldSeason = Path.Combine(oldDir, "Season 01");
        Directory.CreateDirectory(oldSeason);
        var oldEp = Path.Combine(oldSeason, "Severance__phantom_tmdb95396 S01E01.mp4");
        File.WriteAllText(oldEp, "stub");

        var series = MakeSeries(id, "Severance", 2022, path: oldDir);
        _lib.Setup(l => l.GetItemById(id)).Returns(series);

        await InsertVirtualRow(id, 95396, "series", stubPath: null);

        var s = await Build().RunAsync(default);
        Assert.Equal(1, s.Migrated);

        var expectedDir = Path.Combine(_tempRoot, "shows", "Severance (2022) [tmdbid-95396]");
        Assert.True(Directory.Exists(expectedDir));
        Assert.False(Directory.Exists(oldDir));
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

        var movie = MakeMovie(id, "The Boys", 2019, path: newPath);
        _lib.Setup(l => l.GetItemById(id)).Returns(movie);

        // Even with stub_path already populated, the migration uses
        // BaseItem.Path. Pre-populate matching here to model the
        // Materialised-then-demoted case.
        await InsertVirtualRow(id, 1234, "movie", stubPath: newPath);

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
        _lib.Setup(l => l.GetItemById(id)).Returns((BaseItem?)null);
        await InsertVirtualRow(id, 777, "movie", stubPath: null);

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

        var movie = MakeMovie(id, "Foo", 2020, path: oldPath);
        _lib.Setup(l => l.GetItemById(id)).Returns(movie);
        await InsertVirtualRow(id, 55, "movie", stubPath: null);

        var s1 = await Build().RunAsync(default);
        Assert.Equal(1, s1.Migrated);

        var s2 = await Build().RunAsync(default);
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

        var movie = MakeMovie(id, "Foo", 2021, path: oldPath);
        _lib.Setup(l => l.GetItemById(id)).Returns(movie);
        await InsertVirtualRow(id, 99, "movie", stubPath: null);

        var s = await Build().RunAsync(default);
        Assert.Equal(0, s.Migrated);
        Assert.Equal(1, s.SkippedConflict);
        Assert.True(File.Exists(oldPath));
        Assert.Equal("pre-existing-do-not-clobber", File.ReadAllText(dest));
    }

    // ---------- New scenarios covering the bug fix ----------

    [Fact]
    public async Task StubPath_null_in_db_but_BaseItem_path_legacy_migrates_via_baseitem_path()
    {
        // This is the production case that triggered the bug: 19478
        // Virtual rows with stub_path=NULL but BaseItem.Path holding
        // the legacy `_..._phantom_tmdbN` filename.
        var id = Guid.NewGuid();
        var moviesDir = Path.Combine(_tempRoot, "movies");
        var oldPath = Path.Combine(moviesDir, "Back_to_the_Future__phantom_tmdb105.mp4");
        Directory.CreateDirectory(moviesDir);
        File.WriteAllText(oldPath, "stub");

        var movie = MakeMovie(id, "Back to the Future", 1985, path: oldPath);
        _lib.Setup(l => l.GetItemById(id)).Returns(movie);

        // stub_path explicitly NULL.
        await InsertVirtualRow(id, 105, "movie", stubPath: null);

        var s = await Build().RunAsync(default);

        Assert.Equal(1, s.Migrated);
        Assert.Equal(0, s.SkippedNoPath);
        Assert.Equal(0, s.SkippedNotPhantom);

        var expectedNew = Path.Combine(moviesDir, "Back to the Future (1985) [tmdbid-105].mp4");
        Assert.True(File.Exists(expectedNew));
        Assert.False(File.Exists(oldPath));

        var row = await _db.GetPhantomItemAsync(id, default);
        Assert.Equal(expectedNew, row!.StubPath);
        Assert.Equal(expectedNew, movie.Path);
    }

    [Fact]
    public async Task BaseItem_path_already_new_format_db_stub_path_null_syncs_db()
    {
        // BaseItem.Path is already on the new format (perhaps from a
        // previous partial migration via the shell script). phantom
        // row's stub_path is NULL. The migration should NOT rename,
        // but SHOULD bring stub_path into sync.
        var id = Guid.NewGuid();
        var newPath = Path.Combine(_tempRoot, "movies", "Already Migrated (2020) [tmdbid-321].mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
        File.WriteAllText(newPath, "stub");

        var movie = MakeMovie(id, "Already Migrated", 2020, path: newPath);
        _lib.Setup(l => l.GetItemById(id)).Returns(movie);

        await InsertVirtualRow(id, 321, "movie", stubPath: null);

        var s = await Build().RunAsync(default);

        Assert.Equal(0, s.Migrated);
        Assert.Equal(1, s.AlreadyNewFormat);
        Assert.True(File.Exists(newPath));

        var row = await _db.GetPhantomItemAsync(id, default);
        Assert.Equal(newPath, row!.StubPath); // synced
        Assert.Equal(newPath, movie.Path);    // unchanged
    }

    [Fact]
    public async Task BaseItem_path_null_or_empty_skips_with_SkippedNoPath_counter()
    {
        var idNull = Guid.NewGuid();
        var idEmpty = Guid.NewGuid();

        // Movie with no Path set at all.
        var movieNull = MakeMovie(idNull, "PathLess", 2019, path: null);
        _lib.Setup(l => l.GetItemById(idNull)).Returns(movieNull);
        await InsertVirtualRow(idNull, 1, "movie", stubPath: null);

        // Movie with explicitly empty path.
        var movieEmpty = MakeMovie(idEmpty, "PathEmpty", 2019, path: "");
        _lib.Setup(l => l.GetItemById(idEmpty)).Returns(movieEmpty);
        await InsertVirtualRow(idEmpty, 2, "movie", stubPath: null);

        var s = await Build().RunAsync(default);

        Assert.Equal(0, s.Migrated);
        Assert.Equal(2, s.SkippedNoPath);
        Assert.Equal(0, s.Failed);
        Assert.True(s.MarkerSet, "SkippedNoPath does not block the marker");
    }

    [Fact]
    public async Task BaseItem_path_not_phantom_skips_with_warning_and_SkippedNotPhantom_counter()
    {
        var id = Guid.NewGuid();
        // BaseItem.Path points outside the phantom stub tree — neither
        // sentinel nor token. This is an inconsistency (phantom row
        // exists, BaseItem points at a real file).
        var weirdPath = Path.Combine(_tempRoot, "elsewhere", "Real Movie (2020).mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(weirdPath)!);
        File.WriteAllText(weirdPath, "real");

        var movie = MakeMovie(id, "Real Movie", 2020, path: weirdPath);
        _lib.Setup(l => l.GetItemById(id)).Returns(movie);
        await InsertVirtualRow(id, 999, "movie", stubPath: null);

        var s = await Build().RunAsync(default);

        Assert.Equal(0, s.Migrated);
        Assert.Equal(1, s.SkippedNotPhantom);
        Assert.Equal(0, s.Failed);
        // Per the compromise: SkippedNotPhantom does not block the
        // marker (otherwise one bad row blocks all 19478 on every
        // startup). It is logged as a warning instead.
        Assert.True(s.MarkerSet);
        // Did not touch the real file.
        Assert.Equal("real", File.ReadAllText(weirdPath));
    }
}
