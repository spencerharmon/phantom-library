using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public sealed class GostreamPathResolverTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "phantom-gostream-root-" + Guid.NewGuid().ToString("N"));
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "phantom-gostream-root-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void ResolvePath_MissingReturnedPath_UsesConfiguredRootWhenSameFileExists()
    {
        Directory.CreateDirectory(_root);
        var good = Path.Combine(_root, "Backrooms_2026_1080p_fdf0872d.mkv");
        File.WriteAllText(good, "x");

        var resolved = GostreamPathResolver.ResolvePath(
            "/mnt/gostream-mkv-virtual/movies/Backrooms_2026_1080p_fdf0872d.mkv",
            _root);

        Assert.Equal(good, resolved);
    }

    [Fact]
    public void ResolvePath_ExistingReturnedPath_KeepsReturnedPath()
    {
        Directory.CreateDirectory(_root);
        var returned = Path.Combine(_root, "real.mkv");
        File.WriteAllText(returned, "x");
        var otherRoot = Path.Combine(_root, "other");
        Directory.CreateDirectory(otherRoot);
        File.WriteAllText(Path.Combine(otherRoot, "real.mkv"), "y");

        var resolved = GostreamPathResolver.ResolvePath(returned, otherRoot);

        Assert.Equal(returned, resolved);
    }

    [Fact]
    public async Task EnumerateOrphanMovies_ExcludesMaterialisedRowResolvedToConfiguredRoot()
    {
        var moviesRoot = Path.Combine(_root, "movies");
        Directory.CreateDirectory(moviesRoot);
        var good = Path.Combine(moviesRoot, "Backrooms_2026_1080p_fdf0872d.mkv");
        File.WriteAllText(good, "x");

        using var db = new PhantomDb(_dbPath);
        await db.InsertMaterialisedStateAsync(
            1083381,
            "movie",
            -1,
            -1,
            "/stub/Backrooms_2026_1080p_fdf0872d.mkv",
            "/mnt/gostream-mkv-virtual/movies/Backrooms_2026_1080p_fdf0872d.mkv",
            CancellationToken.None);

        var enumerator = new GostreamFilesystemEnumerator(db, NullLogger<GostreamFilesystemEnumerator>.Instance)
        {
            MoviesRootOverride = moviesRoot,
        };

        var got = await enumerator.EnumerateOrphanMoviesAsync(new HashSet<int>(), CancellationToken.None);

        Assert.Empty(got);
    }
}
