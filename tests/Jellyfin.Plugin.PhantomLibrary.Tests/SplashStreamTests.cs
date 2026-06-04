using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Playback;
using MediaBrowser.Common.Configuration;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class SplashStreamTests : IDisposable
{
    private readonly string _cacheRoot;
    private readonly IApplicationPaths _paths;

    public SplashStreamTests()
    {
        _cacheRoot = Path.Combine(Path.GetTempPath(), "splash_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_cacheRoot);
        var mock = new Mock<IApplicationPaths>();
        mock.SetupGet(p => p.CachePath).Returns(_cacheRoot);
        _paths = mock.Object;
    }

    public void Dispose()
    {
        try { Directory.Delete(_cacheRoot, true); } catch { }
    }

    [Fact]
    public async Task First_Call_Extracts_File()
    {
        var path = await SplashStream.GetLocalPathAsync(_paths, default);
        Assert.True(File.Exists(path));
        var size = new FileInfo(path).Length;
        Assert.True(size > 10_000, $"splash too small ({size} bytes) — embedded resource broken?");
    }

    [Fact]
    public async Task Second_Call_Returns_Same_Path_Without_Re_Extracting()
    {
        var p1 = await SplashStream.GetLocalPathAsync(_paths, default);
        var firstWrite = File.GetLastWriteTimeUtc(p1);

        // Sleep a tick to allow detection of any re-write.
        await Task.Delay(50);

        var p2 = await SplashStream.GetLocalPathAsync(_paths, default);
        Assert.Equal(p1, p2);
        Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(p2));
    }

    [Fact]
    public async Task Version_Mismatch_Triggers_Re_Extract()
    {
        var p1 = await SplashStream.GetLocalPathAsync(_paths, default);
        var versionPath = Path.Combine(_cacheRoot, "PhantomLibrary", "splash.version");
        Assert.True(File.Exists(versionPath));

        // Tamper with the stored version so the freshness check fails.
        await File.WriteAllTextAsync(versionPath, "0.0.0-stale");
        File.Delete(p1); // also remove the file to make the re-extract observable
        Assert.False(File.Exists(p1));

        var p2 = await SplashStream.GetLocalPathAsync(_paths, default);
        Assert.True(File.Exists(p2));
        var stored = (await File.ReadAllTextAsync(versionPath)).Trim();
        Assert.Equal(SplashStream.ResolveAssemblyVersion(), stored);
    }
}
