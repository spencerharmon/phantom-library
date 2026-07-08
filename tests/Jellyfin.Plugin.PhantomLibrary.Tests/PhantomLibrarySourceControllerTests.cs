using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Api;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Library;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public sealed class PhantomLibrarySourceControllerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly string _fuseRoot;

    public PhantomLibrarySourceControllerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "phantom-sources-" + Guid.NewGuid().ToString("N") + ".db");
        _fuseRoot = Path.Combine(Path.GetTempPath(), "phantom-sources-fuse-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_fuseRoot);
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
            if (Directory.Exists(_fuseRoot)) Directory.Delete(_fuseRoot, recursive: true);
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

    private sealed class FakeIndexer : IIndexerClient
    {
        private readonly MagnetCandidate[] _magnets;
        public FakeIndexer(IEnumerable<MagnetCandidate> magnets) { _magnets = magnets.ToArray(); }
        public string Name => "fake";
        public bool IsEnabled => true;
        public Task<IReadOnlyList<IndexerCandidate>> SearchAsync(IndexerQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<IndexerCandidate>>(_magnets.Select(m => new IndexerCandidate
            {
                Title = "Test Movie 1080p",
                Magnet = m.Magnet,
                InfoHash = m.InfoHash,
                Size = m.Size,
                Seeders = m.Seeders,
                IndexerName = m.Indexer,
            }).ToArray());
    }

    private async Task SeedMovieAsync(PhantomDb db, int tmdb, string? imdb = "tt0000042")
    {
        await db.UpsertTmdbMetadataAsync(new TmdbMetadataRow(
            TmdbId: tmdb,
            Type: "movie",
            Title: "Test Movie",
            Year: 2020,
            Overview: null,
            PosterUrl: null,
            BackdropUrl: null,
            Genres: null,
            OfficialRating: null,
            CommunityRating: null,
            OriginalTitle: null,
            FetchedAt: DateTimeOffset.UtcNow), CancellationToken.None);
        await db.SetImdbIdAsync(tmdb, "movie", imdb, CancellationToken.None);
    }

    // Series metadata is looked up under type "series" even though the
    // channel/source-manager external id kind is "episode" — episodes
    // resolve IMDb/metadata via their parent series (movie/TV parity for
    // RejectCurrent: see AGENTS.md "Movie/TV parity").
    private async Task SeedSeriesAsync(PhantomDb db, int tmdb, string? imdb = "tt0000099")
    {
        await db.UpsertTmdbMetadataAsync(new TmdbMetadataRow(
            TmdbId: tmdb,
            Type: "series",
            Title: "Test Show",
            Year: 2020,
            Overview: null,
            PosterUrl: null,
            BackdropUrl: null,
            Genres: null,
            OfficialRating: null,
            CommunityRating: null,
            OriginalTitle: null,
            FetchedAt: DateTimeOffset.UtcNow), CancellationToken.None);
        await db.SetImdbIdAsync(tmdb, "series", imdb, CancellationToken.None);
    }

    private static MagnetCandidate Candidate(string label, int seeders = 100)
        => new(
            "magnet:?xt=urn:btih:" + label,
            label,
            5L * 1024 * 1024 * 1024,
            seeders,
            "fake");

    private async Task CacheCurrentAsync(
        PhantomDb db,
        int tmdb,
        MagnetCandidate current,
        string stubPath = "/stub/current.mkv",
        string fusePath = "/fuse/current.mkv",
        string type = "movie",
        int? season = null,
        int? episode = null,
        string imdb = "tt0000042")
    {
        var (sSentinel, eSentinel) = ChannelItemId.ToSentinels(season, episode);
        await db.InsertMaterialisedStateAsync(tmdb, type, sSentinel, eSentinel, stubPath, fusePath, CancellationToken.None);
        await db.PutCachedMagnetAsync(
            new MagnetCacheKey(tmdb, imdb, type, season, episode, "test"),
            new MagnetCacheEntry
            {
                Magnet = current.Magnet,
                InfoHash = current.InfoHash,
                Size = current.Size,
                Seeders = current.Seeders,
                Indexer = current.Indexer,
                CachedAt = DateTimeOffset.UtcNow,
                Ttl = TimeSpan.FromDays(7),
                Source = "user",
            },
            CancellationToken.None);
    }

    private PhantomLibraryController BuildController(
        PhantomDb db,
        IEnumerable<MagnetCandidate> candidates,
        Mock<IGostreamClient>? gostreamMock = null,
        Action<Mock<IGostreamClient>>? gostreamSetup = null,
        PluginConfiguration? cfg = null,
        IFavouriteRecommendationIngestor? recommendationIngestor = null)
    {
        cfg ??= new PluginConfiguration
        {
            SourcePickerPreset = "test",
            MinSeeders = 1,
            MinSizeGb1080p = 1,
            MinSizeGb4K = 1,
            FusePathWaitTimeoutSeconds = 2,
            FusePathPollIntervalMilliseconds = 50,
            MagnetCacheTtlHours = 24,
            UnavailableRetryAfterHours = 24,
        };

        var gostream = gostreamMock ?? new Mock<IGostreamClient>(MockBehavior.Loose);
        gostream.Setup(g => g.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        gostreamSetup?.Invoke(gostream);

        var scorer = new QualityScorer(NullLogger<QualityScorer>.Instance);
        var selector = new MagnetSelector(
            new IIndexerClient[] { new FakeIndexer(candidates) },
            scorer,
            NullLogger<MagnetSelector>.Instance,
            () => cfg);

        var refresh = new Mock<IChannelItemRefreshManager>(MockBehavior.Loose);
        refresh.Setup(r => r.RefreshChannelItemAsync(
                It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<ChannelItemRefreshOptions>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var tmdb = new Mock<ITmdbClient>(MockBehavior.Strict);
        var externalIds = new TmdbExternalIdResolver(db, tmdb.Object, NullLogger<TmdbExternalIdResolver>.Instance);
        var libMgr = new Mock<ILibraryManager>(MockBehavior.Loose);
        var materialiser = new Materialiser(
            libMgr.Object,
            db,
            gostream.Object,
            selector,
            externalIds,
            refresh.Object,
            new ChannelStateProvider(db),
            NullLogger<Materialiser>.Instance,
            () => cfg);

        var state = new ChannelStateProvider(db);
        var sourceManager = new PhantomSourceManager(
            db,
            selector,
            materialiser,
            gostream.Object,
            externalIds,
            refresh.Object,
            state,
            () => cfg);

        var queue = new Mock<IMaterialisationQueue>(MockBehavior.Loose);
        var paths = new Mock<IApplicationPaths>(MockBehavior.Loose);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Path.GetTempPath());
        var users = new Mock<IUserManager>(MockBehavior.Loose);

        var recommendations = recommendationIngestor
            ?? new Mock<IFavouriteRecommendationIngestor>(MockBehavior.Loose).Object;

        return new PhantomLibraryController(
            materialiser,
            queue.Object,
            gostream.Object,
            paths.Object,
            users.Object,
            db,
            sourceManager,
            recommendations);
    }

    [Fact]
    public async Task Sources_BadExternalId_Returns404()
    {
        using var db = await NewDbAsync();
        var ctrl = BuildController(db, Array.Empty<MagnetCandidate>());

        var result = await ctrl.Sources("not-a-channel-id", CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task RejectCurrent_NoCurrent_Returns409()
    {
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 42);
        var ctrl = BuildController(db, new[] { Candidate("ALT") });

        var result = await ctrl.RejectCurrent("movie_42", CancellationToken.None);

        var conflict = Assert.IsType<ConflictObjectResult>(result);
        var payload = Assert.IsType<PhantomSourceOperationResult>(conflict.Value);
        Assert.Equal(PhantomSourceOperationStatus.NoCurrent, payload.Status);
    }

    [Fact]
    public async Task RejectCurrent_InFlight_Returns409WithoutRemoving()
    {
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 42);
        var current = Candidate("CURRENT", 100);
        await CacheCurrentAsync(db, 42, current);
        await db.UpsertMaterialiseInFlightAsync(42, "movie", -1, -1, CancellationToken.None);
        var gostream = new Mock<IGostreamClient>(MockBehavior.Loose);
        var ctrl = BuildController(db, new[] { current, Candidate("ALT", 50) }, gostream);

        var result = await ctrl.RejectCurrent("movie_42", CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result);
        gostream.Verify(g => g.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RejectCurrent_NoAlternate_RejectsDeletesAndRemovesCurrent()
    {
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 42);
        var current = Candidate("CURRENT", 100);
        await CacheCurrentAsync(db, 42, current, "/stub/current.mkv", "/fuse/current.mkv");
        var gostream = new Mock<IGostreamClient>(MockBehavior.Loose);
        var ctrl = BuildController(db, new[] { current }, gostream);

        var result = await ctrl.RejectCurrent("movie_42", CancellationToken.None);

        var unprocessable = Assert.IsType<UnprocessableEntityObjectResult>(result);
        var payload = Assert.IsType<PhantomSourceOperationResult>(unprocessable.Value);
        Assert.Equal(PhantomSourceOperationStatus.NoAlternate, payload.Status);
        Assert.Null(await db.GetMaterialisedStateAsync(42, "movie", -1, -1, CancellationToken.None));
        var failure = await db.GetMagnetFailureAsync(
            new MagnetFailureKey(42, "tt0000042", "movie", null, null, "test", current.Magnet),
            CancellationToken.None);
        Assert.NotNull(failure);
        Assert.Equal("operator_rejected", failure!.Reason);
        gostream.Verify(g => g.RemoveAsync("/stub/current.mkv", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task MaterialiseCandidate_SelectedSuccess_UsesExactMagnet()
    {
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 42);
        var first = Candidate("FIRST", 100);
        var selected = Candidate("SELECTED", 50);
        var selectedFuse = Path.Combine(_fuseRoot, "selected.mkv");
        File.WriteAllText(selectedFuse, "x");
        GostreamAddRequest? captured = null;
        var ctrl = BuildController(
            db,
            new[] { first, selected },
            gostreamSetup: g => g.Setup(x => x.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
                .Returns<GostreamAddRequest, CancellationToken>((req, _) =>
                {
                    captured = req;
                    return Task.FromResult(new GostreamAddResult
                    {
                        StubPath = "/stub/selected.mkv",
                        FusePath = selectedFuse,
                        Hash = selected.InfoHash,
                        Size = selected.Size,
                    });
                }));

        var result = await ctrl.MaterialiseCandidate(
            "movie_42",
            new PhantomMaterialiseCandidateRequest { Magnet = selected.Magnet },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(captured);
        Assert.Equal(selected.Magnet, captured!.Magnet);
        var row = await db.GetMaterialisedStateAsync(42, "movie", -1, -1, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal("/stub/selected.mkv", row!.StubPath);
        var cached = await db.GetCachedMagnetAsync(
            new MagnetCacheKey(42, "tt0000042", "movie", null, null, "test"),
            CancellationToken.None);
        Assert.NotNull(cached);
        Assert.Equal(selected.Magnet, cached!.Magnet);
    }

    [Fact]
    public async Task MaterialiseCandidate_RequestMetadataBypassesStaleRankedList()
    {
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 42);
        var selected = Candidate("STALE", 25);
        var selectedFuse = Path.Combine(_fuseRoot, "stale-selected.mkv");
        File.WriteAllText(selectedFuse, "x");
        GostreamAddRequest? captured = null;
        var ctrl = BuildController(
            db,
            Array.Empty<MagnetCandidate>(),
            gostreamSetup: g => g.Setup(x => x.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
                .Returns<GostreamAddRequest, CancellationToken>((req, _) =>
                {
                    captured = req;
                    return Task.FromResult(new GostreamAddResult
                    {
                        StubPath = "/stub/stale-selected.mkv",
                        FusePath = selectedFuse,
                        Hash = selected.InfoHash,
                        Size = selected.Size,
                    });
                }));

        var result = await ctrl.MaterialiseCandidate(
            "movie_42",
            new PhantomMaterialiseCandidateRequest
            {
                Magnet = selected.Magnet,
                InfoHash = selected.InfoHash,
                Indexer = selected.Indexer,
                Title = "Stale candidate title",
                Size = selected.Size,
                Seeders = selected.Seeders,
            },
            CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(captured);
        Assert.Equal(selected.Magnet, captured!.Magnet);
    }

    [Fact]
    public async Task RejectCurrent_SharedSource_DoesNotRemoveGostreamStub()
    {
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 42);
        await SeedMovieAsync(db, 43);
        var current = Candidate("SHARED", 100);
        await CacheCurrentAsync(db, 42, current, "/stub/shared.mkv", "/fuse/a.mkv");
        await db.InsertMaterialisedStateAsync(43, "movie", -1, -1, "/stub/other.mkv", "/fuse/b.mkv", CancellationToken.None);
        await db.PutCachedMagnetAsync(
            new MagnetCacheKey(43, "tt0000042", "movie", null, null, "test"),
            new MagnetCacheEntry
            {
                Magnet = "magnet:?xt=urn:btih:OTHER",
                InfoHash = current.InfoHash,
                Size = current.Size,
                Seeders = current.Seeders,
                Indexer = current.Indexer,
                CachedAt = DateTimeOffset.UtcNow,
                Ttl = TimeSpan.FromDays(7),
                Source = "user",
            },
            CancellationToken.None);
        var gostream = new Mock<IGostreamClient>(MockBehavior.Loose);
        var ctrl = BuildController(db, new[] { current }, gostream);

        var result = await ctrl.RejectCurrent("movie_42", CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
        gostream.Verify(g => g.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.NotNull(await db.GetMaterialisedStateAsync(43, "movie", -1, -1, CancellationToken.None));
    }

    [Fact]
    public async Task RejectCurrent_BadExternalId_Returns404()
    {
        using var db = await NewDbAsync();
        var ctrl = BuildController(db, Array.Empty<MagnetCandidate>());

        var result = await ctrl.RejectCurrent("not-a-channel-id", CancellationToken.None);

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        var payload = Assert.IsType<PhantomSourceOperationResult>(notFound.Value);
        Assert.Equal(PhantomSourceOperationStatus.NotFound, payload.Status);
    }

    [Fact]
    public async Task RejectCurrent_NextCandidateExists_MaterialisesNextRankedCandidate()
    {
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 42);
        var current = Candidate("CURRENT", 100);
        var alt = Candidate("ALT", 50);
        await CacheCurrentAsync(db, 42, current, "/stub/current.mkv", "/fuse/current.mkv");
        var altFuse = Path.Combine(_fuseRoot, "alt.mkv");
        File.WriteAllText(altFuse, "x");
        GostreamAddRequest? captured = null;
        var gostream = new Mock<IGostreamClient>(MockBehavior.Loose);
        gostream.Setup(g => g.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        gostream.Setup(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
            .Returns<GostreamAddRequest, CancellationToken>((req, _) =>
            {
                captured = req;
                return Task.FromResult(new GostreamAddResult
                {
                    StubPath = "/stub/alt.mkv",
                    FusePath = altFuse,
                    Hash = alt.InfoHash,
                    Size = alt.Size,
                });
            });
        var ctrl = BuildController(db, new[] { current, alt }, gostream);

        var result = await ctrl.RejectCurrent("movie_42", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<PhantomSourceOperationResult>(ok.Value);
        Assert.Equal(PhantomSourceOperationStatus.Success, payload.Status);
        Assert.NotNull(captured);
        Assert.Equal(alt.Magnet, captured!.Magnet);

        // Old source: rejected (magnet_failure_cache) and its materialised_state
        // removed; gostream remove called exactly once for the old (unshared) stub.
        var failure = await db.GetMagnetFailureAsync(
            new MagnetFailureKey(42, "tt0000042", "movie", null, null, "test", current.Magnet),
            CancellationToken.None);
        Assert.NotNull(failure);
        Assert.Equal("operator_rejected", failure!.Reason);
        gostream.Verify(g => g.RemoveAsync("/stub/current.mkv", It.IsAny<CancellationToken>()), Times.Once);

        // New source: the next ranked non-rejected candidate is now materialised.
        var row = await db.GetMaterialisedStateAsync(42, "movie", -1, -1, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal("/stub/alt.mkv", row!.StubPath);
    }

    [Fact]
    public async Task RejectCurrent_SkipsPreviouslyRejectedCandidate_MaterialisesNextNonRejected()
    {
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 42);
        var current = Candidate("CURRENT", 100);
        var alreadyRejected = Candidate("ALT1-PREREJECTED", 80);
        var clean = Candidate("ALT2-CLEAN", 50);
        await CacheCurrentAsync(db, 42, current, "/stub/current.mkv", "/fuse/current.mkv");

        // ALT1 was rejected in a previous RejectCurrent call; its retry window
        // has not elapsed, so it must be skipped in favour of ALT2 even though
        // ALT1 outranks ALT2 on raw seeders.
        var now = DateTimeOffset.UtcNow;
        await db.MarkMagnetFailedAsync(
            new MagnetFailureKey(42, "tt0000042", "movie", null, null, "test", alreadyRejected.Magnet),
            new MagnetFailureEntry
            {
                InfoHash = alreadyRejected.InfoHash,
                Reason = "operator_rejected",
                FailedAt = now,
                RetryAfter = now.AddDays(3650),
            },
            CancellationToken.None);

        var cleanFuse = Path.Combine(_fuseRoot, "clean.mkv");
        File.WriteAllText(cleanFuse, "x");
        GostreamAddRequest? captured = null;
        var gostream = new Mock<IGostreamClient>(MockBehavior.Loose);
        gostream.Setup(g => g.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        gostream.Setup(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
            .Returns<GostreamAddRequest, CancellationToken>((req, _) =>
            {
                captured = req;
                return Task.FromResult(new GostreamAddResult
                {
                    StubPath = "/stub/clean.mkv",
                    FusePath = cleanFuse,
                    Hash = clean.InfoHash,
                    Size = clean.Size,
                });
            });
        var ctrl = BuildController(db, new[] { current, alreadyRejected, clean }, gostream);

        var result = await ctrl.RejectCurrent("movie_42", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<PhantomSourceOperationResult>(ok.Value);
        Assert.Equal(PhantomSourceOperationStatus.Success, payload.Status);
        Assert.NotNull(captured);
        Assert.Equal(clean.Magnet, captured!.Magnet);
        Assert.NotEqual(alreadyRejected.Magnet, captured.Magnet);
    }

    [Fact]
    public async Task RejectCurrent_Episode_NextCandidateExists_MaterialisesNextRankedCandidate()
    {
        // Movie/TV parity for the reject -> reselect flow (AGENTS.md "Movie/TV
        // parity"): the movie-only tests above must not be the sole coverage.
        using var db = await NewDbAsync();
        await SeedSeriesAsync(db, 200);
        var current = Candidate("EP-CURRENT", 100);
        var alt = Candidate("EP-ALT", 50);
        await CacheCurrentAsync(
            db, 200, current, "/stub/ep-current.mkv", "/fuse/ep-current.mkv",
            type: "episode", season: 1, episode: 1, imdb: "tt0000099");
        var altFuse = Path.Combine(_fuseRoot, "ep-alt.mkv");
        File.WriteAllText(altFuse, "x");
        GostreamAddRequest? captured = null;
        var gostream = new Mock<IGostreamClient>(MockBehavior.Loose);
        gostream.Setup(g => g.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        gostream.Setup(g => g.AddAsync(It.IsAny<GostreamAddRequest>(), It.IsAny<CancellationToken>()))
            .Returns<GostreamAddRequest, CancellationToken>((req, _) =>
            {
                captured = req;
                return Task.FromResult(new GostreamAddResult
                {
                    StubPath = "/stub/ep-alt.mkv",
                    FusePath = altFuse,
                    Hash = alt.InfoHash,
                    Size = alt.Size,
                });
            });
        var ctrl = BuildController(db, new[] { current, alt }, gostream);

        var result = await ctrl.RejectCurrent("episode_200_s01e01", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        var payload = Assert.IsType<PhantomSourceOperationResult>(ok.Value);
        Assert.Equal(PhantomSourceOperationStatus.Success, payload.Status);
        Assert.NotNull(captured);
        Assert.Equal(alt.Magnet, captured!.Magnet);
        Assert.Equal(1, captured.Season);
        Assert.Equal(1, captured.Episode);

        gostream.Verify(g => g.RemoveAsync("/stub/ep-current.mkv", It.IsAny<CancellationToken>()), Times.Once);
        var row = await db.GetMaterialisedStateAsync(200, "episode", 1, 1, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal("/stub/ep-alt.mkv", row!.StubPath);
    }

    [Fact]
    public async Task RejectCurrent_Episode_SharedSource_DoesNotRemoveGostreamStub()
    {
        // Movie/TV parity for the most safety-critical property: a gostream
        // hash still referenced by another materialised row must never be
        // removed, for episodes exactly as for movies.
        using var db = await NewDbAsync();
        await SeedSeriesAsync(db, 200);
        await SeedSeriesAsync(db, 201);
        var current = Candidate("EP-SHARED", 100);
        await CacheCurrentAsync(
            db, 200, current, "/stub/ep-shared.mkv", "/fuse/ep-a.mkv",
            type: "episode", season: 1, episode: 1, imdb: "tt0000099");
        await db.InsertMaterialisedStateAsync(201, "episode", 1, 1, "/stub/ep-other.mkv", "/fuse/ep-b.mkv", CancellationToken.None);
        await db.PutCachedMagnetAsync(
            new MagnetCacheKey(201, "tt0000099", "episode", 1, 1, "test"),
            new MagnetCacheEntry
            {
                Magnet = "magnet:?xt=urn:btih:OTHER-EP",
                InfoHash = current.InfoHash,
                Size = current.Size,
                Seeders = current.Seeders,
                Indexer = current.Indexer,
                CachedAt = DateTimeOffset.UtcNow,
                Ttl = TimeSpan.FromDays(7),
                Source = "user",
            },
            CancellationToken.None);
        var gostream = new Mock<IGostreamClient>(MockBehavior.Loose);
        var ctrl = BuildController(db, new[] { current }, gostream);

        var result = await ctrl.RejectCurrent("episode_200_s01e01", CancellationToken.None);

        Assert.IsType<UnprocessableEntityObjectResult>(result);
        gostream.Verify(g => g.RemoveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.NotNull(await db.GetMaterialisedStateAsync(201, "episode", 1, 1, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task IngestRecommendations_NonPositiveTmdbId_Returns400WithoutCallingIngestor(int tmdbId)
    {
        using var db = await NewDbAsync();
        var ingestor = new Mock<IFavouriteRecommendationIngestor>(MockBehavior.Strict);
        var ctrl = BuildController(db, Array.Empty<MagnetCandidate>(), recommendationIngestor: ingestor.Object);

        var result = await ctrl.IngestRecommendations(tmdbId, "movie", CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        ingestor.Verify(
            i => i.IngestForFavouriteAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("person")]
    [InlineData("tv")]
    public async Task IngestRecommendations_InvalidType_Returns400WithoutCallingIngestor(string type)
    {
        using var db = await NewDbAsync();
        var ingestor = new Mock<IFavouriteRecommendationIngestor>(MockBehavior.Strict);
        var ctrl = BuildController(db, Array.Empty<MagnetCandidate>(), recommendationIngestor: ingestor.Object);

        var result = await ctrl.IngestRecommendations(42, type, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        ingestor.Verify(
            i => i.IngestForFavouriteAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task IngestRecommendations_ValidMovie_ReturnsOkAndPassesThroughResult()
    {
        using var db = await NewDbAsync();
        var expected = new FavouriteRecommendationResult(42, "movie", Enabled: true, 10, 8, 8, 8, 8, 0);
        var ingestor = new Mock<IFavouriteRecommendationIngestor>(MockBehavior.Strict);
        ingestor
            .Setup(i => i.IngestForFavouriteAsync(42, "movie", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var ctrl = BuildController(db, Array.Empty<MagnetCandidate>(), recommendationIngestor: ingestor.Object);

        var result = await ctrl.IngestRecommendations(42, "movie", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(expected, ok.Value);
        ingestor.Verify(i => i.IngestForFavouriteAsync(42, "movie", It.IsAny<CancellationToken>()), Times.Once);
    }

    // Movie/TV parity (AGENTS.md "Movie/TV parity"): the series seed path must
    // reach the ingestor exactly as the movie path does.
    [Fact]
    public async Task IngestRecommendations_ValidSeries_ReturnsOkAndPassesThroughResult()
    {
        using var db = await NewDbAsync();
        var expected = new FavouriteRecommendationResult(200, "series", Enabled: true, 6, 5, 5, 5, 0, 5);
        var ingestor = new Mock<IFavouriteRecommendationIngestor>(MockBehavior.Strict);
        ingestor
            .Setup(i => i.IngestForFavouriteAsync(200, "series", It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);
        var ctrl = BuildController(db, Array.Empty<MagnetCandidate>(), recommendationIngestor: ingestor.Object);

        var result = await ctrl.IngestRecommendations(200, "series", CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.Same(expected, ok.Value);
        ingestor.Verify(i => i.IngestForFavouriteAsync(200, "series", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("MOVIE", "movie")]
    [InlineData("  Series  ", "series")]
    public async Task IngestRecommendations_NormalisesTypeCasingAndWhitespace(string input, string normalised)
    {
        using var db = await NewDbAsync();
        var ingestor = new Mock<IFavouriteRecommendationIngestor>(MockBehavior.Strict);
        ingestor
            .Setup(i => i.IngestForFavouriteAsync(42, normalised, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FavouriteRecommendationResult(42, normalised, Enabled: true, 0, 0, 0, 0, 0, 0));
        var ctrl = BuildController(db, Array.Empty<MagnetCandidate>(), recommendationIngestor: ingestor.Object);

        var result = await ctrl.IngestRecommendations(42, input, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result);
        ingestor.Verify(i => i.IngestForFavouriteAsync(42, normalised, It.IsAny<CancellationToken>()), Times.Once);
    }
}
