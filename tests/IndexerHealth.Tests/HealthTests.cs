extern alias legacy;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Jellyfin.Plugin.PhantomLibrary.State;
using Jellyfin.Plugin.PhantomLibrary.State.Db;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using OldDb = legacy::Jellyfin.Plugin.PhantomLibrary.State.PhantomDb;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace IndexerHealth.Tests;

public sealed class HealthTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static IndexerQuery Query(string type, bool interactive = true, string imdb = "tt1234567") => new()
    {
        Type = type, Imdb = imdb, SeriesImdb = imdb, Title = "Example", Season = 1, Episode = 2, Interactive = interactive,
    };

    [Theory]
    [InlineData("movie", false)]
    [InlineData("episode", true)]
    public async Task RetryAfter_SkipsOtherColour_Recovers_AndEmitsMetrics(string type, bool dateHeader)
    {
        await using var rig = await Rig.Create();
        using var server = new IndexerServer { Status = 429, DateHeader = dateHeader };
        using var http = new HttpClient();
        var events = new ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) => { if (instrument.Meter.Name == "Phantom.Indexers") l.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<long>((i, _, tags, _) =>
        {
            if (i.Name == "phantom_indexer_health_total")
                foreach (var tag in tags) if (tag.Key == "status") events.Add((string)tag.Value!);
        });
        listener.Start();
        var blue = rig.Torrentio(http, server.Url, rig.Blue);
        await Assert.ThrowsAsync<IndexerTransientException>(() => blue.SearchAsync(Query(type), Ct));
        server.Status = 200;
        // Recreate the other process's repository/client: no shared memory bucket.
        using var restartedGreen = PhantomDb.CreatePostgres(rig.Dsn);
        var green = rig.Torrentio(http, server.Url + "/different-config", restartedGreen);
        var sw = Stopwatch.StartNew();
        var deferred = await Assert.ThrowsAsync<IndexerTransientException>(() => green.SearchAsync(Query(type, false), Ct));
        Assert.Equal("skip_backoff", deferred.HealthStatus);
        Assert.True(sw.ElapsedMilliseconds < 800);
        Assert.Single(server.Calls);
        Assert.Equal(1L, await rig.Scalar("SELECT throttles FROM indexer_health WHERE throttles>0"));
        await Task.Delay(3200);
        Assert.Single(await green.SearchAsync(Query(type), Ct));
        Assert.Equal(2, server.Calls.Count);
        Assert.Contains("429", events);
        Assert.Contains("skip_backoff", events);
        Assert.Contains("ok", events);
        Assert.Equal(0L, await rig.Scalar("SELECT failures::bigint FROM indexer_health WHERE requests=2"));
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("episode")]
    public async Task SharedBudget_AndInteractivePreemption(string type)
    {
        await using var rig = await Rig.Create();
        using var server = new IndexerServer();
        using var http = new HttpClient();
        var blue = rig.Torrentio(http, server.Url, rig.Blue);
        var green = rig.Torrentio(http, server.Url, rig.Green);
        await Task.WhenAll(blue.SearchAsync(Query(type), Ct), green.SearchAsync(Query(type), Ct));
        var calls = server.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.True((calls[1].At - calls[0].At).TotalMilliseconds >= 900, "both colours spent the same token");
        await rig.Sql("UPDATE indexer_health SET next_at=0, interactive_until=0");
        server.Calls.Clear();
        var background = blue.SearchAsync(Query(type, false, "tt1111111"), Ct);
        await Task.Delay(30);
        await green.SearchAsync(Query(type, true, "tt2222222"), Ct);
        try { await background; }
        catch (IndexerTransientException ex) { Assert.StartsWith("skip_", ex.HealthStatus); }
        calls = server.Calls.ToArray();
        Assert.Contains("tt2222222", calls[0].Path);
        if (calls.Length > 1)
        {
            Assert.Contains("tt1111111", calls[1].Path);
            Assert.True((calls[1].At - calls[0].At).TotalMilliseconds >= 900);
        }
        Assert.Equal(server.Calls.Count + 2L, await rig.Scalar("SELECT requests FROM indexer_health"));
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("episode")]
    public async Task ProwlarrDisabledUntil_DoesNotDelayHealthyIndexer_AndRecovers(string type)
    {
        await using var rig = await Rig.Create();
        using var server = new IndexerServer { DisabledUntil = DateTimeOffset.UtcNow.AddSeconds(3) };
        using var http = new HttpClient();
        var blue = rig.Prowlarr(http, server.Url, rig.Blue);
        var green = rig.Prowlarr(http, server.Url, rig.Green);
        var q = Query(type) with { Imdb = null };
        var discovered = await blue.DiscoverIndexersAsync(q, Ct);
        Assert.Equal(2, discovered.Count);
        var sw = Stopwatch.StartNew();
        var disabled = blue.SearchIndexerAsync(discovered[0], q, Ct);
        var healthy = green.SearchIndexerAsync(discovered[1], q, Ct);
        Assert.Equal("skip_backoff", (await Assert.ThrowsAsync<IndexerTransientException>(() => disabled)).HealthStatus);
        Assert.Single(await healthy);
        Assert.True(sw.ElapsedMilliseconds < 1500);
        Assert.DoesNotContain(server.Calls, c => c.Path.StartsWith("/1/api", StringComparison.Ordinal));
        Assert.Single(server.Calls, c => c.Path == "/api/v1/indexerstatus");
        await Task.Delay(3200);
        Assert.Single(await green.SearchIndexerAsync(discovered[0], q, Ct));
        Assert.Single(server.Calls, c => c.Path.StartsWith("/1/api", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ProwlarrMovie_SubqueriesEachSpendBudget_429StopsSibling()
    {
        await using var rig = await Rig.Create();
        using var server = new IndexerServer { Status = 429 };
        using var http = new HttpClient();
        var client = rig.Prowlarr(http, server.Url, rig.Blue);
        await Assert.ThrowsAsync<IndexerTransientException>(() => client.SearchIndexerAsync(new(1, "first", "torrent"), Query("movie"), Ct));
        Assert.Single(server.Calls, c => c.Path.StartsWith("/1/api", StringComparison.Ordinal));
        server.Status = 200;
        await Task.Delay(3200);
        Assert.Single(await client.SearchIndexerAsync(new(1, "first", "torrent"), Query("movie"), Ct));
        var calls = server.Calls.Where(c => c.Path.StartsWith("/1/api", StringComparison.Ordinal)).ToArray();
        Assert.Equal(3, calls.Length);
        Assert.True((calls[2].At - calls[1].At).TotalMilliseconds >= 900);
    }

    [Theory]
    [InlineData("movie", false)]
    [InlineData("episode", false)]
    [InlineData("movie", true)]
    [InlineData("episode", true)]
    public async Task Redirect429_PreservesDirectSiblings_ButNotFalseEmpty(string type, bool directSibling)
    {
        await using var rig = await Rig.Create();
        using var server = new IndexerServer { Redirects = true, DirectSibling = directSibling };
        using var http = new HttpClient();
        var client = rig.Prowlarr(http, server.Url, rig.Blue);
        var query = Query(type) with { Imdb = null };
        var indexer = new DiscoveredIndexer(1, "first", "torrent");
        if (directSibling)
            Assert.Single(await client.SearchIndexerAsync(indexer, query, Ct));
        else
            Assert.Equal("429", (await Assert.ThrowsAsync<IndexerTransientException>(() => client.SearchIndexerAsync(indexer, query, Ct))).HealthStatus);
        Assert.Equal(1L, await rig.Scalar("SELECT throttles FROM indexer_health WHERE throttles>0"));
        Assert.Single(server.Calls, c => c.Path == "/download");
        Assert.Equal("skip_backoff", (await Assert.ThrowsAsync<IndexerTransientException>(() =>
            rig.Prowlarr(http, server.Url, rig.Green).SearchIndexerAsync(indexer, query, Ct))).HealthStatus);
        Assert.Single(server.Calls, c => c.Path.StartsWith("/1/api", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepeatedFailures_ExponentiallyBackOffWithCap_StaleCompletionIsFenced()
    {
        await using var rig = await Rig.Create();
        using var server = new IndexerServer { Status = 504 };
        using var http = new HttpClient();
        var blue = rig.Torrentio(http, server.Url, rig.Blue);
        for (var n = 0; n < 11; n++)
        {
            await Assert.ThrowsAsync<IndexerTransientException>(() => blue.SearchAsync(Query("episode"), Ct));
            var remaining = await rig.Scalar("SELECT backoff_until-CAST(EXTRACT(EPOCH FROM clock_timestamp())*1000 AS BIGINT) FROM indexer_health");
            Assert.InRange(remaining, Math.Min(300000, 1000L << n) - 800, Math.Min(300000, 1000L << n));
            // Advance the durable deadlines instead of sleeping for a five-minute cap.
            await rig.Sql("UPDATE indexer_health SET backoff_until=0, next_at=0");
        }
        var key = IndexerHealthGate.Key("Torrentio", server.Url);
        var store = new IndexerHealthStore(rig.Green);
        Assert.Equal("admitted", await store.TryAcquireAsync(key, "successor", true, 1000, Ct));
        await store.CompleteAsync(key, "dead-owner", "ok", 0, 0, Ct);
        Assert.Equal(11L, await rig.Scalar("SELECT failures::bigint FROM indexer_health"));
        Assert.Equal(1L, await rig.Scalar("SELECT count(*) FROM indexer_health WHERE owner='successor'"));
        await store.CompleteAsync(key, "successor", "ok", 1, 0, Ct);
        Assert.Equal(0L, await rig.Scalar("SELECT failures::bigint FROM indexer_health"));
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("episode")]
    public async Task InFlightBackgroundIsPreemptedWithoutRefund_TimeoutBacksOff(string type)
    {
        await using var rig = await Rig.Create();
        using var server = new IndexerServer { SlowBackground = true };
        using var http = new HttpClient();
        var blue = rig.Torrentio(http, server.Url, rig.Blue);
        var green = rig.Torrentio(http, server.Url, rig.Green);
        var background = blue.SearchAsync(Query(type, false, "tt1111111"), Ct);
        await server.BackgroundStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var interactive = green.SearchAsync(Query(type), Ct);
        Assert.Equal("skip_preempted", (await Assert.ThrowsAsync<IndexerTransientException>(() => background.WaitAsync(TimeSpan.FromSeconds(5)))).HealthStatus);
        Assert.Single(await interactive.WaitAsync(TimeSpan.FromSeconds(5)));
        // The blocked HTTP response has NEVER been released: progress can only
        // come from genuine cancellation/preemption, not a lucky 3s timer race.
        Assert.False(server.ReleaseBackground.Task.IsCompleted);
        var calls = server.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.True((calls[1].At - calls[0].At).TotalMilliseconds >= 900);
        Assert.Equal(0L, await rig.Scalar("SELECT errors FROM indexer_health"));
        Assert.Equal(2L, await rig.Scalar("SELECT requests FROM indexer_health"));
        await rig.Sql("UPDATE indexer_health SET next_at=0, interactive_until=0");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => green.SearchAsync(
            Query(type, true, "tt1111111") with { Deadline = DateTimeOffset.UtcNow.AddMilliseconds(400) }, Ct));
        Assert.Equal(1L, await rig.Scalar("SELECT errors FROM indexer_health"));
        Assert.Equal("skip_backoff", (await Assert.ThrowsAsync<IndexerTransientException>(() => blue.SearchAsync(Query(type), Ct))).HealthStatus);
    }

    [Fact]
    public async Task OldNewOverlap_IdempotentExpand_ForwardTolerance_AndIndexedAdmission()
    {
        await using var rig = await Rig.Create(initialize: false);
        using var oldBlue = OldDb.CreatePostgres(rig.Dsn);
        await oldBlue.SetMetaAsync("old", "before-expand", Ct);
        Assert.Equal(24L, await rig.Scalar("SELECT version::bigint FROM phantom_schema_meta"));
        await Task.WhenAll(rig.Blue.SetMetaAsync("new", "blue", Ct), rig.Green.SetMetaAsync("new2", "green", Ct));
        Assert.Equal(25L, await rig.Scalar("SELECT version::bigint FROM phantom_schema_meta"));
        using var oldRestarted = OldDb.CreatePostgres(rig.Dsn);
        Assert.Equal("blue", await oldRestarted.GetMetaAsync("new", Ct));
        await oldRestarted.SetMetaAsync("old", "after-expand", Ct);
        Assert.Equal("after-expand", await rig.Green.GetMetaAsync("old", Ct));
        await using var conn = new NpgsqlConnection(rig.Dsn);
        await conn.OpenAsync();
        Assert.False(await SchemaExpandMigrator.ApplyAsync(conn, "v24_v25_indexer_health", new[] { IndexerHealthStore.TableSql }, Ct));
        await rig.Sql("UPDATE phantom_schema_meta SET version=26; ALTER TABLE indexer_health ADD COLUMN future TEXT DEFAULT 'unknown'");
        using var newRestarted = PhantomDb.CreatePostgres(rig.Dsn);
        var store = new IndexerHealthStore(newRestarted);
        Assert.Equal("admitted", await store.TryAcquireAsync("target", "owner", true, 1000, Ct));
        await rig.Sql("INSERT INTO indexer_health (indexer_key) SELECT 'other-' || n FROM generate_series(1,50000) n; ANALYZE indexer_health");
        await using var explain = conn.CreateCommand();
        explain.CommandText = "EXPLAIN (ANALYZE, BUFFERS) UPDATE indexer_health SET next_at=next_at WHERE indexer_key='target' AND backoff_until=0 RETURNING owner";
        await using var reader = await explain.ExecuteReaderAsync();
        var plan = new StringBuilder();
        while (await reader.ReadAsync()) plan.AppendLine(reader.GetString(0));
        Assert.Contains("Index Scan using indexer_health_pkey", plan.ToString());
        Assert.DoesNotContain("Seq Scan", plan.ToString());
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("episode")]
    public async Task RealSelectorAndBuilder_PropagatePriority_AndSkippedOracleRemainsTransient(string type)
    {
        await using var rig = await Rig.Create();
        using var server = new IndexerServer { Status = 429 };
        using var http = new HttpClient();
        var priorities = new ConcurrentQueue<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) => { if (i.Name == "phantom_indexer_health_total") l.EnableMeasurementEvents(i); };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags) if (tag.Key == "priority") priorities.Enqueue((string)tag.Value!);
        });
        listener.Start();
        var cfg = new PluginConfiguration { FastIndexerEarlyReturnEnabled = false };
        using var selector = new MagnetSelector(new[] { rig.Torrentio(http, server.Url, rig.Blue) },
            new QualityScorer(NullLogger<QualityScorer>.Instance), NullLogger<MagnetSelector>.Instance, () => cfg);
        Assert.Equal(MagnetProbeOutcome.IndeterminateTransient,
            (await selector.ProbeAsync(1, "tt1234567", type, 1, 2, "Example", 2020, Ct)).Outcome);
        Assert.Equal(MagnetProbeOutcome.IndeterminateTransient,
            (await selector.ProbeAvailabilityAsync(1, "tt1234567", type, 1, 2, "Example", 2020, Ct)).Outcome);
        var builder = new MagnetCacheBuilder(rig.Green,
            (_, _, _, _, _) => Task.FromResult<MagnetCacheItemMeta?>(new("tt1234567", "Example", 2020)),
            selector.SelectRankedAsync, NullLogger<MagnetCacheBuilder>.Instance, () => cfg, selector.ProbeAsync);
        await rig.Green.EnqueueMagnetCacheJobAsync(1, type, type == "movie" ? -1 : 1, type == "movie" ? -1 : 2, "test", 0, Ct);
        Assert.NotNull(await builder.ProcessNextAsync(Ct));
        await Assert.ThrowsAsync<MagnetCacheTransientBuildException>(() => builder.BuildSynchronousAsync(1, type, 1, 2, "test", cfg, Ct));
        Assert.Equal(new[] { "interactive", "background", "background", "interactive" }, priorities.ToArray());
        Assert.Single(server.Calls);
    }

    [Fact]
    public async Task CancelledWait_DoesNotSpendBudget_AndBackgroundScopeIsolated()
    {
        await using var rig = await Rig.Create();
        using var server = new IndexerServer();
        using var http = new HttpClient();
        var blue = rig.Torrentio(http, server.Url, rig.Blue);
        await blue.SearchAsync(Query("movie"), Ct);
        using var cancellation = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blue.SearchAsync(Query("episode", false), cancellation.Token));
        Assert.Single(server.Calls);
        Assert.True(IndexerSearchScope.IsInteractive);
        await Task.Run(() => { using var scope = new IndexerSearchScope(); Assert.False(IndexerSearchScope.IsInteractive); });
        Assert.True(IndexerSearchScope.IsInteractive);
    }

    private sealed class Rig : IAsyncDisposable
    {
        private readonly string _baseDsn;
        private readonly string _schema = "health_" + Guid.NewGuid().ToString("N");
        public string Dsn { get; }
        public PhantomDb Blue { get; }
        public PhantomDb Green { get; }
        private Rig()
        {
            _baseDsn = Environment.GetEnvironmentVariable("PHANTOM_TEST_POSTGRES_DSN") ?? throw new InvalidOperationException("PostgreSQL is required, never skipped");
            Dsn = new NpgsqlConnectionStringBuilder(_baseDsn) { SearchPath = _schema }.ConnectionString;
            Blue = PhantomDb.CreatePostgres(Dsn);
            Green = PhantomDb.CreatePostgres(Dsn);
        }
        public static async Task<Rig> Create(bool initialize = true)
        {
            var rig = new Rig();
            await using var conn = new NpgsqlConnection(rig._baseDsn);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE SCHEMA " + rig._schema;
            await cmd.ExecuteNonQueryAsync();
            if (initialize) await rig.Blue.GetMetaAsync("init", Ct);
            return rig;
        }
        public TorrentioClient Torrentio(HttpClient http, string url, PhantomDb db) => new(http, NullLogger<TorrentioClient>.Instance, () => url, new IndexerHealthGate(new IndexerHealthStore(db)));
        public ProwlarrClient Prowlarr(HttpClient http, string url, PhantomDb db) => new(http, NullLogger<ProwlarrClient>.Instance, () => (url, "fixture"), health: new IndexerHealthGate(new IndexerHealthStore(db)));
        public async Task Sql(string sql)
        {
            await using var conn = new NpgsqlConnection(Dsn);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync();
        }
        public async Task<long> Scalar(string sql)
        {
            await using var conn = new NpgsqlConnection(Dsn);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(sql, conn);
            return Convert.ToInt64(await cmd.ExecuteScalarAsync());
        }
        public async ValueTask DisposeAsync()
        {
            Blue.Dispose(); Green.Dispose();
            await using var conn = new NpgsqlConnection(_baseDsn);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("DROP SCHEMA " + _schema + " CASCADE", conn);
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private sealed class IndexerServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        private readonly ConcurrentBag<Task> _responses = new();
        public TaskCompletionSource BackgroundStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBackground { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SlowBackground { get; set; }
        public bool Redirects { get; set; }
        public bool DirectSibling { get; set; }
        public ConcurrentQueue<(string Path, DateTimeOffset At)> Calls { get; } = new();
        public string Url { get; }
        public int Status { get; set; } = 200;
        public bool DateHeader { get; set; }
        public DateTimeOffset? DisabledUntil { get; set; }
        public IndexerServer()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            var port = ((IPEndPoint)socket.LocalEndpoint).Port;
            socket.Stop();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _loop = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext context;
                    try { context = await _listener.GetContextAsync(); }
                    catch (HttpListenerException) { break; }
                    catch (ObjectDisposedException) { break; }
                    _responses.Add(ServeAsync(context));
                }
            });
        }
        private async Task ServeAsync(HttpListenerContext context)
        {
            var path = context.Request.RawUrl!;
            Calls.Enqueue((path, DateTimeOffset.UtcNow));
            if (SlowBackground && path.Contains("tt1111111", StringComparison.Ordinal))
            {
                BackgroundStarted.TrySetResult();
                await ReleaseBackground.Task;
            }

            string body;
            if (path == "/api/v1/indexer") body = "[{\"id\":1,\"name\":\"first\",\"enable\":true},{\"id\":2,\"name\":\"healthy\",\"enable\":true}]";
            else if (path == "/api/v1/indexerstatus") body = DisabledUntil is { } until
                ? $"[{{\"indexerId\":1,\"disabledTill\":\"{until:O}\"}}]" : "[]";
            else if (path == "/download")
            {
                context.Response.StatusCode = 429;
                context.Response.Headers["Retry-After"] = "3";
                body = "retry later";
            }
            else if (Redirects)
            {
                var direct = DirectSibling ? "<item><title>Direct</title><link>magnet:?xt=urn:btih:0123456789012345678901234567890123456789</link></item>" : "";
                body = $"<rss><channel><item><title>Redirect</title><link>{Url}/download</link></item>{direct}</channel></rss>";
            }
            else
            {
                context.Response.StatusCode = Status;
                if (Status == 429) context.Response.Headers["Retry-After"] = DateHeader ? DateTimeOffset.UtcNow.AddSeconds(3).ToString("R") : "3";
                body = path.Contains("/stream/", StringComparison.Ordinal)
                    ? "{\"streams\":[{\"infoHash\":\"0123456789012345678901234567890123456789\",\"title\":\"Example\"}]}"
                    : "<rss><channel><item><title>Example</title><link>magnet:?xt=urn:btih:0123456789012345678901234567890123456789</link></item></channel></rss>";
            }

            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentLength64 = bytes.Length;
            try
            {
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
            catch (HttpListenerException) when (SlowBackground) { /* expected peer cancellation */ }
            catch (ObjectDisposedException) when (SlowBackground) { /* listener disposed after cancellation */ }
        }
        public void Dispose()
        {
            ReleaseBackground.TrySetResult();
            Task.WhenAll(_responses).GetAwaiter().GetResult();
            _listener.Close();
            _loop.GetAwaiter().GetResult();
        }
    }
}
