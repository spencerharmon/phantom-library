using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Scheduled;
using Jellyfin.Plugin.PhantomLibrary.State;
using Jellyfin.Plugin.PhantomLibrary.Swarm;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>Serialises every suite that drives the process-wide swarm-health title gauges.</summary>
[CollectionDefinition(Name)]
public sealed class SwarmHealthMetricsCollection
{
    public const string Name = "SwarmHealthMetrics";
}

/// <summary>ROI Priority 0.5 (dht-swarm-health): storage, measurement worker, scoring, shelf, client.</summary>
[Collection(SwarmHealthMetricsCollection.Name)]
public sealed class SwarmHealthTests : IDisposable
{
    private const string ServiceUrl = "http://swarm-health.test:8095";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "phantom-swarm-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        try
        {
            File.Delete(_dbPath);
        }
        catch
        {
            // best-effort
        }
    }

    [Theory]
    [InlineData(null, 0.5f)]
    [InlineData(0, 0f)]
    [InlineData(7, 7f)]
    [InlineData(250, 100f)]
    public void Score_EncodesLiveThenUnmeasuredThenDeadForDescendingSort(int? peers, float expected)
        => Assert.Equal(expected, SwarmHealthScore.ToCriticRating(peers));

    [Fact]
    public void Score_DescendingOrder_IsLiveBySizeThenUnmeasuredThenDead()
    {
        var ordered = new int?[] { 0, null, 3, 80 }
            .OrderByDescending(p => SwarmHealthScore.ToCriticRating(p))
            .ToArray();
        Assert.Equal(new int?[] { 80, 3, null, 0 }, ordered);
    }

    [Fact]
    public void HealthiestRow_RanksLiveSwarmsBySize_SkipsDeadAndUnmeasured()
    {
        var items = new List<ChannelItemInfo>
        {
            Movie(1), Movie(2), Movie(3), Movie(4), Series(5),
        };
        var peers = new Dictionary<int, int> { [1] = 4, [2] = 0, [3] = 40, [5] = 9 };

        var row = SwarmHealthScore.HealthiestRow(items, peers, rowSize: 10);

        Assert.Equal(new[] { "movie_3", "series_5", "movie_1" }, row.Select(i => i.Id).ToArray());
        Assert.Single(SwarmHealthScore.HealthiestRow(items, peers, rowSize: 1));
    }

    [Fact]
    public async Task Db_DueItems_AreAvailableCandidatesWithoutFreshMeasurement_MoviesFirst()
    {
        using var db = await NewDbAsync();
        await InsertAvailabilityAsync(101, "movie", -1, -1, "available", Hash('a'));
        await InsertAvailabilityAsync(102, "movie", -1, -1, "unavailable", Hash('b'));
        await InsertAvailabilityAsync(103, "movie", -1, -1, "available", null);
        await InsertAvailabilityAsync(201, "episode", 1, 2, "available", Hash('C')); // mixed case stored
        await InsertAvailabilityAsync(104, "movie", -1, -1, "available", Hash('d'));
        var now = DateTimeOffset.UtcNow;
        await db.UpsertSwarmHealthAsync(new[] { new SwarmHealthMeasurement(Hash('d'), 104, "movie", -1, -1, 5, now) }, default);

        var due = await db.GetSwarmHealthDueItemsAsync(10, now.AddHours(-1), default);

        Assert.Equal(new[] { (101, "movie"), (201, "episode") }, due.Select(d => (d.TmdbId, d.Type)).ToArray());
        Assert.Equal(Hash('c'), due[1].InfoHash);

        // A measurement older than the staleness bound is due again.
        var later = await db.GetSwarmHealthDueItemsAsync(10, now.AddHours(1), default);
        Assert.Contains(later, d => d.TmdbId == 104);
    }

    [Fact]
    public async Task Db_PeersByTmdb_TakesBestFreshMeasurement_AggregatesEpisodesToSeries()
    {
        using var db = await NewDbAsync();
        var now = DateTimeOffset.UtcNow;
        await db.UpsertSwarmHealthAsync(
            new[]
            {
                new SwarmHealthMeasurement(Hash('1'), 7, "episode", 1, 1, 3, now),
                new SwarmHealthMeasurement(Hash('2'), 7, "episode", 1, 2, 12, now),
                new SwarmHealthMeasurement(Hash('3'), 8, "episode", 1, 1, 50, now.AddDays(-30)),
                new SwarmHealthMeasurement(Hash('4'), 9, "movie", -1, -1, 6, now),
            },
            default);
        await db.UpsertSwarmHealthAsync(new[] { new SwarmHealthMeasurement(Hash('4').ToUpperInvariant(), 9, "movie", -1, -1, 2, now) }, default);

        var series = await db.GetSwarmPeersByTmdbAsync("episode", now.AddDays(-7), default);
        var movies = await db.GetSwarmPeersByTmdbAsync("movie", now.AddDays(-7), default);

        Assert.Equal(12, series[7]);
        Assert.False(series.ContainsKey(8)); // stale measurement does not rank
        Assert.Equal(2, movies[9]);           // upsert is keyed case-insensitively and overwrites
    }

    [Fact]
    public async Task Db_PeersForTmdbIds_IsThePerBatchSliceOfTheFullAggregate()
    {
        using var db = await NewDbAsync();
        var now = DateTimeOffset.UtcNow;
        await db.UpsertSwarmHealthAsync(
            new[]
            {
                new SwarmHealthMeasurement(Hash('a'), 1, "movie", -1, -1, 4, now),
                new SwarmHealthMeasurement(Hash('b'), 2, "movie", -1, -1, 0, now),
                new SwarmHealthMeasurement(Hash('c'), 3, "movie", -1, -1, 8, now.AddDays(-30)), // stale
                new SwarmHealthMeasurement(Hash('d'), 9, "episode", 1, 1, 2, now),
                new SwarmHealthMeasurement(Hash('e'), 9, "episode", 1, 2, 6, now),
            },
            default);

        var movies = await db.GetSwarmPeersForTmdbIdsAsync("movie", new[] { 1, 2, 3, 4 }, now.AddDays(-7), default);
        var series = await db.GetSwarmPeersForTmdbIdsAsync("episode", new[] { 9 }, now.AddDays(-7), default);

        Assert.Equal(new Dictionary<int, int> { [1] = 4, [2] = 0 }, movies);
        Assert.Equal(6, series[9]);
    }

    [Fact]
    public async Task Worker_MeasuresDueCandidatesOncePerInfohash_AndStoresOnlyAnsweredOnes()
    {
        using var db = await NewDbAsync();
        await InsertAvailabilityAsync(301, "movie", -1, -1, "available", Hash('e'));
        await InsertAvailabilityAsync(400, "episode", 1, 1, "available", Hash('f')); // season pack:
        await InsertAvailabilityAsync(400, "episode", 1, 2, "available", Hash('f')); // same infohash
        await InsertAvailabilityAsync(302, "movie", -1, -1, "available", Hash('9'));
        var client = new FakeClient(new Dictionary<string, int> { [Hash('e')] = 14, [Hash('f')] = 0 });
        var worker = NewWorker(db, client, new FakeLibrary().Object);

        var stored = await worker.MeasureOnceAsync(Config(), default);

        Assert.Equal(2, stored);
        Assert.Single(client.Requests);
        Assert.Equal(3, client.Requests[0].Count); // e, f (deduped), 9
        var movies = await db.GetSwarmPeersByTmdbAsync("movie", DateTimeOffset.UtcNow.AddDays(-1), default);
        var series = await db.GetSwarmPeersByTmdbAsync("episode", DateTimeOffset.UtcNow.AddDays(-1), default);
        Assert.Equal(14, movies[301]);
        Assert.False(movies.ContainsKey(302)); // unanswered stays unmeasured (due again next tick)
        Assert.Equal(0, series[400]);
        Assert.Contains(await db.GetSwarmHealthDueItemsAsync(10, DateTimeOffset.UtcNow.AddHours(-1), default), d => d.TmdbId == 302);
    }

    [Fact]
    public async Task Worker_ServiceFailure_StoresNothing()
    {
        using var db = await NewDbAsync();
        await InsertAvailabilityAsync(501, "movie", -1, -1, "available", Hash('a'));
        var worker = NewWorker(db, new FakeClient(null), new Mock<ILibraryManager>(MockBehavior.Strict).Object);

        Assert.Equal(0, await worker.MeasureOnceAsync(Config(), default));
        Assert.Empty(await db.GetSwarmPeersByTmdbAsync("movie", DateTimeOffset.UtcNow.AddDays(-1), default));
    }

    [Theory]
    [InlineData("movie_11", ScoredTitleKind.Measurable, 11)]
    [InlineData("series_12", ScoredTitleKind.Measurable, 12)]
    [InlineData("orphan_0123456789abcdef", ScoredTitleKind.Orphan, 0)]
    [InlineData("orphanseries_0123456789abcdef", ScoredTitleKind.Orphan, 0)]
    [InlineData("season_12_s01", ScoredTitleKind.NotScored, 0)]
    [InlineData("episode_12_s01e02", ScoredTitleKind.NotScored, 0)]
    [InlineData("orphanepisode_0123456789abcdef", ScoredTitleKind.NotScored, 0)]
    [InlineData(null, ScoredTitleKind.NotScored, 0)]
    [InlineData("tt12345", ScoredTitleKind.NotScored, 0)]
    public void Classify_ScoresMovieSeriesAndOrphanTitlesOnly(string? externalId, ScoredTitleKind kind, int tmdb)
        => Assert.Equal((kind, tmdb), SwarmHealthScore.Classify(externalId));

    [Fact]
    public void TargetFor_OrphanIsUnmeasured_DeadBelowUnmeasuredBelowLive_NotScoredIsNull()
    {
        var peers = new Dictionary<int, int> { [1] = 7, [2] = 0 };
        Assert.Equal(7f, SwarmHealthScore.TargetFor("movie_1", peers));
        Assert.Equal(0f, SwarmHealthScore.TargetFor("series_2", peers));
        Assert.Equal(SwarmHealthScore.UnmeasuredScore, SwarmHealthScore.TargetFor("movie_3", peers));
        Assert.Equal(SwarmHealthScore.UnmeasuredScore, SwarmHealthScore.TargetFor("orphan_abc", peers));
        Assert.Null(SwarmHealthScore.TargetFor("episode_1_s01e01", peers));
        Assert.True(SwarmHealthScore.TargetFor("series_2", peers) < SwarmHealthScore.TargetFor("orphan_abc", peers));
        Assert.True(SwarmHealthScore.TargetFor("orphan_abc", peers) < SwarmHealthScore.TargetFor("movie_1", peers));
    }

    [Fact]
    public async Task Sync_SweepsWholeCatalogueInBoundedKeysetBatches_IncludingOrphansAndNullRatings()
    {
        using var db = await NewDbAsync();
        var now = DateTimeOffset.UtcNow;
        await db.UpsertSwarmHealthAsync(
            new[]
            {
                new SwarmHealthMeasurement(Hash('a'), 5, "movie", -1, -1, 37, now),
                new SwarmHealthMeasurement(Hash('b'), 6, "movie", -1, -1, 0, now),
                new SwarmHealthMeasurement(Hash('c'), 9000, "episode", 1, 1, 3, now),
                new SwarmHealthMeasurement(Hash('d'), 9000, "episode", 1, 2, 12, now),
            },
            default);
        var lib = new FakeLibrary();
        for (var i = 1; i <= 120; i++)
        {
            lib.Add(new Movie { Id = Guid.NewGuid(), ExternalId = "movie_" + i, ChannelId = ChannelIds.Movies, CriticRating = i % 3 == 0 ? 42f : null });
        }

        for (var i = 0; i < 7; i++)
        {
            lib.Add(new Movie { Id = Guid.NewGuid(), ExternalId = "orphan_" + i.ToString("x16", System.Globalization.CultureInfo.InvariantCulture), ChannelId = ChannelIds.Movies });
        }

        var series = new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), ExternalId = "series_9000", ChannelId = ChannelIds.Shows };
        lib.Add(series);
        var orphanSeries = new Folder { Id = Guid.NewGuid(), ExternalId = "orphanseries_00000000000000aa", ChannelId = ChannelIds.Shows };
        lib.Add(orphanSeries);
        var episode = new MediaBrowser.Controller.Entities.TV.Episode { Id = Guid.NewGuid(), ExternalId = "episode_9000_s01e01", ChannelId = ChannelIds.Shows, CriticRating = null };
        lib.Add(episode);
        var pager = new FakePager(lib);
        var cfg = Config();
        cfg.SwarmHealthSyncBatchSize = 50;
        var worker = NewWorker(db, new FakeClient(new Dictionary<string, int>()), lib.Object, pager, () => cfg);

        var steps = 0;
        SwarmHealthWorker.SyncStepResult step;
        do
        {
            step = await worker.SyncStepAsync(cfg, default);
            Assert.InRange(step.Read, 0, cfg.SwarmHealthSyncBatchSize);
            steps++;
        }
        while (!step.SweepCompleted && steps < 100);

        Assert.True(step.SweepCompleted);
        Assert.Equal(3, steps); // 129 rows at 50/step: ceil(129/50), crossing phases within a step's budget
        Assert.All(pager.Calls, c => Assert.InRange(c.Limit, 1, cfg.SwarmHealthSyncBatchSize));
        Assert.All(lib.Items.Where(i => i is Movie || i.ExternalId!.StartsWith("series_", StringComparison.Ordinal) || i.ExternalId!.StartsWith("orphanseries_", StringComparison.Ordinal)), i => Assert.NotNull(i.CriticRating));
        Assert.Equal(37f, lib.ByExternal("movie_5").CriticRating);
        Assert.Equal(0f, lib.ByExternal("movie_6").CriticRating);
        Assert.Equal(SwarmHealthScore.UnmeasuredScore, lib.ByExternal("movie_7").CriticRating);
        Assert.Equal(SwarmHealthScore.UnmeasuredScore, lib.ByExternal("movie_9").CriticRating); // stale 42 rewritten
        Assert.Equal(SwarmHealthScore.UnmeasuredScore, lib.ByExternal("orphan_0000000000000003").CriticRating);
        Assert.Equal(12f, series.CriticRating); // best episode swarm
        Assert.Equal(SwarmHealthScore.UnmeasuredScore, orphanSeries.CriticRating);
        Assert.Null(episode.CriticRating); // never a sweep partition
        Assert.DoesNotContain(lib.Updated, i => i is MediaBrowser.Controller.Entities.TV.Episode);

        // A second step right away is idle (sweep gap), never a re-scan.
        var idle = await worker.SyncStepAsync(cfg, default);
        Assert.Equal(0, idle.Read);
    }

    [Fact]
    public async Task Sync_CancelledMidSweep_ResumesFromPersistedCursor_WithoutRereadingCommittedBatches()
    {
        using var db = await NewDbAsync();
        var lib = new FakeLibrary();
        for (var i = 1; i <= 100; i++)
        {
            lib.Add(new Movie { Id = Guid.NewGuid(), ExternalId = "movie_" + i, ChannelId = ChannelIds.Movies });
        }

        var cfg = Config();
        cfg.SwarmHealthSyncBatchSize = 50;
        var pager = new FakePager(lib);
        var first = NewWorker(db, new FakeClient(new Dictionary<string, int>()), lib.Object, pager, () => cfg);
        var step = await first.SyncStepAsync(cfg, default);
        Assert.Equal(50, step.Read);
        Assert.Equal(50, lib.Items.Count(i => i.CriticRating is not null));

        // Cancellation before the next batch commits: nothing is written, cursor unchanged.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.SyncStepAsync(cfg, cts.Token));
        Assert.Equal(50, lib.Items.Count(i => i.CriticRating is not null));

        // "Restart": a brand-new worker instance continues after the last committed id.
        var resumedPager = new FakePager(lib);
        var second = NewWorker(db, new FakeClient(new Dictionary<string, int>()), lib.Object, resumedPager, () => cfg);
        var resumed = await second.SyncStepAsync(cfg, default);
        Assert.Equal(50, resumed.Read); // only the 50 not yet committed
        Assert.Equal(50, resumed.Written);
        Assert.NotNull(resumedPager.Calls[0].After);
        var tail = await second.SyncStepAsync(cfg, default); // exactly-full page: one empty read closes the phase
        Assert.True(tail.SweepCompleted);
        Assert.Equal(0, tail.Read);
        Assert.All(lib.Items, i => Assert.Equal(SwarmHealthScore.UnmeasuredScore, i.CriticRating));
    }

    [Fact]
    public async Task Sync_TwoLibrariesSharingOnePhantomDb_InterleavedWithRestart_BothConvergeIndependently()
    {
        using var db = await NewDbAsync();
        await RunTwoLibraryInterleaveAsync(db);
    }

    [Fact]
    public void CursorKey_IsScopedToTheNormalizedServerId_NeverTheLegacyGlobalKey()
    {
        var id = Guid.NewGuid();
        Assert.Equal("swarm_health_sync_cursor:" + id.ToString("N"), SwarmHealthWorker.CursorKeyFor(id.ToString("D").ToUpperInvariant()));
        Assert.Equal(SwarmHealthWorker.CursorKeyFor(id.ToString("N")), SwarmHealthWorker.CursorKeyFor(id.ToString("B")));
        Assert.NotEqual(SwarmHealthWorker.CursorKeyFor("a"), SwarmHealthWorker.CursorKeyFor("b"));
        Assert.NotEqual(LegacyCursorKey, SwarmHealthWorker.CursorKeyFor(id.ToString("N")));
        Assert.Throws<InvalidOperationException>(() => SwarmHealthWorker.NormalizeTarget(" "));
    }

    [Fact]
    public async Task Sync_TitleGauges_EmitSweepCountsViaOtelAndPrometheus_AndRestoreAfterRestart()
    {
        using var db = await NewDbAsync();
        var now = DateTimeOffset.UtcNow;
        await db.UpsertSwarmHealthAsync(
            new[]
            {
                new SwarmHealthMeasurement(Hash('a'), 1, "movie", -1, -1, 40, now),
                new SwarmHealthMeasurement(Hash('b'), 2, "movie", -1, -1, 3, now),
                new SwarmHealthMeasurement(Hash('c'), 3, "movie", -1, -1, 0, now),
                new SwarmHealthMeasurement(Hash('d'), 4, "movie", -1, -1, 9, now.AddDays(-30)), // stale: unmeasured
                new SwarmHealthMeasurement(Hash('e'), 99, "movie", -1, -1, 9, now), // not in this library
                new SwarmHealthMeasurement(Hash('f'), 500, "episode", 1, 1, 0, now),
                new SwarmHealthMeasurement(Hash('1'), 500, "episode", 1, 2, 6, now), // series 500: best episode live
                new SwarmHealthMeasurement(Hash('2'), 501, "episode", 1, 1, 0, now), // series 501: dead
                new SwarmHealthMeasurement(Hash('3'), 502, "episode", 1, 1, 0, now),
                new SwarmHealthMeasurement(Hash('4'), 502, "episode", 1, 2, 0, now), // series 502: dead
            },
            default);
        var lib = new FakeLibrary();
        foreach (var t in new[] { 1, 2, 3, 4, 5 })
        {
            lib.Add(new Movie { Id = Guid.NewGuid(), ExternalId = "movie_" + t, ChannelId = ChannelIds.Movies });
        }

        for (var t = 1000; t < 1120; t++)
        {
            lib.Add(new Movie { Id = Guid.NewGuid(), ExternalId = "movie_" + t, ChannelId = ChannelIds.Movies }); // unmeasured filler: > one batch
        }

        lib.Add(new Movie { Id = Guid.NewGuid(), ExternalId = "orphan_00000000000000ff", ChannelId = ChannelIds.Movies });
        foreach (var t in new[] { 500, 501, 502, 503 })
        {
            lib.Add(new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), ExternalId = "series_" + t, ChannelId = ChannelIds.Shows });
        }

        var cfg = Config();
        cfg.SwarmHealthSyncBatchSize = 50; // the minimum: 130 rows => a multi-step sweep
        SwarmHealthMetrics.SetTitles("movie", 0, 0);
        SwarmHealthMetrics.SetTitles("series", 0, 0);
        var worker = NewWorker(db, new FakeClient(new Dictionary<string, int>()), lib.Object, new FakePager(lib), () => cfg, "gauge-lib");
        var steps = 0;
        while (!(await worker.SyncStepAsync(cfg, default)).SweepCompleted)
        {
            steps++;
            Assert.True(steps < 50);
            Assert.Equal((0L, 0L, 0L, 0L), SwarmHealthMetrics.TitlesSnapshot()); // only a COMPLETED sweep publishes
        }

        Assert.Equal(2, steps); // two non-completing steps observed zero gauges
        var expected = (MovieLive: 2L, MovieDead: 1L, SeriesLive: 1L, SeriesDead: 2L);
        Assert.Equal(expected, SwarmHealthMetrics.TitlesSnapshot());
        Assert.Equal(expected, ObserveOtelTitles());
        Assert.Equal(expected, await ScrapePrometheusTitlesAsync());

        // Process restart: statics reset; a fresh worker of the SAME library republishes
        // the last completed sweep on its first step, before any new sweep runs.
        SwarmHealthMetrics.SetTitles("movie", 0, 0);
        SwarmHealthMetrics.SetTitles("series", 0, 0);
        var other = NewWorker(db, new FakeClient(new Dictionary<string, int>()), lib.Object, new FakePager(new FakeLibrary()), () => cfg, "another-library");
        await other.SyncStepAsync(cfg, default); // a different library: nothing of gauge-lib's leaks
        Assert.Equal((0L, 0L, 0L, 0L), SwarmHealthMetrics.TitlesSnapshot());
        SwarmHealthMetrics.SetTitles("movie", 0, 0);
        SwarmHealthMetrics.SetTitles("series", 0, 0);
        var restartedPager = new FakePager(lib);
        var restarted = NewWorker(db, new FakeClient(new Dictionary<string, int>()), lib.Object, restartedPager, () => cfg, "gauge-lib");
        var idle = await restarted.SyncStepAsync(cfg, default);
        Assert.Equal(0, idle.Read); // inside the sweep gap: restored, not recounted
        Assert.Empty(restartedPager.Calls);
        Assert.Equal(expected, ObserveOtelTitles());
        Assert.Equal(expected, await ScrapePrometheusTitlesAsync());

        // The next sweep keeps publishing the previous result until it completes, then
        // publishes its own (fresh) count: movie 2 turns dead.
        await db.UpsertSwarmHealthAsync(new[] { new SwarmHealthMeasurement(Hash('b'), 2, "movie", -1, -1, 0, now) }, default);
        var later = NewWorker(db, new FakeClient(new Dictionary<string, int>()), lib.Object, new FakePager(lib), () => cfg, "gauge-lib", () => now.AddHours(2));
        var first = await later.SyncStepAsync(cfg, default);
        Assert.False(first.SweepCompleted);
        Assert.Equal(expected, ObserveOtelTitles());
        while (!(await later.SyncStepAsync(cfg, default)).SweepCompleted)
        {
        }

        Assert.Equal((1L, 2L, 1L, 2L), ObserveOtelTitles());
        Assert.Equal((1L, 2L, 1L, 2L), await ScrapePrometheusTitlesAsync());
    }

    [Fact]
    public async Task Measure_WritesThroughJustMeasuredTitles_ByDeterministicChannelItemId()
    {
        using var db = await NewDbAsync();
        await InsertAvailabilityAsync(701, "movie", -1, -1, "available", Hash('e'));
        await InsertAvailabilityAsync(702, "episode", 1, 1, "available", Hash('f'));
        var lib = new FakeLibrary();
        var movie = new Movie { Id = FakeLibrary.ChannelItemGuid("movie_701", ChannelIds.MoviesName, typeof(Movie)), ExternalId = "movie_701", ChannelId = ChannelIds.Movies };
        var series = new MediaBrowser.Controller.Entities.TV.Series { Id = FakeLibrary.ChannelItemGuid("series_702", ChannelIds.ShowsName, typeof(MediaBrowser.Controller.Entities.TV.Series)), ExternalId = "series_702", ChannelId = ChannelIds.Shows };
        lib.Add(movie);
        lib.Add(series);
        var worker = NewWorker(db, new FakeClient(new Dictionary<string, int> { [Hash('e')] = 250, [Hash('f')] = 0 }), lib.Object, new FakePager(lib), Config);

        Assert.Equal(2, await worker.MeasureOnceAsync(Config(), default));

        Assert.Equal(100f, movie.CriticRating);
        Assert.Equal(0f, series.CriticRating);
    }

    [Fact]
    public async Task Worker_Sync_OneItemFailingToSave_DoesNotAbortThePass()
    {
        // c8b015c ported to the keyset sweep: core deleting an item concurrently makes
        // its save throw (FK violation). The batch falls back to per-item saves, the
        // bad row is counted + skipped, the cursor ADVANCES past it (no stall), and
        // the sweep completes with every other row scored.
        using var db = await NewDbAsync();
        var lib = new FakeLibrary();
        for (var i = 1; i <= 120; i++)
        {
            lib.Add(new Movie { Id = Guid.NewGuid(), ExternalId = "movie_" + i, ChannelId = ChannelIds.Movies });
        }

        var bad = lib.ByExternal("movie_31");
        lib.FailSavesOf(bad);
        var cfg = Config();
        cfg.SwarmHealthSyncBatchSize = 50;
        var pager = new FakePager(lib);
        var worker = NewWorker(db, new FakeClient(new Dictionary<string, int>()), lib.Object, pager, () => cfg);

        var results = new List<SwarmHealthWorker.SyncStepResult>();
        do
        {
            results.Add(await worker.SyncStepAsync(cfg, default));
        }
        while (!results[^1].SweepCompleted && results.Count < 20);

        Assert.True(results[^1].SweepCompleted);
        Assert.Equal(1, results.Sum(r => r.Failed));
        Assert.Equal(119, results.Sum(r => r.Written));
        Assert.Null(bad.CriticRating); // reverted: in-memory state matches the (failed) store
        Assert.All(lib.Items.Where(i => i != bad), i => Assert.Equal(SwarmHealthScore.UnmeasuredScore, i.CriticRating));
        Assert.DoesNotContain(bad, lib.Updated);
        Assert.Equal(120, results.Sum(r => r.Read)); // every row read exactly once
        var raw = await db.GetMetaAsync(SwarmHealthWorker.CursorKeyFor("library-a"), default);
        using var doc = System.Text.Json.JsonDocument.Parse(raw!);
        Assert.True(doc.RootElement.GetProperty("done").GetBoolean());
        Assert.Equal(1, doc.RootElement.GetProperty("failed").GetInt64());
        Assert.Equal(120, doc.RootElement.GetProperty("read").GetInt64());

        // A cancellation is never swallowed as a "failed item".
        var lib2 = new FakeLibrary();
        lib2.Add(new Movie { Id = Guid.NewGuid(), ExternalId = "movie_1", ChannelId = ChannelIds.Movies });
        using var cts = new CancellationTokenSource();
        lib2.CancelOnSave(cts);
        var w2 = NewWorker(db, new FakeClient(new Dictionary<string, int>()), lib2.Object, new FakePager(lib2), () => cfg, "library-c");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => w2.SyncStepAsync(cfg, cts.Token));
        Assert.Null(await db.GetMetaAsync(SwarmHealthWorker.CursorKeyFor("library-c"), default)); // cursor did not advance
        Assert.Null(lib2.ByExternal("movie_1").CriticRating); // in-memory change rolled back with the cancelled write
    }

    [Fact]
    public async Task Measure_WriteThrough_OneItemFailingToSave_DoesNotAbortTheOthers()
    {
        using var db = await NewDbAsync();
        await InsertAvailabilityAsync(801, "movie", -1, -1, "available", Hash('a'));
        await InsertAvailabilityAsync(802, "movie", -1, -1, "available", Hash('b'));
        var lib = new FakeLibrary();
        var bad = new Movie { Id = FakeLibrary.ChannelItemGuid("movie_801", ChannelIds.MoviesName, typeof(Movie)), ExternalId = "movie_801", ChannelId = ChannelIds.Movies, CriticRating = 3f };
        var good = new Movie { Id = FakeLibrary.ChannelItemGuid("movie_802", ChannelIds.MoviesName, typeof(Movie)), ExternalId = "movie_802", ChannelId = ChannelIds.Movies };
        lib.Add(bad);
        lib.Add(good);
        lib.FailSavesOf(bad);
        var worker = NewWorker(db, new FakeClient(new Dictionary<string, int> { [Hash('a')] = 9, [Hash('b')] = 0 }), lib.Object, new FakePager(lib), Config);

        Assert.Equal(2, await worker.MeasureOnceAsync(Config(), default)); // measurements stored regardless

        Assert.Equal(0f, good.CriticRating);
        Assert.Equal(3f, bad.CriticRating); // reverted to its stored value; the sweep re-scores it later
        Assert.Contains(good, lib.Updated);
        Assert.DoesNotContain(bad, lib.Updated);
    }

    [Fact]
    public async Task Sync_FeedsTheProviderCacheIncrementally_FromItsBoundedBatches()
    {
        using var db = await NewDbAsync();
        SwarmHealthCache.Clear();
        var now = DateTimeOffset.UtcNow;
        await db.UpsertSwarmHealthAsync(
            new[]
            {
                new SwarmHealthMeasurement(Hash('a'), 1, "movie", -1, -1, 11, now),
                new SwarmHealthMeasurement(Hash('b'), 900, "episode", 1, 1, 4, now),
                new SwarmHealthMeasurement(Hash('c'), 77, "movie", -1, -1, 5, now), // not in this library: never cached
            },
            default);
        var lib = new FakeLibrary();
        lib.Add(new Movie { Id = Guid.NewGuid(), ExternalId = "movie_1", ChannelId = ChannelIds.Movies });
        lib.Add(new Movie { Id = Guid.NewGuid(), ExternalId = "movie_2", ChannelId = ChannelIds.Movies });
        lib.Add(new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), ExternalId = "series_900", ChannelId = ChannelIds.Shows });
        var cfg = Config();
        cfg.SwarmHealthSyncBatchSize = 50;
        var worker = NewWorker(db, new FakeClient(new Dictionary<string, int>()), lib.Object, new FakePager(lib), () => cfg);

        Assert.True((await worker.SyncStepAsync(cfg, default)).SweepCompleted);

        Assert.True(SwarmHealthCache.TryGet(false, 1, now.AddMinutes(-1), out var p1));
        Assert.Equal(11, p1);
        Assert.True(SwarmHealthCache.TryGet(false, 2, now.AddMinutes(-1), out var p2));
        Assert.Null(p2); // known unmeasured
        Assert.True(SwarmHealthCache.TryGet(true, 900, now.AddMinutes(-1), out var p900));
        Assert.Equal(4, p900);
        Assert.False(SwarmHealthCache.TryGet(false, 77, now.AddMinutes(-1), out _));
        Assert.Equal(3, SwarmHealthCache.Count);
        Assert.False(SwarmHealthCache.TryGet(false, 1, now.AddMinutes(5), out _)); // older than the bound = miss
        SwarmHealthCache.Clear();
    }

    [Fact]
    public async Task Rows_AppendHealthiestSwarmsShelf_OnlyWhenEnabledAndLive()
    {
        using var db = await NewDbAsync();
        await db.UpsertSwarmHealthAsync(new[] { new SwarmHealthMeasurement(Hash('a'), 21, "movie", -1, -1, 9, DateTimeOffset.UtcNow) }, default);
        var items = new List<ChannelItemInfo> { Movie(20), Movie(21) };
        var baseRows = Array.Empty<CuratedRow>();

        var enabled = await SwarmHealthRows.AppendHealthiestAsync(baseRows, items, db, "movie", Config(), default);
        var disabledCfg = Config();
        disabledCfg.SwarmHealthServiceUrl = string.Empty;
        var disabled = await SwarmHealthRows.AppendHealthiestAsync(baseRows, items, db, "movie", disabledCfg, default);

        var row = Assert.Single(enabled);
        Assert.Equal(CuratedRows.KeyHealthiestSwarms, row.Key);
        Assert.Equal("movie_21", Assert.Single(row.Items).Id);
        Assert.Empty(disabled);
    }

    [Fact]
    public async Task Client_PostsInfohashes_ParsesResults()
    {
        string? body = null;
        var handler = new LambdaHandler(async req =>
        {
            Assert.Equal("http://swarm-health.test:8095/measure", req.RequestUri!.ToString());
            body = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"results\":[{\"info_hash\":\"" + Hash('a') + "\",\"dht_peers\":8,\"measured_at\":1}],\"dht_nodes\":200}",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var client = new SwarmHealthClient(new HttpClient(handler));

        var result = await client.MeasureAsync(ServiceUrl + "/", new[] { Hash('a'), Hash('b') }, default);

        Assert.Contains("\"info_hashes\":[\"" + Hash('a') + "\",\"" + Hash('b') + "\"]", body, StringComparison.Ordinal);
        Assert.Equal(8, result[Hash('a').ToUpperInvariant()]);
        Assert.False(result.ContainsKey(Hash('b')));
    }

    internal const string LegacyCursorKey = "swarm_health_sync_cursor";

    /// <summary>
    /// p05-baseitem-writeback review 3: two Jellyfin libraries (A, B) with the SAME
    /// channel titles but independent BaseItems share one Phantom DB. Their sweep steps
    /// interleave one-for-one, A restarts mid-sweep, and an unqualified legacy cursor
    /// claiming a finished sweep sits in plugin_meta. BOTH libraries must end with every
    /// scored title non-NULL and their own completed cursor; with one shared cursor each
    /// library would page only the other's leftovers and keep NULLs.
    /// </summary>
    internal static async Task RunTwoLibraryInterleaveAsync(PhantomDb db)
    {
        var now = DateTimeOffset.UtcNow;
        await db.UpsertSwarmHealthAsync(
            new[]
            {
                new SwarmHealthMeasurement(Hash('a'), 3, "movie", -1, -1, 21, now),
                new SwarmHealthMeasurement(Hash('b'), 4, "movie", -1, -1, 0, now),
                new SwarmHealthMeasurement(Hash('c'), 900, "episode", 1, 1, 5, now),
            },
            default);
        await db.SetMetaAsync(LegacyCursorKey, "{\"sweep\":7,\"phase\":2,\"lastId\":\"ffffffff-ffff-ffff-ffff-ffffffffffff\",\"startedAt\":1,\"done\":false,\"read\":99}", default);
        FakeLibrary NewLibrary()
        {
            var lib = new FakeLibrary();
            for (var i = 1; i <= 250; i++)
            {
                lib.Add(new Movie { Id = Guid.NewGuid(), ExternalId = "movie_" + i, ChannelId = ChannelIds.Movies });
            }

            lib.Add(new Movie { Id = Guid.NewGuid(), ExternalId = "orphan_00000000000000a1", ChannelId = ChannelIds.Movies });
            lib.Add(new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), ExternalId = "series_900", ChannelId = ChannelIds.Shows });
            lib.Add(new MediaBrowser.Controller.Entities.TV.Series { Id = Guid.NewGuid(), ExternalId = "series_901", ChannelId = ChannelIds.Shows });
            lib.Add(new Folder { Id = Guid.NewGuid(), ExternalId = "orphanseries_00000000000000b2", ChannelId = ChannelIds.Shows });
            return lib;
        }

        var libA = NewLibrary();
        var libB = NewLibrary();
        const int Total = 254;
        var cfg = Config();
        cfg.SwarmHealthSyncBatchSize = 50; // the minimum: 254 rows => 6 steps per library
        var targetA = Guid.NewGuid().ToString("D");
        var targetB = Guid.NewGuid().ToString("N");
        var a = NewWorker(db, new FakeClient(new Dictionary<string, int>()), libA.Object, new FakePager(libA), () => cfg, targetA);
        var b = NewWorker(db, new FakeClient(new Dictionary<string, int>()), libB.Object, new FakePager(libB), () => cfg, targetB);
        bool doneA = false, doneB = false;
        for (var round = 0; round < 40 && !(doneA && doneB); round++)
        {
            if (round == 2)
            {
                a = NewWorker(db, new FakeClient(new Dictionary<string, int>()), libA.Object, new FakePager(libA), () => cfg, targetA.ToUpperInvariant());
            }

            if (!doneA)
            {
                doneA = (await a.SyncStepAsync(cfg, default)).SweepCompleted;
            }

            if (!doneB)
            {
                doneB = (await b.SyncStepAsync(cfg, default)).SweepCompleted;
            }
        }

        Assert.True(doneA && doneB);
        foreach (var lib in new[] { libA, libB })
        {
            Assert.All(lib.Items, i => Assert.NotNull(i.CriticRating));
            Assert.Equal(21f, lib.ByExternal("movie_3").CriticRating);
            Assert.Equal(0f, lib.ByExternal("movie_4").CriticRating);
            Assert.Equal(5f, lib.ByExternal("series_900").CriticRating);
            Assert.Equal(SwarmHealthScore.UnmeasuredScore, lib.ByExternal("series_901").CriticRating);
            Assert.Equal(SwarmHealthScore.UnmeasuredScore, lib.ByExternal("orphanseries_00000000000000b2").CriticRating);
        }

        foreach (var target in new[] { targetA, targetB })
        {
            var raw = await db.GetMetaAsync(SwarmHealthWorker.CursorKeyFor(target), default);
            Assert.NotNull(raw);
            using var doc = System.Text.Json.JsonDocument.Parse(raw!);
            Assert.True(doc.RootElement.GetProperty("done").GetBoolean());
            Assert.Equal(1, doc.RootElement.GetProperty("sweep").GetInt32());
            Assert.Equal(Total, doc.RootElement.GetProperty("read").GetInt64()); // each library read ALL its own rows once
            Assert.Equal(2, doc.RootElement.GetProperty("published").GetProperty("movieLive").GetInt64() + doc.RootElement.GetProperty("published").GetProperty("movieDead").GetInt64());
        }

        // The unqualified legacy cursor was neither trusted nor rewritten.
        Assert.Contains("\"sweep\":7", await db.GetMetaAsync(LegacyCursorKey, default), StringComparison.Ordinal);
    }

    private static (long, long, long, long) ObserveOtelTitles()
    {
        var seen = new Dictionary<string, long>(StringComparer.Ordinal);
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Name == "phantom_swarm_health_titles")
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            string? type = null, health = null;
            foreach (var t in tags)
            {
                if (t.Key == "type")
                {
                    type = (string?)t.Value;
                }
                else if (t.Key == "health")
                {
                    health = (string?)t.Value;
                }
            }

            seen[type + "/" + health] = value;
        });
        listener.Start();
        listener.RecordObservableInstruments();
        return (seen["movie/live"], seen["movie/dead"], seen["series/live"], seen["series/dead"]);
    }

    private static async Task<(long, long, long, long)> ScrapePrometheusTitlesAsync()
    {
        using var stream = new MemoryStream();
        await Prometheus.Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
        var text = Encoding.UTF8.GetString(stream.ToArray());
        long Value(string type, string health)
        {
            var prefix = $"phantom_swarm_health_titles{{type=\"{type}\",health=\"{health}\"}} ";
            var line = text.Split('\n').Single(l => l.StartsWith(prefix, StringComparison.Ordinal));
            return long.Parse(line[prefix.Length..], System.Globalization.CultureInfo.InvariantCulture);
        }

        return (Value("movie", "live"), Value("movie", "dead"), Value("series", "live"), Value("series", "dead"));
    }

    internal static string Hash(char c) => new string(char.ToLowerInvariant(c), 40).Replace(char.ToLowerInvariant(c), c);

    private static ChannelItemInfo Movie(int tmdb) => new() { Id = ChannelItemId.ForMovie(tmdb).Encode(), Name = "m" + tmdb };

    private static ChannelItemInfo Series(int tmdb) => new() { Id = ChannelItemId.ForSeries(tmdb).Encode(), Name = "s" + tmdb };

    internal static PluginConfiguration Config() => new()
    {
        SwarmHealthServiceUrl = ServiceUrl,
        SwarmHealthBatchSize = 50,
        SwarmHealthRecheckHours = 24,
        SwarmHealthRankWindowDays = 7,
        CuratedRowSize = 10,
    };

    private static SwarmHealthWorker NewWorker(PhantomDb db, SwarmHealthClient client, ILibraryManager lib)
        => NewWorker(db, client, lib, new FakePager(new FakeLibrary()), Config);

    private static SwarmHealthWorker NewWorker(PhantomDb db, SwarmHealthClient client, ILibraryManager lib, IChannelScorePager pager, Func<PluginConfiguration> cfg, string target = "library-a", Func<DateTimeOffset>? clock = null)
        => new(db, client, lib, pager, NullLogger<SwarmHealthWorker>.Instance, cfg, clock ?? (() => DateTimeOffset.UtcNow), () => target);

    private async Task<PhantomDb> NewDbAsync()
    {
        var db = new PhantomDb(_dbPath);
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        return db;
    }

    private async Task InsertAvailabilityAsync(int tmdbId, string type, int season, int episode, string status, string? infoHash)
    {
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO availability_items
                (tmdb_id, type, season, episode, status, next_check_at, priority, candidate_info_hash)
            VALUES ($tmdb,$type,$season,$episode,$status,$next,0,$hash);";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$season", season);
        cmd.Parameters.AddWithValue("$episode", episode);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$next", DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$hash", (object?)infoHash ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    internal sealed class FakeClient : SwarmHealthClient
    {
        private readonly Dictionary<string, int>? _answers;

        public FakeClient(Dictionary<string, int>? answers)
            : base(new HttpClient())
        {
            _answers = answers;
        }

        public List<IReadOnlyCollection<string>> Requests { get; } = new();

        public override Task<IReadOnlyDictionary<string, int>> MeasureAsync(string baseUrl, IReadOnlyCollection<string> infoHashes, CancellationToken ct)
        {
            Requests.Add(infoHashes);
            if (_answers is null)
            {
                throw new HttpRequestException("service down");
            }

            IReadOnlyDictionary<string, int> result = infoHashes
                .Where(h => _answers.ContainsKey(h))
                .ToDictionary(h => h, h => _answers[h], StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(result);
        }
    }

    /// <summary>In-memory stand-in for the persisted channel BaseItems + the ILibraryManager write path.</summary>
    internal sealed class FakeLibrary
    {
        private readonly Mock<ILibraryManager> _mock = new(MockBehavior.Strict);

        public FakeLibrary()
        {
            _mock.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns<Guid>(id => Items.FirstOrDefault(i => i.Id == id)!);
            _mock.Setup(l => l.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>())).Returns<string, Type>(NewItemId);
            _mock.Setup(l => l.UpdateItemsAsync(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
                .Returns<IReadOnlyList<BaseItem>, BaseItem, ItemUpdateType, CancellationToken>((items, _, _, ct) =>
                {
                    _cancelOnSave?.Cancel();
                    ct.ThrowIfCancellationRequested();
                    if (items.Any(i => _failing.Contains(i.Id)))
                    {
                        // Like a real batched SaveChanges: one bad row fails the whole batch.
                        throw new InvalidOperationException("FK violation (item deleted concurrently)");
                    }

                    Updated.AddRange(items);
                    return Task.CompletedTask;
                });
        }

        private readonly HashSet<Guid> _failing = new();
        private CancellationTokenSource? _cancelOnSave;

        /// <summary>Every save including <paramref name="item"/> throws (concurrent-delete FK violation).</summary>
        public void FailSavesOf(BaseItem item) => _failing.Add(item.Id);

        /// <summary>The next save cancels <paramref name="cts"/> first (cancellation mid-write).</summary>
        public void CancelOnSave(CancellationTokenSource cts) => _cancelOnSave = cts;

        public List<BaseItem> Items { get; } = new();

        public List<BaseItem> Updated { get; } = new();

        public ILibraryManager Object => _mock.Object;

        /// <summary>Same derivation as Jellyfin's LibraryManager.GetNewItemId (case-insensitive ids).</summary>
        public static Guid NewItemId(string key, Type type)
            => new(System.Security.Cryptography.MD5.HashData(Encoding.Unicode.GetBytes((type.FullName + key).ToLowerInvariant())));

        public static Guid ChannelItemGuid(string externalId, string channelName, Type type) => NewItemId(externalId + channelName + "16", type);

        public void Add(BaseItem item) => Items.Add(item);

        public BaseItem ByExternal(string externalId) => Items.Single(i => i.ExternalId == externalId);
    }

    internal sealed class FakePager : IChannelScorePager
    {
        private readonly FakeLibrary _lib;

        public FakePager(FakeLibrary lib) => _lib = lib;

        public List<(ChannelScorePhase Phase, Guid? After, int Limit)> Calls { get; } = new();

        public string AccessPath => "fake";

        public Task<IReadOnlyList<ChannelScoreRow>> NextPageAsync(ChannelScorePhase phase, Guid? afterId, int limit, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((phase, afterId, limit));
            IReadOnlyList<ChannelScoreRow> page = _lib.Items
                .Where(i => i.ChannelId == phase.ChannelId && i.GetType().FullName == phase.ItemType)
                .Where(i => afterId is null || string.CompareOrdinal(i.Id.ToString("D").ToUpperInvariant(), afterId.Value.ToString("D").ToUpperInvariant()) > 0)
                .OrderBy(i => i.Id.ToString("D").ToUpperInvariant(), StringComparer.Ordinal)
                .Take(limit)
                .Select(i => new ChannelScoreRow(i.Id, i.ExternalId, i.CriticRating))
                .ToList();
            return Task.FromResult(page);
        }
    }

    private sealed class LambdaHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _fn;

        public LambdaHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> fn) => _fn = fn;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _fn(request);
    }
}
