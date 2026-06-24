using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class PhantomMaterialisingMediaSourceProviderTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _root;
    private readonly PhantomDb _db;

    public PhantomMaterialisingMediaSourceProviderTests()
    {
        var stamp = Guid.NewGuid().ToString("N");
        _dbPath = Path.Combine(Path.GetTempPath(), "phantom-open-tests-" + stamp + ".db");
        _root = Path.Combine(Path.GetTempPath(), "phantom-open-root-" + stamp);
        Directory.CreateDirectory(_root);
        _db = new PhantomDb(_dbPath);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task OpenMediaSource_MovieMaterialised_ProbesAudioStreams()
    {
        var path = Path.Combine(_root, "movie.mkv");
        await File.WriteAllTextAsync(path, "x", CancellationToken.None);
        await _db.InsertMaterialisedStateAsync(9901, "movie", -1, -1, "/stub", path, CancellationToken.None);
        var provider = CreateProvider();

        var opened = await provider.OpenMediaSource("phantom:movie_9901", new(), CancellationToken.None);

        Assert.Equal(path, opened.MediaSource.Path);
        Assert.Equal(new[] { 1, 2 }, opened.MediaSource.MediaStreams.Where(s => s.Type == MediaStreamType.Audio).Select(s => s.Index));
        Assert.Equal(1, opened.MediaSource.DefaultAudioStreamIndex);
    }

    [Fact]
    public async Task OpenMediaSource_EpisodeMaterialised_ProbesAudioStreams()
    {
        var path = Path.Combine(_root, "episode.mkv");
        await File.WriteAllTextAsync(path, "x", CancellationToken.None);
        await _db.InsertMaterialisedStateAsync(9902, "episode", 1, 2, "/stub", path, CancellationToken.None);
        var provider = CreateProvider();

        var opened = await provider.OpenMediaSource("phantom:episode_9902_s01e02", new(), CancellationToken.None);

        Assert.Equal(path, opened.MediaSource.Path);
        Assert.Equal(new[] { 1, 2 }, opened.MediaSource.MediaStreams.Where(s => s.Type == MediaStreamType.Audio).Select(s => s.Index));
        Assert.Equal(1, opened.MediaSource.DefaultAudioStreamIndex);
    }

    private PhantomMaterialisingMediaSourceProvider CreateProvider()
    {
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Strict);
        var encoder = new Mock<IMediaEncoder>(MockBehavior.Loose);
        encoder.Setup(e => e.GetMediaInfo(It.IsAny<MediaInfoRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MediaInfo
            {
                MediaStreams = new List<MediaStream>
                {
                    new() { Index = 0, Type = MediaStreamType.Video, IsDefault = true },
                    new() { Index = 1, Type = MediaStreamType.Audio, Language = "pol", IsDefault = true },
                    new() { Index = 2, Type = MediaStreamType.Audio, Language = "eng" },
                },
            });
        return new PhantomMaterialisingMediaSourceProvider(
            _db,
            materialiser.Object,
            encoder.Object,
            NullLogger<PhantomMaterialisingMediaSourceProvider>.Instance,
            () => new PluginConfiguration { FusePathWaitTimeoutSeconds = 1, FusePathPollIntervalMilliseconds = 50 });
    }
}
