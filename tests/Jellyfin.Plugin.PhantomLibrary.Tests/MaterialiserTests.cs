using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

public class MaterialiserTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _fuseMount;

    public MaterialiserTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "phantom-mat-" + Guid.NewGuid().ToString("N") + ".db");
        _fuseMount = Path.Combine(Path.GetTempPath(), "phantom-fuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fuseMount);
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            if (Directory.Exists(_fuseMount)) Directory.Delete(_fuseMount, recursive: true);
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

    private (Materialiser sut, Mock<IGostreamClient> gostream, Mock<IChannelItemRefreshManager> refresh, IIndexerClient indexer, PhantomDb db, PluginConfiguration cfg) BuildSut(
        PhantomDb db,
        string? imdb = "tt0000042",
        string? fusePath = null,
        Action<Mock<IGostreamClient>>? gostreamSetup = null,
        MagnetCandidate? magnet = null,
        bool magnetReturnsNull = false,
        MagnetCandidate[]? magnets = null,
        Exception? indexerException = null)
    {
        fusePath ??= Path.Combine(_fuseMount, "movie.mkv");
        File.WriteAllText(fusePath, "x"); // pre-create so WaitForFusePathAsync returns immediately

        var gostream = new Mock<IGostreamClient>(MockBehavior.Loose);
        gostream.Setup(g => g.ValidateAsync(It.IsAny<GostreamValidateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GostreamValidateRequest req, CancellationToken _) => new GostreamValidateResult
            {
                Status = "valid",
                Hash = "abc",
                SelectedFile = new GostreamSelectedFile { Id = 0, Path = "movie.mkv", Size = 100 },
                ValidationSessionId = req.ValidationSessionId,
            });
        if (gostreamSetup is null)
        {
            gostream.Setup(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new GostreamAddResult
                {
                    StubPath = "/var/gostream/stubs/movie.mkv",
                    FusePath = fusePath,
                    Hash = "abc",
                    Size = 100,
                });
        }
        else
        {
            gostreamSetup(gostream);
        }

        var refresh = new Mock<IChannelItemRefreshManager>(MockBehavior.Loose);
        refresh.Setup(r => r.RefreshChannelItemAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<ChannelItemRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        IIndexerClient indexer = indexerException is not null
            ? new ThrowingIndexer(indexerException)
            : magnetReturnsNull
                ? new FakeIndexer(Array.Empty<MagnetCandidate>())
                : new FakeIndexer(magnets ?? new[] { magnet ?? new MagnetCandidate("magnet:?xt=urn:btih:DEAD", "DEAD", 5L * 1024 * 1024 * 1024, 50, "test") });
        var scorer = new QualityScorer(NullLogger<QualityScorer>.Instance);
        var cfg = new PluginConfiguration
        {
            FusePathWaitTimeoutSeconds = 2,
            FusePathPollIntervalMilliseconds = 50,
            UnavailableRetryAfterHours = 24,
            MagnetCacheTtlHours = 24,
            MaterialiseInFlightStaleMinutes = 10,
            SourcePickerPreset = "test",
            MinSeeders = 1,
            MinSizeGb1080p = 1,
            MinSizeGb4K = 1,
        };

        var selector = new MagnetSelector(
            new IIndexerClient[] { indexer },
            scorer,
            NullLogger<MagnetSelector>.Instance,
            () => cfg);

        // Pre-seed the external-id cache so the resolver returns the
        // configured value without hitting TMDB. "movie" lookups are
        // used for tmdbId 42-99; "series" lookups for 100+; we seed
        // the lookup type that matches the test's intended call.
        if (imdb is not null)
        {
            db.SetImdbIdAsync(42, "movie", imdb, CancellationToken.None).GetAwaiter().GetResult();
            db.SetImdbIdAsync(99, "movie", imdb, CancellationToken.None).GetAwaiter().GetResult();
            db.SetImdbIdAsync(50, "movie", imdb, CancellationToken.None).GetAwaiter().GetResult();
            db.SetImdbIdAsync(60, "movie", imdb, CancellationToken.None).GetAwaiter().GetResult();
            db.SetImdbIdAsync(70, "movie", imdb, CancellationToken.None).GetAwaiter().GetResult();
            db.SetImdbIdAsync(80, "movie", imdb, CancellationToken.None).GetAwaiter().GetResult();
            db.SetImdbIdAsync(90, "movie", imdb, CancellationToken.None).GetAwaiter().GetResult();
            db.SetImdbIdAsync(300, "movie", imdb, CancellationToken.None).GetAwaiter().GetResult();
            db.SetImdbIdAsync(200, "series", imdb, CancellationToken.None).GetAwaiter().GetResult();
        }
        else
        {
            // Negative cache seed for episodic test (series lookup).
            db.SetImdbIdAsync(200, "series", null, CancellationToken.None).GetAwaiter().GetResult();
        }

        var tmdb = new Mock<ITmdbClient>(MockBehavior.Strict);
        var externalIds = new TmdbExternalIdResolver(db, tmdb.Object, NullLogger<TmdbExternalIdResolver>.Instance);

        var libMgr = new Mock<ILibraryManager>(MockBehavior.Loose);
        var state = new ChannelStateProvider(db);

        var sut = new Materialiser(
            libMgr.Object,
            db,
            gostream.Object,
            selector,
            externalIds,
            refresh.Object,
            state,
            NullLogger<Materialiser>.Instance,
            () => cfg);

        return (sut, gostream, refresh, indexer, db, cfg);
    }

    private sealed class ThrowingIndexer : IIndexerClient
    {
        private readonly Exception _exception;
        public ThrowingIndexer(Exception exception) { _exception = exception; }
        public string Name => "throwing";
        public bool IsEnabled => true;
        public Task<System.Collections.Generic.IReadOnlyList<IndexerCandidate>> SearchAsync(IndexerQuery query, CancellationToken ct)
            => Task.FromException<System.Collections.Generic.IReadOnlyList<IndexerCandidate>>(_exception);
    }

    private sealed class FakeIndexer : IIndexerClient
    {
        private readonly MagnetCandidate[] _magnets;
        public FakeIndexer(System.Collections.Generic.IEnumerable<MagnetCandidate> magnets) { _magnets = magnets.ToArray(); }
        public string Name => "fake";
        public bool IsEnabled => true;
        public Task<System.Collections.Generic.IReadOnlyList<IndexerCandidate>> SearchAsync(IndexerQuery query, CancellationToken ct)
        {
            return Task.FromResult<System.Collections.Generic.IReadOnlyList<IndexerCandidate>>(_magnets.Select(m => new IndexerCandidate
            {
                Title = m.Title ?? "Test Movie 1080p",
                Magnet = m.Magnet,
                InfoHash = m.InfoHash,
                Size = m.Size,
                Seeders = m.Seeders,
                IndexerName = m.Indexer,
            }).ToArray());
        }
    }

    private static async Task SeedMovieMetadataAsync(PhantomDb db, int tmdb, string title = "Test Movie", int year = 2020)
    {
        await db.UpsertTmdbMetadataAsync(new TmdbMetadataRow(
            TmdbId: tmdb,
            Type: "movie",
            Title: title,
            Year: year,
            Overview: null, PosterUrl: null, BackdropUrl: null,
            Genres: null, OfficialRating: null, CommunityRating: null,
            OriginalTitle: null,
            FetchedAt: DateTimeOffset.UtcNow), CancellationToken.None);
    }

    private static async Task SeedSeriesMetadataAsync(PhantomDb db, int tmdb, string title = "Test Show", int year = 2020)
    {
        await db.UpsertTmdbMetadataAsync(new TmdbMetadataRow(
            TmdbId: tmdb,
            Type: "series",
            Title: title,
            Year: year,
            Overview: null, PosterUrl: null, BackdropUrl: null,
            Genres: null, OfficialRating: null, CommunityRating: null,
            OriginalTitle: null,
            FetchedAt: DateTimeOffset.UtcNow), CancellationToken.None);
    }

    // ---- happy path ----

    [Fact]
    public async Task TupleMovie_HappyPath_WritesMaterialisedState_CallsRefreshTwice()
    {
        using var db = await NewDbAsync();
        await SeedMovieMetadataAsync(db, 42);
        var (sut, gostream, refresh, _, _, _) = BuildSut(db);

        var outcome = await sut.MaterialiseAsync(42, "movie", null, null, MaterialiseTrigger.Manual, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.Success, outcome.Status);
        var row = await db.GetMaterialisedStateAsync(42, "movie", -1, -1, CancellationToken.None);
        Assert.NotNull(row);
        Assert.False(await db.IsMaterialiseInFlightAsync(42, "movie", -1, -1, CancellationToken.None));
        refresh.Verify(r => r.RefreshChannelItemAsync(
            It.IsAny<Guid>(), It.IsAny<string>(),
            It.Is<ChannelItemRefreshOptions>(o => o.ForceUpdate && !o.ForceProbe && o.InvalidateMediaInfoCache),
            It.IsAny<CancellationToken>()), Times.Once);
        refresh.Verify(r => r.RefreshChannelItemAsync(
            It.IsAny<Guid>(), It.IsAny<string>(),
            It.Is<ChannelItemRefreshOptions>(o => o.ForceUpdate && o.ForceProbe && o.InvalidateMediaInfoCache),
            It.IsAny<CancellationToken>()), Times.Once);
        refresh.Verify(r => r.RefreshChannelItemAsync(
            It.IsAny<Guid>(), It.IsAny<string>(),
            It.Is<ChannelItemRefreshOptions>(o => !o.ForceUpdate && !o.ForceProbe && o.InvalidateMediaInfoCache),
            It.IsAny<CancellationToken>()), Times.Once);
        gostream.Verify(g => g.ValidateAsync(
            It.Is<GostreamValidateRequest>(r => r.AllowedVideoContainers != null && r.AllowedVideoContainers.SequenceEqual(new[] { "MKV" })),
            It.IsAny<CancellationToken>()), Times.Once);
        gostream.Verify(g => g.AddAsync(
            It.Is<GostreamAddRequest>(r => r.AllowedVideoContainers != null && r.AllowedVideoContainers.SequenceEqual(new[] { "MKV" })),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SentinelDiscipline_MovieUsesMinusOnePair()
    {
        using var db = await NewDbAsync();
        await SeedMovieMetadataAsync(db, 99);
        var (sut, _, _, _, _, _) = BuildSut(db);

        var outcome = await sut.MaterialiseAsync(99, "movie", null, null, MaterialiseTrigger.Manual, CancellationToken.None);
        Assert.Equal(MaterialisationStatus.Success, outcome.Status);

        // Verify the row is keyed on (-1, -1), not (NULL, NULL) or (0, 0).
        var fetched = await db.GetMaterialisedStateAsync(99, "movie", -1, -1, CancellationToken.None);
        Assert.NotNull(fetched);
        Assert.Equal(-1, fetched!.Season);
        Assert.Equal(-1, fetched.Episode);
    }

    // ---- idempotency ----

    [Fact]
    public async Task AlreadyMaterialised_ReturnsDuplicate_NoGostreamCall()
    {
        using var db = await NewDbAsync();
        var existingFuse = Path.Combine(_fuseMount, "already.mkv");
        File.WriteAllText(existingFuse, "x");
        await db.InsertMaterialisedStateAsync(50, "movie", -1, -1, "/stub", existingFuse, CancellationToken.None);
        var (sut, gostream, _, _, _, _) = BuildSut(db);

        var outcome = await sut.MaterialiseAsync(50, "movie", null, null, MaterialiseTrigger.Manual, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.Duplicate, outcome.Status);
        gostream.Verify(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AlreadyMaterialisedButFileMissing_RematerialisesAndReplacesState()
    {
        using var db = await NewDbAsync();
        await SeedMovieMetadataAsync(db, 50);
        await db.InsertMaterialisedStateAsync(50, "movie", -1, -1, "/stub/old", Path.Combine(_fuseMount, "missing-old.mkv"), CancellationToken.None);
        var replacementFuse = Path.Combine(_fuseMount, "replacement.mkv");
        var (sut, gostream, _, _, _, _) = BuildSut(db, fusePath: replacementFuse);

        var outcome = await sut.MaterialiseAsync(50, "movie", null, null, MaterialiseTrigger.Play, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.Success, outcome.Status);
        var row = await db.GetMaterialisedStateAsync(50, "movie", -1, -1, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal(replacementFuse, row!.FusePath);
        gostream.Verify(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task AlreadyInFlight_ReturnsAlreadyInProgress_NoGostreamCall()
    {
        using var db = await NewDbAsync();
        await db.UpsertMaterialiseInFlightAsync(60, "movie", -1, -1, CancellationToken.None);
        var (sut, gostream, _, _, _, _) = BuildSut(db);

        var outcome = await sut.MaterialiseAsync(60, "movie", null, null, MaterialiseTrigger.Manual, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.AlreadyInProgress, outcome.Status);
        gostream.Verify(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task LeakedInFlightRow_OlderThanStaleThreshold_ReclaimedWithoutRestart()
    {
        // ROI regression: a materialise hard-killed mid-flight leaks its
        // materialise_in_flight claim (the finally-block delete never ran).
        // Before this fix, only a startup sweep (MaterialiseInFlightSweeper)
        // could clear it, so a retry landing before the NEXT process restart
        // returned AlreadyInProgress forever. Assert the retry alone —
        // no restart, no sweeper invocation — now succeeds once the row is
        // older than MaterialiseInFlightStaleMinutes.
        using var db = await NewDbAsync();
        await SeedMovieMetadataAsync(db, 60);

        // Simulate the leaked row directly (as a hard-killed process would
        // leave it): insert then backdate started_at past the threshold,
        // WITHOUT ever calling DeleteMaterialiseInFlightAsync.
        await db.UpsertMaterialiseInFlightAsync(60, "movie", -1, -1, CancellationToken.None);
        var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE materialise_in_flight SET started_at = $t WHERE tmdb_id = 60;";
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync();
        }

        var (sut, gostream, _, _, _, _) = BuildSut(db);

        var outcome = await sut.MaterialiseAsync(60, "movie", null, null, MaterialiseTrigger.Manual, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.Success, outcome.Status);
        gostream.Verify(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        var row = await db.GetMaterialisedStateAsync(60, "movie", -1, -1, CancellationToken.None);
        Assert.NotNull(row);
        Assert.False(await db.IsMaterialiseInFlightAsync(60, "movie", -1, -1, CancellationToken.None));
    }

    [Fact]
    public async Task FreshInFlightRow_UnderStaleThreshold_StillBlocksConcurrentDuplicate()
    {
        // Safety case required by the ROI: a claim from a genuinely-running
        // materialise (fresh started_at) must still block a concurrent
        // duplicate — the reclaim only applies past the stale threshold.
        using var db = await NewDbAsync();
        await SeedMovieMetadataAsync(db, 60);
        await db.UpsertMaterialiseInFlightAsync(60, "movie", -1, -1, CancellationToken.None);
        var (sut, gostream, _, _, _, _) = BuildSut(db);

        var outcome = await sut.MaterialiseAsync(60, "movie", null, null, MaterialiseTrigger.Manual, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.AlreadyInProgress, outcome.Status);
        gostream.Verify(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ConcurrentMaterialise_SameTuple_OnlyOneGostreamAdd()
    {
        using var db = await NewDbAsync();
        await SeedMovieMetadataAsync(db, 60);
        var fuse = Path.Combine(_fuseMount, "concurrent.mkv");
        File.WriteAllText(fuse, "x");
        var addEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var addCalls = 0;
        var (sut, gostream, _, _, _, _) = BuildSut(db, fusePath: fuse, gostreamSetup: g =>
            g.Setup(x => x.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    Interlocked.Increment(ref addCalls);
                    addEntered.TrySetResult();
                    await releaseAdd.Task;
                    return new GostreamAddResult
                    {
                        StubPath = "/var/gostream/stubs/concurrent.mkv",
                        FusePath = fuse,
                        Hash = "abc",
                        Size = 100,
                    };
                }));

        var first = sut.MaterialiseAsync(60, "movie", null, null, MaterialiseTrigger.Play, CancellationToken.None);
        await addEntered.Task;
        var second = sut.MaterialiseAsync(60, "movie", null, null, MaterialiseTrigger.Play, CancellationToken.None);
        releaseAdd.SetResult();

        var outcomes = await Task.WhenAll(first, second);

        Assert.Contains(outcomes, o => o.Status == MaterialisationStatus.Success);
        Assert.Contains(outcomes, o => o.Status == MaterialisationStatus.AlreadyInProgress);
        Assert.Equal(1, addCalls);
        gostream.Verify(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- failure paths ----

    [Fact]
    public async Task TransientIndexerFailure_DoesNotWriteUnavailableMarker()
    {
        using var db = await NewDbAsync();
        await SeedMovieMetadataAsync(db, 90);
        var (sut, gostream, _, _, _, _) = BuildSut(db, indexerException: new IndexerTransientException("timeout"));

        var outcome = await sut.MaterialiseAsync(90, "movie", null, null, MaterialiseTrigger.Manual, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.Error, outcome.Status);
        Assert.Null(await db.IsMarkedUnavailableAsync(new UnavailableKey(90, "tt0000042", "movie", null, null), CancellationToken.None));
        gostream.Verify(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GostreamFails_NoMaterialisedRow_InFlightCleanedUp_ErrorReturned()
    {
        using var db = await NewDbAsync();
        await SeedMovieMetadataAsync(db, 70);
        var (sut, gostream, _, _, _, _) = BuildSut(db, gostreamSetup: g =>
            g.Setup(x => x.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
             .ThrowsAsync(new InvalidOperationException("gostream down")));

        var outcome = await sut.MaterialiseAsync(70, "movie", null, null, MaterialiseTrigger.Manual, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.Error, outcome.Status);
        Assert.Contains("gostream down", outcome.Error);
        Assert.Null(await db.GetMaterialisedStateAsync(70, "movie", -1, -1, CancellationToken.None));
        Assert.False(await db.IsMaterialiseInFlightAsync(70, "movie", -1, -1, CancellationToken.None));
    }

    [Fact]
    public async Task GostreamReturnsMissingFusePath_TriesNextCandidate_NoBadMaterialisedState()
    {
        using var db = await NewDbAsync();
        await SeedMovieMetadataAsync(db, 300);
        var bad = new MagnetCandidate("magnet:?xt=urn:btih:BAD", "BAD", 5L * 1024 * 1024 * 1024, 100, "test");
        var good = new MagnetCandidate("magnet:?xt=urn:btih:GOOD", "GOOD", 5L * 1024 * 1024 * 1024, 50, "test");
        var missing = Path.Combine(_fuseMount, "missing.mkv");
        var goodFuse = Path.Combine(_fuseMount, "good-after-missing.mkv");
        var (sut, _, _, _, _, cfg) = BuildSut(db,
            magnets: new[] { bad, good },
            gostreamSetup: g => g.Setup(x => x.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
                .Returns<GostreamAddRequest, CancellationToken>((req, _) =>
                {
                    if (req.Magnet == bad.Magnet)
                    {
                        return Task.FromResult(new GostreamAddResult { StubPath = "/stub/bad", FusePath = missing, Hash = "bad", Size = 1 });
                    }

                    File.WriteAllText(goodFuse, "x");
                    return Task.FromResult(new GostreamAddResult { StubPath = "/stub/good", FusePath = goodFuse, Hash = "good", Size = 1 });
                }));

        var outcome = await sut.MaterialiseAsync(300, "movie", null, null, MaterialiseTrigger.Manual, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.Success, outcome.Status);
        var row = await db.GetMaterialisedStateAsync(300, "movie", -1, -1, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal(goodFuse, row!.FusePath);
        var failure = await db.GetMagnetFailureAsync(
            new MagnetFailureKey(300, "tt0000042", "movie", null, null, cfg.SourcePickerPreset, bad.Magnet),
            CancellationToken.None);
        Assert.NotNull(failure);
        Assert.Equal("fuse_path_missing", failure!.Reason);
    }

    [Fact]
    public async Task GostreamRejectsFirstCandidate_TriesNextAndMarksMagnetFailed()
    {
        using var db = await NewDbAsync();
        await SeedMovieMetadataAsync(db, 300);
        var bad = new MagnetCandidate("magnet:?xt=urn:btih:BAD", "BAD", 5L * 1024 * 1024 * 1024, 100, "test");
        var good = new MagnetCandidate("magnet:?xt=urn:btih:GOOD", "GOOD", 5L * 1024 * 1024 * 1024, 50, "test");
        var calls = 0;
        var (sut, gostream, _, _, _, cfg) = BuildSut(db,
            magnets: new[] { bad, good },
            gostreamSetup: g => g.Setup(x => x.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
                .Returns<GostreamAddRequest, CancellationToken>((req, _) =>
                {
                    calls++;
                    if (req.Magnet == bad.Magnet)
                    {
                        throw new GostreamNoValidFilesException("gostream no_valid_files: target_episode_not_found");
                    }

                    var fuse = Path.Combine(_fuseMount, "retry-good.mkv");
                    File.WriteAllText(fuse, "x");
                    return Task.FromResult(new GostreamAddResult
                    {
                        StubPath = "/var/gostream/stubs/good.mkv",
                        FusePath = fuse,
                        Hash = "good",
                        Size = 100,
                    });
                }));

        var outcome = await sut.MaterialiseAsync(300, "movie", null, null, MaterialiseTrigger.Manual, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.Success, outcome.Status);
        Assert.Equal(2, calls);
        Assert.NotNull(await db.GetMaterialisedStateAsync(300, "movie", -1, -1, CancellationToken.None));
        var failure = await db.GetMagnetFailureAsync(
            new MagnetFailureKey(300, "tt0000042", "movie", null, null, cfg.SourcePickerPreset, bad.Magnet),
            CancellationToken.None);
        Assert.NotNull(failure);
        Assert.Equal("target_episode_not_found", failure!.Reason);
        var cached = await db.GetCachedMagnetAsync(
            new MagnetCacheKey(300, "tt0000042", "movie", null, null, cfg.SourcePickerPreset),
            CancellationToken.None);
        Assert.NotNull(cached);
        Assert.Equal(good.Magnet, cached!.Magnet);
        gostream.Verify(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task EpisodeMaterialise_SkipsCandidatesWithWrongSeriesYear()
    {
        using var db = await NewDbAsync();
        await SeedSeriesMetadataAsync(db, 200, "The Twilight Zone", 1959);
        var wrong = new MagnetCandidate(
            "magnet:?xt=urn:btih:BAD2019000000000000000000000000000000000",
            "BAD2019000000000000000000000000000000000",
            5L * 1024 * 1024 * 1024,
            100,
            "test")
        {
            Title = "The Twilight Zone 2019 S01E01 The Comedian 1080p WEB-DL",
        };
        var correct = new MagnetCandidate(
            "magnet:?xt=urn:btih:ABC1959000000000000000000000000000000000",
            "ABC1959000000000000000000000000000000000",
            2L * 1024 * 1024 * 1024,
            5,
            "test")
        {
            Title = "The Twilight Zone S01E01 Where Is Everybody 720p WEB-DL",
        };

        GostreamAddRequest? added = null;
        var (sut, gostream, _, _, _, cfg) = BuildSut(
            db,
            imdb: "tt0052520",
            magnets: new[] { wrong, correct },
            gostreamSetup: g =>
            {
                g.Setup(x => x.ValidateAsync(It.IsAny<GostreamValidateRequest>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((GostreamValidateRequest req, CancellationToken _) => new GostreamValidateResult
                    {
                        Status = "valid",
                        Hash = req.Magnet.Contains("ABC1959", StringComparison.Ordinal) ? correct.InfoHash : wrong.InfoHash,
                        SelectedFile = new GostreamSelectedFile { Id = 7, Path = "The Twilight Zone S01E01.mkv", Size = correct.Size },
                        ValidationSessionId = req.ValidationSessionId,
                    });
                g.Setup(x => x.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
                    .Returns<GostreamAddRequest, CancellationToken>((req, _) =>
                    {
                        added = req;
                        var fuse = Path.Combine(_fuseMount, "twilight-zone-good.mkv");
                        File.WriteAllText(fuse, "x");
                        return Task.FromResult(new GostreamAddResult
                        {
                            StubPath = "/var/gostream/stubs/good.mkv",
                            FusePath = fuse,
                            Hash = correct.InfoHash,
                            Size = correct.Size,
                        });
                    });
            });

        var outcome = await sut.MaterialiseAsync(200, "episode", 1, 1, MaterialiseTrigger.Manual, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.Success, outcome.Status);
        Assert.NotNull(added);
        Assert.Equal(correct.Magnet, added!.Magnet);
        gostream.Verify(g => g.ValidateAsync(It.Is<GostreamValidateRequest>(r => r.Magnet == wrong.Magnet), It.IsAny<CancellationToken>()), Times.Never);
        var failure = await db.GetMagnetFailureAsync(
            new MagnetFailureKey(200, "tt0052520", "episode", 1, 1, cfg.SourcePickerPreset, wrong.Magnet),
            CancellationToken.None);
        Assert.NotNull(failure);
        Assert.Equal("series_year_mismatch", failure!.Reason);
    }

    [Fact]
    public async Task PreFlightRefreshThrows_MaterialiseStillProceeds()
    {
        using var db = await NewDbAsync();
        await SeedMovieMetadataAsync(db, 80);
        var (sut, _, refresh, _, _, _) = BuildSut(db);
        var callCount = 0;
        refresh.Reset();
        refresh.Setup(r => r.RefreshChannelItemAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<ChannelItemRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, ChannelItemRefreshOptions, CancellationToken>((_, _, opts, _) =>
            {
                callCount++;
                if (callCount == 1) throw new InvalidOperationException("pre-flight failed");
                return Task.CompletedTask;
            });

        var outcome = await sut.MaterialiseAsync(80, "movie", null, null, MaterialiseTrigger.Manual, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.Success, outcome.Status);
        // post-flight refresh still happened
        Assert.True(callCount >= 2);
    }

    [Fact]
    public async Task PostFlightRefreshThrows_InvalidatesMediaInfoCacheForSecondPlay()
    {
        using var db = await NewDbAsync();
        await SeedMovieMetadataAsync(db, 70);
        var (sut, _, refresh, _, _, _) = BuildSut(db);
        refresh.Reset();
        refresh.Setup(r => r.RefreshChannelItemAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<ChannelItemRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Returns<Guid, string, ChannelItemRefreshOptions, CancellationToken>((_, _, opts, _) =>
            {
                if (opts.ForceProbe)
                {
                    throw new InvalidOperationException("post-flight probe failed");
                }

                return Task.CompletedTask;
            });

        var outcome = await sut.MaterialiseAsync(70, "movie", null, null, MaterialiseTrigger.Play, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.Success, outcome.Status);
        refresh.Verify(r => r.RefreshChannelItemAsync(
            It.IsAny<Guid>(), It.IsAny<string>(),
            It.Is<ChannelItemRefreshOptions>(o => o.ForceUpdate && !o.ForceProbe && o.InvalidateMediaInfoCache),
            It.IsAny<CancellationToken>()), Times.Once);
        refresh.Verify(r => r.RefreshChannelItemAsync(
            It.IsAny<Guid>(), It.IsAny<string>(),
            It.Is<ChannelItemRefreshOptions>(o => o.ForceUpdate && o.ForceProbe && o.InvalidateMediaInfoCache),
            It.IsAny<CancellationToken>()), Times.Once);
        refresh.Verify(r => r.RefreshChannelItemAsync(
            It.IsAny<Guid>(), It.IsAny<string>(),
            It.Is<ChannelItemRefreshOptions>(o => !o.ForceUpdate && !o.ForceProbe && o.InvalidateMediaInfoCache),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public async Task MagnetSelectorReturnsNull_WritesUnavailableMarker_ReturnsError()
    {
        using var db = await NewDbAsync();
        await SeedMovieMetadataAsync(db, 90);
        var (sut, _, _, _, _, cfg) = BuildSut(db, magnetReturnsNull: true);

        var outcome = await sut.MaterialiseAsync(90, "movie", null, null, MaterialiseTrigger.Manual, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.Error, outcome.Status);
        var marker = await db.IsMarkedUnavailableAsync(
            new UnavailableKey(TmdbId: 90, ImdbId: "tt0000042", Type: "movie", Season: null, Episode: null),
            CancellationToken.None);
        Assert.True(marker.HasValue);
        Assert.False(await db.IsMaterialiseInFlightAsync(90, "movie", -1, -1, CancellationToken.None));
    }

    [Fact]
    public async Task Materialise_ProbesFreshSourcesEvenWhenCachedCandidatesExist()
    {
        using var db = await NewDbAsync();
        await SeedMovieMetadataAsync(db, 42);
        var old = new MagnetCandidate("magnet:?xt=urn:btih:OLD", "OLD", 5L * 1024 * 1024 * 1024, 10, "old") { Title = "Old 1080p" };
        var fresh = new MagnetCandidate("magnet:?xt=urn:btih:NEW", "NEW", 8L * 1024 * 1024 * 1024, 50, "fresh") { Title = "Fresh 1080p" };
        await db.UpsertSourceCandidatesAsync(42, "movie", -1, -1, "test", new[] { old }, "details_probe", TimeSpan.FromHours(1), CancellationToken.None);
        var validatedMagnets = new List<string>();
        string? addedMagnet = null;
        var (sut, _, _, indexer, _, cfg) = BuildSut(
            db,
            magnets: new[] { fresh },
            gostreamSetup: g =>
            {
                g.Setup(x => x.ValidateAsync(It.IsAny<GostreamValidateRequest>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((GostreamValidateRequest req, CancellationToken _) =>
                    {
                        validatedMagnets.Add(req.Magnet);
                        return new GostreamValidateResult
                        {
                            Status = "valid",
                            Hash = req.Magnet.Contains("NEW", StringComparison.Ordinal) ? "NEW" : "OLD",
                            SelectedFile = new GostreamSelectedFile { Id = 0, Path = "movie.mkv", Size = 100 },
                            ValidationSessionId = req.ValidationSessionId,
                        };
                    });
                g.Setup(x => x.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((GostreamAddRequest req, CancellationToken _) =>
                    {
                        addedMagnet = req.Magnet;
                        return new GostreamAddResult
                        {
                            StubPath = "/var/gostream/stubs/movie.mkv",
                            FusePath = Path.Combine(_fuseMount, "movie.mkv"),
                            Hash = req.Magnet.Contains("NEW", StringComparison.Ordinal) ? "NEW" : "OLD",
                            Size = 100,
                        };
                    });
            });
        cfg.SourceValidationParallelism = 1;
        cfg.SourceValidationWindowSize = 1;
        File.WriteAllText(Path.Combine(_fuseMount, "movie.mkv"), "x");

        var outcome = await sut.MaterialiseAsync(42, "movie", null, null, MaterialiseTrigger.Manual, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.Success, outcome.Status);
        Assert.Contains(fresh.Magnet, validatedMagnets);
        Assert.Equal(fresh.Magnet, addedMagnet);
        var rows = await db.ListSourceCandidatesAsync(42, "movie", -1, -1, "test", includeExpired: false, CancellationToken.None);
        Assert.Contains(rows, r => r.Magnet == old.Magnet);
        Assert.Contains(rows, r => r.Magnet == fresh.Magnet);
        Assert.IsType<FakeIndexer>(indexer);
    }

    [Fact]
    public async Task TransientValidationFailure_UsesShortRetryMarker_NotUnavailableRetryWindow()
    {
        using var db = await NewDbAsync();
        await SeedMovieMetadataAsync(db, 90);
        var (sut, gostream, _, _, _, cfg) = BuildSut(db, gostreamSetup: g =>
        {
            g.Setup(x => x.ValidateAsync(It.IsAny<GostreamValidateRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((GostreamValidateRequest req, CancellationToken _) => new GostreamValidateResult
                {
                    Status = "transient",
                    Reason = "validation_cancelled",
                    ValidationSessionId = req.ValidationSessionId,
                });
        });
        cfg.SourceValidationTransientRetryMinutes = 7;

        var before = DateTimeOffset.UtcNow;
        var outcome = await sut.MaterialiseAsync(90, "movie", null, null, MaterialiseTrigger.Manual, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.Error, outcome.Status);
        Assert.Contains("transient validation failure", outcome.Error, StringComparison.OrdinalIgnoreCase);
        var marker = await db.IsMarkedUnavailableAsync(
            new UnavailableKey(TmdbId: 90, ImdbId: "tt0000042", Type: "movie", Season: null, Episode: null),
            CancellationToken.None);
        Assert.True(marker.HasValue);
        Assert.InRange(marker.Value, before.AddMinutes(6), before.AddMinutes(9));
        gostream.Verify(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ---- type rejects ----

    [Fact]
    public async Task SeriesType_Rejected()
    {
        using var db = await NewDbAsync();
        var (sut, _, _, _, _, _) = BuildSut(db);
        var outcome = await sut.MaterialiseAsync(1, "series", null, null, MaterialiseTrigger.Manual, CancellationToken.None);
        Assert.Equal(MaterialisationStatus.Error, outcome.Status);
        Assert.Contains("Series-level", outcome.Error);
    }

    [Fact]
    public async Task EpisodeWithoutImdb_ReturnsError()
    {
        using var db = await NewDbAsync();
        await SeedSeriesMetadataAsync(db, 200);
        var (sut, _, _, _, _, _) = BuildSut(db, imdb: null);
        var outcome = await sut.MaterialiseAsync(200, "episode", 1, 1, MaterialiseTrigger.Manual, CancellationToken.None);
        Assert.Equal(MaterialisationStatus.Error, outcome.Status);
        Assert.Contains("IMDB", outcome.Error);
    }

    [Fact]
    public async Task EpisodeWithoutSeasonOrEpisode_ReturnsError()
    {
        using var db = await NewDbAsync();
        var (sut, _, _, _, _, _) = BuildSut(db);
        var outcome = await sut.MaterialiseAsync(1, "episode", null, null, MaterialiseTrigger.Manual, CancellationToken.None);
        Assert.Equal(MaterialisationStatus.Error, outcome.Status);
    }

    [Fact]
    public async Task Episode_LeakedInFlightRow_OlderThanStaleThreshold_ReclaimedWithoutRestart()
    {
        // Movie/TV parity for LeakedInFlightRow_OlderThanStaleThreshold_ReclaimedWithoutRestart.
        using var db = await NewDbAsync();
        await SeedSeriesMetadataAsync(db, 200);

        await db.UpsertMaterialiseInFlightAsync(200, "episode", 1, 1, CancellationToken.None);
        var cs = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        await using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE materialise_in_flight SET started_at = $t WHERE tmdb_id = 200 AND type = 'episode';";
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync();
        }

        var (sut, gostream, _, _, _, _) = BuildSut(db);

        var outcome = await sut.MaterialiseAsync(200, "episode", 1, 1, MaterialiseTrigger.Manual, CancellationToken.None);

        Assert.Equal(MaterialisationStatus.Success, outcome.Status);
        gostream.Verify(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        var row = await db.GetMaterialisedStateAsync(200, "episode", 1, 1, CancellationToken.None);
        Assert.NotNull(row);
        Assert.False(await db.IsMaterialiseInFlightAsync(200, "episode", 1, 1, CancellationToken.None));
    }

    [Fact]
    public async Task UnsupportedType_ReturnsError()
    {
        using var db = await NewDbAsync();
        var (sut, _, _, _, _, _) = BuildSut(db);
        var outcome = await sut.MaterialiseAsync(1, "audio", null, null, MaterialiseTrigger.Manual, CancellationToken.None);
        Assert.Equal(MaterialisationStatus.Error, outcome.Status);
        Assert.Contains("Unsupported", outcome.Error);
    }

    [Fact]
    public async Task LegacyGuidWrapper_RoutesMovieExternalIdToTuplePath_EvenWhenRuntimeChannelIdDoesNotRoundTrip()
    {
        using var db = await NewDbAsync();
        await SeedMovieMetadataAsync(db, 42);

        // Video.SourceType consults a static IRecordingsManager; stub it
        // so it doesn't NRE.
        var recordings = new Mock<MediaBrowser.Controller.LiveTv.IRecordingsManager>(MockBehavior.Loose);
        recordings.Setup(r => r.GetActiveRecordingInfo(It.IsAny<string>())).Returns((MediaBrowser.Controller.LiveTv.ActiveRecordingInfo?)null);
        MediaBrowser.Controller.Entities.Video.RecordingsManager = recordings.Object;

        var jellyfinItemId = Guid.NewGuid();
        var item = new MediaBrowser.Controller.Entities.Movies.Movie
        {
            Name = "x",
            ExternalId = "movie_42",
            ChannelId = Guid.NewGuid(),
        };
        var libMgr = new Mock<ILibraryManager>(MockBehavior.Loose);
        libMgr.Setup(l => l.GetItemById(jellyfinItemId)).Returns(item);

        var state = new ChannelStateProvider(db);
        var tmdb = new Mock<ITmdbClient>(MockBehavior.Strict);
        var externalIds = new TmdbExternalIdResolver(db, tmdb.Object, NullLogger<TmdbExternalIdResolver>.Instance);
        await db.SetImdbIdAsync(42, "movie", "tt0000042", CancellationToken.None);
        var gostream = new Mock<IGostreamClient>(MockBehavior.Loose);
        var fusePath = Path.Combine(_fuseMount, "legacy.mkv");
        File.WriteAllText(fusePath, "x");
        gostream.Setup(g => g.ValidateAsync(It.IsAny<GostreamValidateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((GostreamValidateRequest req, CancellationToken _) => new GostreamValidateResult
            {
                Status = "valid",
                Hash = "h",
                SelectedFile = new GostreamSelectedFile { Id = 0, Path = "legacy.mkv", Size = 1 },
                ValidationSessionId = req.ValidationSessionId,
            });
        gostream.Setup(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GostreamAddResult { StubPath = "/stub", FusePath = fusePath, Hash = "h", Size = 1 });
        var refresh = new Mock<IChannelItemRefreshManager>(MockBehavior.Loose);
        refresh.Setup(r => r.RefreshChannelItemAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<ChannelItemRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var cfg = new PluginConfiguration { FusePathWaitTimeoutSeconds = 1, FusePathPollIntervalMilliseconds = 50, MinSeeders = 1, MinSizeGb1080p = 1, MinSizeGb4K = 1 };
        var indexer = new FakeIndexer(new[] { new MagnetCandidate("magnet:?xt=urn:btih:DEAD", "DEAD", 5L * 1024 * 1024 * 1024, 10, "f") });
        var selector = new MagnetSelector(new IIndexerClient[] { indexer }, new QualityScorer(NullLogger<QualityScorer>.Instance), NullLogger<MagnetSelector>.Instance, () => cfg);
        var legacySut = new Materialiser(libMgr.Object, db, gostream.Object, selector, externalIds, refresh.Object, state, NullLogger<Materialiser>.Instance, () => cfg);

        var outcome = await legacySut.MaterialiseAsync(jellyfinItemId, MaterialiseTrigger.Manual, CancellationToken.None);
        Assert.Equal(MaterialisationStatus.Success, outcome.Status);
        Assert.NotNull(await db.GetMaterialisedStateAsync(42, "movie", -1, -1, CancellationToken.None));
    }

    [Fact]
    public async Task LegacyGuidWrapper_SeriesExternalIdRejected()
    {
        using var db = await NewDbAsync();
        var jellyfinItemId = Guid.NewGuid();
        var item = new MediaBrowser.Controller.Entities.TV.Series
        {
            Name = "x",
            ExternalId = "series_99",
            ChannelId = ChannelIds.Shows,
        };
        var libMgr = new Mock<ILibraryManager>(MockBehavior.Loose);
        libMgr.Setup(l => l.GetItemById(jellyfinItemId)).Returns(item);
        var state = new ChannelStateProvider(db);
        var tmdb = new Mock<ITmdbClient>(MockBehavior.Strict);
        var externalIds = new TmdbExternalIdResolver(db, tmdb.Object, NullLogger<TmdbExternalIdResolver>.Instance);
        var cfg = new PluginConfiguration();
        var selector = new MagnetSelector(Array.Empty<IIndexerClient>(), new QualityScorer(NullLogger<QualityScorer>.Instance), NullLogger<MagnetSelector>.Instance, () => cfg);
        var refresh = new Mock<IChannelItemRefreshManager>(MockBehavior.Loose);
        var gostream = new Mock<IGostreamClient>(MockBehavior.Loose);
        var sut = new Materialiser(libMgr.Object, db, gostream.Object, selector, externalIds, refresh.Object, state, NullLogger<Materialiser>.Instance, () => cfg);

        var outcome = await sut.MaterialiseAsync(jellyfinItemId, MaterialiseTrigger.Manual, CancellationToken.None);
        Assert.Equal(MaterialisationStatus.Error, outcome.Status);
        Assert.Contains("Series-level", outcome.Error);
    }

    [Fact]
    public async Task TmdbMetadataMiss_RaisesErrorViaCatch()
    {
        using var db = await NewDbAsync();
        // no SeedMovieMetadataAsync → tmdb_metadata miss
        var (sut, _, _, _, _, _) = BuildSut(db);
        var outcome = await sut.MaterialiseAsync(300, "movie", null, null, MaterialiseTrigger.Manual, CancellationToken.None);
        Assert.Equal(MaterialisationStatus.Error, outcome.Status);
        Assert.Contains("tmdb_metadata miss", outcome.Error);
        Assert.False(await db.IsMaterialiseInFlightAsync(300, "movie", -1, -1, CancellationToken.None));
    }
}
