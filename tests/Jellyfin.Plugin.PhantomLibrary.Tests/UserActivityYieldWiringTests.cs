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
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// ROI P6 (item 2): every user-initiated availability path must BUMP the
/// target availability row's priority above the background backlog AND STAMP
/// the user-activity yield marker, so the background sweep (already
/// priority-first + marker-honouring, landed earlier) preempts to the user's
/// item and backs off the UI.
///
/// These tests are the regression guard for the CALLER WIRING. They FAIL
/// against a tree where the callers do not promote (priority stays 0, marker
/// stays null, so the sweep would claim the backlog first) and PASS once the
/// wiring is threaded in. Movie AND episode parity is covered, and each of
/// the four user actions — playback, materialise, autopilot, favourite — is
/// exercised through its real entry point.
/// </summary>
public sealed class UserActivityYieldWiringTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "phantom-p6-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly string _fuseMount = Path.Combine(Path.GetTempPath(), "phantom-p6-fuse-" + Guid.NewGuid().ToString("N"));

    public UserActivityYieldWiringTests() => Directory.CreateDirectory(_fuseMount);

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            if (Directory.Exists(_fuseMount)) Directory.Delete(_fuseMount, recursive: true);
        }
        catch
        {
            // best-effort
        }
    }

    // ---- Materialise / Play / Autopilot / Favourite all funnel through the
    // real Materialiser.MaterialiseCoreAsync, which is where the promote is
    // wired. Driving the real Materialiser with each trigger proves the
    // wiring for the materialise, playback, autopilot and favourite paths.

    [Theory]
    [InlineData(MaterialiseTrigger.Manual)]   // explicit materialise
    [InlineData(MaterialiseTrigger.Play)]     // playback-triggered materialise
    [InlineData(MaterialiseTrigger.Autopilot)] // autopilot prefetch
    [InlineData(MaterialiseTrigger.Favourite)] // favourite ingest -> materialise
    public async Task Materialise_Movie_BumpsPriorityAndStampsMarker_AllUserTriggers(MaterialiseTrigger trigger)
    {
        using var db = await NewDbAsync();
        const int userTmdb = 42;
        await db.SetImdbIdAsync(userTmdb, "movie", "tt0000042", CancellationToken.None);

        // A large background backlog: many movie rows that are far more overdue
        // than the user's item, all at background priority 0.
        var backlog = await SeedBacklogAsync(db, count: 50, type: "movie");
        // The user's own item exists at background priority, LESS overdue.
        await InsertAvailabilityAsync(db, userTmdb, "movie", -1, -1, status: "unknown",
            nextCheckAt: DateTimeOffset.UtcNow.AddMinutes(-1), priority: 0);

        var before = await ReadPriorityAsync(userTmdb, "movie", -1, -1);
        Assert.Equal(0, before);
        Assert.Null(await db.GetUserActivityAtAsync(CancellationToken.None));

        var sut = BuildMaterialiser(db);
        await sut.MaterialiseAsync(userTmdb, "movie", null, null, trigger, CancellationToken.None);

        // Priority bumped above the backlog.
        var after = await ReadPriorityAsync(userTmdb, "movie", -1, -1);
        Assert.Equal(PhantomDb.UserActivityPriority, after);
        Assert.True(after > 0);

        // Marker stamped.
        Assert.NotNull(await db.GetUserActivityAtAsync(CancellationToken.None));

        // The sweep's own claim ordering now prefers the user's item over the
        // (older, larger) backlog — proving the promote actually preempts.
        var lease = await db.ClaimDueAvailabilityAsync(
            "sweep", TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow, "policy", CancellationToken.None, "movie");
        Assert.NotNull(lease);
        Assert.Equal(userTmdb, lease!.TmdbId);
        Assert.DoesNotContain(lease.TmdbId, backlog);
    }

    [Theory]
    [InlineData(MaterialiseTrigger.Manual)]
    [InlineData(MaterialiseTrigger.Play)]
    [InlineData(MaterialiseTrigger.Autopilot)]
    [InlineData(MaterialiseTrigger.Favourite)]
    public async Task Materialise_Episode_BumpsPriorityAndStampsMarker_AllUserTriggers(MaterialiseTrigger trigger)
    {
        using var db = await NewDbAsync();
        const int seriesTmdb = 200;
        // series lookup negative-cache so resolver returns quickly.
        await db.SetImdbIdAsync(seriesTmdb, "series", "tt0000200", CancellationToken.None);

        var backlog = await SeedBacklogAsync(db, count: 50, type: "episode");
        // User's own episode row, background priority, less overdue.
        await InsertAvailabilityAsync(db, seriesTmdb, "episode", 1, 1, status: "unknown",
            nextCheckAt: DateTimeOffset.UtcNow.AddMinutes(-1), priority: 0);

        Assert.Equal(0, await ReadPriorityAsync(seriesTmdb, "episode", 1, 1));
        Assert.Null(await db.GetUserActivityAtAsync(CancellationToken.None));

        var sut = BuildMaterialiser(db);
        await sut.MaterialiseAsync(seriesTmdb, "episode", 1, 1, trigger, CancellationToken.None);

        Assert.Equal(PhantomDb.UserActivityPriority, await ReadPriorityAsync(seriesTmdb, "episode", 1, 1));
        Assert.NotNull(await db.GetUserActivityAtAsync(CancellationToken.None));

        var lease = await db.ClaimDueAvailabilityAsync(
            "sweep", TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow, "policy", CancellationToken.None, "episode");
        Assert.NotNull(lease);
        Assert.Equal(seriesTmdb, lease!.TmdbId);
        Assert.Equal(1, lease.Season);
        Assert.Equal(1, lease.Episode);
        Assert.DoesNotContain(lease.TmdbId, backlog);
    }

    // ---- The details/playback source view (PhantomSourceManager) is the other
    // user-initiated path; it does not always materialise, so it is wired
    // separately. A refresh-candidates view must promote + stamp.

    [Fact]
    public async Task SourceView_RefreshCandidates_Movie_BumpsPriorityAndStampsMarker()
    {
        using var db = await NewDbAsync();
        const int userTmdb = 700;
        await SeedMovieCatalogueAsync(db, userTmdb);
        var backlog = await SeedBacklogAsync(db, count: 50, type: "movie");
        await InsertAvailabilityAsync(db, userTmdb, "movie", -1, -1, status: "unknown",
            nextCheckAt: DateTimeOffset.UtcNow.AddMinutes(-1), priority: 0);

        Assert.Equal(0, await ReadPriorityAsync(userTmdb, "movie", -1, -1));
        Assert.Null(await db.GetUserActivityAtAsync(CancellationToken.None));

        var mgr = BuildSourceManager(db);
        var externalId = ChannelItemId.ForMovie(userTmdb).Encode();
        await mgr.GetSourcesAsync(externalId, refreshCandidates: true, CancellationToken.None);

        Assert.Equal(PhantomDb.UserActivityPriority, await ReadPriorityAsync(userTmdb, "movie", -1, -1));
        Assert.NotNull(await db.GetUserActivityAtAsync(CancellationToken.None));

        var lease = await db.ClaimDueAvailabilityAsync(
            "sweep", TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow, "policy", CancellationToken.None, "movie");
        Assert.NotNull(lease);
        Assert.Equal(userTmdb, lease!.TmdbId);
        Assert.DoesNotContain(lease.TmdbId, backlog);
    }

    [Fact]
    public async Task SourceView_RefreshCandidates_Episode_BumpsPriorityAndStampsMarker()
    {
        using var db = await NewDbAsync();
        const int seriesTmdb = 800;
        await SeedEpisodeCatalogueAsync(db, seriesTmdb, 1, 1);
        var backlog = await SeedBacklogAsync(db, count: 50, type: "episode");
        await InsertAvailabilityAsync(db, seriesTmdb, "episode", 1, 1, status: "unknown",
            nextCheckAt: DateTimeOffset.UtcNow.AddMinutes(-1), priority: 0);

        Assert.Equal(0, await ReadPriorityAsync(seriesTmdb, "episode", 1, 1));
        Assert.Null(await db.GetUserActivityAtAsync(CancellationToken.None));

        var mgr = BuildSourceManager(db);
        var externalId = ChannelItemId.ForEpisode(seriesTmdb, 1, 1).Encode();
        await mgr.GetSourcesAsync(externalId, refreshCandidates: true, CancellationToken.None);

        Assert.Equal(PhantomDb.UserActivityPriority, await ReadPriorityAsync(seriesTmdb, "episode", 1, 1));
        Assert.NotNull(await db.GetUserActivityAtAsync(CancellationToken.None));

        var lease = await db.ClaimDueAvailabilityAsync(
            "sweep", TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow, "policy", CancellationToken.None, "episode");
        Assert.NotNull(lease);
        Assert.Equal(seriesTmdb, lease!.TmdbId);
        Assert.Equal(1, lease.Season);
        Assert.Equal(1, lease.Episode);
    }

    [Fact]
    public async Task SourceView_NoRefresh_DoesNotPromoteOrStamp()
    {
        // A passive view (refreshCandidates=false) is not a probe-driving user
        // action, so it must NOT bump priority or stamp the marker.
        using var db = await NewDbAsync();
        const int userTmdb = 900;
        await SeedMovieCatalogueAsync(db, userTmdb);
        await InsertAvailabilityAsync(db, userTmdb, "movie", -1, -1, status: "unknown",
            nextCheckAt: DateTimeOffset.UtcNow.AddMinutes(-1), priority: 0);

        var mgr = BuildSourceManager(db);
        var externalId = ChannelItemId.ForMovie(userTmdb).Encode();
        await mgr.GetSourcesAsync(externalId, refreshCandidates: false, CancellationToken.None);

        Assert.Equal(0, await ReadPriorityAsync(userTmdb, "movie", -1, -1));
        Assert.Null(await db.GetUserActivityAtAsync(CancellationToken.None));
    }

    // ---------- builders ----------

    private async Task<PhantomDb> NewDbAsync()
    {
        var db = new PhantomDb(_dbPath);
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        return db;
    }

    private Materialiser BuildMaterialiser(PhantomDb db)
    {
        var gostream = new Mock<IGostreamClient>(MockBehavior.Loose);
        var refresh = new Mock<IChannelItemRefreshManager>(MockBehavior.Loose);
        // An empty indexer so materialise resolves to "unavailable" quickly
        // WITHOUT throwing — the promote happens before any probe, so the
        // materialise outcome is irrelevant to this test.
        IIndexerClient indexer = new EmptyIndexer();
        var scorer = new QualityScorer(NullLogger<QualityScorer>.Instance);
        var cfg = new PluginConfiguration
        {
            FusePathWaitTimeoutSeconds = 2,
            FusePathPollIntervalMilliseconds = 50,
            MaterialiseInFlightStaleMinutes = 10,
            SourcePickerPreset = "test",
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

    private PhantomSourceManager BuildSourceManager(PhantomDb db)
    {
        var gostream = new Mock<IGostreamClient>(MockBehavior.Loose);
        var refresh = new Mock<IChannelItemRefreshManager>(MockBehavior.Loose);
        var materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        IIndexerClient indexer = new EmptyIndexer();
        var scorer = new QualityScorer(NullLogger<QualityScorer>.Instance);
        var cfg = new PluginConfiguration
        {
            SourcePickerPreset = "test",
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

    private async Task<List<int>> SeedBacklogAsync(PhantomDb db, int count, string type)
    {
        var ids = new List<int>();
        var baseTmdb = 10_000_000;
        for (var i = 0; i < count; i++)
        {
            var tmdb = baseTmdb + i;
            // Background rows are FAR more overdue than the user's item and at
            // priority 0, so without a bump they would be claimed first.
            if (type == "movie")
            {
                await InsertAvailabilityAsync(db, tmdb, "movie", -1, -1, status: "unknown",
                    nextCheckAt: DateTimeOffset.UtcNow.AddDays(-10), priority: 0);
            }
            else
            {
                await InsertAvailabilityAsync(db, tmdb, "episode", 1, 1, status: "unknown",
                    nextCheckAt: DateTimeOffset.UtcNow.AddDays(-10), priority: 0);
            }

            ids.Add(tmdb);
        }

        return ids;
    }

    private async Task InsertAvailabilityAsync(PhantomDb db, int tmdbId, string type, int season, int episode, string status, DateTimeOffset nextCheckAt, int priority)
    {
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO availability_items
                (tmdb_id, type, season, episode, status, next_check_at, priority)
            VALUES ($tmdb,$type,$season,$episode,$status,$next,$priority)
            ON CONFLICT(tmdb_id, type, season, episode) DO UPDATE SET
                status=excluded.status, next_check_at=excluded.next_check_at, priority=excluded.priority;";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$season", season);
        cmd.Parameters.AddWithValue("$episode", episode);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$next", nextCheckAt.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$priority", priority);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> ReadPriorityAsync(int tmdbId, string type, int season, int episode)
    {
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT priority FROM availability_items WHERE tmdb_id=$tmdb AND type=$type AND season=$s AND episode=$e;";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$s", season);
        cmd.Parameters.AddWithValue("$e", episode);
        var raw = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(raw, System.Globalization.CultureInfo.InvariantCulture);
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
