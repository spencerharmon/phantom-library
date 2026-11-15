using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Reflection;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.Scheduled;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Moq;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public sealed class AvailabilityProbeBatchWorkerTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("promotion")]
    [InlineData("fairness")]
    [InlineData("cancel")]
    [InlineData("unconsumed")]
    [InlineData("expired")]
    [InlineData("stolen")]
    [InlineData("yield")]
    public Task SqliteWorker(string scenario) => RunAsync(scenario, postgres: false);

    [WorkerBatchPostgresFact]
    public async Task PostgresWorker_RealBatchClaimAndProbeRegressions()
    {
        foreach (var scenario in new[] { "success", "promotion", "fairness", "cancel", "unconsumed", "expired", "stolen", "yield", "deadline" })
        {
            await RunAsync(scenario, postgres: true);
        }
    }

    private static async Task RunAsync(string scenario, bool postgres)
    {
        await using var rig = await WorkerRig.CreateAsync(postgres);
        switch (scenario)
        {
            case "success":
                await SuccessfulBatchAsync(rig);
                break;
            case "promotion":
                await PromotionAsync(rig);
                break;
            case "fairness":
                await FairnessAsync(rig);
                break;
            case "cancel":
                await CancellationAsync(rig);
                break;
            case "unconsumed":
            case "expired":
                await UnstartedLeaseAsync(rig, expired: scenario == "expired");
                break;
            case "stolen":
                await StolenLeaseAsync(rig);
                break;
            case "yield":
                await UserYieldAsync(rig);
                break;
            case "deadline":
                await LeaseDeadlineAsync(rig);
                break;
            default:
                throw new ArgumentException(scenario, nameof(scenario));
        }
    }

    private static async Task SuccessfulBatchAsync(WorkerRig rig)
    {
        rig.Config.AvailabilityMaxBatchSize = 5;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new ConcurrentQueue<(int Id, string Type)>();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var worker = rig.Worker(async (id, _, type, _, _, _, _, ct) =>
        {
            calls.Enqueue((id, type));
            if (calls.Count == 2) started.TrySetResult();
            await proceed.Task.WaitAsync(ct);
            return Available();
        });
        var movieVersion = rig.State.DataVersion(ChannelStateProvider.KindMovies);
        var showVersion = rig.State.DataVersion(ChannelStateProvider.KindShows);
        var tick = TickAsync(worker, stop.Token);
        try
        {
            await started.Task.WaitAsync(stop.Token);
            Assert.Equal(2, calls.Count);
            Assert.Equal(2, await rig.ScalarAsync("SELECT COUNT(*) FROM availability_items WHERE lease_owner IS NOT NULL"));
            var until = await rig.ScalarAsync("SELECT MAX(lease_until) FROM availability_items");
            Assert.InRange(until - DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 1, 60);
            proceed.TrySetResult();
            await tick.WaitAsync(stop.Token);
            Assert.Equal(5, calls.Count);
            Assert.Contains(calls, item => item.Type == "movie");
            Assert.Contains(calls, item => item.Type == "episode");
            Assert.NotEqual(movieVersion, rig.State.DataVersion(ChannelStateProvider.KindMovies));
            Assert.NotEqual(showVersion, rig.State.DataVersion(ChannelStateProvider.KindShows));
            foreach (var (id, type) in calls)
            {
                var number = type == "episode" ? 1 : -1;
                var row = await rig.Db.GetAvailabilityItemAsync(id, type, number, number, default);
                Assert.Equal("available", row!.Status);
                Assert.Null(row.LeaseOwner);
                Assert.Single(await rig.Db.ListSourceCandidatesAsync(id, type, number, number, "test", false, default));
                Assert.NotNull(await rig.Db.GetCachedMagnetAsync(new MagnetCacheKey(id, "tt" + id, type,
                    type == "episode" ? 1 : null, type == "episode" ? 1 : null, "test"), default));
            }
            Assert.Equal(0, await rig.ScalarAsync("SELECT COUNT(*) FROM availability_items WHERE lease_owner IS NOT NULL"));
        }
        finally
        {
            stop.Cancel();
            proceed.TrySetResult();
            await tick;
        }
    }

    private static async Task PromotionAsync(WorkerRig rig)
    {
        rig.Config.AvailabilityMaxBatchSize = 3;
        // First opportunity is movies; the next prefers episodes. An urgent
        // movie must nevertheless preempt that typed background preference.
        await rig.Db.SetAvailabilityPriorityAsync(101, "movie", -1, -1, PhantomDb.UserActivityPriority, default);
        await rig.Db.SetAvailabilityPriorityAsync(102, "movie", -1, -1, PhantomDb.UserActivityPriority, default);
        var calls = new ConcurrentQueue<int>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var worker = rig.Worker(async (id, _, _, _, _, _, _, ct) =>
        {
            calls.Enqueue(id);
            if (calls.Count == 2) started.TrySetResult();
            await proceed.Task.WaitAsync(ct);
            return Available();
        });
        var tick = TickAsync(worker, stop.Token);
        try
        {
            await started.Task.WaitAsync(stop.Token);
            Assert.All(calls, id => Assert.Contains(id, new[] { 101, 102 }));
            await rig.Db.SetAvailabilityPriorityAsync(104, "movie", -1, -1, PhantomDb.UserActivityPriority, default);
            proceed.TrySetResult();
            await tick.WaitAsync(stop.Token);
            Assert.Equal(104, calls.Last());
            Assert.Equal(3, calls.Count);
        }
        finally
        {
            stop.Cancel();
            proceed.TrySetResult();
            await tick;
        }
    }

    private static async Task FairnessAsync(WorkerRig rig)
    {
        rig.Config.AvailabilityMaxBatchSize = 1;
        var calls = new List<string>();
        using var worker = rig.Worker((_, _, type, _, _, _, _, _) =>
        {
            calls.Add(type);
            return Task.FromResult(Available());
        });
        for (var i = 0; i < 4; i++) await TickAsync(worker, default);
        Assert.Equal(new[] { "episode", "movie", "episode", "movie" }, calls);
    }

    private static async Task CancellationAsync(WorkerRig rig)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var worker = rig.Worker(async (_, _, _, _, _, _, _, ct) =>
        {
            if (Interlocked.Increment(ref count) == 2) started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Available();
        });
        var tick = TickAsync(worker, stop.Token);
        try
        {
            await started.Task.WaitAsync(stop.Token);
        }
        finally
        {
            stop.Cancel();
            await tick;
        }
        Assert.Equal(2, count);
        Assert.Equal(0, await rig.ScalarAsync("SELECT COUNT(*) FROM availability_items WHERE lease_owner IS NOT NULL"));
        Assert.Equal(2, await rig.ScalarAsync("SELECT SUM(attempt_count) FROM availability_items"));
        Assert.Equal(0, await rig.ScalarAsync("SELECT COUNT(*) FROM source_candidates"));
    }

    private static async Task UnstartedLeaseAsync(WorkerRig rig, bool expired)
    {
        var now = DateTimeOffset.UtcNow;
        var leases = await rig.Db.ClaimDueAvailabilityBatchAsync("old-worker", TimeSpan.FromMinutes(1),
            expired ? now.AddMinutes(-2) : now, "policy", 2, default);
        Assert.Equal(2, leases.Count);
        IReadOnlyList<AvailabilityItemRow> replacements = Array.Empty<AvailabilityItemRow>();
        if (expired)
        {
            replacements = await rig.Db.ClaimDueAvailabilityBatchAsync("new-worker", TimeSpan.FromMinutes(1), now,
                "policy", 2, default);
            Assert.Equal(2, replacements.Count);
        }
        using var stop = new CancellationTokenSource();
        if (!expired) stop.Cancel();
        var calls = 0;
        using var worker = rig.Worker((_, _, _, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(Available());
        });
        var tasks = leases.Select(lease => ClaimedAsync(worker, rig.Config, lease, stop.Token)).ToArray();
        if (expired) await Task.WhenAll(tasks);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.WhenAll(tasks));
        Assert.Equal(0, calls);
        Assert.Equal(0, await rig.ScalarAsync("SELECT COUNT(*) FROM source_candidates"));
        if (expired)
        {
            foreach (var replacement in replacements)
            {
                var row = await rig.Db.GetAvailabilityItemAsync(replacement.TmdbId, replacement.Type,
                    replacement.Season, replacement.Episode, default);
                Assert.Equal("new-worker", row!.LeaseOwner);
                await rig.Db.ReleaseAvailabilityLeaseAsync(replacement, default, unconsumed: true);
            }
        }
        else
        {
            Assert.Equal(0, await rig.ScalarAsync("SELECT SUM(attempt_count) FROM availability_items"));
            Assert.Equal(0, await rig.ScalarAsync("SELECT COUNT(*) FROM availability_items WHERE lease_owner IS NOT NULL"));
        }
    }

    private static async Task StolenLeaseAsync(WorkerRig rig)
    {
        rig.Config.AvailabilityMaxBatchSize = 1;
        var calls = 0;
        AvailabilityItemRow? replacement = null;
        using var worker = rig.Worker(async (id, _, type, season, episode, _, _, _) =>
        {
            calls++;
            var active = await rig.Db.GetAvailabilityItemAsync(id, type, season ?? -1, episode ?? -1, default);
            await rig.Db.ReleaseAvailabilityLeaseAsync(active!, default);
            await rig.Db.SetAvailabilityPriorityAsync(id, type, season ?? -1, episode ?? -1,
                PhantomDb.UserActivityPriority, default);
            replacement = (await rig.Db.ClaimDueAvailabilityBatchAsync("replacement", TimeSpan.FromMinutes(1),
                DateTimeOffset.UtcNow, "policy", 1, default)).Single();
            Assert.Equal(id, replacement.TmdbId);
            return Available();
        });
        await TickAsync(worker, default);
        Assert.Equal(1, calls);
        Assert.NotNull(replacement);
        Assert.Equal(0, await rig.ScalarAsync("SELECT COUNT(*) FROM source_candidates"));
        Assert.Equal(0, await rig.ScalarAsync("SELECT COUNT(*) FROM magnet_cache"));
        Assert.Equal(1, await rig.ScalarAsync("SELECT COUNT(*) FROM availability_items WHERE lease_owner='replacement'"));
        await rig.Db.ReleaseAvailabilityLeaseAsync(replacement!, default, unconsumed: true);
    }

    private static async Task UserYieldAsync(WorkerRig rig)
    {
        rig.Config.AvailabilityYieldToUserSeconds = 60;
        var calls = 0;
        using var worker = rig.Worker(async (_, _, _, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            await rig.Db.TouchUserActivityAsync(DateTimeOffset.UtcNow, default);
            return Available();
        });
        await TickAsync(worker, default);
        Assert.Equal(2, calls);
        Assert.Equal(0, await rig.ScalarAsync("SELECT COUNT(*) FROM availability_items WHERE lease_owner IS NOT NULL"));
    }

    private static async Task LeaseDeadlineAsync(WorkerRig rig)
    {
        rig.Config.AvailabilityMaxBatchSize = 2;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var cancelled = 0;
        var activeIds = new ConcurrentBag<int>();
        var began = DateTimeOffset.UtcNow;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(75));
        using var worker = rig.Worker(async (id, _, type, season, episode, _, _, ct) =>
        {
            activeIds.Add(id);
            if (Interlocked.Increment(ref calls) == 2) started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancelled);
                // Each probe is cancelled while its own lease still excludes
                // reclaim, not after waiting for another member of a FIFO.
                var row = await rig.Db.GetAvailabilityItemAsync(id, type, season ?? -1, episode ?? -1, default);
                Assert.NotNull(row!.LeaseOwner);
                throw;
            }
            return Available();
        });
        var tick = TickAsync(worker, stop.Token);
        try
        {
            await started.Task.WaitAsync(stop.Token);
            var otherLeases = await rig.Db.ClaimDueAvailabilityBatchAsync("competitor", TimeSpan.FromMinutes(1),
                DateTimeOffset.UtcNow, "policy", 2, default);
            foreach (var other in otherLeases)
            {
                Assert.DoesNotContain(other.TmdbId, activeIds);
                await rig.Db.ReleaseAvailabilityLeaseAsync(other, default, unconsumed: true);
            }
            await tick.WaitAsync(stop.Token);
            Assert.False(stop.IsCancellationRequested);
            Assert.Equal(2, cancelled);
            Assert.InRange((DateTimeOffset.UtcNow - began).TotalSeconds, 55, 70);
            Assert.Equal(0, await rig.ScalarAsync("SELECT COUNT(*) FROM availability_items WHERE lease_owner IS NOT NULL"));
            Assert.Equal(0, await rig.ScalarAsync("SELECT COUNT(*) FROM source_candidates"));
        }
        finally
        {
            stop.Cancel();
            await tick;
        }
    }

    private static MagnetProbeResult Available() => MagnetProbeResult.Available(new[]
    {
        new MagnetCandidate("magnet:?xt=urn:btih:0123456789012345678901234567890123456789",
            "0123456789012345678901234567890123456789", 5L * 1024 * 1024 * 1024, 40, "test"),
    });

    private static Task TickAsync(AvailabilityProbeWorker worker, CancellationToken ct)
        => (Task)typeof(AvailabilityProbeWorker).GetMethod("TickAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(worker, new object[] { ct })!;

    private static Task<bool> ClaimedAsync(AvailabilityProbeWorker worker, PluginConfiguration cfg,
        AvailabilityItemRow lease, CancellationToken ct)
        => (Task<bool>)typeof(AvailabilityProbeWorker).GetMethod("ProbeClaimedAvailabilityAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(worker, new object[] { cfg, lease, "policy", ct })!;

    private sealed class WorkerRig : IAsyncDisposable
    {
        private readonly bool _postgres;
        private readonly string _location;
        private readonly string _schema;
        private WorkerRig(bool postgres, string location, string schema)
        {
            _postgres = postgres;
            _location = location;
            _schema = schema;
            Db = postgres ? PhantomDb.CreatePostgres(location) : new PhantomDb(location);
            State = new ChannelStateProvider(Db);
        }

        public PhantomDb Db { get; }
        public ChannelStateProvider State { get; }
        public PluginConfiguration Config { get; } = new()
        {
            AvailabilityProbeEnabled = true,
            AvailabilityMaxBatchSize = 6,
            AvailabilityLeaseMinutes = 1,
            AvailabilityYieldToUserSeconds = 0,
            EpisodeReleaseDelayHours = 0,
            SourcePickerPreset = "test",
        };

        public static async Task<WorkerRig> CreateAsync(bool postgres)
        {
            var schema = "worker_batch_" + Guid.NewGuid().ToString("N");
            var location = Path.Combine(Path.GetTempPath(), schema + ".db");
            if (postgres)
            {
                var dsn = Environment.GetEnvironmentVariable("PHANTOM_TEST_POSTGRES_DSN")
                    ?? throw new InvalidOperationException("PostgreSQL worker tests require PHANTOM_TEST_POSTGRES_DSN");
                await using var conn = new NpgsqlConnection(dsn);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand($"CREATE SCHEMA {schema}", conn);
                await cmd.ExecuteNonQueryAsync();
                location = new NpgsqlConnectionStringBuilder(dsn) { SearchPath = schema }.ToString();
            }
            var rig = new WorkerRig(postgres, location, schema);
            try
            {
                await rig.Db.SetMetaAsync("__init__", "1", default);
                for (var i = 1; i <= 4; i++)
                {
                    foreach (var type in new[] { "movie", "series" })
                    {
                        var id = (type == "movie" ? 100 : 200) + i;
                        await rig.Db.UpsertCatalogueHitsAsync(new[]
                        {
                            new TmdbMetadataRow(id, type, "Test title", 2020, null, null, null, null,
                                null, null, null, DateTimeOffset.UtcNow),
                        }, 1, DateTimeOffset.UtcNow, default);
                        await rig.Db.SetImdbIdAsync(id, type, "tt" + id, default);
                        if (type == "series")
                        {
                            await rig.ExecuteAsync($"""
                                INSERT INTO series_episode_catalogue
                                    (series_tmdb_id,episode_tmdb_id,season,episode,air_date,first_seen_at,last_seen_at)
                                VALUES ({id},{id * 10},1,1,'2020-01-01',0,0);
                                INSERT INTO availability_items (tmdb_id,type,season,episode,status,next_check_at)
                                VALUES ({id},'episode',1,1,'unknown',0);
                                """);
                        }
                    }
                }
                await rig.ExecuteAsync("UPDATE series_expansion_state SET next_expand_at=2000000000; UPDATE availability_items SET next_check_at=0;");
                return rig;
            }
            catch
            {
                await rig.DisposeAsync();
                throw;
            }
        }

        public AvailabilityProbeWorker Worker(AvailabilityProbeWorker.ProbeDelegate probe)
        {
            var tmdb = Mock.Of<ITmdbClient>();
            var indexer = new Mock<IIndexerClient>();
            indexer.SetupGet(x => x.IsEnabled).Returns(true);
            indexer.SetupGet(x => x.IsAvailabilityOracle).Returns(true);
            var selector = new MagnetSelector(new[] { indexer.Object },
                new QualityScorer(NullLogger<QualityScorer>.Instance), NullLogger<MagnetSelector>.Instance, () => Config);
            return new AvailabilityProbeWorker(Db, selector,
                new TmdbExternalIdResolver(Db, tmdb, NullLogger<TmdbExternalIdResolver>.Instance),
                tmdb, State, new ProbeLogger(), () => Config, probe);
        }

        private DbConnection Connection() => _postgres
            ? new NpgsqlConnection(_location)
            : new SqliteConnection("Data Source=" + _location);

        public async Task<long> ScalarAsync(string sql)
        {
            await using var conn = Connection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        private async Task ExecuteAsync(string sql)
        {
            await using var conn = Connection();
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await cmd.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync()
        {
            Db.Dispose();
            if (_postgres)
            {
                await using var conn = new NpgsqlConnection(_location);
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand($"DROP SCHEMA {_schema} CASCADE", conn);
                await cmd.ExecuteNonQueryAsync();
            }
            else
            {
                using var pool = new SqliteConnection("Data Source=" + _location);
                SqliteConnection.ClearPool(pool);
                File.Delete(_location);
            }
        }
    }
}

internal sealed class ProbeLogger : ILogger<AvailabilityProbeWorker>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        => Console.WriteLine($"{level}: {formatter(state, error)} {error}");
}

public sealed class WorkerBatchPostgresFactAttribute : FactAttribute
{
    public WorkerBatchPostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PHANTOM_TEST_POSTGRES_DSN")))
        {
            Skip = "Requires a disposable PostgreSQL server in PHANTOM_TEST_POSTGRES_DSN";
        }
    }
}
