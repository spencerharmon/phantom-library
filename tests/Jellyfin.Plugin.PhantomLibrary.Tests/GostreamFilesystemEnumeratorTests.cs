using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class GostreamFilesystemEnumeratorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _moviesRoot;
    private readonly string _showsRoot;
    private readonly PhantomDb _db;

    public GostreamFilesystemEnumeratorTests()
    {
        var stamp = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), "phantom-fs-tests-" + stamp + ".db");
        _moviesRoot = Path.Combine(Path.GetTempPath(), "phantom-fs-movies-" + stamp);
        _showsRoot = Path.Combine(Path.GetTempPath(), "phantom-fs-shows-" + stamp);
        Directory.CreateDirectory(_moviesRoot);
        Directory.CreateDirectory(_showsRoot);
        _db = new PhantomDb(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_moviesRoot)) Directory.Delete(_moviesRoot, true); } catch { }
        try { if (Directory.Exists(_showsRoot)) Directory.Delete(_showsRoot, true); } catch { }
    }

    private GostreamFilesystemEnumerator NewEnumerator()
    {
        return new GostreamFilesystemEnumerator(_db, NullLogger<GostreamFilesystemEnumerator>.Instance)
        {
            MoviesRootOverride = _moviesRoot,
            ShowsRootOverride = _showsRoot,
        };
    }

    [Fact]
    public async Task Orphans_EmptyRoot_ReturnsEmpty()
    {
        var e = NewEnumerator();
        var got = await e.EnumerateOrphanMoviesAsync(new HashSet<int>(), CancellationToken.None);
        Assert.Empty(got);
    }

    [Fact]
    public async Task Orphans_IncludesUnknownVideoFile_WithNullTmdb()
    {
        var path = Path.Combine(_moviesRoot, "Unknown Movie (2020).mkv");
        File.WriteAllText(path, string.Empty);

        var e = NewEnumerator();
        var got = await e.EnumerateOrphanMoviesAsync(new HashSet<int>(), CancellationToken.None);

        Assert.Single(got);
        Assert.Equal(path, got[0].Path);
        Assert.Null(got[0].TmdbId);
    }

    [Fact]
    public async Task Orphans_ExcludesMaterialisedFusePath()
    {
        var materialised = Path.Combine(_moviesRoot, "Materialised (2021).mkv");
        var orphan = Path.Combine(_moviesRoot, "Orphan (2022).mkv");
        File.WriteAllText(materialised, string.Empty);
        File.WriteAllText(orphan, string.Empty);

        await _db.InsertMaterialisedStateAsync(42, "movie", -1, -1, "/stub/m.mkv", materialised, CancellationToken.None);

        var e = NewEnumerator();
        var got = await e.EnumerateOrphanMoviesAsync(new HashSet<int>(), CancellationToken.None);

        Assert.Single(got);
        Assert.Equal(orphan, got[0].Path);
    }

    [Fact]
    public async Task Orphans_SkipsNonVideoFiles()
    {
        File.WriteAllText(Path.Combine(_moviesRoot, "readme.txt"), "ignore me");
        File.WriteAllText(Path.Combine(_moviesRoot, "movie.mkv"), string.Empty);

        var e = NewEnumerator();
        var got = await e.EnumerateOrphanMoviesAsync(new HashSet<int>(), CancellationToken.None);

        Assert.Single(got);
        Assert.EndsWith("movie.mkv", got[0].Path, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Orphans_RecursesIntoSubdirectories()
    {
        var sub = Path.Combine(_moviesRoot, "subfolder");
        Directory.CreateDirectory(sub);
        var deep = Path.Combine(sub, "Deep (2023).mkv");
        File.WriteAllText(deep, string.Empty);

        var e = NewEnumerator();
        var got = await e.EnumerateOrphanMoviesAsync(new HashSet<int>(), CancellationToken.None);

        Assert.Single(got);
        Assert.Equal(deep, got[0].Path);
    }

    [Fact]
    public async Task LookupOrphanByHash_FindsMatchingFile()
    {
        var path = Path.Combine(_moviesRoot, "Findable (2020).mkv");
        File.WriteAllText(path, string.Empty);
        var expectedHash = ChannelItemId.ForOrphanPath(path).OrphanHash!;

        var e = NewEnumerator();
        var got = await e.LookupOrphanByHashAsync(expectedHash, CancellationToken.None);

        Assert.NotNull(got);
        Assert.Equal(path, got!.Path);
    }

    [Fact]
    public async Task LookupOrphanByHash_ReturnsNullForUnknownHash()
    {
        File.WriteAllText(Path.Combine(_moviesRoot, "a.mkv"), string.Empty);
        var e = NewEnumerator();
        var got = await e.LookupOrphanByHashAsync("ffffffffffffffff", CancellationToken.None);
        Assert.Null(got);
    }

    [Fact]
    public async Task LookupOrphanByHash_ExcludesMaterialisedFile()
    {
        var path = Path.Combine(_moviesRoot, "Mat.mkv");
        File.WriteAllText(path, string.Empty);
        await _db.InsertMaterialisedStateAsync(7, "movie", -1, -1, "/stub", path, CancellationToken.None);
        var hash = ChannelItemId.ForOrphanPath(path).OrphanHash!;

        var e = NewEnumerator();
        var got = await e.LookupOrphanByHashAsync(hash, CancellationToken.None);
        Assert.Null(got);
    }

    [Fact]
    public async Task Series_EnumeratesSeriesSeasonsAndEpisodes()
    {
        var seriesDir = Path.Combine(_showsRoot, "My Show (2020)");
        var season1 = Path.Combine(seriesDir, "Season 1");
        var season2 = Path.Combine(seriesDir, "Season 2");
        Directory.CreateDirectory(season1);
        Directory.CreateDirectory(season2);
        File.WriteAllText(Path.Combine(season1, "S01E01.mkv"), string.Empty);
        File.WriteAllText(Path.Combine(season1, "S01E02.mkv"), string.Empty);
        File.WriteAllText(Path.Combine(season2, "S02E01.mkv"), string.Empty);

        var e = NewEnumerator();
        var got = await e.EnumerateSeriesAsync(CancellationToken.None);

        Assert.Single(got);
        var s = got[0];
        Assert.Equal(seriesDir, s.DirectoryPath);
        Assert.Equal(2, s.Seasons.Count);
        var s1 = s.Seasons.Single(x => x.SeasonNumber == 1);
        var s2 = s.Seasons.Single(x => x.SeasonNumber == 2);
        Assert.Equal(2, s1.Episodes.Count);
        Assert.Single(s2.Episodes);
    }

    [Fact]
    public async Task Series_ExcludesMaterialisedEpisodeFusePath()
    {
        var seriesDir = Path.Combine(_showsRoot, "My Show (2020)");
        var season1 = Path.Combine(seriesDir, "Season.01");
        Directory.CreateDirectory(season1);
        var materialised = Path.Combine(season1, "My_Show_S01E01_aaaaaaa.mkv");
        var external = Path.Combine(season1, "My_Show_S01E02_bbbbbbb.mkv");
        File.WriteAllText(materialised, string.Empty);
        File.WriteAllText(external, string.Empty);
        await _db.InsertMaterialisedStateAsync(42, "episode", 1, 1, "/stub", materialised, CancellationToken.None);

        var e = NewEnumerator();
        var got = await e.EnumerateSeriesAsync(CancellationToken.None);

        var series = Assert.Single(got);
        var season = Assert.Single(series.Seasons);
        var episode = Assert.Single(season.Episodes);
        Assert.Equal(external, episode.Path);
    }

    [Fact]
    public async Task Series_EmptyRoot_ReturnsEmpty()
    {
        var e = NewEnumerator();
        var got = await e.EnumerateSeriesAsync(CancellationToken.None);
        Assert.Empty(got);
    }
}
