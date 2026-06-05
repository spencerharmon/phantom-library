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
}
