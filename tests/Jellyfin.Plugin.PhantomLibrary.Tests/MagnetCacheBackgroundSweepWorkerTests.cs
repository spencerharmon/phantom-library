using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Scheduled;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// ROI Priority 6, revised architecture item 2b
/// (p6-magnet-cache-background-sweep): regression guard for the lowest-
/// priority magnet-cache lane. <see cref="MagnetCacheBackgroundSweepWorker"/>
/// walks available items lacking a fresh <c>source_candidates</c> entry and
/// enqueues LOW-priority (<see cref="PhantomDb.BackgroundSweepMagnetCachePriority"/>)
/// <c>magnet_cache_jobs</c> rows. These tests FAIL against a tree without
/// the worker/query (nothing gets enqueued, or a large low-priority backlog
/// starves an opportunistic job) and PASS once it is wired in.
/// </summary>
public sealed class MagnetCacheBackgroundSweepWorkerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "phantom-p6-sweep-" + Guid.NewGuid().ToString("N") + ".db");

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

    [Fact]
    public async Task Tick_Movie_EnqueuesLowPriorityJobForAvailableItemMissingCache()
    {
        using var db = await NewDbAsync();
        const int tmdb = 500001;
        await InsertAvailabilityAsync(db, tmdb, "movie", -1, -1, "available");

        var worker = BuildWorker(db, Config());
        await InvokeTickAsync(worker);

        var job = await db.GetMagnetCacheJobAsync(tmdb, "movie", -1, -1, "test-preset", CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(PhantomDb.BackgroundSweepMagnetCachePriority, job!.Priority);
    }

    [Fact]
    public async Task Tick_Episode_EnqueuesLowPriorityJobForAvailableItemMissingCache()
    {
        using var db = await NewDbAsync();
        const int seriesTmdb = 500002;
        await InsertAvailabilityAsync(db, seriesTmdb, "episode", 1, 1, "available");

        var worker = BuildWorker(db, Config());
        await InvokeTickAsync(worker);

        var job = await db.GetMagnetCacheJobAsync(seriesTmdb, "episode", 1, 1, "test-preset", CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(PhantomDb.BackgroundSweepMagnetCachePriority, job!.Priority);
    }

    [Fact]
    public async Task Tick_UnavailableItem_DoesNotEnqueue()
    {
        using var db = await NewDbAsync();
        const int tmdb = 500003;
        await InsertAvailabilityAsync(db, tmdb, "movie", -1, -1, "unavailable");

        var worker = BuildWorker(db, Config());
        await InvokeTickAsync(worker);

        Assert.Null(await db.GetMagnetCacheJobAsync(tmdb, "movie", -1, -1, "test-preset", CancellationToken.None));
    }

    [Fact]
    public async Task Tick_ItemWithFreshCache_DoesNotEnqueue()
    {
        using var db = await NewDbAsync();
        const int tmdb = 500004;
        await InsertAvailabilityAsync(db, tmdb, "movie", -1, -1, "available");
        await InsertSourceCandidateAsync(db, tmdb, "movie", -1, -1, "test-preset", DateTimeOffset.UtcNow.AddHours(1));

        var worker = BuildWorker(db, Config());
        await InvokeTickAsync(worker);

        Assert.Null(await db.GetMagnetCacheJobAsync(tmdb, "movie", -1, -1, "test-preset", CancellationToken.None));
    }

    [Fact]
    public async Task Tick_StaleCacheEntryPastTtl_IsReenqueued()
    {
        // TTL: a cache entry whose expires_at has already passed is treated
        // exactly like a missing one and re-enqueued.
        using var db = await NewDbAsync();
        const int tmdb = 500005;
        await InsertAvailabilityAsync(db, tmdb, "movie", -1, -1, "available");
        await InsertSourceCandidateAsync(db, tmdb, "movie", -1, -1, "test-preset", DateTimeOffset.UtcNow.AddHours(-1));

        var worker = BuildWorker(db, Config());
        await InvokeTickAsync(worker);

        var job = await db.GetMagnetCacheJobAsync(tmdb, "movie", -1, -1, "test-preset", CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(PhantomDb.BackgroundSweepMagnetCachePriority, job!.Priority);
    }

    [Fact]
    public async Task Tick_RecentUserActivity_YieldsAndEnqueuesNothing()
    {
        using var db = await NewDbAsync();
        const int tmdb = 500006;
        await InsertAvailabilityAsync(db, tmdb, "movie", -1, -1, "available");
        await db.TouchUserActivityAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        var cfg = Config();
        cfg.AvailabilityYieldToUserSeconds = 60;
        var worker = BuildWorker(db, cfg);
        await InvokeTickAsync(worker);

        Assert.Null(await db.GetMagnetCacheJobAsync(tmdb, "movie", -1, -1, "test-preset", CancellationToken.None));
    }

    [Fact]
    public async Task Tick_LargeLowPriorityBacklog_StillLosesClaimToCompetingOpportunisticJob()
    {
        // A large low-priority background backlog is built by the sweep, but
        // a competing high-priority opportunistic job (or, equivalently, a
        // live activity marker preventing the sweep from ever running) still
        // claims ahead of it. Movie parity: exercised with movies here,
        // episode parity in the sibling test below.
        using var db = await NewDbAsync();
        for (var i = 0; i < 50; i++)
        {
            var tmdb = 600_000 + i;
            await InsertAvailabilityAsync(db, tmdb, "movie", -1, -1, "available");
        }

        var worker = BuildWorker(db, Config());
        await InvokeTickAsync(worker, batchOverride: 50);

        // The whole backlog is now queued at the background priority.
        for (var i = 0; i < 50; i++)
        {
            var tmdb = 600_000 + i;
            var job = await db.GetMagnetCacheJobAsync(tmdb, "movie", -1, -1, "test-preset", CancellationToken.None);
            Assert.NotNull(job);
            Assert.Equal(PhantomDb.BackgroundSweepMagnetCachePriority, job!.Priority);
        }

        // A user touches an unrelated item — opportunistic enqueue at the
        // high-priority lane.
        const int userTmdb = 700_000;
        await db.EnqueueOpportunisticMagnetCacheJobAsync(userTmdb, "movie", null, null, "test-preset", CancellationToken.None);

        var claimed = await db.ClaimNextMagnetCacheJobAsync("builder", TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.Equal(userTmdb, claimed!.TmdbId);
    }

    [Fact]
    public async Task Tick_LargeLowPriorityBacklog_Episode_StillLosesClaimToCompetingOpportunisticJob()
    {
        using var db = await NewDbAsync();
        for (var i = 0; i < 50; i++)
        {
            var tmdb = 610_000 + i;
            await InsertAvailabilityAsync(db, tmdb, "episode", 1, 1, "available");
        }

        var worker = BuildWorker(db, Config());
        await InvokeTickAsync(worker, batchOverride: 50);

        for (var i = 0; i < 50; i++)
        {
            var tmdb = 610_000 + i;
            var job = await db.GetMagnetCacheJobAsync(tmdb, "episode", 1, 1, "test-preset", CancellationToken.None);
            Assert.NotNull(job);
            Assert.Equal(PhantomDb.BackgroundSweepMagnetCachePriority, job!.Priority);
        }

        const int seriesTmdb = 710_000;
        await db.EnqueueOpportunisticMagnetCacheJobAsync(seriesTmdb, "episode", 1, 1, "test-preset", CancellationToken.None);

        var claimed = await db.ClaimNextMagnetCacheJobAsync("builder", TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.Equal(seriesTmdb, claimed!.TmdbId);
        Assert.Equal(1, claimed.Season);
        Assert.Equal(1, claimed.Episode);
    }

    [Fact]
    public async Task Tick_AlreadyPendingJob_IsNotReenqueuedTwice()
    {
        using var db = await NewDbAsync();
        const int tmdb = 500007;
        await InsertAvailabilityAsync(db, tmdb, "movie", -1, -1, "available");
        // A prior tick already enqueued it at background priority.
        await db.EnqueueMagnetCacheJobAsync(tmdb, "movie", -1, -1, "test-preset", PhantomDb.BackgroundSweepMagnetCachePriority, CancellationToken.None);

        var worker = BuildWorker(db, Config());
        await InvokeTickAsync(worker);

        // Still exactly one job row, still pending (not disturbed).
        var job = await db.GetMagnetCacheJobAsync(tmdb, "movie", -1, -1, "test-preset", CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal("pending", job!.Status);
    }

    [Fact]
    public async Task Tick_Disabled_DoesNotEnqueue()
    {
        using var db = await NewDbAsync();
        const int tmdb = 500008;
        await InsertAvailabilityAsync(db, tmdb, "movie", -1, -1, "available");

        var cfg = Config();
        cfg.MagnetCacheSweepEnabled = false;
        var worker = BuildWorker(db, cfg);
        await InvokeTickAsync(worker);

        Assert.Null(await db.GetMagnetCacheJobAsync(tmdb, "movie", -1, -1, "test-preset", CancellationToken.None));
    }

    // ---------- builders ----------

    private async Task<PhantomDb> NewDbAsync()
    {
        var db = new PhantomDb(_dbPath);
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        return db;
    }

    private static PluginConfiguration Config() => new()
    {
        SourcePickerPreset = "test-preset",
        MagnetCacheSweepEnabled = true,
        MagnetCacheSweepMinIntervalSeconds = 5,
        MagnetCacheSweepMaxIntervalSeconds = 30,
        MagnetCacheSweepBatchSize = 5,
        AvailabilityYieldToUserSeconds = 20,
    };

    private static MagnetCacheBackgroundSweepWorker BuildWorker(PhantomDb db, PluginConfiguration cfg)
        => new(db, NullLogger<MagnetCacheBackgroundSweepWorker>.Instance, () => cfg);

    private static async Task InvokeTickAsync(MagnetCacheBackgroundSweepWorker worker, int? batchOverride = null)
    {
        if (batchOverride is { } b)
        {
            var cfgField = typeof(MagnetCacheBackgroundSweepWorker).GetField("_configProvider", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(nameof(MagnetCacheBackgroundSweepWorker), "_configProvider");
            var provider = (Func<PluginConfiguration>)(cfgField.GetValue(worker) ?? throw new InvalidOperationException());
            provider().MagnetCacheSweepBatchSize = b;
        }

        var method = typeof(MagnetCacheBackgroundSweepWorker).GetMethod("TickAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(MagnetCacheBackgroundSweepWorker), "TickAsync");
        var task = (Task)(method.Invoke(worker, new object[] { CancellationToken.None })
            ?? throw new InvalidOperationException("TickAsync returned null"));
        await task;
    }

    // ---------- seeding helpers ----------

    private async Task InsertAvailabilityAsync(PhantomDb db, int tmdbId, string type, int season, int episode, string status)
    {
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO availability_items
                (tmdb_id, type, season, episode, status, next_check_at, priority)
            VALUES ($tmdb,$type,$season,$episode,$status,$next,0)
            ON CONFLICT(tmdb_id, type, season, episode) DO UPDATE SET
                status=excluded.status;";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$season", season);
        cmd.Parameters.AddWithValue("$episode", episode);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$next", DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertSourceCandidateAsync(PhantomDb db, int tmdbId, string type, int season, int episode, string preset, DateTimeOffset expiresAt)
    {
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO source_candidates
                (tmdb_id,type,season,episode,preset,magnet,info_hash,indexer,title,seeders,size,rank,source,fetched_at,expires_at)
            VALUES ($tmdb,$type,$season,$episode,$preset,'magnet:?xt=test','deadbeef','test-indexer','Test Title',10,1000000,1,'test',$fetched,$expires);";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$season", season);
        cmd.Parameters.AddWithValue("$episode", episode);
        cmd.Parameters.AddWithValue("$preset", preset);
        cmd.Parameters.AddWithValue("$fetched", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$expires", expiresAt.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();
    }
}
