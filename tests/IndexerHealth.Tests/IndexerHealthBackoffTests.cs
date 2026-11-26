using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Diagnostics;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Microsoft.Extensions.DependencyInjection;
using MediaBrowser.Controller;
using Moq;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

// Mandatory integration tests: absent PostgreSQL is a FAILURE, not a silent skip.
public sealed class IndexerHealthBackoffTests : IAsyncLifetime
{
    private readonly string _schema = "health_" + Guid.NewGuid().ToString("N");
    private string _dsn = null!;
    private PhantomDb _blue = null!;
    private PhantomDb _green = null!;
    private readonly FakeIndexer _server = new();
    private readonly List<HttpClient> _http = new();
    private readonly List<ProwlarrIndexerDirectory> _directories = new();

    public async Task InitializeAsync()
    {
        var dsn = Environment.GetEnvironmentVariable("PHANTOM_TEST_POSTGRES_DSN");
        Assert.False(string.IsNullOrWhiteSpace(dsn), "PHANTOM_TEST_POSTGRES_DSN is mandatory");
        _dsn = new NpgsqlConnectionStringBuilder(dsn) { SearchPath = _schema }.ToString();
        await SqlAsync($"CREATE SCHEMA {_schema}");
        _blue = PhantomDb.CreatePostgres(_dsn);
        _green = PhantomDb.CreatePostgres(_dsn);
        await _blue.SetMetaAsync("fixture", "old-data", default);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        foreach (var client in _http) client.Dispose();
        foreach (var dir in _directories) dir.Dispose();
        _blue?.Dispose();
        _green?.Dispose();
        if (_dsn is not null) await SqlAsync($"DROP SCHEMA {_schema} CASCADE");
    }

    private async Task<object?> SqlAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(_dsn);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    private IIndexerClient Client(string kind, PhantomDb db)
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.Add(http);
        var gate = new IndexerHealthGate(db);
        if (kind == "Torrentio")
            return new TorrentioClient(http, NullLogger<TorrentioClient>.Instance, () => _server.Url, gate);
        var dir = new ProwlarrIndexerDirectory();
        _directories.Add(dir);
        return new ProwlarrClient(http, NullLogger<ProwlarrClient>.Instance, () => (_server.Url, "fixture"), dir, gate);
    }

    private static IndexerQuery Query(string type, IndexerPriority priority = IndexerPriority.Interactive)
        => new() { Type = type, Imdb = type == "movie" ? "tt1234567" : null,
            SeriesImdb = "tt1234567", Title = type == "episode" ? "Fixture" : null,
            Season = type == "episode" ? 1 : null, Episode = type == "episode" ? 2 : null, Priority = priority };

    private static Task<IReadOnlyList<IndexerCandidate>> Search(IIndexerClient client, IndexerQuery q, CancellationToken ct = default)
        => client is ProwlarrClient p ? p.SearchIndexerAsync(new DiscoveredIndexer(1, "fixture-indexer", "torrent"), q, ct) : client.SearchAsync(q, ct);

    [Theory]
    [InlineData("movie", "Torrentio")]
    [InlineData("episode", "Torrentio")]
    [InlineData("movie", "Prowlarr")]
    [InlineData("episode", "Prowlarr")]
    public async Task RetryAfter_SkipsOtherColour_ThenRecovers(string type, string kind)
    {
        var statuses = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Meter.Name == IndexerSearchMetrics.MeterName && i.Name == IndexerSearchMetrics.HealthName) l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags) if (tag.Key == "status") statuses.Add(tag.Value!.ToString()!);
        });
        listener.Start();
        var blue = Client(kind, _blue);
        var green = Client(kind, _green);
        _server.Status = 429;
        _server.Retry = "6";
        var failed = await Assert.ThrowsAsync<IndexerTransientException>(() => Search(blue, Query(type)));
        Assert.Equal(429, failed.HttpStatus);
        var watch = Stopwatch.StartNew();
        var skipped = await Assert.ThrowsAsync<IndexerTransientException>(() => Search(green, Query(type)));
        Assert.Equal("skipped_backoff", skipped.HealthStatus);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), watch.Elapsed.ToString());
        Assert.Single(_server.Requests);
        Assert.Equal(1L, await SqlAsync("SELECT rate_limits FROM indexer_health WHERE requests > 0"));
        Assert.Equal(1L, await SqlAsync("SELECT skipped FROM indexer_health WHERE requests > 0"));
        _server.Status = 200;
        await Task.Delay(6100);
        Assert.Single(await Search(green, Query(type)));
        Assert.Equal(2, _server.Requests.Count);
        Assert.Equal(0, await SqlAsync("SELECT failures FROM indexer_health WHERE requests > 0"));
        Assert.Contains("429", statuses);
        Assert.Contains("skipped_backoff", statuses);
        Assert.Contains("ok", statuses);
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("episode")]
    public async Task DisabledStatus_IsShared_OtherIndexerStillWorks_AndRecoveryProbes(string type)
    {
        _server.DisabledTill = DateTimeOffset.UtcNow.AddSeconds(3);
        var blue = (ProwlarrClient)Client("Prowlarr", _blue);
        var green = (ProwlarrClient)Client("Prowlarr", _green);
        var first = await Assert.ThrowsAsync<IndexerTransientException>(() => Search(blue, Query(type)));
        Assert.Equal("skipped_backoff", first.HealthStatus);
        var second = await Assert.ThrowsAsync<IndexerTransientException>(() => Search(green, Query(type)));
        Assert.Equal("skipped_backoff", second.HealthStatus);
        Assert.Empty(_server.Requests);
        Assert.Single(await blue.SearchIndexerAsync(new DiscoveredIndexer(2, "healthy", "torrent"), Query(type), default));
        Assert.All(_server.Requests, x => Assert.StartsWith("/2/", x.Path));
        await Task.Delay(3100);
        Assert.Single(await Search(green, Query(type)));
        Assert.Equal(2, _server.StatusRequests); // directory cache; not per search/subquery
    }

    [Theory]
    [InlineData("movie", "Torrentio")]
    [InlineData("episode", "Torrentio")]
    [InlineData("movie", "Prowlarr")]
    [InlineData("episode", "Prowlarr")]
    public async Task TwoColours_ShareOneRequestSpacingBudget(string type, string kind)
    {
        var blue = Client(kind, _blue);
        var green = Client(kind, _green);
        await Task.WhenAll(Search(blue, Query(type)), Search(green, Query(type)));
        var sent = _server.Requests.OrderBy(x => x.At).ToArray();
        Assert.Equal(2, sent.Length);
        Assert.True(sent[1].At - sent[0].At >= TimeSpan.FromMilliseconds(kind == "Torrentio" ? 1900 : 900));
        Assert.Equal(2L, await SqlAsync("SELECT requests FROM indexer_health WHERE requests > 0"));
        Assert.Equal(1, _server.MaxConcurrent);
    }

    [Theory]
    [InlineData("movie", "Torrentio")]
    [InlineData("episode", "Torrentio")]
    [InlineData("movie", "Prowlarr")]
    [InlineData("episode", "Prowlarr")]
    public async Task Interactive_PreemptsQueuedBackgroundAcrossColours_WithoutRefund(string type, string kind)
    {
        var blue = Client(kind, _blue);
        var green = Client(kind, _green);
        // Spend one slot as background, so no earlier interactive reservation exists.
        await Search(blue, Query(type, IndexerPriority.Background));
        var background = Search(blue, Query(type, IndexerPriority.Background));
        await Task.Delay(150);
        Assert.Single(await Search(green, Query(type)));
        var skipped = await Assert.ThrowsAsync<IndexerTransientException>(async () => await background);
        Assert.Equal("skipped_preempted", skipped.HealthStatus);
        Assert.Equal(2, _server.Requests.Count); // no second BG request, no refunded token
        var sent = _server.Requests.OrderBy(x => x.At).ToArray();
        Assert.True(sent[1].At - sent[0].At >= TimeSpan.FromMilliseconds(kind == "Torrentio" ? 1900 : 900));
        await Task.Delay(500); // reservation expires; background is not permanently starved
        Assert.Single(await Search(blue, Query(type, IndexerPriority.Background)));
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("episode")]
    public async Task DownloadRedirects_AlsoSpendBudget_AndHonour429(string type)
    {
        _server.DownloadLinks = true;
        var blue = Client("Prowlarr", _blue);
        var failed = await Assert.ThrowsAsync<IndexerTransientException>(() => Search(blue, Query(type)));
        Assert.Equal(429, failed.HttpStatus);
        var requests = _server.Requests.OrderBy(r => r.At).ToArray();
        Assert.Equal(2, requests.Length);
        Assert.True(requests[1].At - requests[0].At >= TimeSpan.FromMilliseconds(900));
        var skipped = await Assert.ThrowsAsync<IndexerTransientException>(() => Search(Client("Prowlarr", _green), Query(type)));
        Assert.Equal("skipped_backoff", skipped.HealthStatus);
        Assert.Equal(2, _server.Requests.Count);
    }

    [Fact]
    public async Task RepeatedErrors_ExponentialCap_HttpDateFloor_AndSuccessReset()
    {
        var client = Client("Torrentio", _blue);
        _server.Status = 504;
        foreach (var seconds in new[] { 5, 10, 20, 40, 80, 160, 300, 300 })
        {
            await Assert.ThrowsAsync<IndexerTransientException>(() => Search(client, Query("movie")));
            var remaining = Convert.ToInt64(await SqlAsync("SELECT backoff_until - CAST(EXTRACT(EPOCH FROM clock_timestamp()) * 1000 AS BIGINT) FROM indexer_health WHERE requests > 0"));
            Assert.InRange(remaining, seconds * 1000L - 1000, seconds * 1000L);
            // Advance only the fixture's cooldown, preserving the real failure streak.
            await SqlAsync("UPDATE indexer_health SET backoff_until = 0, next_at = 0");
        }
        _server.Status = 429;
        _server.Retry = DateTimeOffset.UtcNow.AddMinutes(10).ToString("R");
        await Assert.ThrowsAsync<IndexerTransientException>(() => Search(client, Query("episode")));
        var floor = Convert.ToInt64(await SqlAsync("SELECT backoff_until - CAST(EXTRACT(EPOCH FROM clock_timestamp()) * 1000 AS BIGINT) FROM indexer_health WHERE requests > 0"));
        Assert.InRange(floor, 598000L, 600000L); // cap must NOT truncate explicit Retry-After
        await SqlAsync("UPDATE indexer_health SET backoff_until = 0, next_at = 0");
        _server.Status = 200;
        await Search(Client("Torrentio", _green), Query("episode"));
        Assert.Equal(0, await SqlAsync("SELECT failures FROM indexer_health WHERE requests > 0"));
        Assert.Equal(9L, await SqlAsync("SELECT errors FROM indexer_health WHERE requests > 0"));
    }

    [Fact]
    public async Task Cancellation_ReleasesOwner_ButDoesNotRefundBudget()
    {
        _server.DelayMs = 700;
        var blue = Client("Torrentio", _blue);
        using var ct = new CancellationTokenSource(250);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Search(blue, Query("movie"), ct.Token));
        Assert.Equal(0L, await SqlAsync("SELECT lease_until FROM indexer_health WHERE requests > 0"));
        Assert.Equal(1L, await SqlAsync("SELECT requests FROM indexer_health WHERE requests > 0"));
        _server.DelayMs = 0;
        Assert.Single(await Search(Client("Torrentio", _green), Query("episode")));
        Assert.Equal(2, _server.Requests.Count);
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("episode")]
    public async Task RealSelector_BackgroundAndInteractiveUseSharedGate_TimeoutIsBackoff(string type)
    {
        var cfg = new PluginConfiguration { IndexerSearchBudgetMs = 10000, IndexerProbeTimeoutSeconds = 5, FastIndexerEarlyReturnEnabled = false };
        using var selector = new MagnetSelector(new[] { Client("Torrentio", _blue) },
            new QualityScorer(NullLogger<QualityScorer>.Instance), NullLogger<MagnetSelector>.Instance, () => cfg);
        await selector.ProbeAvailabilityAsync(42, "tt1234567", type, 1, 2, "Fixture", 2020, default);
        // Background oracle does not reserve interactive capacity.
        Assert.Equal(0L, await SqlAsync("SELECT interactive_until FROM indexer_health"));
        await selector.SelectRankedBackgroundAsync(42, "tt1234567", type, 1, 2, "Fixture", 2020, default);
        Assert.Equal(0L, await SqlAsync("SELECT interactive_until FROM indexer_health"));
        await selector.ProbeAsync(42, "tt1234567", type, 1, 2, "Fixture", 2020, default);
        Assert.True(Convert.ToInt64(await SqlAsync("SELECT interactive_until FROM indexer_health")) > 0);
        // A collector timeout (not a user cancellation) must penalize a hung upstream.
        await SqlAsync("UPDATE indexer_health SET next_at = 0");
        _server.DelayMs = 5500;
        var result = await selector.ProbeAsync(42, "tt1234567", type, 1, 2, "Fixture", 2020, default);
        Assert.Equal(MagnetProbeOutcome.IndeterminateTransient, result.Outcome);
        Assert.Equal("timeout", await SqlAsync("SELECT last_status FROM indexer_health"));
        await Assert.ThrowsAsync<IndexerTransientException>(() => selector.SelectRankedBackgroundAsync(42, "tt1234567", type, 1, 2, "Fixture", 2020, default));
        var skipped = await Assert.ThrowsAsync<IndexerTransientException>(() => Search(Client("Torrentio", _green), Query(type)));
        Assert.Equal("skipped_backoff", skipped.HealthStatus);
    }

    [Fact]
    public void ProductionRegistration_RequiresTheSharedGate()
    {
        var services = new ServiceCollection();
        new PluginServiceRegistrator().RegisterServices(services, Mock.Of<IServerApplicationHost>());
        services.AddSingleton(_blue);
        services.AddLogging();
        using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IndexerHealthGate>());
        Assert.NotNull(sp.GetRequiredService<ProwlarrClient>());
        Assert.NotNull(sp.GetRequiredService<TorrentioClient>());
        Assert.All(typeof(TorrentioClient).GetConstructors(), c => Assert.Contains(c.GetParameters(), p => p.ParameterType == typeof(IndexerHealthGate)));
        Assert.All(typeof(ProwlarrClient).GetConstructors(), c => Assert.Contains(c.GetParameters(), p => p.ParameterType == typeof(IndexerHealthGate)));
    }

    [Fact]
    public async Task Expand_IsIdempotent_Concurrent_OldNewOverlap_AndIndexed()
    {
        // Real previous plugin assembly (built from pinned parent source by the harness).
        var oldPath = Environment.GetEnvironmentVariable("PHANTOM_TEST_OLD_PLUGIN");
        Assert.False(string.IsNullOrEmpty(oldPath), "Old plugin build is mandatory");
        var context = new AssemblyLoadContext("old-plugin-" + _schema, isCollectible: true);
        context.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies.FirstOrDefault(a => a.GetName().Name == name.Name);
        var oldAssembly = context.LoadFromAssemblyPath(oldPath!);
        var oldType = oldAssembly.GetType(typeof(PhantomDb).FullName!)!;
        Assert.Equal(24, oldType.GetField("CurrentSchemaVersion")!.GetRawConstantValue());
        var shapeSql = "SELECT string_agg(column_name || ':' || data_type || ':' || is_nullable || ':' || COALESCE(column_default, ''), ',' ORDER BY ordinal_position) FROM information_schema.columns WHERE table_schema = '" + _schema + "' AND table_name = 'indexer_health'";
        var freshShape = await SqlAsync(shapeSql);
        // Recreate the *real old* v24 schema, not a hand-written approximation.
        _blue.Dispose(); _green.Dispose();
        await SqlAsync($"DROP SCHEMA {_schema} CASCADE; CREATE SCHEMA {_schema}");
        using var old = (IDisposable)oldType.GetMethod("CreatePostgres")!.Invoke(null, new object[] { _dsn })!;
        var set = oldType.GetMethod("SetMetaAsync")!;
        async Task OldWrite(string value) => await (Task)set.Invoke(old, new object[] { "overlap", value, CancellationToken.None })!;
        await OldWrite("before");
        Assert.Equal(24, await SqlAsync("SELECT version FROM phantom_schema_meta"));
        _blue = PhantomDb.CreatePostgres(_dsn); _green = PhantomDb.CreatePostgres(_dsn);
        await Task.WhenAll(_blue.RegisterIndexerAsync("fixture", false, 0, default),
            _green.RegisterIndexerAsync("fixture", false, 0, default), OldWrite("during"));
        Assert.Equal(25, await SqlAsync("SELECT version FROM phantom_schema_meta"));
        Assert.Equal(freshShape, await SqlAsync(shapeSql));
        Assert.Equal(1L, await SqlAsync("SELECT count(*) FROM phantom_schema_meta"));
        // Restart old build against NEW schema: minimum-version gate must tolerate it.
        using var restarted = (IDisposable)oldType.GetMethod("CreatePostgres")!.Invoke(null, new object[] { _dsn })!;
        await (Task)set.Invoke(restarted, new object[] { "overlap", "after", CancellationToken.None })!;
        Assert.Equal("after", await _green.GetMetaAsync("overlap", default));
        await Search(Client("Torrentio", _blue), Query("movie"));
        Assert.Equal(25, await SqlAsync("SELECT version FROM phantom_schema_meta"));
        var statements = PhantomDb.GetExpandMigrationStatements("v24_v25_indexer_health");
        foreach (var ddl in statements) { await SqlAsync(ddl); await SqlAsync(ddl); }
        // Production-shaped health registry, with a real planner assertion (no forced enable_seqscan=off).
        await SqlAsync("INSERT INTO indexer_health(indexer_key) SELECT 'other-' || g FROM generate_series(1,20000) g; ANALYZE indexer_health");
        var plan = (string)(await SqlAsync("EXPLAIN (FORMAT JSON) UPDATE indexer_health SET skipped = skipped + 1 WHERE indexer_key = 'fixture'"))!;
        Assert.Contains("Index Scan", plan);
        Assert.Contains("indexer_health_pkey", plan);
        Assert.DoesNotContain("Seq Scan", plan);
        context.Unload();
    }

    private sealed class FakeIndexer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _loop;
        private readonly ConcurrentBag<Task> _handlers = new();
        private int _active;
        public string Url { get; }
        public int Status { get; set; } = 200;
        public string? Retry { get; set; }
        public DateTimeOffset? DisabledTill { get; set; }
        public int DelayMs { get; set; }
        public bool DownloadLinks { get; set; }
        public int StatusRequests;
        public int MaxConcurrent;
        public ConcurrentQueue<(string Path, DateTimeOffset At)> Requests { get; } = new();

        public FakeIndexer()
        {
            var port = new TcpListener(IPAddress.Loopback, 0);
            port.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)port.LocalEndpoint).Port;
            port.Stop();
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _loop = RunAsync();
        }

        private async Task RunAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                    _handlers.Add(HandleAsync(await _listener.GetContextAsync().WaitAsync(_stop.Token)));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            var path = ctx.Request.Url!.AbsolutePath;
            string body;
            if (path == "/api/v1/indexerstatus")
            {
                Interlocked.Increment(ref StatusRequests);
                body = DisabledTill is { } until ? $"[{{\"indexerId\":1,\"disabledTill\":\"{until:O}\"}}]" : "[]";
            }
            else if (path == "/api/v1/indexer")
                body = "[{\"id\":1,\"name\":\"fixture-indexer\",\"enable\":true,\"protocol\":\"torrent\"}]";
            else
            {
                Requests.Enqueue((path, DateTimeOffset.UtcNow));
                var active = Interlocked.Increment(ref _active);
                MaxConcurrent = Math.Max(active, MaxConcurrent);
                await Task.Delay(DelayMs);
                Interlocked.Decrement(ref _active);
                ctx.Response.StatusCode = Status;
                if (Retry is not null) ctx.Response.Headers["Retry-After"] = Retry;
                body = path.StartsWith("/stream/", StringComparison.Ordinal)
                    ? "{\"streams\":[{\"infoHash\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"title\":\"Fixture 1080p 👤 20\"}]}"
                    : "<rss><channel><item><title>Fixture 1080p</title><link>magnet:?xt=urn:btih:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa</link></item></channel></rss>";
                if (DownloadLinks)
                {
                    body = $"<rss><channel><item><title>Fixture 1080p</title><link>{Url}/download</link></item></channel></rss>";
                    if (path == "/download")
                    {
                        ctx.Response.StatusCode = 429;
                        ctx.Response.Headers["Retry-After"] = "6";
                    }
                }
            }
            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentLength64 = bytes.Length;
            try { await ctx.Response.OutputStream.WriteAsync(bytes); }
            catch (HttpListenerException) { /* cancelled client disconnected */ }
            finally { ctx.Response.Close(); }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            await _loop;
            await Task.WhenAll(_handlers);
            _listener.Close(); _stop.Dispose();
        }
    }
}
