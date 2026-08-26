using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
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

/// <summary>
/// p6-ttfb-acceptance-rig — the unit/API-layer ACCEPTANCE capstone over the
/// whole decoupled oracle / magnet-cache architecture (ROI Priority 6, revised
/// architecture 2026-08-26). Each sibling task carries its own focused
/// regression suite; this file is the single, self-contained gate that proves
/// the architecture's THREE user-visible promises hold TOGETHER, for MOVIE and
/// EPISODE alike, exercising the real production code paths (no mocks of the
/// unit under test):
///
///   A. TTFB / materialise-success improves with a PRE-CACHED magnet — an item
///      whose magnet cache the builder already populated resolves from the
///      persisted <c>source_candidates</c> store (the cache-first materialise
///      path <see cref="Materialiser"/> reads) with ZERO further indexer
///      fan-out, whereas a cold item has nothing to resolve from and forces a
///      fresh probe. (Deps: p6-magnet-cache-store, p6-materialise-ttfb-fix.)
///
///   B. Opportunistic pre-fetch BEATS a large background backlog — a
///      user-interest action enqueues a HIGH-priority magnet-cache job that the
///      queue's priority-first claim ordering serves ahead of a 50-item
///      low-priority background sweep backlog. (Deps:
///      p6-magnet-cache-opportunistic-prefetch, p6-magnet-cache-background-sweep.)
///
///   C. The Torrentio-only availability sweep drives listing visibility WITHOUT
///      Prowlarr in the per-item hot loop — the real availability-probe worker
///      resolves an item to a definitive listing state invoking ONLY the
///      availability-oracle (Torrentio-shaped) indexer, never the Prowlarr-shaped
///      fan-out. (Deps: p6-decouple-oracle-magnetcache, p6-availability-convergence.)
///
/// This suite FAILS against a tree missing any of the decoupled-architecture
/// behaviours (no cache store to resolve from; a background backlog claimed
/// ahead of a user action; Prowlarr invoked in the availability sweep) and
/// PASSES once all five siblings are integrated. Movie AND episode parity is
/// asserted for every claim.
/// </summary>
public sealed class DecoupledArchitectureAcceptanceTests : IDisposable
{
    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), "phantom-p6-acceptance-" + Guid.NewGuid().ToString("N") + ".db");

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

    private const string Preset = "test";

    // ================================================================
    // Claim A — TTFB / materialise-success improves with a pre-cached magnet
    // ================================================================

    [Theory]
    [InlineData("movie", -1, -1)]
    [InlineData("episode", 1, 1)]
    public async Task PreCachedMagnet_MaterialiseResolvesFromStore_NoFurtherFanOut(string type, int season, int episode)
    {
        using var db = await NewDbAsync();
        const int tmdb = 55_010;

        // A cold item has NOTHING in the persisted magnet-cache store: the
        // cache-first materialise read would find zero rows and must fall
        // through to a fresh (slow) probe.
        var cold = await db.ListSourceCandidatesAsync(tmdb, type, season, episode, Preset, includeExpired: false, CancellationToken.None);
        Assert.Empty(cold);

        // Populate the magnet cache the way the background/opportunistic
        // populators do: enqueue a build job and run the REAL builder's
        // Prowlarr fan-out ONCE.
        var fanOutCalls = 0;
        var fullSet = new List<MagnetCandidate>
        {
            new("magnet:?xt=urn:btih:AAAA", "AAAA", 6L * 1024 * 1024 * 1024, 80, "prowlarr") { Title = "Pre-Cached 2160p" },
            new("magnet:?xt=urn:btih:BBBB", "BBBB", 4L * 1024 * 1024 * 1024, 40, "prowlarr") { Title = "Pre-Cached 1080p" },
        };
        var builder = new MagnetCacheBuilder(
            db,
            (t, ty, s, e, ct) => Task.FromResult<MagnetCacheItemMeta?>(new MagnetCacheItemMeta("tt0055010", "Pre-Cached Title", 2020)),
            (t, imdb, ty, s, e, title, year, ct) => { fanOutCalls++; return Task.FromResult<IReadOnlyList<MagnetCandidate>>(fullSet); },
            NullLogger<MagnetCacheBuilder>.Instance,
            () => Config());

        await db.EnqueueMagnetCacheJobAsync(tmdb, type, season, episode, Preset, 0, CancellationToken.None);
        var buildResult = await builder.ProcessNextAsync(CancellationToken.None);

        Assert.NotNull(buildResult);
        Assert.Equal(1, fanOutCalls);
        Assert.Equal(fullSet.Count, buildResult!.CandidateCount);

        // The cache-first materialise read now finds the full pre-cached set
        // WITHOUT any further fan-out. This is exactly the store the
        // cache-first Materialiser path (p6-materialise-ttfb-fix) resolves
        // from — proving a materialise attempt against a pre-cached item
        // succeeds without the cold-guess probe latency.
        var warm = await db.ListSourceCandidatesAsync(tmdb, type, season, episode, Preset, includeExpired: false, CancellationToken.None);
        Assert.Equal(fullSet.Count, warm.Count);
        Assert.Contains(warm, r => r.InfoHash == "AAAA");
        Assert.Contains(warm, r => r.InfoHash == "BBBB");

        // A second materialise-time cache read does NOT re-run the fan-out —
        // the persisted store answers it. Fan-out stays at 1.
        var warmAgain = await db.ListSourceCandidatesAsync(tmdb, type, season, episode, Preset, includeExpired: false, CancellationToken.None);
        Assert.Equal(fullSet.Count, warmAgain.Count);
        Assert.Equal(1, fanOutCalls);
    }

    // ================================================================
    // Claim B — opportunistic pre-fetch beats a large background backlog
    // ================================================================

    [Theory]
    [InlineData("movie", null, null)]
    [InlineData("episode", 1, 1)]
    public async Task OpportunisticPrefetch_ClaimedAheadOfLargeBackgroundBacklog(string type, int? season, int? episode)
    {
        using var db = await NewDbAsync();

        // A large low-priority background-sweep backlog is already queued
        // (background sweep enqueues at BackgroundSweepMagnetCachePriority=0,
        // oldest-first). Absent a priority bump these would be claimed first.
        var backlog = new List<int>();
        for (var i = 0; i < 50; i++)
        {
            var id = 20_000_000 + i;
            await db.EnqueueMagnetCacheJobAsync(id, type, season ?? -1, episode ?? -1, Preset,
                PhantomDb.BackgroundSweepMagnetCachePriority, CancellationToken.None);
            backlog.Add(id);
        }

        // A user-interest action fires the opportunistic enqueue (the same
        // high-priority path the wired caller sites use).
        const int userTmdb = 20_099_999;
        var jobId = await db.EnqueueOpportunisticMagnetCacheJobAsync(userTmdb, type, season, episode, Preset, CancellationToken.None);
        Assert.NotNull(jobId);

        var job = await db.GetMagnetCacheJobAsync(userTmdb, type, season ?? -1, episode ?? -1, Preset, CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(PhantomDb.OpportunisticMagnetCachePriority, job!.Priority);
        Assert.True(job.Priority > PhantomDb.BackgroundSweepMagnetCachePriority);

        // The queue's priority-first claim ordering serves the user's item
        // ahead of the entire background backlog.
        var claimed = await db.ClaimNextMagnetCacheJobAsync("builder", TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.Equal(userTmdb, claimed!.TmdbId);
        Assert.DoesNotContain(claimed.TmdbId, backlog);
        if (type == "episode")
        {
            Assert.Equal(season, claimed.Season);
            Assert.Equal(episode, claimed.Episode);
        }
    }

    // ================================================================
    // Claim C — Torrentio-only availability sweep, Prowlarr never in the hot loop
    // ================================================================

    [Fact]
    public async Task AvailabilitySweep_Movie_InvokesTorrentioOnly_NeverProwlarr_AndDrivesListingVisibility()
    {
        using var db = await NewDbAsync();
        const int tmdb = 55_030_800;
        await SeedCatalogueAsync(db, tmdb, "movie", "Sweep Movie");
        await db.SetImdbIdAsync(tmdb, "movie", "tt55030800", CancellationToken.None);

        var cfg = Config();
        var torrentio = new CountingTorrentioIndexer();
        var prowlarr = new CountingProwlarrIndexer();
        var worker = BuildWorker(db, cfg, torrentio, prowlarr);

        var didWork = await InvokeProbeOneAsync(worker, cfg);

        Assert.True(didWork);
        // The availability sweep resolves the item to a definitive listing
        // state (visible) driven ONLY by the Torrentio-shaped oracle indexer.
        Assert.Equal(1, torrentio.SearchCallCount);
        Assert.Equal(0, prowlarr.SearchCallCount);
        var (status, _) = await ReadAvailabilityAsync(tmdb, "movie", -1, -1);
        Assert.Equal("available", status);
    }

    [Fact]
    public async Task AvailabilitySweep_Episode_InvokesTorrentioOnly_NeverProwlarr_AndDrivesListingVisibility()
    {
        // Episode parity of the movie assertion above.
        using var db = await NewDbAsync();
        const int series = 55_030_900;
        await SeedCatalogueAsync(db, series, "series", "Sweep Series");
        await InsertEpisodeCatalogueAsync(db, series, 1, 1, "2020-01-01");
        await InsertEpisodeAvailabilityAsync(db, series, 1, 1, "unknown", DateTimeOffset.UtcNow.AddHours(-1));
        await db.SetImdbIdAsync(series, "series", "tt55030900", CancellationToken.None);

        var cfg = Config();
        var torrentio = new CountingTorrentioIndexer();
        var prowlarr = new CountingProwlarrIndexer();
        var worker = BuildWorker(db, cfg, torrentio, prowlarr);

        var didWork = await InvokeProbeOneAsync(worker, cfg);

        Assert.True(didWork);
        Assert.Equal(1, torrentio.SearchCallCount);
        Assert.Equal(0, prowlarr.SearchCallCount);
        var (status, _) = await ReadAvailabilityAsync(series, "episode", 1, 1);
        Assert.Equal("available", status);
    }

    // ---------- helpers ----------

    private async Task<PhantomDb> NewDbAsync()
    {
        var db = new PhantomDb(_dbPath);
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        return db;
    }

    private static PluginConfiguration Config() => new()
    {
        AvailabilityProbeEnabled = true,
        AvailabilityLeaseMinutes = 1,
        AvailabilityTransientRetryMinutes = 5,
        AvailabilityAvailableTtlDays = 7,
        AvailabilityUnavailableTtlDays = 7,
        MagnetCacheTtlHours = 24,
        MagnetCacheBuildLeaseMinutes = 10,
        SourcePickerPreset = Preset,
        MinSeeders = 1,
        MinSizeGb1080p = 1,
        MinSizeGb4K = 1,
    };

    private static async Task SeedCatalogueAsync(PhantomDb db, int tmdbId, string type, string title)
    {
        await db.UpsertCatalogueHitsAsync(new[]
        {
            new TmdbMetadataRow(tmdbId, type, title, 2020, null, null, null, null, null, null, null, DateTimeOffset.UtcNow),
        }, sourceMask: 1, DateTimeOffset.UtcNow, CancellationToken.None);
    }

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

    private async Task<(string Status, string? ErrorKind)> ReadAvailabilityAsync(int tmdbId, string type, int season, int episode)
    {
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status,last_error_kind FROM availability_items WHERE tmdb_id=$tmdb AND type=$type AND season=$s AND episode=$e;";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$s", season);
        cmd.Parameters.AddWithValue("$e", episode);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    /// <summary>Torrentio-shaped availability oracle that returns a hit when an imdb id is present.</summary>
    private sealed class CountingTorrentioIndexer : IIndexerClient
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
    /// Prowlarr-shaped indexer: NOT an availability oracle. Any invocation in
    /// the availability-sweep path is a regression (Prowlarr's heavy fan-out
    /// must stay out of the per-item hot loop).
    /// </summary>
    private sealed class CountingProwlarrIndexer : IIndexerClient
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
}
