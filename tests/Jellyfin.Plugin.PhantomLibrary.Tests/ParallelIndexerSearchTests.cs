using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Diagnostics;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// p13-parallel-indexer-search (ROI Priority 13) regression coverage, driven by
/// <c>scripts/tests/p13-parallel-indexer-search.test.sh</c>.
///
/// A fake Prowlarr (<see cref="FakeProwlarr"/>) serves <c>/api/v1/indexer</c> plus
/// per-indexer Torznab responses (<c>/{id}/api</c>) with one indexer fast, one
/// HUNG (never answers until cancelled), one SLOW past the budget, one 5xx, one
/// disabled and one usenet. The REAL <see cref="ProwlarrClient"/> +
/// <see cref="MagnetSelector"/> + <see cref="MagnetCacheLateResultWriter"/> (over a
/// real SQLite <see cref="PhantomDb"/>) are exercised end to end, movie AND episode.
/// The fake records any hit on the legacy aggregate <c>/api/v1/search</c>; the
/// old aggregate path fails these tests (its single call waits on the hung
/// indexer, so nothing returns within budget and nothing per-indexer is cached).
/// </summary>
public sealed class ParallelIndexerSearchTests : IDisposable
{
    private const string BaseUrl = "http://prowlarr.test:9696";
    private const string ApiKey = "test-key";

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), "phantom-p13-tests-" + Guid.NewGuid().ToString("N") + ".db");

    // Unique per test instance so parallel test classes never collide on the
    // static meter (the indexer label comes from the fake's discovered names).
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

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

    private string Ix(string role) => $"{role}-{_tag}";

    private static PluginConfiguration Config(int budgetMs, bool earlyReturn, int minEarly = 1) => new()
    {
        MinSeeders = 5,
        MinSizeGb1080p = 1,
        MinSizeGb4K = 1,
        QualityPreset = QualityPreset.GostreamDefault,
        IndexerProbeTimeoutSeconds = 5,
        IndexerSearchBudgetMs = budgetMs,
        FastIndexerEarlyReturnEnabled = earlyReturn,
        MinEarlyReturnCandidates = minEarly,
        EarlyReturnMinElapsedMs = 0,
        MagnetCacheTtlHours = 24,
        SourcePickerPreset = "gostream-default",
    };

    private async Task<PhantomDb> NewDbAsync()
    {
        var db = new PhantomDb(_dbPath);
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        return db;
    }

    private static Mock<IIndexerClient> Torrentio(Func<IReadOnlyList<IndexerCandidate>> result)
    {
        var m = new Mock<IIndexerClient>(MockBehavior.Strict);
        m.SetupGet(i => i.IsEnabled).Returns(true);
        m.SetupGet(i => i.Name).Returns("Torrentio");
        m.SetupGet(i => i.IsAvailabilityOracle).Returns(true);
        m.SetupGet(i => i.RequiresImdb).Returns(true);
        m.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(result);
        return m;
    }

    private static IndexerCandidate TorrentioHit(string title, string hash)
        => new()
        {
            Title = title,
            Magnet = "magnet:?xt=urn:btih:" + hash,
            InfoHash = hash,
            Size = 6L * 1024 * 1024 * 1024,
            Seeders = 25,
            IndexerName = "Torrentio",
        };

    private (MagnetSelector Selector, ProwlarrClient Prowlarr) Build(
        FakeProwlarr fake,
        PluginConfiguration cfg,
        PhantomDb db,
        params IIndexerClient[] extra)
    {
        var prowlarr = new ProwlarrClient(
            new HttpClient(fake),
            NullLogger<ProwlarrClient>.Instance,
            () => (BaseUrl, ApiKey),
            new ProwlarrIndexerDirectory(() => TimeSpan.FromMinutes(15), () => DateTimeOffset.UtcNow));
        var sink = new MagnetCacheLateResultWriter(db, NullLogger<MagnetCacheLateResultWriter>.Instance, () => cfg);
        var selector = new MagnetSelector(
            new IIndexerClient[] { prowlarr }.Concat(extra).ToArray(),
            new QualityScorer(NullLogger<QualityScorer>.Instance),
            NullLogger<MagnetSelector>.Instance,
            () => cfg,
            lateSink: sink);
        return (selector, prowlarr);
    }

    /// <summary>The standard fake: fast / hung / slow-past-budget / 5xx / disabled / usenet.</summary>
    private FakeProwlarr StandardFake(string itemTitle, TimeSpan slowDelay)
    {
        var fake = new FakeProwlarr(ApiKey);
        fake.AddIndexer(11, Ix("fast"), enable: true, IndexerBehaviour.Respond(TimeSpan.Zero, Item($"{itemTitle} 1080p FAST", "AAAA1111", 40)));
        fake.AddIndexer(12, Ix("hung"), enable: true, IndexerBehaviour.Hang());
        fake.AddIndexer(13, Ix("slow"), enable: true, IndexerBehaviour.Respond(slowDelay, Item($"{itemTitle} 2160p SLOW", "BBBB2222", 90, sizeGb: 30)));
        fake.AddIndexer(14, Ix("broken"), enable: true, IndexerBehaviour.Status(HttpStatusCode.ServiceUnavailable));
        fake.AddIndexer(15, Ix("off"), enable: false, IndexerBehaviour.Respond(TimeSpan.Zero, Item($"{itemTitle} 1080p OFF", "DDDD4444", 99)));
        fake.AddIndexer(16, Ix("nzb"), enable: true, IndexerBehaviour.Respond(TimeSpan.Zero, Item($"{itemTitle} 1080p NZB", "EEEE5555", 99)), protocol: "usenet");
        return fake;
    }

    private static TorznabItem Item(string title, string hash, int seeders, long sizeGb = 5)
        => new(title, hash, seeders, sizeGb * 1024L * 1024L * 1024L);

    [Theory]
    [InlineData("movie")]
    [InlineData("episode")]
    public async Task Budget_ReturnsWithinBudget_DespiteHungAndSlowIndexers_LateResultLandsInMagnetCache(string type)
    {
        const int budgetMs = 1000;
        var slowDelay = TimeSpan.FromMilliseconds(2500);
        var isEpisode = type == "episode";
        var title = isEpisode ? "Show S01E02" : "Movie";
        var tmdb = isEpisode ? 7002 : 7001;
        var cfg = Config(budgetMs, earlyReturn: false);
        var db = await NewDbAsync();
        var fake = StandardFake(title, slowDelay);
        using var metrics = new MetricCapture(_tag);
        var (selector, _) = Build(fake, cfg, db, Torrentio(() => new[] { TorrentioHit($"{title} 1080p TIO", "CCCC3333") }).Object);
        using (selector)
        {
            var sw = Stopwatch.StartNew();
            var probe = await selector.ProbeAsync(
                tmdb, "tt0000001", type, isEpisode ? 1 : null, isEpisode ? 2 : null,
                isEpisode ? "Show" : "Movie", 2020, CancellationToken.None);
            sw.Stop();

            // Within the budget (+ tolerance), regardless of the hung (forever) and
            // slow (2.5 s) indexers — the old aggregate path blocked on them.
            Assert.True(sw.ElapsedMilliseconds < budgetMs + 750, $"probe took {sw.ElapsedMilliseconds}ms, budget {budgetMs}ms");
            Assert.True(sw.ElapsedMilliseconds >= budgetMs - 50, $"early-return was off, so the budget should have been used: {sw.ElapsedMilliseconds}ms");
            Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
            var hashes = probe.Candidates.Select(c => c.InfoHash).ToHashSet();
            Assert.Contains("AAAA1111", hashes); // fast Prowlarr indexer (in budget)
            Assert.Contains("CCCC3333", hashes); // Torrentio, concurrently
            Assert.DoesNotContain("BBBB2222", hashes); // slow one was not waited for
            Assert.DoesNotContain("DDDD4444", hashes); // disabled indexer never queried
            Assert.DoesNotContain("EEEE5555", hashes); // usenet indexer never queried
            Assert.Equal("Prowlarr", probe.Candidates.First(c => c.InfoHash == "AAAA1111").Indexer);

            // Per-indexer Torznab, never the aggregate endpoint; disabled/usenet skipped.
            Assert.Equal(0, fake.AggregateSearchHits);
            Assert.Equal(1, fake.IndexerListHits);
            Assert.True(fake.TorznabHits(11) >= 1 && fake.TorznabHits(12) >= 1 && fake.TorznabHits(13) >= 1 && fake.TorznabHits(14) >= 1);
            Assert.Equal(0, fake.TorznabHits(15));
            Assert.Equal(0, fake.TorznabHits(16));

            // The slow indexer finishes in the background and its release is
            // written to the P6 magnet cache (source_candidates) — not discarded.
            var deadline = DateTime.UtcNow.AddSeconds(10);
            IReadOnlyList<SourceCandidateRow> rows = Array.Empty<SourceCandidateRow>();
            while (DateTime.UtcNow < deadline)
            {
                rows = await db.ListSourceCandidatesAsync(
                    tmdb, type, isEpisode ? 1 : -1, isEpisode ? 2 : -1, "gostream-default", includeExpired: false, CancellationToken.None);
                if (rows.Any(r => r.InfoHash == "BBBB2222"))
                {
                    break;
                }

                await Task.Delay(50);
            }

            var late = Assert.Single(rows, r => r.InfoHash == "BBBB2222");
            Assert.Equal(MagnetCacheLateResultWriter.SourceTag, late.Source);
            Assert.Equal(90, late.Seeders);
            // The late write is the FULL re-ranked set (in-budget + late), so the
            // fast and Torrentio releases are cached alongside it, rank-coherent.
            Assert.Contains(rows, r => r.InfoHash == "AAAA1111");
            Assert.Contains(rows, r => r.InfoHash == "CCCC3333");
            Assert.Equal(rows.Count, rows.Select(r => r.Rank).Distinct().Count());

            // The hung indexer is cancelled by the per-request timeout (5 s) in the background.
            var idle = selector.WhenLateWorkIdleAsync();
            Assert.Same(idle, await Task.WhenAny(idle, Task.Delay(TimeSpan.FromSeconds(10))));
            Assert.Equal(0, selector.LateCollectorsActive);
        }

        // Metrics: per-indexer request count by status + phase, latency, candidates,
        // budget-expired-with-N-candidates. Labels come from the DISCOVERED set.
        var req = metrics.Snapshot(IndexerSearchMetrics.RequestsName);
        Assert.Contains(req, m => (string?)m.Tags["indexer"] == Ix("fast") && (string?)m.Tags["status"] == "ok" && (string?)m.Tags["phase"] == "in_budget" && (string?)m.Tags["client"] == "Prowlarr");
        Assert.Contains(req, m => (string?)m.Tags["indexer"] == Ix("broken") && (string?)m.Tags["status"] == "error" && (string?)m.Tags["phase"] == "in_budget");
        Assert.Contains(req, m => (string?)m.Tags["indexer"] == Ix("slow") && (string?)m.Tags["status"] == "ok" && (string?)m.Tags["phase"] == "late");
        Assert.Contains(req, m => (string?)m.Tags["indexer"] == Ix("hung") && (string?)m.Tags["status"] == "timeout" && (string?)m.Tags["phase"] == "late");
        Assert.DoesNotContain(req, m => (string?)m.Tags["indexer"] == Ix("off") || (string?)m.Tags["indexer"] == Ix("nzb"));
        var lat = metrics.Snapshot(IndexerSearchMetrics.LatencyName);
        Assert.Contains(lat, m => (string?)m.Tags["indexer"] == Ix("slow") && m.Value >= slowDelay.TotalMilliseconds - 100);
        var cands = metrics.Snapshot(IndexerSearchMetrics.CandidatesName);
        Assert.Contains(cands, m => (string?)m.Tags["indexer"] == Ix("fast") && m.Value == 1);
        Assert.Contains(cands, m => (string?)m.Tags["indexer"] == Ix("slow") && (string?)m.Tags["phase"] == "late" && m.Value == 1);
        // 2 candidates (fast Prowlarr + Torrentio) were held when the budget expired.
        Assert.Contains(metrics.SnapshotAll(IndexerSearchMetrics.BudgetExpiredName), m => (string?)m.Tags["item_type"] == type && m.Value == 2);
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("episode")]
    public async Task EarlyReturn_OnceNHighConfidenceCandidatesArrive_WellBeforeBudget(string type)
    {
        const int budgetMs = 8000;
        var isEpisode = type == "episode";
        var title = isEpisode ? "Show S01E02" : "Movie";
        var tmdb = isEpisode ? 7102 : 7101;
        var cfg = Config(budgetMs, earlyReturn: true, minEarly: 2);
        var db = await NewDbAsync();
        var fake = new FakeProwlarr(ApiKey);
        fake.AddIndexer(21, Ix("fast"), enable: true, IndexerBehaviour.Respond(TimeSpan.Zero, Item($"{title} 1080p A", "A1A1A1A1", 40)));
        // Low-seeder (below MinSeeders=5): must NOT count toward the N high-confidence.
        fake.AddIndexer(22, Ix("weak"), enable: true, IndexerBehaviour.Respond(TimeSpan.Zero, Item($"{title} 1080p W", "B2B2B2B2", 1)));
        fake.AddIndexer(23, Ix("second"), enable: true, IndexerBehaviour.Respond(TimeSpan.FromMilliseconds(300), Item($"{title} 1080p S", "C3C3C3C3", 30)));
        fake.AddIndexer(24, Ix("hung"), enable: true, IndexerBehaviour.Hang());
        using var metrics = new MetricCapture(_tag);
        var (selector, _) = Build(fake, cfg, db);
        using (selector)
        {
            var sw = Stopwatch.StartNew();
            var probe = await selector.ProbeAsync(
                tmdb, "tt0000002", type, isEpisode ? 1 : null, isEpisode ? 2 : null,
                isEpisode ? "Show" : "Movie", 2020, CancellationToken.None);
            sw.Stop();

            // Returned once 2 high-confidence candidates were in (~300 ms), not at
            // the 8 s budget and not blocked by the hung indexer.
            Assert.True(sw.ElapsedMilliseconds < 2500, $"expected early return, took {sw.ElapsedMilliseconds}ms");
            Assert.True(sw.ElapsedMilliseconds >= 250, $"returned before the 2nd high-confidence candidate arrived: {sw.ElapsedMilliseconds}ms");
            Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
            var hashes = probe.Candidates.Select(c => c.InfoHash).ToList();
            Assert.Contains("A1A1A1A1", hashes);
            Assert.Contains("C3C3C3C3", hashes);
            Assert.DoesNotContain("B2B2B2B2", hashes); // scorer floor still applied
            Assert.Equal(0, fake.AggregateSearchHits);
            Assert.Equal(1, selector.LateCollectorsActive); // hung request continues in the background

            // Shutdown cancels the background request promptly (bounded, cancellable).
            var idle = selector.WhenLateWorkIdleAsync();
            selector.Dispose();
            Assert.Same(idle, await Task.WhenAny(idle, Task.Delay(TimeSpan.FromSeconds(2))));
        }

        Assert.Contains(
            metrics.Snapshot(IndexerSearchMetrics.RequestsName),
            m => (string?)m.Tags["indexer"] == Ix("hung") && (string?)m.Tags["status"] == "cancelled" && (string?)m.Tags["phase"] == "late");
    }

    [Fact]
    public async Task Budget_NothingArrivedYet_IsTransient_NotDefinitiveUnavailable_AndLateResultStillCached()
    {
        // Only a slow indexer: at the budget there is nothing to rank. That must
        // NOT be reported as a definitive "unavailable" (the indexer is still
        // running) — and its eventual result still reaches the magnet cache.
        var cfg = Config(500, earlyReturn: true);
        var db = await NewDbAsync();
        var fake = new FakeProwlarr(ApiKey);
        fake.AddIndexer(31, Ix("slow"), enable: true, IndexerBehaviour.Respond(TimeSpan.FromMilliseconds(1500), Item("Movie 1080p SLOW", "F0F0F0F0", 50)));
        var (selector, _) = Build(fake, cfg, db);
        using (selector)
        {
            var probe = await selector.ProbeAsync(7201, "tt0000003", "movie", null, null, "Movie", 2020, CancellationToken.None);
            Assert.Equal(MagnetProbeOutcome.IndeterminateTransient, probe.Outcome);
            Assert.Equal("indexer_budget_expired", probe.ErrorKind);

            await selector.WhenLateWorkIdleAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var rows = await db.ListSourceCandidatesAsync(7201, "movie", -1, -1, "gostream-default", includeExpired: false, CancellationToken.None);
            Assert.Contains(rows, r => r.InfoHash == "F0F0F0F0" && r.Source == MagnetCacheLateResultWriter.SourceTag);
        }
    }

    [Fact]
    public async Task CallerCancellation_StillPropagates_AndCancelsInFlightRequests()
    {
        var cfg = Config(5000, earlyReturn: true);
        var db = await NewDbAsync();
        var fake = new FakeProwlarr(ApiKey);
        fake.AddIndexer(41, Ix("hung"), enable: true, IndexerBehaviour.Hang());
        var (selector, _) = Build(fake, cfg, db);
        using (selector)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var sw = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                selector.ProbeAsync(7301, "tt0000004", "movie", null, null, "Movie", 2020, cts.Token));
            Assert.True(sw.ElapsedMilliseconds < 2000, $"cancellation took {sw.ElapsedMilliseconds}ms");
            await selector.WhenLateWorkIdleAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    [Fact]
    public async Task Discovery_IsCachedOnTtl_AndRefreshedAfterExpiry_NewIndexerPickedUp()
    {
        var now = DateTimeOffset.UtcNow;
        var directory = new ProwlarrIndexerDirectory(() => TimeSpan.FromMinutes(15), () => now);
        var fake = new FakeProwlarr(ApiKey);
        fake.AddIndexer(51, Ix("a"), enable: true, IndexerBehaviour.Respond(TimeSpan.Zero, Item("Movie 1080p A", "AA00AA00", 10)));
        var client = new ProwlarrClient(new HttpClient(fake), NullLogger<ProwlarrClient>.Instance, () => (BaseUrl, ApiKey), directory);
        var q = new IndexerQuery { Type = "movie", Title = "Movie", Year = 2020 };

        var first = await client.DiscoverIndexersAsync(q, CancellationToken.None);
        Assert.Equal(new[] { Ix("a") }, first.Select(i => i.Name));
        fake.AddIndexer(52, Ix("b"), enable: true, IndexerBehaviour.Respond(TimeSpan.Zero, Item("Movie 1080p B", "BB00BB00", 10)));

        now = now.AddMinutes(5);
        var cached = await client.DiscoverIndexersAsync(q, CancellationToken.None);
        Assert.Single(cached);
        Assert.Equal(1, fake.IndexerListHits);

        now = now.AddMinutes(11);
        var refreshed = await client.DiscoverIndexersAsync(q, CancellationToken.None);
        Assert.Equal(new[] { Ix("a"), Ix("b") }, refreshed.Select(i => i.Name));
        Assert.Equal(2, fake.IndexerListHits);

        // A failing refresh falls back to the last good list (stale-while-error).
        fake.FailIndexerList = true;
        now = now.AddMinutes(30);
        var stale = await client.DiscoverIndexersAsync(q, CancellationToken.None);
        Assert.Equal(2, stale.Count);
    }

    [Fact]
    public async Task Discovery_FailsWithNoCache_ProbeIsTransient_TorrentioStillAnswers()
    {
        var cfg = Config(2000, earlyReturn: true);
        var db = await NewDbAsync();
        var fake = new FakeProwlarr(ApiKey) { FailIndexerList = true };
        var (selector, _) = Build(fake, cfg, db, Torrentio(() => new[] { TorrentioHit("Movie 1080p TIO", "CCCC3333") }).Object);
        using (selector)
        {
            var probe = await selector.ProbeAsync(7401, "tt0000005", "movie", null, null, "Movie", 2020, CancellationToken.None);
            Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
            Assert.Equal("CCCC3333", Assert.Single(probe.Candidates).InfoHash);

            var (onlyProwlarr, _) = Build(fake, cfg, db);
            using (onlyProwlarr)
            {
                var p2 = await onlyProwlarr.ProbeAsync(7402, "tt0000006", "movie", null, null, "Movie", 2020, CancellationToken.None);
                Assert.Equal(MagnetProbeOutcome.IndeterminateTransient, p2.Outcome);
                Assert.Contains("Prowlarr:discovery_failed", p2.ErrorMessage, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task SameRelease_FromSeveralProwlarrIndexers_IsRankedOnce()
    {
        var cfg = Config(2000, earlyReturn: false);
        var db = await NewDbAsync();
        var fake = new FakeProwlarr(ApiKey);
        fake.AddIndexer(61, Ix("x"), enable: true, IndexerBehaviour.Respond(TimeSpan.Zero, Item("Movie 1080p DUP", "DEDEDEDE", 20)));
        fake.AddIndexer(62, Ix("y"), enable: true, IndexerBehaviour.Respond(TimeSpan.Zero, Item("Movie 1080p DUP", "dedededE", 20)));
        var (selector, _) = Build(fake, cfg, db);
        using (selector)
        {
            var probe = await selector.ProbeAsync(7501, "tt0000007", "movie", null, null, "Movie", 2020, CancellationToken.None);
            Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
            Assert.Single(probe.Candidates);
        }
    }

    // ------------------------------------------------------------------
    // Fake Prowlarr
    // ------------------------------------------------------------------

    internal sealed record TorznabItem(string Title, string Hash, int Seeders, long Size);

    internal sealed class IndexerBehaviour
    {
        public TimeSpan Delay { get; private init; }
        public bool Hangs { get; private init; }
        public HttpStatusCode StatusCode { get; private init; } = HttpStatusCode.OK;
        public IReadOnlyList<TorznabItem> Items { get; private init; } = Array.Empty<TorznabItem>();

        public static IndexerBehaviour Respond(TimeSpan delay, params TorznabItem[] items) => new() { Delay = delay, Items = items };
        public static IndexerBehaviour Hang() => new() { Hangs = true };
        public static IndexerBehaviour Status(HttpStatusCode code) => new() { StatusCode = code };
    }

    /// <summary>
    /// In-process fake Prowlarr: <c>GET /api/v1/indexer</c> (JSON list) and
    /// <c>GET /{id}/api?t=search</c> (Torznab RSS). Honors request cancellation
    /// like a real socket would. Counts hits on the legacy aggregate
    /// <c>/api/v1/search</c> (which it serves by waiting on every indexer, i.e.
    /// exactly as slow as the slowest/hung one, like real Prowlarr).
    /// </summary>
    internal sealed class FakeProwlarr : HttpMessageHandler
    {
        private readonly string _apiKey;
        private readonly List<(int Id, string Name, bool Enable, string Protocol, IndexerBehaviour Behaviour)> _indexers = new();
        private readonly Dictionary<int, int> _torznabHits = new();
        private readonly object _lock = new();
        private int _aggregateHits;
        private int _listHits;

        public FakeProwlarr(string apiKey) => _apiKey = apiKey;

        public bool FailIndexerList { get; set; }

        public int AggregateSearchHits => Volatile.Read(ref _aggregateHits);

        public int IndexerListHits => Volatile.Read(ref _listHits);

        public int TorznabHits(int id)
        {
            lock (_lock)
            {
                return _torznabHits.GetValueOrDefault(id);
            }
        }

        public void AddIndexer(int id, string name, bool enable, IndexerBehaviour behaviour, string protocol = "torrent")
        {
            lock (_lock)
            {
                _indexers.Add((id, name, enable, protocol, behaviour));
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var queryKey = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["apikey"];
            var headerKey = request.Headers.TryGetValues("X-Api-Key", out var v) ? v.FirstOrDefault() : null;
            if (headerKey != _apiKey && queryKey != _apiKey)
            {
                return new HttpResponseMessage(HttpStatusCode.Unauthorized);
            }

            if (path == "/api/v1/indexer")
            {
                Interlocked.Increment(ref _listHits);
                if (FailIndexerList)
                {
                    return new HttpResponseMessage(HttpStatusCode.BadGateway);
                }

                List<(int Id, string Name, bool Enable, string Protocol, IndexerBehaviour Behaviour)> snapshot;
                lock (_lock)
                {
                    snapshot = _indexers.ToList();
                }

                var json = "[" + string.Join(",", snapshot.Select(i =>
                    $"{{\"id\":{i.Id},\"name\":\"{i.Name}\",\"enable\":{(i.Enable ? "true" : "false")},\"protocol\":\"{i.Protocol}\",\"priority\":25}}")) + "]";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            }

            if (path == "/api/v1/search")
            {
                Interlocked.Increment(ref _aggregateHits);
                List<IndexerBehaviour> all;
                lock (_lock)
                {
                    all = _indexers.Where(i => i.Enable).Select(i => i.Behaviour).ToList();
                }

                foreach (var b in all)
                {
                    if (b.Hangs)
                    {
                        await Task.Delay(Timeout.Infinite, ct);
                    }

                    await Task.Delay(b.Delay, ct);
                }

                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", Encoding.UTF8, "application/json") };
            }

            var segments = path.Trim('/').Split('/');
            if (segments.Length == 2 && segments[1] == "api" && int.TryParse(segments[0], out var id))
            {
                (int Id, string Name, bool Enable, string Protocol, IndexerBehaviour Behaviour) ix;
                lock (_lock)
                {
                    _torznabHits[id] = _torznabHits.GetValueOrDefault(id) + 1;
                    ix = _indexers.FirstOrDefault(i => i.Id == id);
                }

                if (ix.Behaviour is null)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                var b = ix.Behaviour;
                if (b.Hangs)
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }

                if (b.Delay > TimeSpan.Zero)
                {
                    await Task.Delay(b.Delay, ct);
                }

                if (b.StatusCode != HttpStatusCode.OK)
                {
                    return new HttpResponseMessage(b.StatusCode) { Content = new StringContent("upstream down") };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Torznab(ix.Name, b.Items), Encoding.UTF8, "application/rss+xml"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        internal static string Torznab(string indexer, IEnumerable<TorznabItem> items)
        {
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.Append("<rss version=\"2.0\" xmlns:torznab=\"http://torznab.com/schemas/2015/feed\"><channel>");
            sb.Append("<title>").Append(SecurityElement.Escape(indexer)).Append("</title>");
            foreach (var it in items)
            {
                var magnet = "magnet:?xt=urn:btih:" + it.Hash + "&dn=" + Uri.EscapeDataString(it.Title);
                sb.Append("<item>");
                sb.Append("<title>").Append(SecurityElement.Escape(it.Title)).Append("</title>");
                sb.Append("<size>").Append(it.Size).Append("</size>");
                sb.Append("<link>").Append(SecurityElement.Escape(magnet)).Append("</link>");
                sb.Append("<torznab:attr name=\"seeders\" value=\"").Append(it.Seeders).Append("\"/>");
                sb.Append("<torznab:attr name=\"peers\" value=\"").Append(it.Seeders + 3).Append("\"/>");
                sb.Append("<torznab:attr name=\"infohash\" value=\"").Append(it.Hash).Append("\"/>");
                sb.Append("<torznab:attr name=\"magneturl\" value=\"").Append(SecurityElement.Escape(magnet)).Append("\"/>");
                sb.Append("</item>");
            }

            sb.Append("</channel></rss>");
            return sb.ToString();
        }
    }

    /// <summary>Thread-safe capture of the <see cref="IndexerSearchMetrics.MeterName"/> meter.</summary>
    private sealed class MetricCapture : IDisposable
    {
        private readonly MeterListener _listener;
        private readonly List<(string Instrument, double Value, Dictionary<string, object?> Tags)> _all = new();
        private readonly string _tag;

        public MetricCapture(string tag)
        {
            _tag = tag;
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, l) =>
                {
                    if (instrument.Meter.Name == IndexerSearchMetrics.MeterName)
                    {
                        l.EnableMeasurementEvents(instrument);
                    }
                },
            };
            _listener.SetMeasurementEventCallback<double>((i, value, tags, _) => Add(i.Name, value, tags));
            _listener.SetMeasurementEventCallback<long>((i, value, tags, _) => Add(i.Name, value, tags));
            _listener.Start();
        }

        private void Add(string name, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var d = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var t in tags)
            {
                d[t.Key] = t.Value;
            }

            lock (_all)
            {
                _all.Add((name, value, d));
            }
        }

        /// <summary>Measurements of <paramref name="instrument"/> whose indexer label belongs to this test.</summary>
        public List<(string Instrument, double Value, Dictionary<string, object?> Tags)> Snapshot(string instrument)
        {
            lock (_all)
            {
                return _all.Where(m => m.Instrument == instrument
                    && m.Tags.TryGetValue("indexer", out var ix) && ix is string s && s.EndsWith(_tag, StringComparison.Ordinal)).ToList();
            }
        }

        public List<(string Instrument, double Value, Dictionary<string, object?> Tags)> SnapshotAll(string instrument)
        {
            lock (_all)
            {
                return _all.Where(m => m.Instrument == instrument).ToList();
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
