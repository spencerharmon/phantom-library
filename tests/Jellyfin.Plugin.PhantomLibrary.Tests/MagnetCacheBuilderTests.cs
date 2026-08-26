using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// Regression coverage for p6-magnet-cache-store: the magnet-cache job queue
/// (<c>magnet_cache_jobs</c>) + the Prowlarr-backed <see cref="MagnetCacheBuilder"/>.
/// Verifies (movie AND episode parity):
///  - enqueuing a build job and running the builder populates the item's
///    <c>source_candidates</c> row with the full mocked Prowlarr candidate set;
///  - a stale entry is refreshed on re-run (new candidate set replaces old);
///  - two competing jobs are claimed in priority order.
/// </summary>
public sealed class MagnetCacheBuilderTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), "phantom-mcb-tests-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private async Task<PhantomDb> NewDbAsync()
    {
        var db = new PhantomDb(_dbPath);
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        return db;
    }

    private static PluginConfiguration Config() => new()
    {
        MagnetCacheTtlHours = 24,
        MagnetCacheBuildLeaseMinutes = 10,
    };

    private static MagnetCandidate Candidate(string hash, string indexer, int seeders)
        => new($"magnet:?xt=urn:btih:{hash}", hash, 1_000_000_000L, seeders, indexer) { Title = $"Release {hash}" };

    // A fan-out stub that returns a preseeded candidate set and records the
    // number of times it was invoked (proving the builder ran the fan-out).
    private static MagnetCacheBuilder.FanOut StubFanOut(
        IReadOnlyList<MagnetCandidate> candidates,
        Action? onInvoke = null)
        => (tmdbId, imdbId, type, season, episode, title, year, ct) =>
        {
            onInvoke?.Invoke();
            return Task.FromResult(candidates);
        };

    private static MagnetCacheBuilder.MetaResolver StubMeta(string title = "Some Title", int? year = 2020)
        => (tmdbId, type, season, episode, ct) =>
            Task.FromResult<MagnetCacheItemMeta?>(new MagnetCacheItemMeta("tt1234567", title, year));

    private static MagnetCacheBuilder Builder(
        PhantomDb db,
        MagnetCacheBuilder.FanOut fanOut,
        MagnetCacheBuilder.MetaResolver? meta = null,
        PluginConfiguration? cfg = null)
        => new(
            db,
            meta ?? StubMeta(),
            fanOut,
            NullLogger<MagnetCacheBuilder>.Instance,
            () => cfg ?? Config());

    [Theory]
    [InlineData("movie", -1, -1)]
    [InlineData("episode", 2, 5)]
    public async Task EnqueueThenBuild_PopulatesFullCandidateSet(string type, int season, int episode)
    {
        using var db = await NewDbAsync();
        const int tmdb = 42;
        const string preset = "gostream-default";

        var fullSet = new List<MagnetCandidate>
        {
            Candidate("aaaa", "indexer-1", 500),
            Candidate("bbbb", "indexer-2", 300),
            Candidate("cccc", "indexer-3", 100),
        };
        var invoked = 0;
        var builder = Builder(db, StubFanOut(fullSet, () => invoked++));

        // No candidates before the build.
        var before = await db.ListSourceCandidatesAsync(tmdb, type, season, episode, preset, true, CancellationToken.None);
        Assert.Empty(before);

        await db.EnqueueMagnetCacheJobAsync(tmdb, type, season, episode, preset, 0, CancellationToken.None);
        var result = await builder.ProcessNextAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(1, invoked); // the builder actually ran the Prowlarr fan-out
        Assert.Equal(fullSet.Count, result!.CandidateCount);

        // The full candidate set is now in source_candidates for this item.
        var after = await db.ListSourceCandidatesAsync(tmdb, type, season, episode, preset, true, CancellationToken.None);
        Assert.Equal(fullSet.Count, after.Count);
        Assert.Contains(after, c => c.InfoHash == "aaaa");
        Assert.Contains(after, c => c.InfoHash == "bbbb");
        Assert.Contains(after, c => c.InfoHash == "cccc");

        // The job is marked done with its candidate count.
        var job = await db.GetMagnetCacheJobAsync(tmdb, type, season, episode, preset, CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal("done", job!.Status);
        Assert.Equal(fullSet.Count, job.CandidateCount);
    }

    [Theory]
    [InlineData("movie", -1, -1)]
    [InlineData("episode", 1, 1)]
    public async Task Rebuild_RefreshesStaleEntry(string type, int season, int episode)
    {
        using var db = await NewDbAsync();
        const int tmdb = 77;
        const string preset = "gostream-default";

        // First build: two candidates.
        var firstSet = new List<MagnetCandidate>
        {
            Candidate("old1", "indexer-1", 200),
            Candidate("old2", "indexer-2", 150),
        };
        await db.EnqueueMagnetCacheJobAsync(tmdb, type, season, episode, preset, 0, CancellationToken.None);
        await Builder(db, StubFanOut(firstSet)).ProcessNextAsync(CancellationToken.None);

        var first = await db.ListSourceCandidatesAsync(tmdb, type, season, episode, preset, true, CancellationToken.None);
        Assert.Equal(2, first.Count);

        // Re-enqueue (job returns to pending) and rebuild with a fresh set.
        var secondSet = new List<MagnetCandidate>
        {
            Candidate("new1", "indexer-1", 900),
            Candidate("new2", "indexer-2", 800),
            Candidate("new3", "indexer-3", 700),
        };
        await db.EnqueueMagnetCacheJobAsync(tmdb, type, season, episode, preset, 0, CancellationToken.None);
        var rebuilt = await Builder(db, StubFanOut(secondSet)).ProcessNextAsync(CancellationToken.None);
        Assert.NotNull(rebuilt);
        Assert.Equal(3, rebuilt!.CandidateCount);

        var second = await db.ListSourceCandidatesAsync(tmdb, type, season, episode, preset, true, CancellationToken.None);
        // The refreshed set is present. The new top-ranked candidate is rank 1.
        Assert.Contains(second, c => c.InfoHash == "new1" && c.Rank == 1);
        Assert.Contains(second, c => c.InfoHash == "new2");
        Assert.Contains(second, c => c.InfoHash == "new3");
    }

    [Fact]
    public async Task CompetingJobs_ClaimedInPriorityOrder()
    {
        using var db = await NewDbAsync();
        const string preset = "gostream-default";

        // Two competing jobs at different priorities.
        await db.EnqueueMagnetCacheJobAsync(100, "movie", -1, -1, preset, priority: 1, CancellationToken.None);
        await db.EnqueueMagnetCacheJobAsync(200, "movie", -1, -1, preset, priority: 9, CancellationToken.None);

        var owner = "test-builder";
        var lease = TimeSpan.FromMinutes(5);

        var firstClaim = await db.ClaimNextMagnetCacheJobAsync(owner, lease, CancellationToken.None);
        Assert.NotNull(firstClaim);
        Assert.Equal(200, firstClaim!.TmdbId); // higher priority first
        Assert.Equal("running", firstClaim.Status);

        var secondClaim = await db.ClaimNextMagnetCacheJobAsync(owner, lease, CancellationToken.None);
        Assert.NotNull(secondClaim);
        Assert.Equal(100, secondClaim!.TmdbId); // lower priority second

        // Both are now leased; nothing else claimable.
        var third = await db.ClaimNextMagnetCacheJobAsync(owner, lease, CancellationToken.None);
        Assert.Null(third);
    }

    [Fact]
    public async Task Enqueue_RaisesPriority_NeverLowers()
    {
        using var db = await NewDbAsync();
        const string preset = "gostream-default";

        await db.EnqueueMagnetCacheJobAsync(5, "movie", -1, -1, preset, priority: 3, CancellationToken.None);
        // A lower-priority re-enqueue must not lower the effective priority.
        var afterLower = await db.EnqueueMagnetCacheJobAsync(5, "movie", -1, -1, preset, priority: 1, CancellationToken.None);
        Assert.Equal(3, afterLower);
        // A higher-priority re-enqueue raises it.
        var afterHigher = await db.EnqueueMagnetCacheJobAsync(5, "movie", -1, -1, preset, priority: 7, CancellationToken.None);
        Assert.Equal(7, afterHigher);
    }

    [Fact]
    public async Task Build_WithNoMetadata_FailsJob()
    {
        using var db = await NewDbAsync();
        const string preset = "gostream-default";
        var builder = Builder(
            db,
            StubFanOut(new List<MagnetCandidate>()),
            meta: (tmdbId, type, season, episode, ct) => Task.FromResult<MagnetCacheItemMeta?>(null));

        await db.EnqueueMagnetCacheJobAsync(9, "movie", -1, -1, preset, 0, CancellationToken.None);
        var result = await builder.ProcessNextAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(0, result!.CandidateCount);
        var job = await db.GetMagnetCacheJobAsync(9, "movie", -1, -1, preset, CancellationToken.None);
        Assert.Equal("failed", job!.Status);
    }
}
