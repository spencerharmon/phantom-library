using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.Scheduled;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public sealed class AvailabilityProbeWorkerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "phantom-availability-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task StartAsync_DisabledConfigurationStillArmsTimerForLaterEnable()
    {
        using var db = await NewDbAsync();
        var cfg = Config();
        cfg.AvailabilityProbeEnabled = false;
        var worker = BuildWorker(db, cfg, new EmptyIndexer());

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var timerField = typeof(AvailabilityProbeWorker).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(nameof(AvailabilityProbeWorker), "_timer");
            Assert.NotNull(timerField.GetValue(worker));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [Fact]
    public async Task Tick_PrefersSeriesExpansionSoMovieBacklogDoesNotStarveTv()
    {
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 99000010);
        await SeedSeriesAsync(db, 99000100);
        await db.SetImdbIdAsync(99000010, "movie", "tt99000010", CancellationToken.None);
        var cfg = Config();
        cfg.AvailabilityMaxBatchSize = 1;
        var tmdb = new Mock<ITmdbClient>(MockBehavior.Loose);
        tmdb.Setup(t => t.GetSeriesAsync(99000100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TmdbSeriesDetails(
                99000100,
                "TV Parity Series",
                "TV Parity Series",
                string.Empty,
                string.Empty,
                "2020-01-01",
                "2020-01-01",
                0,
                0,
                Array.Empty<string>(),
                string.Empty,
                1,
                1,
                Array.Empty<string>(),
                "tt99000100"));
        tmdb.Setup(t => t.GetSeasonAsync(99000100, 1, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TmdbSeasonDetails
            {
                SeriesTmdbId = 99000100,
                SeasonNumber = 1,
                Episodes = new List<TmdbEpisodeSummary>
                {
                    new() { Id = 990001001, SeasonNumber = 1, EpisodeNumber = 1, Name = "Pilot", AirDate = "2020-01-01" },
                },
            });
        var worker = BuildWorker(db, cfg, new EmptyIndexer(), tmdb.Object);

        await InvokeTickAsync(worker);

        Assert.Equal(1, await CountEpisodeAvailabilityRowsAsync());
        Assert.Equal(0, await ReadAttemptCountAsync(99000010, "movie", -1, -1));
    }

    [Fact]
    public async Task TransientAllFail_DoesNotWriteUnavailableOrMarker()
    {
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 99000001);
        await db.SetImdbIdAsync(99000001, "movie", "tt99000001", CancellationToken.None);
        var cfg = Config();
        var worker = BuildWorker(db, cfg, new TransientIndexer("timeout"));

        var didWork = await InvokeProbeOneAsync(worker, cfg);

        Assert.True(didWork);
        var (status, error) = await ReadAvailabilityAsync(99000001);
        Assert.Equal("unknown", status);
        Assert.Equal("indexer_partial_or_total_failure", error);
        Assert.Null(await db.IsMarkedUnavailableAsync(new UnavailableKey(99000001, "tt99000001", "movie", null, null), CancellationToken.None));
        Assert.Equal(0, await CountRowsAsync("magnet_failure_cache"));
    }

    [Fact]
    public async Task DefinitiveEmpty_WritesUnavailable()
    {
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 99000002);
        await db.SetImdbIdAsync(99000002, "movie", "tt99000002", CancellationToken.None);
        var cfg = Config();
        var worker = BuildWorker(db, cfg, new EmptyIndexer());

        var didWork = await InvokeProbeOneAsync(worker, cfg);

        Assert.True(didWork);
        var (status, _) = await ReadAvailabilityAsync(99000002);
        Assert.Equal("unavailable", status);
    }

    [Fact]
    public async Task NoCapableIndexer_AppliesLongBackoffAndLeavesStatusUnknown()
    {
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 99000003);
        // No imdb set: mirrors the "no imdb + no capable indexer" case.
        var cfg = Config();
        cfg.AvailabilityNoIndexerRetryHours = 24;
        cfg.AvailabilityTransientRetryMinutes = 30;
        var before = DateTimeOffset.UtcNow;
        var worker = BuildWorker(
            db,
            cfg,
            new EmptyIndexer(),
            probe: (_, _, _, _, _, _, _, _) => Task.FromResult(
                new MagnetProbeResult(MagnetProbeOutcome.NoCapableIndexer, Array.Empty<MagnetCandidate>(), "no_capable_indexer", "no enabled indexer")));

        var didWork = await InvokeProbeOneAsync(worker, cfg);

        Assert.True(didWork);
        var (status, error, nextCheck) = await ReadAvailabilityFullAsync(99000003, "movie", -1, -1);
        Assert.Equal("unknown", status);
        Assert.Equal("no_capable_indexer", error);
        // Backoff must be the long (>= NoIndexerRetryHours) horizon, NOT the
        // 30-minute transient retry.
        var delay = DateTimeOffset.FromUnixTimeSeconds(nextCheck) - before;
        Assert.True(delay >= TimeSpan.FromHours(cfg.AvailabilityNoIndexerRetryHours) - TimeSpan.FromMinutes(5),
            $"expected >= {cfg.AvailabilityNoIndexerRetryHours}h backoff, got {delay}");
        Assert.True(delay > TimeSpan.FromHours(1), "backoff must be far longer than a 30-minute transient");
    }

    [Fact]
    public async Task PreFilter_NoCapableIndexer_DeepDefersWithoutInvokingProbe()
    {
        // p6-prefilter-unavailable: unlike NoCapableIndexer_AppliesLongBackoff...
        // above (which synthesizes the outcome via a probe delegate), this
        // exercises the real claim-path pre-classification: no imdb id + a
        // Torrentio-shaped indexer (RequiresImdb=true) must deep-defer WITHOUT
        // ever invoking the probe/indexer layer.
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 99000600);
        // Deliberately no imdb id set.
        await InsertMovieAvailabilityAsync(db, 99000600, status: "unknown", nextCheckAt: DateTimeOffset.UtcNow.AddHours(-1), priority: 0);
        var cfg = Config();
        cfg.AvailabilityNoIndexerRetryHours = 24;
        var before = DateTimeOffset.UtcNow;
        var probeCalled = false;
        var worker = BuildWorker(
            db,
            cfg,
            new TorrentioLikeIndexer(),
            probe: (_, _, _, _, _, _, _, _) =>
            {
                probeCalled = true;
                return Task.FromResult(MagnetProbeResult.DefinitiveUnavailable());
            });

        var didWork = await InvokeProbeOneAsync(worker, cfg);

        Assert.True(didWork);
        Assert.False(probeCalled, "no-capable-indexer must be pre-filtered before reaching the probe delegate");
        var (status, error, nextCheck) = await ReadAvailabilityFullAsync(99000600, "movie", -1, -1);
        Assert.Equal("unknown", status);
        Assert.Equal("no_capable_indexer", error);
        var delay = DateTimeOffset.FromUnixTimeSeconds(nextCheck) - before;
        Assert.True(delay >= TimeSpan.FromHours(cfg.AvailabilityNoIndexerRetryHours) - TimeSpan.FromMinutes(5),
            $"expected >= {cfg.AvailabilityNoIndexerRetryHours}h backoff, got {delay}");
        Assert.True(delay > TimeSpan.FromHours(1), "backoff must be far longer than a 30-minute transient");
    }

    // ---- p6-decouple-oracle-magnetcache: end-to-end oracle/magnet-cache split ----
    // ROI Priority 6, revised architecture item 1 (2026-08-26). These exercise the
    // worker's REAL (non-delegate-overridden) probe path across BOTH a Torrentio-shaped
    // and a Prowlarr-shaped indexer, proving the availability sweep invokes only
    // Torrentio end to end.

    [Fact]
    public async Task Sweep_ImdbBearingMovie_InvokesTorrentioOnly_NeverProwlarr()
    {
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 99000800);
        await db.SetImdbIdAsync(99000800, "movie", "tt99000800", CancellationToken.None);
        var cfg = Config();
        var torrentio = new TorrentioLikeAvailableIndexer();
        var prowlarr = new ProwlarrLikeIndexer();
        var worker = BuildWorker(db, cfg, torrentio, prowlarr);

        var didWork = await InvokeProbeOneAsync(worker, cfg);

        Assert.True(didWork);
        Assert.Equal(1, torrentio.SearchCallCount);
        Assert.Equal(0, prowlarr.SearchCallCount);
        var (status, _) = await ReadAvailabilityAsync(99000800);
        Assert.Equal("available", status);
    }

    [Fact]
    public async Task Sweep_ImdbBearingEpisode_InvokesTorrentioOnly_NeverProwlarr()
    {
        // Episode parity of the movie assertion above.
        using var db = await NewDbAsync();
        await SeedSeriesAsync(db, 99000900);
        await InsertEpisodeCatalogueAsync(db, 99000900, 1, 1, "2020-01-01");
        await InsertEpisodeAvailabilityAsync(db, 99000900, 1, 1, "unknown", DateTimeOffset.UtcNow.AddHours(-1));
        await db.SetImdbIdAsync(99000900, "series", "tt99000900", CancellationToken.None);
        var cfg = Config();
        var torrentio = new TorrentioLikeAvailableIndexer();
        var prowlarr = new ProwlarrLikeIndexer();
        var worker = BuildWorker(db, cfg, torrentio, prowlarr);

        var didWork = await InvokeProbeOneAsync(worker, cfg);

        Assert.True(didWork);
        Assert.Equal(1, torrentio.SearchCallCount);
        Assert.Equal(0, prowlarr.SearchCallCount);
    }

    [Fact]
    public async Task Sweep_NoImdb_ReconcilesWithProwlarr_NoHighConfidenceMagnet_StillDeepDefers()
    {
        // No-IMDB movie, availability-probe-reconcile-001 item 1: unlike the
        // OLD pre-p6-reconcile behaviour (Prowlarr never invoked), the sweep
        // must now attempt a Prowlarr RECONCILE before deep-deferring the
        // abstained oracle (Torrentio) verdict, since Prowlarr does not
        // require an IMDB id and could still resolve a definitive verdict.
        // Here Prowlarr returns zero candidates too, so the final outcome is
        // still the same "no_capable_indexer" deep defer as before — the
        // reconcile attempt happened (SearchCallCount>0) but found nothing.
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 99000950);
        // Deliberately no imdb id set.
        var cfg = Config();
        cfg.AvailabilityNoIndexerRetryHours = 24;
        var torrentio = new TorrentioLikeIndexer();
        var prowlarr = new ProwlarrLikeIndexer();
        var worker = BuildWorker(db, cfg, torrentio, prowlarr);

        var didWork = await InvokeProbeOneAsync(worker, cfg);

        Assert.True(didWork);
        Assert.Equal(1, prowlarr.SearchCallCount);
        var (status, error) = await ReadAvailabilityAsync(99000950);
        Assert.Equal("unknown", status);
        Assert.Equal("no_capable_indexer", error);
    }

    [Fact]
    public async Task Sweep_NoImdb_TorrentioAbstains_ProwlarrHighConfidenceMagnet_ReconcilesToAvailable()
    {
        // availability-probe-reconcile-001 item 1, the positive case: Torrentio
        // abstains (no IMDB id) but Prowlarr holds a magnet clearing the SAME
        // high-confidence (MinSeeders/size) bar the oracle path would have
        // applied — the sweep must reach a DEFINITIVE "available" verdict from
        // Prowlarr rather than surfacing availability_abstain/no_capable_indexer.
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 99000951);
        // Deliberately no imdb id set.
        var cfg = Config();
        var torrentio = new TorrentioLikeIndexer();
        var prowlarr = new ProwlarrHighConfidenceIndexer();
        var worker = BuildWorker(db, cfg, torrentio, prowlarr);

        var didWork = await InvokeProbeOneAsync(worker, cfg);

        Assert.True(didWork);
        Assert.Equal(1, prowlarr.SearchCallCount);
        var (status, error) = await ReadAvailabilityAsync(99000951);
        Assert.Equal("available", status);
        Assert.Null(error);
    }

    [Fact]
    public async Task Sweep_ImdbBearingMovie_TorrentioAvailable_NeverReconcilesWithProwlarr()
    {
        // Parity guard: when the oracle (Torrentio) itself already reaches a
        // definitive Available verdict, the reconcile fan-out to Prowlarr must
        // never fire — reconcile is strictly an abstain-only fallback, never an
        // extra cost on the ordinary hot-loop path.
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 99000952);
        await db.SetImdbIdAsync(99000952, "movie", "tt99000952", CancellationToken.None);
        var cfg = Config();
        var torrentio = new TorrentioLikeAvailableIndexer();
        var prowlarr = new ProwlarrLikeIndexer();
        var worker = BuildWorker(db, cfg, torrentio, prowlarr);

        var didWork = await InvokeProbeOneAsync(worker, cfg);

        Assert.True(didWork);
        Assert.Equal(0, prowlarr.SearchCallCount);
        var (status, _) = await ReadAvailabilityAsync(99000952);
        Assert.Equal("available", status);
    }

    // ---- p6-availability-convergence: no-forever-churn + TTL reprobe ----

    [Fact]
    public async Task Transient_EscalatesToLongBackoffAfterMaxAttempts_Movie()
    {
        // Convergence guarantee (movie): a permanently-transient item (every
        // probe throws IndexerTransientException) must not churn the short
        // AvailabilityTransientRetryMinutes cadence forever. Once
        // AttemptCount exceeds AvailabilityTransientMaxAttempts, the backoff
        // escalates to the bounded AvailabilityTransientEscalatedRetryHours.
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 99000700);
        await db.SetImdbIdAsync(99000700, "movie", "tt99000700", CancellationToken.None);
        var cfg = Config();
        cfg.AvailabilityTransientRetryMinutes = 5;
        cfg.AvailabilityTransientMaxAttempts = 2;
        cfg.AvailabilityTransientEscalatedRetryHours = 24;
        var worker = BuildWorker(db, cfg, new TransientIndexer("timeout"));

        long nextCheck = 0;
        for (var i = 0; i < 3; i++)
        {
            await InsertMovieAvailabilityAsync(db, 99000700, status: "unknown", nextCheckAt: DateTimeOffset.UtcNow.AddHours(-1), priority: 0);
            var didWork = await InvokeProbeOneAsync(worker, cfg);
            Assert.True(didWork);
            (_, _, nextCheck) = await ReadAvailabilityFullAsync(99000700, "movie", -1, -1);
        }

        // Third attempt: attempt_count (2 prior + this claim = 3) exceeds
        // MaxAttempts(2) -> escalated long backoff, not the 5-minute cadence.
        var before = DateTimeOffset.UtcNow;
        var delay = DateTimeOffset.FromUnixTimeSeconds(nextCheck) - before;
        Assert.True(delay >= TimeSpan.FromHours(cfg.AvailabilityTransientEscalatedRetryHours) - TimeSpan.FromMinutes(5),
            $"expected escalated >= {cfg.AvailabilityTransientEscalatedRetryHours}h backoff after exceeding max attempts, got {delay}");
        Assert.True(await ReadAttemptCountAsync(99000700, "movie", -1, -1) > cfg.AvailabilityTransientMaxAttempts);
    }

    [Fact]
    public async Task Transient_EscalatesToLongBackoffAfterMaxAttempts_Episode()
    {
        // Convergence guarantee (episode/TV parity of the movie test above).
        using var db = await NewDbAsync();
        await SeedSeriesAsync(db, 99000701);
        await db.SetImdbIdAsync(99000701, "series", "tt99000701", CancellationToken.None);
        var pastAirDate = DateTimeOffset.UtcNow.AddDays(-30).Date;
        await InsertEpisodeCatalogueAsync(db, 99000701, season: 1, episode: 1, airDate: pastAirDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        var cfg = Config();
        cfg.AvailabilityTransientRetryMinutes = 5;
        cfg.AvailabilityTransientMaxAttempts = 2;
        cfg.AvailabilityTransientEscalatedRetryHours = 24;
        cfg.EpisodeReleaseDelayHours = 0;
        var worker = BuildWorker(db, cfg, new TransientIndexer("timeout"));

        long nextCheck = 0;
        for (var i = 0; i < 3; i++)
        {
            await InsertEpisodeAvailabilityAsync(db, 99000701, season: 1, episode: 1, status: "unknown", nextCheckAt: DateTimeOffset.UtcNow.AddHours(-1));
            var didWork = await InvokeProbeOneAsync(worker, cfg);
            Assert.True(didWork);
            (_, _, nextCheck) = await ReadAvailabilityFullAsync(99000701, "episode", 1, 1);
        }

        var before = DateTimeOffset.UtcNow;
        var delay = DateTimeOffset.FromUnixTimeSeconds(nextCheck) - before;
        Assert.True(delay >= TimeSpan.FromHours(cfg.AvailabilityTransientEscalatedRetryHours) - TimeSpan.FromMinutes(5),
            $"expected escalated >= {cfg.AvailabilityTransientEscalatedRetryHours}h backoff after exceeding max attempts, got {delay}");
        Assert.True(await ReadAttemptCountAsync(99000701, "episode", 1, 1) > cfg.AvailabilityTransientMaxAttempts);
    }

    [Fact]
    public async Task DefinitiveOutcome_ResetsAttemptCountAfterPriorTransientChurn_Movie()
    {
        // Convergence guarantee: once an item reaches a DEFINITIVE state
        // (available/unavailable) its attempt_count resets to 0, so a later
        // TTL re-probe starts the escalation window fresh rather than
        // inheriting stale churn from a since-resolved problem.
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 99000702);
        await db.SetImdbIdAsync(99000702, "movie", "tt99000702", CancellationToken.None);
        var cfg = Config();
        cfg.AvailabilityTransientRetryMinutes = 5;
        var transientWorker = BuildWorker(db, cfg, new TransientIndexer("timeout"));
        for (var i = 0; i < 3; i++)
        {
            await InsertMovieAvailabilityAsync(db, 99000702, status: "unknown", nextCheckAt: DateTimeOffset.UtcNow.AddHours(-1), priority: 0);
            Assert.True(await InvokeProbeOneAsync(transientWorker, cfg));
        }

        Assert.True(await ReadAttemptCountAsync(99000702, "movie", -1, -1) > 0);

        // The source now resolves definitively -> attempt_count must reset.
        await InsertMovieAvailabilityAsync(db, 99000702, status: "unknown", nextCheckAt: DateTimeOffset.UtcNow.AddHours(-1), priority: 0);
        var definitiveWorker = BuildWorker(db, cfg, new EmptyIndexer());
        Assert.True(await InvokeProbeOneAsync(definitiveWorker, cfg));

        var (status, _) = await ReadAvailabilityAsync(99000702);
        Assert.Equal("unavailable", status);
        Assert.Equal(0, await ReadAttemptCountAsync(99000702, "movie", -1, -1));
    }

    // ---- availability-probe-reconcile-001 item 2: TTL re-probe (never stale forever) ----

    [Fact]
    public async Task AvailableRow_PastNextCheckAt_IsClaimedAndReProbedOnTtlExpiry()
    {
        // A row previously marked "available" whose TTL (next_check_at) has
        // already passed must be re-claimed and re-probed on the next tick —
        // never served as a stale verdict forever. Here the source has since
        // gone away (EmptyIndexer), so the re-probe must flip it to unavailable.
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 99000703);
        await db.SetImdbIdAsync(99000703, "movie", "tt99000703", CancellationToken.None);
        await InsertMovieAvailabilityAsync(db, 99000703, status: "available", nextCheckAt: DateTimeOffset.UtcNow.AddSeconds(-1), priority: 0);
        var cfg = Config();
        var worker = BuildWorker(db, cfg, new EmptyIndexer());

        var didWork = await InvokeProbeOneAsync(worker, cfg);

        Assert.True(didWork);
        var (status, _) = await ReadAvailabilityAsync(99000703);
        Assert.Equal("unavailable", status);
    }

    [Fact]
    public async Task UnavailableRow_PastNextCheckAt_IsClaimedAndReProbedOnTtlExpiry()
    {
        // Negative-cache counterpart: an expired "unavailable" TTL must also
        // be re-claimed rather than served stale forever. Here the source has
        // since appeared, so the re-probe must flip it to available.
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 99000704);
        await db.SetImdbIdAsync(99000704, "movie", "tt99000704", CancellationToken.None);
        await InsertMovieAvailabilityAsync(db, 99000704, status: "unavailable", nextCheckAt: DateTimeOffset.UtcNow.AddSeconds(-1), priority: 0);
        var cfg = Config();
        var worker = BuildWorker(db, cfg, new TorrentioLikeAvailableIndexer());

        var didWork = await InvokeProbeOneAsync(worker, cfg);

        Assert.True(didWork);
        var (status, _) = await ReadAvailabilityAsync(99000704);
        Assert.Equal("available", status);
    }

    // ---- availability-probe-reconcile-001 item 3: bounded exponential negative-cache backoff ----

    [Fact]
    public void ComputeNegativeCacheTtl_FirstConfirmedNegative_UsesBaseTtl()
    {
        var cfg = Config();
        cfg.AvailabilityUnavailableTtlDays = 7;
        cfg.AvailabilityUnavailableMaxTtlDays = 56;
        var lease = MakeLease(negativeStreak: 0);

        var ttl = AvailabilityProbeWorker.ComputeNegativeCacheTtl(cfg, lease);

        Assert.Equal(TimeSpan.FromDays(7), ttl);
    }

    [Fact]
    public void ComputeNegativeCacheTtl_RepeatedConfirmedNegatives_GrowsExponentially()
    {
        var cfg = Config();
        cfg.AvailabilityUnavailableTtlDays = 7;
        cfg.AvailabilityUnavailableMaxTtlDays = 1000;

        Assert.Equal(TimeSpan.FromDays(7), AvailabilityProbeWorker.ComputeNegativeCacheTtl(cfg, MakeLease(0)));
        Assert.Equal(TimeSpan.FromDays(14), AvailabilityProbeWorker.ComputeNegativeCacheTtl(cfg, MakeLease(1)));
        Assert.Equal(TimeSpan.FromDays(28), AvailabilityProbeWorker.ComputeNegativeCacheTtl(cfg, MakeLease(2)));
        Assert.Equal(TimeSpan.FromDays(56), AvailabilityProbeWorker.ComputeNegativeCacheTtl(cfg, MakeLease(3)));
    }

    [Fact]
    public void ComputeNegativeCacheTtl_IsBoundedByConfiguredMax()
    {
        var cfg = Config();
        cfg.AvailabilityUnavailableTtlDays = 7;
        cfg.AvailabilityUnavailableMaxTtlDays = 30;

        // Streak large enough that the unbounded doubling would blow far past
        // the cap; the result must never exceed AvailabilityUnavailableMaxTtlDays.
        var ttl = AvailabilityProbeWorker.ComputeNegativeCacheTtl(cfg, MakeLease(10));

        Assert.Equal(TimeSpan.FromDays(30), ttl);
    }

    [Fact]
    public async Task RepeatedConfirmedNegative_GrowsBackoff_ThenResetsOnPositive()
    {
        // End-to-end: three consecutive confirmed-negative probes must grow the
        // effective TTL each time (bounded exponential backoff), and a
        // subsequent positive probe must reset both status and the streak.
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 99000705);
        await db.SetImdbIdAsync(99000705, "movie", "tt99000705", CancellationToken.None);
        var cfg = Config();
        cfg.AvailabilityUnavailableTtlDays = 1;
        cfg.AvailabilityUnavailableMaxTtlDays = 100;
        var negativeWorker = BuildWorker(db, cfg, new EmptyIndexer());

        var delays = new List<TimeSpan>();
        for (var i = 0; i < 3; i++)
        {
            await InsertMovieAvailabilityAsync(db, 99000705, status: "unknown", nextCheckAt: DateTimeOffset.UtcNow.AddHours(-1), priority: 0);
            var before = DateTimeOffset.UtcNow;
            Assert.True(await InvokeProbeOneAsync(negativeWorker, cfg));
            var (status, _, nextCheck) = await ReadAvailabilityFullAsync(99000705, "movie", -1, -1);
            Assert.Equal("unavailable", status);
            delays.Add(DateTimeOffset.FromUnixTimeSeconds(nextCheck) - before);
        }

        // Each successive confirmed-negative backoff must be strictly longer
        // than the previous (bounded exponential growth), never flat/fixed.
        Assert.True(delays[1] > delays[0] + TimeSpan.FromHours(1), $"expected delay[1] ({delays[1]}) > delay[0] ({delays[0]}) + slack");
        Assert.True(delays[2] > delays[1] + TimeSpan.FromHours(1), $"expected delay[2] ({delays[2]}) > delay[1] ({delays[1]}) + slack");

        // Now the source resolves positively -> status flips and the negative
        // streak resets (proven indirectly: a subsequent confirmed negative
        // reverts to the base TTL, not a continuation of the prior escalation).
        await InsertMovieAvailabilityAsync(db, 99000705, status: "unknown", nextCheckAt: DateTimeOffset.UtcNow.AddHours(-1), priority: 0);
        var positiveWorker = BuildWorker(db, cfg, new TorrentioLikeAvailableIndexer());
        Assert.True(await InvokeProbeOneAsync(positiveWorker, cfg));
        var (availStatus, _) = await ReadAvailabilityAsync(99000705);
        Assert.Equal("available", availStatus);

        await InsertMovieAvailabilityAsync(db, 99000705, status: "unknown", nextCheckAt: DateTimeOffset.UtcNow.AddHours(-1), priority: 0);
        var beforeReset = DateTimeOffset.UtcNow;
        Assert.True(await InvokeProbeOneAsync(negativeWorker, cfg));
        var (resetStatus, _, resetNextCheck) = await ReadAvailabilityFullAsync(99000705, "movie", -1, -1);
        Assert.Equal("unavailable", resetStatus);
        var resetDelay = DateTimeOffset.FromUnixTimeSeconds(resetNextCheck) - beforeReset;
        Assert.True(resetDelay < delays[0] + TimeSpan.FromHours(1), $"expected reset delay ({resetDelay}) back near the base TTL, not a continuation of the prior escalation ({delays[0]})");
    }

    private static AvailabilityItemRow MakeLease(int negativeStreak) => new(
        TmdbId: 1,
        Type: "movie",
        Season: -1,
        Episode: -1,
        Status: "unknown",
        CheckedAt: null,
        NextCheckAt: DateTimeOffset.UtcNow,
        CandidateMagnet: null,
        CandidateInfoHash: null,
        CandidateSize: null,
        CandidateSeeders: null,
        CandidateIndexer: null,
        CandidateSource: null,
        ProbeGeneration: 0,
        LeaseOwner: null,
        AttemptCount: 0,
        NegativeStreak: negativeStreak);

    [Fact]
    public async Task PreFilter_ProwlarrCapable_NoImdb_DoesNotDeepDeferAndReachesProbe()
    {
        // p6-prowlarr-indexer-wiring parity: once a title-based indexer (e.g.
        // Prowlarr) is enabled, a no-imdb title must NOT be pre-filtered as
        // no-capable-indexer — the probe still runs and the item stays in the
        // ready set.
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 99000601);
        await InsertMovieAvailabilityAsync(db, 99000601, status: "unknown", nextCheckAt: DateTimeOffset.UtcNow.AddHours(-1), priority: 0);
        var cfg = Config();
        var probeCalled = false;
        var worker = BuildWorker(
            db,
            cfg,
            new EmptyIndexer(), // RequiresImdb=false (default), like Prowlarr
            probe: (_, _, _, _, _, _, _, _) =>
            {
                probeCalled = true;
                return Task.FromResult(MagnetProbeResult.DefinitiveUnavailable());
            });

        var didWork = await InvokeProbeOneAsync(worker, cfg);

        Assert.True(didWork);
        Assert.True(probeCalled, "a title-based-capable indexer must not be pre-filtered out");
        var (status, error) = await ReadAvailabilityAsync(99000601);
        Assert.Equal("unavailable", status);
    }

    [Fact]
    public async Task PreFilter_FutureReleaseYearMovie_DeepDefersToJanFirstBoundary()
    {
        using var db = await NewDbAsync();
        var futureYear = DateTimeOffset.UtcNow.Year + 1;
        await db.UpsertCatalogueHitsAsync(new[]
        {
            new TmdbMetadataRow(99000602, "movie", "Future Movie", futureYear, null, null, null, null, null, null, null, DateTimeOffset.UtcNow),
        }, sourceMask: 1, DateTimeOffset.UtcNow, CancellationToken.None);
        await db.SetImdbIdAsync(99000602, "movie", "tt99000602", CancellationToken.None);
        await InsertMovieAvailabilityAsync(db, 99000602, status: "unknown", nextCheckAt: DateTimeOffset.UtcNow.AddHours(-1), priority: 0);
        var cfg = Config();
        var probeCalled = false;
        var worker = BuildWorker(
            db,
            cfg,
            new EmptyIndexer(),
            probe: (_, _, _, _, _, _, _, _) =>
            {
                probeCalled = true;
                return Task.FromResult(MagnetProbeResult.DefinitiveUnavailable());
            });

        var didWork = await InvokeProbeOneAsync(worker, cfg);

        Assert.True(didWork);
        Assert.False(probeCalled, "an unreleased movie must be deep-deferred before reaching the probe");
        var (status, error, nextCheck) = await ReadAvailabilityFullAsync(99000602, "movie", -1, -1);
        Assert.Equal("unknown", status);
        Assert.Equal("unreleased", error);
        Assert.Equal(new DateTimeOffset(futureYear, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(), nextCheck);
    }

    [Fact]
    public async Task PreFilter_FutureAiredEpisode_DeepDefersToReleaseBoundary()
    {
        using var db = await NewDbAsync();
        await SeedSeriesAsync(db, 99000603);
        var futureAirDate = DateTimeOffset.UtcNow.AddDays(30).Date;
        await InsertEpisodeCatalogueAsync(db, 99000603, season: 1, episode: 1, airDate: futureAirDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        await InsertEpisodeAvailabilityAsync(db, 99000603, season: 1, episode: 1, status: "unknown", nextCheckAt: DateTimeOffset.UtcNow.AddHours(-1));
        var cfg = Config();
        cfg.EpisodeReleaseDelayHours = 12;
        var probeCalled = false;
        var worker = BuildWorker(
            db,
            cfg,
            new EmptyIndexer(),
            probe: (_, _, _, _, _, _, _, _) =>
            {
                probeCalled = true;
                return Task.FromResult(MagnetProbeResult.DefinitiveUnavailable());
            });

        var didWork = await InvokeProbeOneAsync(worker, cfg);

        Assert.True(didWork);
        Assert.False(probeCalled, "a not-yet-aired episode must be deep-deferred before reaching the probe");
        var (status, error, nextCheck) = await ReadAvailabilityFullAsync(99000603, "episode", 1, 1);
        Assert.Equal("unknown", status);
        Assert.Equal("unreleased", error);
        var expectedBoundary = new DateTimeOffset(futureAirDate, TimeSpan.Zero).AddHours(12);
        Assert.True(
            Math.Abs((DateTimeOffset.FromUnixTimeSeconds(nextCheck) - expectedBoundary).TotalMinutes) < 5,
            $"expected boundary ~{expectedBoundary}, got {DateTimeOffset.FromUnixTimeSeconds(nextCheck)}");
    }

    [Fact]
    public async Task PreFilter_ReleasedEpisode_ReachesProbeAndStaysReady()
    {
        // Parity check: a released, capable episode must NOT be pre-filtered —
        // it reaches the probe like today, staying in the ready set.
        using var db = await NewDbAsync();
        await SeedSeriesAsync(db, 99000604);
        var pastAirDate = DateTimeOffset.UtcNow.AddDays(-30).Date;
        await InsertEpisodeCatalogueAsync(db, 99000604, season: 1, episode: 1, airDate: pastAirDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        await InsertEpisodeAvailabilityAsync(db, 99000604, season: 1, episode: 1, status: "unknown", nextCheckAt: DateTimeOffset.UtcNow.AddHours(-1));
        var cfg = Config();
        var probeCalled = false;
        var worker = BuildWorker(
            db,
            cfg,
            new EmptyIndexer(),
            probe: (_, _, _, _, _, _, _, _) =>
            {
                probeCalled = true;
                return Task.FromResult(MagnetProbeResult.DefinitiveUnavailable());
            });

        var didWork = await InvokeProbeOneAsync(worker, cfg);

        Assert.True(didWork);
        Assert.True(probeCalled, "a released episode must reach the probe, not be deep-deferred");
        var (status, _, _) = await ReadAvailabilityFullAsync(99000604, "episode", 1, 1);
        Assert.Equal("unavailable", status);
    }

    [Fact]
    public async Task Claim_PrefersHigherPriorityOverOlderNextCheck()
    {
        using var db = await NewDbAsync();
        var now = DateTimeOffset.UtcNow;
        // Low-priority row is "more overdue" (older next_check_at) but priority 0.
        await InsertMovieAvailabilityAsync(db, 99000201, status: "unavailable", nextCheckAt: now.AddHours(-10), priority: 0);
        // High-priority row is less overdue but priority 5.
        await InsertMovieAvailabilityAsync(db, 99000202, status: "unavailable", nextCheckAt: now.AddHours(-1), priority: 5);

        var lease = await db.ClaimDueAvailabilityAsync(
            "test-owner", TimeSpan.FromMinutes(5), now, "policy", CancellationToken.None, "movie");

        Assert.NotNull(lease);
        Assert.Equal(99000202, lease!.TmdbId);
    }

    [Fact]
    public async Task SeriesExpansion_OnlyRepresentativeEpisodeIsDueNow()
    {
        using var db = await NewDbAsync();
        await SeedSeriesAsync(db, 99000300);
        var cfg = Config();
        cfg.AvailabilityBackgroundEpisodesPerSeries = 1;
        cfg.AvailabilityDeferredEpisodeDays = 30;
        var tmdb = new Mock<ITmdbClient>(MockBehavior.Loose);
        tmdb.Setup(t => t.GetSeriesAsync(99000300, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TmdbSeriesDetails(
                99000300, "Rep Series", "Rep Series", string.Empty, string.Empty,
                "2019-01-01", "2019-01-01", 0, 0, Array.Empty<string>(), string.Empty, 1, 3,
                Array.Empty<string>(), "tt99000300"));
        tmdb.Setup(t => t.GetSeasonAsync(99000300, 1, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TmdbSeasonDetails
            {
                SeriesTmdbId = 99000300,
                SeasonNumber = 1,
                Episodes = new List<TmdbEpisodeSummary>
                {
                    // Deliberately out of air-date order to prove the earliest
                    // aired episode is the representative.
                    new() { Id = 3, SeasonNumber = 1, EpisodeNumber = 3, Name = "Third", AirDate = "2019-03-01" },
                    new() { Id = 1, SeasonNumber = 1, EpisodeNumber = 1, Name = "First", AirDate = "2019-01-01" },
                    new() { Id = 2, SeasonNumber = 1, EpisodeNumber = 2, Name = "Second", AirDate = "2019-02-01" },
                },
            });
        var worker = BuildWorker(db, cfg, new EmptyIndexer(), tmdb.Object);

        await InvokeExpandOneSeriesAsync(worker, cfg);

        Assert.Equal(3, await CountEpisodeAvailabilityRowsAsync());
        var now = DateTimeOffset.UtcNow;
        var dueNow = await CountEpisodesDueAtOrBeforeAsync(99000300, now.AddMinutes(1));
        Assert.Equal(1, dueNow);
        // The single due episode is the earliest aired (S1E1).
        var repNext = await ReadEpisodeNextCheckAsync(99000300, 1, 1);
        Assert.True(DateTimeOffset.FromUnixTimeSeconds(repNext) <= now.AddMinutes(1));
        // Siblings deferred well into the future (>= ~29 days out).
        var sibNext = await ReadEpisodeNextCheckAsync(99000300, 1, 2);
        Assert.True(DateTimeOffset.FromUnixTimeSeconds(sibNext) >= now.AddDays(29),
            "non-representative episodes must be deferred");
    }

    [Fact]
    public async Task Tick_YieldsWhenUserActivityIsRecent()
    {
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 99000401);
        await db.SetImdbIdAsync(99000401, "movie", "tt99000401", CancellationToken.None);
        // Make the movie row due now so, absent yielding, the tick would probe.
        await InsertMovieAvailabilityAsync(db, 99000401, status: "unknown", nextCheckAt: DateTimeOffset.UtcNow.AddHours(-1), priority: 0);
        var cfg = Config();
        cfg.AvailabilityYieldToUserSeconds = 60;
        await db.TouchUserActivityAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        var worker = BuildWorker(db, cfg, new EmptyIndexer());

        await InvokeTickAsync(worker);

        // No probe ran: attempt_count stays 0 and status stays unknown.
        Assert.Equal(0, await ReadAttemptCountAsync(99000401, "movie", -1, -1));
        var (status, _) = await ReadAvailabilityAsync(99000401);
        Assert.Equal("unknown", status);
    }

    private async Task<PhantomDb> NewDbAsync()
    {
        var db = new PhantomDb(_dbPath);
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        return db;
    }

    private static Task SeedMovieAsync(PhantomDb db, int tmdbId)
        => SeedCatalogueAsync(db, tmdbId, "movie", "Availability Test Movie");

    private static Task SeedSeriesAsync(PhantomDb db, int tmdbId)
        => SeedCatalogueAsync(db, tmdbId, "series", "TV Parity Series");

    private static async Task SeedCatalogueAsync(PhantomDb db, int tmdbId, string type, string title)
    {
        await db.UpsertCatalogueHitsAsync(new[]
        {
            new TmdbMetadataRow(
                tmdbId,
                type,
                title,
                2020,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                DateTimeOffset.UtcNow),
        }, sourceMask: 1, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    private static PluginConfiguration Config() => new()
    {
        AvailabilityProbeEnabled = true,
        AvailabilityLeaseMinutes = 1,
        AvailabilityTransientRetryMinutes = 5,
        AvailabilityAvailableTtlDays = 7,
        AvailabilityUnavailableTtlDays = 7,
        MagnetCacheTtlHours = 24,
        SourcePickerPreset = "test",
        MinSeeders = 1,
        MinSizeGb1080p = 1,
        MinSizeGb4K = 1,
    };

    private static AvailabilityProbeWorker BuildWorker(PhantomDb db, PluginConfiguration cfg, IIndexerClient indexer)
    {
        var tmdb = new Mock<ITmdbClient>(MockBehavior.Loose);
        return BuildWorker(db, cfg, indexer, tmdb.Object);
    }

    private static AvailabilityProbeWorker BuildWorker(PhantomDb db, PluginConfiguration cfg, IIndexerClient indexer, ITmdbClient tmdb)
    {
        var externalIds = new TmdbExternalIdResolver(db, tmdb, NullLogger<TmdbExternalIdResolver>.Instance);
        var scorer = new QualityScorer(NullLogger<QualityScorer>.Instance);
        var selector = new MagnetSelector(new[] { indexer }, scorer, NullLogger<MagnetSelector>.Instance, () => cfg);
        return new AvailabilityProbeWorker(
            db,
            selector,
            externalIds,
            tmdb,
            new ChannelStateProvider(db),
            NullLogger<AvailabilityProbeWorker>.Instance,
            () => cfg);
    }

    /// <summary>
    /// Wires the REAL (non-delegate-overridden) <see cref="MagnetSelector.ProbeAvailabilityAsync"/>
    /// path across MULTIPLE indexers — used to prove the worker's availability sweep
    /// invokes only the availability-oracle-eligible indexer(s) end to end (ROI
    /// Priority 6, revised architecture item 1).
    /// </summary>
    private static AvailabilityProbeWorker BuildWorker(PhantomDb db, PluginConfiguration cfg, params IIndexerClient[] indexers)
    {
        var tmdb = new Mock<ITmdbClient>(MockBehavior.Loose).Object;
        var externalIds = new TmdbExternalIdResolver(db, tmdb, NullLogger<TmdbExternalIdResolver>.Instance);
        var scorer = new QualityScorer(NullLogger<QualityScorer>.Instance);
        var selector = new MagnetSelector(indexers, scorer, NullLogger<MagnetSelector>.Instance, () => cfg);
        return new AvailabilityProbeWorker(
            db,
            selector,
            externalIds,
            tmdb,
            new ChannelStateProvider(db),
            NullLogger<AvailabilityProbeWorker>.Instance,
            () => cfg);
    }

    private static AvailabilityProbeWorker BuildWorker(
        PhantomDb db,
        PluginConfiguration cfg,
        IIndexerClient indexer,
        AvailabilityProbeWorker.ProbeDelegate probe)
    {
        var tmdb = new Mock<ITmdbClient>(MockBehavior.Loose).Object;
        var externalIds = new TmdbExternalIdResolver(db, tmdb, NullLogger<TmdbExternalIdResolver>.Instance);
        var scorer = new QualityScorer(NullLogger<QualityScorer>.Instance);
        var selector = new MagnetSelector(new[] { indexer }, scorer, NullLogger<MagnetSelector>.Instance, () => cfg);
        return new AvailabilityProbeWorker(
            db,
            selector,
            externalIds,
            tmdb,
            new ChannelStateProvider(db),
            NullLogger<AvailabilityProbeWorker>.Instance,
            () => cfg,
            probe);
    }

    private static async Task<bool> InvokeProbeOneAsync(AvailabilityProbeWorker worker, PluginConfiguration cfg)
    {
        var method = typeof(AvailabilityProbeWorker).GetMethod(
            "ProbeOneAvailabilityAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(PluginConfiguration), typeof(CancellationToken) },
            modifiers: null)
            ?? throw new MissingMethodException(nameof(AvailabilityProbeWorker), "ProbeOneAvailabilityAsync");
        var task = (Task<bool>)(method.Invoke(worker, new object[] { cfg, CancellationToken.None })
            ?? throw new InvalidOperationException("ProbeOneAvailabilityAsync returned null"));
        return await task;
    }

    private static async Task InvokeTickAsync(AvailabilityProbeWorker worker)
    {
        var method = typeof(AvailabilityProbeWorker).GetMethod("TickAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(AvailabilityProbeWorker), "TickAsync");
        var task = (Task)(method.Invoke(worker, new object[] { CancellationToken.None })
            ?? throw new InvalidOperationException("TickAsync returned null"));
        await task;
    }

    private static async Task InvokeExpandOneSeriesAsync(AvailabilityProbeWorker worker, PluginConfiguration cfg)
    {
        var method = typeof(AvailabilityProbeWorker).GetMethod(
            "ExpandOneSeriesAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(AvailabilityProbeWorker), "ExpandOneSeriesAsync");
        var task = (Task<bool>)(method.Invoke(worker, new object[] { cfg, CancellationToken.None })
            ?? throw new InvalidOperationException("ExpandOneSeriesAsync returned null"));
        await task;
    }

    private async Task InsertMovieAvailabilityAsync(PhantomDb db, int tmdbId, string status, DateTimeOffset nextCheckAt, int priority)
    {
        // Ensure the schema exists before we write a raw row.
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        // Seed a raw availability_items row directly so tests control status,
        // next_check_at, and priority precisely.
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO availability_items
                (tmdb_id, type, season, episode, status, next_check_at, priority)
            VALUES ($tmdb,'movie',-1,-1,$status,$next,$priority)
            ON CONFLICT(tmdb_id, type, season, episode) DO UPDATE SET
                status=excluded.status, next_check_at=excluded.next_check_at, priority=excluded.priority;";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$next", nextCheckAt.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$priority", priority);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertEpisodeAvailabilityAsync(PhantomDb db, int seriesTmdbId, int season, int episode, string status, DateTimeOffset nextCheckAt)
    {
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO availability_items
                (tmdb_id, type, season, episode, status, next_check_at, priority)
            VALUES ($tmdb,'episode',$season,$episode,$status,$next,0)
            ON CONFLICT(tmdb_id, type, season, episode) DO UPDATE SET
                status=excluded.status, next_check_at=excluded.next_check_at;";
        cmd.Parameters.AddWithValue("$tmdb", seriesTmdbId);
        cmd.Parameters.AddWithValue("$season", season);
        cmd.Parameters.AddWithValue("$episode", episode);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$next", nextCheckAt.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task InsertEpisodeCatalogueAsync(PhantomDb db, int seriesTmdbId, int season, int episode, string airDate)
    {
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO series_episode_catalogue
                (series_tmdb_id, episode_tmdb_id, season, episode, air_date, first_seen_at, last_seen_at)
            VALUES ($tmdb,$episodeTmdb,$season,$episode,$air,$now,$now)
            ON CONFLICT(series_tmdb_id, season, episode) DO UPDATE SET
                air_date=excluded.air_date, last_seen_at=excluded.last_seen_at;";
        cmd.Parameters.AddWithValue("$tmdb", seriesTmdbId);
        cmd.Parameters.AddWithValue("$episodeTmdb", seriesTmdbId * 1000 + season * 100 + episode);
        cmd.Parameters.AddWithValue("$season", season);
        cmd.Parameters.AddWithValue("$episode", episode);
        cmd.Parameters.AddWithValue("$air", airDate);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();
    }


    private async Task<int> CountEpisodesDueAtOrBeforeAsync(int seriesTmdbId, DateTimeOffset at)
    {
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM availability_items WHERE tmdb_id=$tmdb AND type='episode' AND next_check_at<=$at;";
        cmd.Parameters.AddWithValue("$tmdb", seriesTmdbId);
        cmd.Parameters.AddWithValue("$at", at.ToUnixTimeSeconds());
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<long> ReadEpisodeNextCheckAsync(int seriesTmdbId, int season, int episode)
    {
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT next_check_at FROM availability_items WHERE tmdb_id=$tmdb AND type='episode' AND season=$s AND episode=$e;";
        cmd.Parameters.AddWithValue("$tmdb", seriesTmdbId);
        cmd.Parameters.AddWithValue("$s", season);
        cmd.Parameters.AddWithValue("$e", episode);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<(string Status, string? ErrorKind, long NextCheck)> ReadAvailabilityFullAsync(int tmdbId, string type, int season, int episode)
    {
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status,last_error_kind,next_check_at FROM availability_items WHERE tmdb_id=$tmdb AND type=$type AND season=$s AND episode=$e;";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$s", season);
        cmd.Parameters.AddWithValue("$e", episode);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetInt64(2));
    }

    private async Task<int> CountRowsAsync(string table)
    {
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM " + table + ";";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<int> CountEpisodeAvailabilityRowsAsync()
    {
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM availability_items WHERE type='episode';";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<int> ReadAttemptCountAsync(int tmdbId, string type, int season, int episode)
    {
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT attempt_count FROM availability_items WHERE tmdb_id=$tmdb AND type=$type AND season=$season AND episode=$episode;";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$season", season);
        cmd.Parameters.AddWithValue("$episode", episode);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<(string Status, string? ErrorKind)> ReadAvailabilityAsync(int tmdbId)
    {
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status,last_error_kind FROM availability_items WHERE tmdb_id=$tmdb AND type='movie' AND season=-1 AND episode=-1;";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    private sealed class TransientIndexer(string message) : IIndexerClient
    {
        public string Name => "transient";
        public bool IsEnabled => true;
        // These generic fakes stand in for "whatever the single configured indexer
        // is" across tests unrelated to the Torrentio/Prowlarr availability-oracle
        // split (ROI Priority 6, revised architecture item 1) — mark them
        // availability-oracle-eligible so the worker's real ProbeAvailabilityAsync
        // path still exercises them via BuildWorker's single-indexer overload.
        public bool IsAvailabilityOracle => true;
        public Task<IReadOnlyList<IndexerCandidate>> SearchAsync(IndexerQuery query, CancellationToken ct)
            => throw new IndexerTransientException(message);
    }

    private sealed class EmptyIndexer : IIndexerClient
    {
        public string Name => "empty";
        public bool IsEnabled => true;
        public bool IsAvailabilityOracle => true;
        public Task<IReadOnlyList<IndexerCandidate>> SearchAsync(IndexerQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<IndexerCandidate>>(Array.Empty<IndexerCandidate>());
    }

    /// <summary>Torrentio-shaped fake: RequiresImdb=true, abstains without an imdb id.</summary>
    private sealed class TorrentioLikeIndexer : IIndexerClient
    {
        public string Name => "Torrentio";
        public bool IsEnabled => true;
        public bool RequiresImdb => true;
        public bool IsAvailabilityOracle => true;
        public Task<IReadOnlyList<IndexerCandidate>> SearchAsync(IndexerQuery query, CancellationToken ct)
            => throw new IndexerNotApplicableException("Torrentio requires an IMDB id");
    }

    /// <summary>
    /// Torrentio-shaped fake that RETURNS a candidate when an imdb id is present
    /// (rather than always abstaining like <see cref="TorrentioLikeIndexer"/>), so
    /// end-to-end availability-sweep tests can assert a real Available outcome while
    /// still counting invocations.
    /// </summary>
    private sealed class TorrentioLikeAvailableIndexer : IIndexerClient
    {
        public int SearchCallCount { get; private set; }
        public string Name => "Torrentio";
        public bool IsEnabled => true;
        public bool RequiresImdb => true;
        public bool IsAvailabilityOracle => true;
        public Task<IReadOnlyList<IndexerCandidate>> SearchAsync(IndexerQuery query, CancellationToken ct)
        {
            SearchCallCount++;
            if (string.IsNullOrWhiteSpace(query.Imdb) && string.IsNullOrWhiteSpace(query.SeriesImdb))
            {
                throw new IndexerNotApplicableException("Torrentio requires an IMDB id");
            }

            IReadOnlyList<IndexerCandidate> hits = new[]
            {
                new IndexerCandidate
                {
                    Title = "Torrentio candidate",
                    Magnet = "magnet:?xt=urn:btih:" + Guid.NewGuid().ToString("N"),
                    InfoHash = Guid.NewGuid().ToString("N"),
                    Size = 5L * 1024 * 1024 * 1024,
                    Seeders = 40,
                    IndexerName = "Torrentio",
                },
            };
            return Task.FromResult(hits);
        }
    }

    /// <summary>
    /// Prowlarr-shaped fake: does NOT require an IMDB id and is NOT an
    /// availability-oracle indexer (ROI Priority 6, revised architecture item 1).
    /// It must never be invoked while the oracle (Torrentio) itself can reach a
    /// definitive verdict; it IS invoked as the availability-probe-reconcile-001
    /// item 1 reconcile fallback when the oracle abstains. This fake returns no
    /// candidates (a reconcile attempt that finds nothing); see
    /// <see cref="ProwlarrHighConfidenceIndexer"/> for the reconcile-succeeds case.
    /// </summary>
    private sealed class ProwlarrLikeIndexer : IIndexerClient
    {
        public int SearchCallCount { get; private set; }
        public string Name => "Prowlarr";
        public bool IsEnabled => true;
        public bool RequiresImdb => false;
        public bool IsAvailabilityOracle => false;
        public Task<IReadOnlyList<IndexerCandidate>> SearchAsync(IndexerQuery query, CancellationToken ct)
        {
            SearchCallCount++;
            return Task.FromResult<IReadOnlyList<IndexerCandidate>>(Array.Empty<IndexerCandidate>());
        }
    }

    /// <summary>
    /// Prowlarr-shaped fake that RETURNS a high-confidence candidate (clears the
    /// same MinSeeders/size bar the oracle path enforces) — the reconcile-
    /// succeeds case of availability-probe-reconcile-001 item 1.
    /// </summary>
    private sealed class ProwlarrHighConfidenceIndexer : IIndexerClient
    {
        public int SearchCallCount { get; private set; }
        public string Name => "Prowlarr";
        public bool IsEnabled => true;
        public bool RequiresImdb => false;
        public bool IsAvailabilityOracle => false;
        public Task<IReadOnlyList<IndexerCandidate>> SearchAsync(IndexerQuery query, CancellationToken ct)
        {
            SearchCallCount++;
            IReadOnlyList<IndexerCandidate> hits = new[]
            {
                new IndexerCandidate
                {
                    Title = "Prowlarr high-confidence candidate",
                    Magnet = "magnet:?xt=urn:btih:" + Guid.NewGuid().ToString("N"),
                    InfoHash = Guid.NewGuid().ToString("N"),
                    Size = 5L * 1024 * 1024 * 1024,
                    Seeders = 40,
                    IndexerName = "Prowlarr",
                },
            };
            return Task.FromResult(hits);
        }
    }
}
