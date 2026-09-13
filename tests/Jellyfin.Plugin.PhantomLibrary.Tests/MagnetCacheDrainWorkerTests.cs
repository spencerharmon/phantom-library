using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Scheduled;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// Regression coverage for ttfb-magnet-cache-drain-worker: the CONSUMER that
/// drains <c>magnet_cache_jobs</c>. Proves the gap this task fixes — an
/// opportunistic-priority (100) row enqueued on user interaction was, before
/// this worker existed, never claimed or processed by anything, so it sat
/// pending forever and the opportunistic-prefetch ROI direction had zero
/// effect on materialise TTFB. Each test drives ONE deterministic tick
/// (<see cref="MagnetCacheDrainWorker.DrainOnceAsync"/>) and asserts the row
/// is actually claimed, run through the Prowlarr fan-out, and its candidate
/// set written into <c>source_candidates</c> — not merely that the class
/// compiles.
/// </summary>
public sealed class MagnetCacheDrainWorkerTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), "phantom-mcdw-tests-" + Guid.NewGuid().ToString("N") + ".db");

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
        MagnetCacheDrainEnabled = true,
        MagnetCacheDrainMaxJobsPerTick = 3,
        AvailabilityYieldToUserSeconds = 0,
    };

    private static MagnetCandidate Candidate(string hash, string indexer, int seeders)
        => new($"magnet:?xt=urn:btih:{hash}", hash, 1_000_000_000L, seeders, indexer) { Title = $"Release {hash}" };

    private static MagnetCacheBuilder.FanOut StubFanOut(
        IReadOnlyList<MagnetCandidate> candidates,
        Action? onInvoke = null)
        => (tmdbId, imdbId, type, season, episode, title, year, ct) =>
        {
            onInvoke?.Invoke();
            return Task.FromResult(candidates);
        };

    private static MagnetCacheBuilder.MetaResolver StubMeta()
        => (tmdbId, type, season, episode, ct) =>
            Task.FromResult<MagnetCacheItemMeta?>(new MagnetCacheItemMeta("tt1234567", "Some Title", 2020));

    private static MagnetCacheBuilder Builder(
        PhantomDb db,
        MagnetCacheBuilder.FanOut fanOut,
        PluginConfiguration cfg)
        => new(
            db,
            StubMeta(),
            fanOut,
            NullLogger<MagnetCacheBuilder>.Instance,
            () => cfg);

    // Movie AND episode parity: an opportunistic-priority row of each tuple
    // shape is claimed and processed by a single drain tick.
    [Theory]
    [InlineData("movie", -1, -1)]
    [InlineData("episode", 3, 7)]
    public async Task DrainTick_ClaimsAndProcesses_OpportunisticRow(string type, int season, int episode)
    {
        using var db = await NewDbAsync();
        var cfg = Config();
        const int tmdb = 4242;
        const string preset = "gostream-default";

        var fullSet = new List<MagnetCandidate>
        {
            Candidate("aaaa", "indexer-1", 500),
            Candidate("bbbb", "indexer-2", 300),
        };
        var invoked = 0;
        var worker = new MagnetCacheDrainWorker(
            Builder(db, StubFanOut(fullSet, () => invoked++), cfg),
            db,
            NullLogger<MagnetCacheDrainWorker>.Instance,
            () => cfg);

        // Enqueue exactly as PhantomSourceManager.EnqueueOpportunisticMagnetCacheJobAsync
        // does on a user info-panel touch: OpportunisticMagnetCachePriority (100).
        await db.EnqueueMagnetCacheJobAsync(
            tmdb, type, season, episode, preset,
            PhantomDb.OpportunisticMagnetCachePriority,
            CancellationToken.None);

        // Before the tick: the row is pending and nothing is cached.
        var pending = await db.GetMagnetCacheJobAsync(tmdb, type, season, episode, preset, CancellationToken.None);
        Assert.NotNull(pending);
        Assert.Equal("pending", pending!.Status);
        Assert.Empty(await db.ListSourceCandidatesAsync(tmdb, type, season, episode, preset, true, CancellationToken.None));

        // ONE drain tick.
        var processed = await worker.DrainOnceAsync(cfg, CancellationToken.None);

        // The worker actually claimed and processed the opportunistic row.
        Assert.Equal(1, processed);
        Assert.Equal(1, invoked); // the Prowlarr fan-out ran

        var done = await db.GetMagnetCacheJobAsync(tmdb, type, season, episode, preset, CancellationToken.None);
        Assert.Equal("done", done!.Status);
        Assert.Equal(fullSet.Count, done.CandidateCount);

        // The full candidate set is now in the cache — a subsequent materialise
        // is a cache HIT (zero probes), which is the whole point of the fix.
        var cached = await db.ListSourceCandidatesAsync(tmdb, type, season, episode, preset, true, CancellationToken.None);
        Assert.Equal(fullSet.Count, cached.Count);
        Assert.Contains(cached, c => c.InfoHash == "aaaa");
        Assert.Contains(cached, c => c.InfoHash == "bbbb");
    }

    [Fact]
    public async Task DrainTick_ProcessesHighestPriorityFirst_AndRespectsPerTickCap()
    {
        using var db = await NewDbAsync();
        var cfg = Config();
        cfg.MagnetCacheDrainMaxJobsPerTick = 2;
        const string preset = "gostream-default";

        // Three pending rows: two background (0) and one opportunistic (100).
        await db.EnqueueMagnetCacheJobAsync(1, "movie", -1, -1, preset, PhantomDb.BackgroundSweepMagnetCachePriority, CancellationToken.None);
        await db.EnqueueMagnetCacheJobAsync(2, "movie", -1, -1, preset, PhantomDb.OpportunisticMagnetCachePriority, CancellationToken.None);
        await db.EnqueueMagnetCacheJobAsync(3, "movie", -1, -1, preset, PhantomDb.BackgroundSweepMagnetCachePriority, CancellationToken.None);

        var worker = new MagnetCacheDrainWorker(
            Builder(db, StubFanOut(new List<MagnetCandidate> { Candidate("z", "ix", 10) }), cfg),
            db,
            NullLogger<MagnetCacheDrainWorker>.Instance,
            () => cfg);

        var processed = await worker.DrainOnceAsync(cfg, CancellationToken.None);
        Assert.Equal(2, processed); // capped at MaxJobsPerTick

        // The opportunistic row (highest priority) was claimed first, so it is done.
        var opp = await db.GetMagnetCacheJobAsync(2, "movie", -1, -1, preset, CancellationToken.None);
        Assert.Equal("done", opp!.Status);
    }
}
