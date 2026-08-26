using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// ROI Priority 6, revised architecture item 2a
/// (p6-magnet-cache-opportunistic-prefetch): the SAME caller sites
/// p6-yield-to-user-callers wired for the availability-priority bump +
/// activity-marker stamp (<see cref="Materialiser"/> and
/// <see cref="PhantomSourceManager"/>) must ALSO enqueue a HIGH-priority
/// <c>magnet_cache_jobs</c> row for the touched item, alongside — never
/// instead of — the existing promote.
///
/// These tests are the regression guard for that wiring. They FAIL against a
/// tree where the callers do not enqueue (no job row exists / the job would
/// lose to a larger low-priority background backlog) and PASS once the
/// wiring is threaded in. Movie AND episode parity is covered, and each of
/// the four user actions — playback, materialise, autopilot, favourite — is
/// exercised through its real entry point.
/// </summary>
public sealed class OpportunisticMagnetCachePrefetchTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "phantom-p6-mc-" + Guid.NewGuid().ToString("N") + ".db");

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

    // ---- Materialise / Play / Autopilot / Favourite all funnel through the
    // real Materialiser.MaterialiseCoreAsync, which is where the enqueue is
    // wired. Driving the real Materialiser with each trigger proves the
    // wiring for the materialise, playback, autopilot and favourite paths.

    [Theory]
    [InlineData(MaterialiseTrigger.Manual)]    // explicit materialise
    [InlineData(MaterialiseTrigger.Play)]      // playback-triggered materialise
    [InlineData(MaterialiseTrigger.Autopilot)] // autopilot prefetch
    [InlineData(MaterialiseTrigger.Favourite)] // favourite ingest -> materialise
    public async Task Materialise_Movie_EnqueuesHighPriorityMagnetCacheJob_AllUserTriggers(MaterialiseTrigger trigger)
    {
        using var db = await NewDbAsync();
        const int userTmdb = 42;
        const string preset = "test";
        await db.SetImdbIdAsync(userTmdb, "movie", "tt0000042", CancellationToken.None);

        // A large low-priority background-sweep backlog already queued.
        var backlog = await SeedBacklogAsync(db, count: 50, type: "movie", preset);

        Assert.Null(await db.GetMagnetCacheJobAsync(userTmdb, "movie", -1, -1, preset, CancellationToken.None));

        var sut = BuildMaterialiser(db, preset);
        await sut.MaterialiseAsync(userTmdb, "movie", null, null, trigger, CancellationToken.None);

        var job = await db.GetMagnetCacheJobAsync(userTmdb, "movie", -1, -1, preset, CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(PhantomDb.OpportunisticMagnetCachePriority, job!.Priority);
        Assert.True(job.Priority > 0);

        // The queue's own priority-first claim ordering now prefers the
        // user's item over the (much larger) background backlog.
        var claimed = await db.ClaimNextMagnetCacheJobAsync("builder", TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.Equal(userTmdb, claimed!.TmdbId);
        Assert.DoesNotContain(claimed.TmdbId, backlog);
    }

    [Theory]
    [InlineData(MaterialiseTrigger.Manual)]
    [InlineData(MaterialiseTrigger.Play)]
    [InlineData(MaterialiseTrigger.Autopilot)]
    [InlineData(MaterialiseTrigger.Favourite)]
    public async Task Materialise_Episode_EnqueuesHighPriorityMagnetCacheJob_AllUserTriggers(MaterialiseTrigger trigger)
    {
        using var db = await NewDbAsync();
        const int seriesTmdb = 200;
        const string preset = "test";
        await db.SetImdbIdAsync(seriesTmdb, "series", "tt0000200", CancellationToken.None);

        var backlog = await SeedBacklogAsync(db, count: 50, type: "episode", preset);

        Assert.Null(await db.GetMagnetCacheJobAsync(seriesTmdb, "episode", 1, 1, preset, CancellationToken.None));

        var sut = BuildMaterialiser(db, preset);
        await sut.MaterialiseAsync(seriesTmdb, "episode", 1, 1, trigger, CancellationToken.None);

        var job = await db.GetMagnetCacheJobAsync(seriesTmdb, "episode", 1, 1, preset, CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(PhantomDb.OpportunisticMagnetCachePriority, job!.Priority);

        var claimed = await db.ClaimNextMagnetCacheJobAsync("builder", TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.Equal(seriesTmdb, claimed!.TmdbId);
        Assert.Equal(1, claimed.Season);
        Assert.Equal(1, claimed.Episode);
        Assert.DoesNotContain(claimed.TmdbId, backlog);
    }

    // ---- The details/playback source view (PhantomSourceManager) is the
    // other user-initiated path; it does not always materialise, so it is
    // wired separately. A refresh-candidates view must enqueue.

    [Fact]
    public async Task SourceView_RefreshCandidates_Movie_EnqueuesHighPriorityMagnetCacheJob()
    {
        using var db = await NewDbAsync();
        const int userTmdb = 700;
        const string preset = "test";
        await SeedMovieCatalogueAsync(db, userTmdb);
        var backlog = await SeedBacklogAsync(db, count: 50, type: "movie", preset);

        Assert.Null(await db.GetMagnetCacheJobAsync(userTmdb, "movie", -1, -1, preset, CancellationToken.None));

        var mgr = BuildSourceManager(db, preset);
        var externalId = ChannelItemId.ForMovie(userTmdb).Encode();
        await mgr.GetSourcesAsync(externalId, refreshCandidates: true, CancellationToken.None);

        var job = await db.GetMagnetCacheJobAsync(userTmdb, "movie", -1, -1, preset, CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(PhantomDb.OpportunisticMagnetCachePriority, job!.Priority);

        var claimed = await db.ClaimNextMagnetCacheJobAsync("builder", TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.Equal(userTmdb, claimed!.TmdbId);
        Assert.DoesNotContain(claimed.TmdbId, backlog);
    }

    [Fact]
    public async Task SourceView_RefreshCandidates_Episode_EnqueuesHighPriorityMagnetCacheJob()
    {
        using var db = await NewDbAsync();
        const int seriesTmdb = 800;
        const string preset = "test";
        await SeedEpisodeCatalogueAsync(db, seriesTmdb, 1, 1);
        var backlog = await SeedBacklogAsync(db, count: 50, type: "episode", preset);

        Assert.Null(await db.GetMagnetCacheJobAsync(seriesTmdb, "episode", 1, 1, preset, CancellationToken.None));

        var mgr = BuildSourceManager(db, preset);
        var externalId = ChannelItemId.ForEpisode(seriesTmdb, 1, 1).Encode();
        await mgr.GetSourcesAsync(externalId, refreshCandidates: true, CancellationToken.None);

        var job = await db.GetMagnetCacheJobAsync(seriesTmdb, "episode", 1, 1, preset, CancellationToken.None);
        Assert.NotNull(job);
        Assert.Equal(PhantomDb.OpportunisticMagnetCachePriority, job!.Priority);

        var claimed = await db.ClaimNextMagnetCacheJobAsync("builder", TimeSpan.FromMinutes(5), CancellationToken.None);
        Assert.NotNull(claimed);
        Assert.Equal(seriesTmdb, claimed!.TmdbId);
        Assert.Equal(1, claimed.Season);
        Assert.Equal(1, claimed.Episode);
        Assert.DoesNotContain(claimed.TmdbId, backlog);
    }

    [Fact]
    public async Task SourceView_NoRefresh_DoesNotEnqueueMagnetCacheJob()
    {
        // A passive view (refreshCandidates=false) is not a probe-driving
        // user action, so it must NOT enqueue an opportunistic job.
        using var db = await NewDbAsync();
        const int userTmdb = 900;
        const string preset = "test";
        await SeedMovieCatalogueAsync(db, userTmdb);

        var mgr = BuildSourceManager(db, preset);
        var externalId = ChannelItemId.ForMovie(userTmdb).Encode();
        await mgr.GetSourcesAsync(externalId, refreshCandidates: false, CancellationToken.None);

        Assert.Null(await db.GetMagnetCacheJobAsync(userTmdb, "movie", -1, -1, preset, CancellationToken.None));
    }

    // ---------- builders ----------

    private async Task<PhantomDb> NewDbAsync()
    {
        var db = new PhantomDb(_dbPath);
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        return db;
    }

    private Materialiser BuildMaterialiser(PhantomDb db, string preset)
    {
        var gostream = new Mock<IGostreamClient>(MockBehavior.Loose);
        var refresh = new Mock<IChannelItemRefreshManager>(MockBehavior.Loose);
        // An empty indexer so materialise resolves to "unavailable" quickly
        // WITHOUT throwing — the enqueue happens before any probe, so the
        // materialise outcome is irrelevant to this test.
        IIndexerClient indexer = new EmptyIndexer();
        var scorer = new QualityScorer(NullLogger<QualityScorer>.Instance);
        var cfg = new PluginConfiguration
        {
            FusePathWaitTimeoutSeconds = 2,
            FusePathPollIntervalMilliseconds = 50,
            MaterialiseInFlightStaleMinutes = 10,
            SourcePickerPreset = preset,
            MinSeeders = 1,
            MinSizeGb1080p = 1,
            MinSizeGb4K = 1,
        };
        var selector = new MagnetSelector(new[] { indexer }, scorer, NullLogger<MagnetSelector>.Instance, () => cfg);
        var tmdb = new Mock<ITmdbClient>(MockBehavior.Loose);
        var externalIds = new TmdbExternalIdResolver(db, tmdb.Object, NullLogger<TmdbExternalIdResolver>.Instance);
        var libMgr = new Mock<ILibraryManager>(MockBehavior.Loose);
        var state = new ChannelStateProvider(db);

        return new Materialiser(
            libMgr.Object, db, gostream.Object, selector, externalIds,
            refresh.Object, state, NullLogger<Materialiser>.Instance, () => cfg);
    }

    private PhantomSourceManager BuildSourceManager(PhantomDb db, string preset)
    {
        var gostream = new Mock<IGostreamClient>(MockBehavior.Loose);
        var refresh = new Mock<IChannelItemRefreshManager>(MockBehavior.Loose);
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        IIndexerClient indexer = new EmptyIndexer();
        var scorer = new QualityScorer(NullLogger<QualityScorer>.Instance);
        var cfg = new PluginConfiguration
        {
            SourcePickerPreset = preset,
            MinSeeders = 1,
            MinSizeGb1080p = 1,
            MinSizeGb4K = 1,
            MaterialiseInFlightStaleMinutes = 10,
        };
        var selector = new MagnetSelector(new[] { indexer }, scorer, NullLogger<MagnetSelector>.Instance, () => cfg);
        var tmdb = new Mock<ITmdbClient>(MockBehavior.Loose);
        var externalIds = new TmdbExternalIdResolver(db, tmdb.Object, NullLogger<TmdbExternalIdResolver>.Instance);
        var state = new ChannelStateProvider(db);
        return new PhantomSourceManager(
            db, selector, materialiser.Object, gostream.Object, externalIds, refresh.Object, state, () => cfg);
    }

    // ---------- seeding helpers ----------

    private async Task<List<int>> SeedBacklogAsync(PhantomDb db, int count, string type, string preset)
    {
        var ids = new List<int>();
        var baseTmdb = 10_000_000;
        for (var i = 0; i < count; i++)
        {
            var tmdb = baseTmdb + i;
            // Background rows are enqueued at priority 0 (background-sweep
            // default), so without a high-priority bump they would be
            // claimed first (oldest-enqueued-first tiebreak favours them).
            if (type == "movie")
            {
                await db.EnqueueMagnetCacheJobAsync(tmdb, "movie", -1, -1, preset, 0, CancellationToken.None);
            }
            else
            {
                await db.EnqueueMagnetCacheJobAsync(tmdb, "episode", 1, 1, preset, 0, CancellationToken.None);
            }

            ids.Add(tmdb);
        }

        return ids;
    }

    private async Task SeedMovieCatalogueAsync(PhantomDb db, int tmdbId)
    {
        await db.SetImdbIdAsync(tmdbId, "movie", "tt" + tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);
        await db.UpsertTmdbMetadataAsync(new TmdbMetadataRow(
            tmdbId, "movie", "Source View Movie", 2020, null, null, null, null, null, null, null, DateTimeOffset.UtcNow), CancellationToken.None);
    }

    private async Task SeedEpisodeCatalogueAsync(PhantomDb db, int seriesTmdbId, int season, int episode)
    {
        await db.SetImdbIdAsync(seriesTmdbId, "series", "tt" + seriesTmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);
        await db.UpsertTmdbMetadataAsync(new TmdbMetadataRow(
            seriesTmdbId, "series", "Source View Series", 2020, null, null, null, null, null, null, null, DateTimeOffset.UtcNow), CancellationToken.None);
        await db.UpsertTmdbEpisodeAsync(new TmdbEpisodeRow(
            seriesTmdbId, season, episode, "Ep", null, null, "2020-01-02", null, DateTimeOffset.UtcNow), CancellationToken.None);
    }

    private sealed class EmptyIndexer : IIndexerClient
    {
        public string Name => "empty";
        public bool IsEnabled => true;
        public Task<IReadOnlyList<IndexerCandidate>> SearchAsync(IndexerQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<IndexerCandidate>>(Array.Empty<IndexerCandidate>());
    }
}
