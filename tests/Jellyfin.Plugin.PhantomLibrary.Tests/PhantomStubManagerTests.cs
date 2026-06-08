using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Library;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class PhantomStubManagerTests : IDisposable
{
    private readonly string _tempRoot;
    private readonly string _cacheRoot;
    private readonly Mock<IApplicationPaths> _paths = new();
    private readonly PluginConfiguration _cfg = new();

    public PhantomStubManagerTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "phantom_stub_test_" + Guid.NewGuid().ToString("N"));
        _cacheRoot = Path.Combine(Path.GetTempPath(), "phantom_stub_cache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheRoot);
        _cfg.PhantomStubRoot = _tempRoot;
        _paths.SetupGet(p => p.CachePath).Returns(_cacheRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempRoot, recursive: true); } catch { }
        try { Directory.Delete(_cacheRoot, recursive: true); } catch { }
    }

    private PhantomStubManager Build() => new(_paths.Object, NullLogger<PhantomStubManager>.Instance, () => _cfg);

    [Fact]
    public async Task DeriveFilename_PureAndDeterministic()
    {
        var m = Build();
        await m.BootstrapAsync(default);
        var a = m.DeriveFilename("Hello World", 42, PhantomMediaKind.Movie);
        var b = m.DeriveFilename("Hello World", 42, PhantomMediaKind.Movie);
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task DeriveFilename_DifferentTmdb_DifferentName()
    {
        var m = Build();
        await m.BootstrapAsync(default);
        var a = m.DeriveFilename("X", 1, PhantomMediaKind.Movie);
        var b = m.DeriveFilename("X", 2, PhantomMediaKind.Movie);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public async Task DeriveFilename_SameSafeForm_DifferentTmdb_StillUnique()
    {
        var m = Build();
        await m.BootstrapAsync(default);
        var a = m.DeriveFilename("Foo: Bar!", 1, PhantomMediaKind.Movie);
        var b = m.DeriveFilename("Foo/Bar?", 2, PhantomMediaKind.Movie);
        // Both sanitize to the same safe stem, but the tmdb id keeps them apart.
        Assert.NotEqual(a, b);
        Assert.Contains("__phantom_tmdb1.", a);
        Assert.Contains("__phantom_tmdb2.", b);
    }

    [Fact]
    public async Task DeriveFilename_SentinelAlwaysPresent()
    {
        var m = Build();
        await m.BootstrapAsync(default);
        var name = m.DeriveFilename("Anything", 99, PhantomMediaKind.Series);
        Assert.Contains(PhantomStubManager.Sentinel, name, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeriveFilename_StripsSpecialChars()
    {
        var m = Build();
        await m.BootstrapAsync(default);
        var name = m.DeriveFilename("Hello: World's / Test ☃ Show", 7, PhantomMediaKind.Movie);
        // Only A-Z, 0-9, underscore, dot are allowed.
        var stem = Path.GetFileNameWithoutExtension(name);
        foreach (var ch in stem)
        {
            Assert.True(char.IsLetterOrDigit(ch) || ch == '_',
                $"Unexpected character '{ch}' in '{name}'");
        }
    }

    [Fact]
    public async Task DeriveFilename_EmptyTitle_Untitled()
    {
        var m = Build();
        await m.BootstrapAsync(default);
        var name = m.DeriveFilename("   ", 5, PhantomMediaKind.Movie);
        Assert.StartsWith("untitled__phantom_tmdb5.", name);
    }

    [Fact]
    public async Task CreateAsync_CreatesSymlink_AndIsIdempotent()
    {
        var m = Build();
        await m.BootstrapAsync(default);
        var p1 = await m.CreateAsync("Cool Movie", 1234, PhantomMediaKind.Movie, default);
        Assert.True(File.Exists(p1));
        var fi = new FileInfo(p1);
        Assert.False(string.IsNullOrEmpty(fi.LinkTarget));

        // Second call: same path, no throw.
        var p2 = await m.CreateAsync("Cool Movie", 1234, PhantomMediaKind.Movie, default);
        Assert.Equal(p1, p2);
        Assert.True(File.Exists(p2));
    }

    [Fact]
    public async Task DeleteAsync_RoundTrip()
    {
        var m = Build();
        await m.BootstrapAsync(default);
        var p = await m.CreateAsync("X", 7, PhantomMediaKind.Movie, default);
        Assert.True(File.Exists(p));
        await m.DeleteAsync(p, default);
        Assert.False(File.Exists(p));
        // Idempotent delete.
        await m.DeleteAsync(p, default);
    }

    [Fact]
    public async Task BootstrapAsync_NotWritable_Throws()
    {
        // Point root at a path under a non-existent read-only ancestor.
        // /proc/1 is owned by root and not writable by the test process.
        _cfg.PhantomStubRoot = "/proc/1/cant-create-here";
        var m = Build();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => m.BootstrapAsync(default));
        Assert.Contains("chown", ex.Message, StringComparison.Ordinal);
    }

    // ── PLAN §M13: per-series subdir stub layout ────────────────────

    [Fact]
    public async Task CreateAsync_Series_CreatesPerSeriesDir()
    {
        var m = Build();
        await m.BootstrapAsync(default);
        var dir = await m.CreateAsync("Cool Show", 555, PhantomMediaKind.Series, default);
        Assert.True(Directory.Exists(dir),
            $"Series stub must be a directory; got {dir}");
        Assert.StartsWith(Path.Combine(_tempRoot, "shows"), dir, StringComparison.Ordinal);
        Assert.Contains("__phantom_tmdb555", Path.GetFileName(dir), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_Series_CreatesSeasonOneAndS01E01Symlink()
    {
        var m = Build();
        await m.BootstrapAsync(default);
        var dir = await m.CreateAsync("Show Two", 42, PhantomMediaKind.Series, default);
        var seasonDir = Path.Combine(dir, "Season 01");
        Assert.True(Directory.Exists(seasonDir));

        var (_, _, episodeFile) = m.DeriveSeriesStubPaths("Show Two", 42);
        Assert.True(File.Exists(episodeFile),
            $"Expected episode symlink at {episodeFile}");
        var fi = new FileInfo(episodeFile);
        Assert.False(string.IsNullOrEmpty(fi.LinkTarget),
            "Episode entry must be a symlink to the splash.");
        Assert.Contains(" S01E01.", Path.GetFileName(episodeFile), StringComparison.Ordinal);
        Assert.Contains("__phantom_tmdb42", Path.GetFileName(episodeFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_Series_IsIdempotent()
    {
        var m = Build();
        await m.BootstrapAsync(default);
        var d1 = await m.CreateAsync("Show Three", 77, PhantomMediaKind.Series, default);
        var d2 = await m.CreateAsync("Show Three", 77, PhantomMediaKind.Series, default);
        Assert.Equal(d1, d2);
        Assert.True(Directory.Exists(d2));
        var (_, _, episodeFile) = m.DeriveSeriesStubPaths("Show Three", 77);
        Assert.True(File.Exists(episodeFile));
    }

    [Fact]
    public async Task CreateAsync_Series_ReturnsSeriesDirNotInnerFile()
    {
        var m = Build();
        await m.BootstrapAsync(default);
        var ret = await m.CreateAsync("Show Four", 11, PhantomMediaKind.Series, default);
        Assert.True(Directory.Exists(ret));
        Assert.False(File.Exists(ret) && !Directory.Exists(ret));
        var (seriesDir, _, episodeFile) = m.DeriveSeriesStubPaths("Show Four", 11);
        Assert.Equal(seriesDir, ret);
        Assert.NotEqual(episodeFile, ret);
    }

    [Fact]
    public async Task DeleteAsync_RecursivelyRemovesSeriesDir()
    {
        var m = Build();
        await m.BootstrapAsync(default);
        var dir = await m.CreateAsync("Show Five", 1234, PhantomMediaKind.Series, default);
        Assert.True(Directory.Exists(dir));
        await m.DeleteAsync(dir, default);
        Assert.False(Directory.Exists(dir));
        // Idempotent on missing.
        await m.DeleteAsync(dir, default);
    }

    [Fact]
    public async Task DeleteAsync_RefusesToRemoveDirWithoutSentinel()
    {
        var m = Build();
        await m.BootstrapAsync(default);
        var foreignDir = Path.Combine(_tempRoot, "shows", "not_a_phantom_dir");
        Directory.CreateDirectory(foreignDir);
        var canary = Path.Combine(foreignDir, "keep.txt");
        File.WriteAllText(canary, "do not delete");

        await m.DeleteAsync(foreignDir, default);

        Assert.True(Directory.Exists(foreignDir),
            "DeleteAsync must refuse to recursively delete a directory without the phantom sentinel.");
        Assert.True(File.Exists(canary));
    }
}
