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

    [Theory]
    [InlineData("movie", 9911, "phantom:movie_9911", -1, -1)]
    [InlineData("episode", 9912, "phantom:episode_9912_s01e02", 1, 2)]
    public async Task OpenMediaSource_FusePathMissing_EagerReregistersOnceThenSucceeds(
        string type, int tmdb, string token, int seasonKey, int episodeKey)
    {
        // The primary materialise registers and writes materialised_state but the
        // FUSE path gostream is expected to expose does NOT appear within the first
        // bounded poll window. The provider must then issue EXACTLY ONE eager
        // re-register and, on its success, check the FUSE file again — which now
        // appears because the eager re-register is what made gostream expose it.
        var path = Path.Combine(_root, type + "-reregister.mkv");

        var call = 0;
        var fuseFileChecksAfterReregister = 0;
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Strict);
        materialiser
            .Setup(m => m.MaterialiseAsync(tmdb, type, It.IsAny<int?>(), It.IsAny<int?>(), MaterialiseTrigger.Play, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                var n = Interlocked.Increment(ref call);
                if (n == 1)
                {
                    // Primary materialise: register succeeds and state is written,
                    // but the FUSE file is NOT yet present.
                    await _db.InsertMaterialisedStateAsync(tmdb, type, seasonKey, episodeKey, "/stub", path, CancellationToken.None);
                }
                else
                {
                    // Eager re-register (the retry under test): this is what finally
                    // makes gostream expose the FUSE path.
                    Interlocked.Increment(ref fuseFileChecksAfterReregister);
                    await File.WriteAllTextAsync(path, "x", CancellationToken.None);
                }

                return MaterialisationOutcome.Success(path, "/stub");
            });

        var provider = CreateProvider(materialiser);

        var opened = await provider.OpenMediaSource(token, new(), CancellationToken.None);

        Assert.Equal(path, opened.MediaSource.Path);
        // Exactly one eager re-register (call #2) beyond the primary materialise,
        // and the FUSE file was checked again after it succeeded (the open resolved).
        Assert.Equal(2, call);
        Assert.Equal(1, fuseFileChecksAfterReregister);
        Assert.True(File.Exists(path));
    }

    [Theory]
    [InlineData("movie", 9921, "phantom:movie_9921", -1, -1)]
    [InlineData("episode", 9922, "phantom:episode_9922_s01e02", 1, 2)]
    public async Task OpenMediaSource_FusePathMissing_EagerReregisterFails_FailsFastNoExtraAttempt(
        string type, int tmdb, string token, int seasonKey, int episodeKey)
    {
        // The FUSE file never appears. After the first poll window the provider
        // issues one eager re-register; that call itself FAILS, so the provider
        // must fail fast to gostream_cannot_fetch (FileNotFoundException, which
        // OpenMediaSource classifies as CauseGostreamCannotFetch and re-throws) with
        // NO further re-register attempt — no stacked retries, no infinite loop.
        var path = Path.Combine(_root, type + "-reregister-fail.mkv");

        var call = 0;
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Strict);
        materialiser
            .Setup(m => m.MaterialiseAsync(tmdb, type, It.IsAny<int?>(), It.IsAny<int?>(), MaterialiseTrigger.Play, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                var n = Interlocked.Increment(ref call);
                if (n == 1)
                {
                    // Primary materialise: register succeeds, state written, no file.
                    await _db.InsertMaterialisedStateAsync(tmdb, type, seasonKey, episodeKey, "/stub", path, CancellationToken.None);
                    return MaterialisationOutcome.Success(path, "/stub");
                }

                // Eager re-register fails.
                return MaterialisationOutcome.ErrorResult("gostream register failed");
            });

        var provider = CreateProvider(materialiser);

        await Assert.ThrowsAsync<FileNotFoundException>(
            () => provider.OpenMediaSource(token, new(), CancellationToken.None));

        // Exactly one eager re-register attempt (call #2) — no third attempt.
        Assert.Equal(2, call);
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("movie", 9931, "phantom:movie_9931", -1, -1)]
    [InlineData("episode", 9932, "phantom:episode_9932_s01e02", 1, 2)]
    public async Task OpenMediaSource_AlreadyInProgress_PollTimeout_EagerReclaimReclaimsStaleClaim(
        string type, int tmdb, string token, int seasonKey, int episodeKey)
    {
        // The primary MaterialiseAsync call observes AlreadyInProgress (a concurrent request
        // already holds the in-flight claim). WaitForMaterialisedStateAsync's poll loop never
        // sees a materialised_state row because the claim it is waiting behind is actually
        // leaked/stale. On poll-timeout the provider must issue exactly ONE additional
        // MaterialiseAsync call for the same item: since the claim really is stale, this second
        // call reclaims it (Materialiser's own steal-if-stale logic) and performs a real
        // materialise, writing the materialised_state row. The provider must then re-read/extend
        // the wait by one bounded window and resolve successfully — no timeout.
        var path = Path.Combine(_root, type + "-reclaim.mkv");
        await File.WriteAllTextAsync(path, "x", CancellationToken.None);

        var call = 0;
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Strict);
        materialiser
            .Setup(m => m.MaterialiseAsync(tmdb, type, It.IsAny<int?>(), It.IsAny<int?>(), MaterialiseTrigger.Play, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                var n = Interlocked.Increment(ref call);
                if (n == 1)
                {
                    // Primary call: another request already holds the (leaked) claim.
                    return MaterialisationOutcome.AlreadyInProgress;
                }

                // Eager reclaim call (the retry under test): the claim was actually stale, so
                // this call reclaims it and performs the real materialise.
                await _db.InsertMaterialisedStateAsync(tmdb, type, seasonKey, episodeKey, "/stub", path, CancellationToken.None);
                return MaterialisationOutcome.Success(path, "/stub");
            });

        var provider = CreateProvider(materialiser);

        var opened = await provider.OpenMediaSource(token, new(), CancellationToken.None);

        Assert.Equal(path, opened.MediaSource.Path);
        // Exactly one eager reclaim call (call #2) beyond the primary AlreadyInProgress call.
        Assert.Equal(2, call);
    }

    [Theory]
    [InlineData("movie", 9941, "phantom:movie_9941")]
    [InlineData("episode", 9942, "phantom:episode_9942_s01e02")]
    public async Task OpenMediaSource_AlreadyInProgress_PollTimeout_EagerReclaimStillInProgress_FailsFastNoThirdCall(
        string type, int tmdb, string token)
    {
        // The primary call observes AlreadyInProgress; the poll loop times out with no
        // materialised_state row. The provider issues the one eager reclaim MaterialiseAsync
        // call, but the original claim is still FRESH, so the reclaim attempt itself returns
        // AlreadyInProgress again. The provider must fail fast to the existing TimeoutException
        // path with no third call -- no stacked retries, no unbounded backoff.
        var call = 0;
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Strict);
        materialiser
            .Setup(m => m.MaterialiseAsync(tmdb, type, It.IsAny<int?>(), It.IsAny<int?>(), MaterialiseTrigger.Play, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                Interlocked.Increment(ref call);
                return Task.FromResult(MaterialisationOutcome.AlreadyInProgress);
            });

        var provider = CreateProvider(materialiser);

        await Assert.ThrowsAsync<TimeoutException>(
            () => provider.OpenMediaSource(token, new(), CancellationToken.None));

        // Exactly one eager reclaim attempt (call #2) beyond the primary call -- no third call.
        Assert.Equal(2, call);
    }

    private PhantomMaterialisingMediaSourceProvider CreateProvider()
    {
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Strict);
        return CreateProvider(materialiser);
    }

    private PhantomMaterialisingMediaSourceProvider CreateProvider(Mock<IMaterialiser> materialiser)
    {
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
