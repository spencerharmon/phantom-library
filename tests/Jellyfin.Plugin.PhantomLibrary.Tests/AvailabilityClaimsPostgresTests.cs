using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.Scheduled;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Jellyfin.Plugin.PhantomLibrary.State;
using Jellyfin.Plugin.PhantomLibrary.State.Db;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

[CollectionDefinition("Availability claims PostgreSQL", DisableParallelization = true)]
public sealed class AvailabilityClaimsPostgresCollection;

[Collection("Availability claims PostgreSQL")]
public sealed class AvailabilityClaimsPostgresTests(ITestOutputHelper output)
{
    private const int Rows = 4_000_000;
    private readonly string _schema = "claims_" + Guid.NewGuid().ToString("N");
    private readonly string _evidence = Environment.GetEnvironmentVariable("PHANTOM_TEST_RESULTS")
        ?? throw new InvalidOperationException("PHANTOM_TEST_RESULTS required");
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);
    private NpgsqlConnection _connection = null!;
    private string _dsn = null!;

    private async Task<object?> Sql(string sql)
    {
        // Fixture seeding and clone rehearsal touch 4M rows on shared CI disk.
        // This is NOT the claim budget: every actual claim EXPLAIN below still
        // unconditionally rejects >=5ms, including physical reads, without retry.
        await using var command = new NpgsqlCommand(sql, _connection) { CommandTimeout = 1200 };
        return await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task FourMillionRows_IndexedClaims_Overlap_Reschedule_Worker()
    {
        var supplied = Environment.GetEnvironmentVariable("PHANTOM_TEST_POSTGRES_DSN");
        Assert.False(string.IsNullOrWhiteSpace(supplied), "PostgreSQL is mandatory, never skipped");
        _dsn = new NpgsqlConnectionStringBuilder(supplied) { SearchPath = _schema, MaxPoolSize = 20 }.ConnectionString;
        await using var connection = new NpgsqlConnection(_dsn);
        _connection = connection;
        await connection.OpenAsync();
        await Sql($"CREATE SCHEMA {_schema}");
        try
        {
            // Synthetic clone of the real v24 schema and its old writer shape.
            var legacy = (string)typeof(PhantomDb).GetField("SchemaV10Sql", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
            await Sql(legacy);
            await Sql("CREATE TABLE phantom_schema_meta(version integer NOT NULL); INSERT INTO phantom_schema_meta VALUES(24)");
            var future = Now.AddDays(30).ToUnixTimeSeconds();
            await Sql($@"INSERT INTO availability_items(tmdb_id,type,season,episode,status,checked_at,next_check_at,
priority,probe_policy_hash,candidate_magnet,candidate_info_hash,candidate_size,candidate_seeders,candidate_indexer,candidate_source)
SELECT CASE WHEN n<=1000000 THEN n ELSE 1000001+(n-1000001)/100 END,
CASE WHEN n<=1000000 THEN 'movie' ELSE 'episode' END,
CASE WHEN n<=1000000 THEN -1 ELSE 1+(n-1000001)%100/10 END,
CASE WHEN n<=1000000 THEN -1 ELSE 1+(n-1000001)%10 END,
CASE n%6 WHEN 3 THEN 'available' WHEN 4 THEN 'unavailable' ELSE 'unknown' END,
CASE WHEN n%6=2 THEN NULL ELSE {Now.AddDays(-1).ToUnixTimeSeconds()} END,
{future}+n%86400,CASE n%6 WHEN 0 THEN 100 WHEN 1 THEN 50 ELSE 0 END,'stable',
'magnet:?xt=urn:btih:'||md5(n::text)||'&dn=Realistic+Catalogue+Release+1080p',md5(n::text),1500000000,n%120,'fixture-indexer','indexer'
FROM generate_series(1,{Rows}) n;
ANALYZE availability_items");
            Assert.Equal((long)Rows, await Sql("SELECT count(*) FROM availability_items"));
            await Sql("CHECKPOINT");
            await SaveBeforePlans();
            await File.WriteAllTextAsync(Path.Combine(_evidence, "postgres-settings.json"), (string)(await Sql(@"SELECT json_agg(s)::text FROM
(SELECT name,setting,unit,source FROM pg_settings WHERE category LIKE 'Query Tuning%' OR name IN ('server_version','shared_buffers','effective_cache_size','work_mem','max_connections','track_io_timing')) s"))!);
            Assert.Equal("on", await Sql("SHOW enable_seqscan"));
            using var db = PhantomDb.CreatePostgres(_dsn);
            await db.CountCatalogueItemsAsync("movie", null, default); // actual expand/startup
            Assert.Equal((long)Rows, await Sql("SELECT count(*) FROM availability_items WHERE claim_bucket=-1"));
            Assert.Empty(await db.ClaimDueAvailabilityBatchAsync("before-admission", Lease, Now, "stable", 32, default));
            Assert.False(await AvailabilitySchedule.ExpandAsync(connection, default)); // verified replay
            // Clone rehearsal: predicted counts BEFORE the first UPDATE; then
            // use the actual resumable 256-row scheduler, never a test-only bulk SQL.
            var predicted = (long)(await Sql("SELECT count(*) FROM availability_items WHERE claim_bucket=-1"))!;
            long actual = 0;
            var clock = Now;
            for (var offset = 0; offset < 512; offset += 256)
            {
                var changed = await db.RescheduleAvailabilityPolicyAsync("stable", clock, default);
                Assert.Equal(256, changed);
                actual += changed;
                if (offset == 0)
                    Assert.Equal(0, await db.RescheduleAvailabilityPolicyAsync("churn-too-soon", clock, default));
                clock = clock.AddSeconds(1);
            }
            // Populate the rest with a legacy writer's unchanged due assignment.
            // This is fixture-only clone preparation, not an alternate production
            // rescheduler. Count BEFORE any bulk UPDATE and verify the trigger
            // admits exactly those rows without changing legacy values.
            var legacyPredicted = (long)(await Sql("SELECT count(*) FROM availability_items WHERE claim_bucket=-1"))!;
            var legacyActual = (long)(await Sql(@"WITH changed AS (UPDATE availability_items SET next_check_at=next_check_at
WHERE claim_bucket=-1 RETURNING 1) SELECT count(*) FROM changed"))!;
            Assert.Equal(legacyPredicted, legacyActual);
            Assert.Equal(predicted, actual + legacyActual);
            Assert.Equal(0L, await Sql("SELECT count(*) FROM availability_items WHERE claim_bucket=-1"));
            Assert.Equal((long)Rows, await Sql($"SELECT count(*) FROM availability_items WHERE next_check_at >= {future} AND probe_policy_hash='stable'"));
            output.WriteLine($"CLONE REHEARSAL predicted={predicted} scheduler_actual={actual}; legacy_writer_predicted={legacyPredicted} actual={legacyActual}; slices <=256; rate limit enforced; legacy due/policy unchanged");
            await Sql("VACUUM (ANALYZE) availability_items");
            // Finish synthetic bulk preparation before measuring steady-state
            // claims. Otherwise a dirty buffer eviction can synchronously flush
            // the fixture's millions-row write storm during an unrelated claim.
            // The real rescheduler is capped at 256 rows, not this fixture storm.
            await Sql("CHECKPOINT");
            output.WriteLine("FIXTURE VACUUM/ANALYZE and durability CHECKPOINT complete before claim measurement; no per-query warmup/retry");
            await ExplainVariants("empty");

            // Only 192 due rows among 4M; both kinds, all six ordering buckets.
            await Sql($"UPDATE availability_items SET next_check_at={Now.AddMinutes(-1).ToUnixTimeSeconds()} WHERE tmdb_id BETWEEN 1 AND 96 OR tmdb_id=1000001");
            await ExplainVariants("sparse");
            await using (var locked = new NpgsqlConnection(_dsn))
            {
                await locked.OpenAsync();
                await using var contention = await locked.BeginTransactionAsync();
                await using var rowLock = new NpgsqlCommand("SELECT 1 FROM availability_items WHERE tmdb_id=6 AND type='movie' FOR UPDATE", locked, contention);
                await rowLock.ExecuteScalarAsync();
                await ExplainVariants("contention");
                await contention.RollbackAsync();
            }
            await ConcurrencyAndRecovery(db);
            await Worker(db);
            await PolicyChurn(db, clock.AddSeconds(2));
            output.WriteLine("PASS 4000000 rows; empty/sparse plans; no Seq Scan or >=5ms claim; disjoint concurrent batches; expiry/fencing; worker movie/episode progress; cursor wrap; policy churn");
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(_dsn);
            await cleanup.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {_schema} CASCADE", cleanup) { CommandTimeout = 60 };
            await drop.ExecuteNonQueryAsync();
            NpgsqlConnection.ClearPool(connection);
        }
    }

    [Fact]
    public async Task BoundedSweepCompletesDuringChurnAndPreservesLegacyLeases()
    {
        var supplied = Environment.GetEnvironmentVariable("PHANTOM_TEST_POSTGRES_DSN");
        Assert.False(string.IsNullOrWhiteSpace(supplied), "PostgreSQL required");
        _dsn = new NpgsqlConnectionStringBuilder(supplied) { SearchPath = _schema }.ConnectionString;
        await using var connection = new NpgsqlConnection(_dsn);
        _connection = connection;
        await connection.OpenAsync();
        await Sql($"CREATE SCHEMA {_schema}");
        try
        {
            await Sql((string)typeof(PhantomDb).GetField("SchemaV10Sql", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!);
            await Sql("CREATE TABLE phantom_schema_meta(version integer NOT NULL); INSERT INTO phantom_schema_meta VALUES(999)");
            await Sql($@"INSERT INTO availability_items(tmdb_id,type,season,episode,status,next_check_at,probe_policy_hash,lease_owner,lease_until)
SELECT n,CASE WHEN n%2=0 THEN 'movie' ELSE 'episode' END,
CASE WHEN n%2=0 THEN -1 ELSE 1 END,CASE WHEN n%2=0 THEN -1 ELSE 1 END,'unknown',
{Now.AddDays(1).ToUnixTimeSeconds()},'old',CASE WHEN n%5=0 THEN 'old-color' ELSE NULL END,
CASE WHEN n%5=0 THEN {Now.AddMinutes(10).ToUnixTimeSeconds()} ELSE NULL END FROM generate_series(1,600) n");
            using var db = PhantomDb.CreatePostgres(_dsn);
            Assert.Equal(256, await db.RescheduleAvailabilityPolicyAsync("A", Now, default));
            Assert.Equal(256, await db.RescheduleAvailabilityPolicyAsync("B", Now.AddSeconds(1), default));
            Assert.Equal(88, await db.RescheduleAvailabilityPolicyAsync("C", Now.AddSeconds(2), default));
            Assert.Equal(1, await Sql("SELECT scanning FROM availability_reschedule"));
            Assert.Equal(-1, await Sql("SELECT cursor_tmdb FROM availability_reschedule"));
            using var restarted = PhantomDb.CreatePostgres(_dsn);
            for (var i = 3; i <= 5; i++) Assert.Equal(0, await restarted.RescheduleAvailabilityPolicyAsync("C", Now.AddSeconds(i), default));
            Assert.Equal(0, await Sql("SELECT scanning FROM availability_reschedule"));
            Assert.Equal("C", await Sql("SELECT policy FROM availability_reschedule"));
            Assert.Equal(999, await Sql("SELECT version FROM phantom_schema_meta"));
            Assert.Equal(120L, await Sql($"SELECT count(*) FROM availability_items WHERE lease_owner='old-color' AND claim_due={Now.AddMinutes(10).ToUnixTimeSeconds()}"));
            var claimed = new List<AvailabilityItemRow>();
            for (var i = 0; i < 20; i++) claimed.AddRange(await restarted.ClaimDueAvailabilityBatchAsync("new-color", Lease, Now.AddSeconds(6), "C", 32, default));
            Assert.Equal(480, claimed.Count);
            Assert.Equal(480, claimed.Select(Key).Distinct().Count());
            Assert.DoesNotContain(claimed, r => r.TmdbId % 5 == 0);
            Assert.Equal(2, claimed.Select(r => r.Type).Distinct().Count());
            var expired = await restarted.ClaimDueAvailabilityBatchAsync("after-old-expiry", Lease, Now.AddMinutes(11), "C", 32, default);
            Assert.NotEmpty(expired);
            // Old-color named-column writes recompute the projection immediately.
            await Sql($"UPDATE availability_items SET lease_owner=NULL,lease_until=NULL,next_check_at={Now.AddDays(2).ToUnixTimeSeconds()},status='available',checked_at={Now.ToUnixTimeSeconds()} WHERE tmdb_id=5");
            Assert.Equal(3L, await Sql("SELECT claim_bucket FROM availability_items WHERE tmdb_id=5"));
            Assert.Equal(Now.AddDays(2).ToUnixTimeSeconds(), await Sql("SELECT claim_due FROM availability_items WHERE tmdb_id=5"));
            Assert.Equal(0, await restarted.RescheduleAvailabilityPolicyAsync("C", Now.AddDays(1), default));
            Assert.Equal(Now.AddDays(2).ToUnixTimeSeconds(), await Sql("SELECT next_check_at::bigint FROM availability_items WHERE tmdb_id=5"));
            output.WriteLine("SMALL CLONE predicted=600 actual=256+256+88; churn A/B/C completes; restart resumes; stable policy idle; 480 disjoint claims, 120 old leases protected; old writer projections; future schema 999 retained");
        }
        finally
        {
            await using var cleanup = new NpgsqlConnection(_dsn);
            await cleanup.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {_schema} CASCADE", cleanup);
            await drop.ExecuteNonQueryAsync();
            NpgsqlConnection.ClearPool(connection);
        }
    }

    private async Task SaveBeforePlans()
    {
        var where = $"(next_check_at<={Now.ToUnixTimeSeconds()} OR probe_policy_hash IS NULL OR probe_policy_hash<>'stable') AND (lease_until IS NULL OR lease_until<{Now.ToUnixTimeSeconds()})";
        foreach (var (name, predicate, order) in new[]
        {
            ("all", "", "priority DESC,CASE WHEN checked_at IS NULL THEN 0 WHEN status='available' THEN 1 WHEN status='unavailable' THEN 2 ELSE 3 END,next_check_at"),
            ("movie", "AND type='movie'", "priority DESC,CASE WHEN checked_at IS NULL THEN 0 WHEN status='available' THEN 1 WHEN status='unavailable' THEN 2 ELSE 3 END,next_check_at"),
            ("episode-after-cursor", "AND type='episode' AND tmdb_id>1010000", "priority DESC,tmdb_id,CASE WHEN checked_at IS NULL THEN 0 WHEN status='available' THEN 1 WHEN status='unavailable' THEN 2 ELSE 3 END,season,episode"),
        })
        {
            var plan = (string)(await Sql($"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) SELECT tmdb_id,type,season,episode FROM availability_items WHERE {where} {predicate} ORDER BY {order} LIMIT 1"))!;
            await File.WriteAllTextAsync(Path.Combine(_evidence, $"before-{name}.json"), plan);
        }
    }

    private async Task ExplainVariants(string phase)
    {
        foreach (var type in new[] { "movie", "episode" })
        for (var bucket = 0; bucket < AvailabilitySchedule.BucketCount; bucket++)
        foreach (var cursorVariant in type == "episode" ? new[] { "start", "mid", "end" } : new[] { "start" })
        {
            // Roll back EXPLAIN's actual UPDATE so every variant sees the same
            // fixture. No planner flags, prewarming, timing retries or skip.
            await using var tx = await _connection.BeginTransactionAsync();
            await using var cmd = PhantomDb.CreateAvailabilityClaimCommand(_connection, "explain", Lease, Now,
                type, bucket, 32, cursorVariant switch
                {
                    "mid" => new PhantomDb.AvailabilityCursor(Now.AddMinutes(-1).ToUnixTimeSeconds(), 1000001, 1, 5),
                    "end" => new PhantomDb.AvailabilityCursor(Now.ToUnixTimeSeconds(), int.MaxValue, int.MaxValue, int.MaxValue),
                    _ => null,
                });
            cmd.Transaction = tx;
            cmd.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + cmd.CommandText;
            var json = (string)(await cmd.ExecuteScalarAsync())!;
            var name = $"after-{phase}-{type}-{bucket}-{cursorVariant}";
            await File.WriteAllTextAsync(Path.Combine(_evidence, name + ".json"), json);
            using var plan = JsonDocument.Parse(json);
            var root = plan.RootElement[0];
            RejectSequential(root.GetProperty("Plan"));
            var ms = root.GetProperty("Execution Time").GetDouble();
            Assert.True(ms < 5, $"{name}: {ms}ms >=5ms (full plan retained)");
            output.WriteLine($"PLAN {name} {ms:F3}ms");
            await tx.RollbackAsync();
        }
    }

    private static void RejectSequential(JsonElement node)
    {
        Assert.DoesNotContain("Seq Scan", node.GetProperty("Node Type").GetString());
        if (node.TryGetProperty("Plans", out var children)) foreach (var child in children.EnumerateArray()) RejectSequential(child);
    }

    private static string Key(AvailabilityItemRow r) => $"{r.TmdbId}/{r.Type}/{r.Season}/{r.Episode}";

    private async Task ConcurrencyAndRecovery(PhantomDb db)
    {
        // Each instance has its own process semaphore; deliberately also hold the
        // first instance's semaphore so the new claim cannot accidentally use it.
        var semaphore = (SemaphoreSlim)typeof(PhantomDb).GetField("_writeLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(db)!;
        await semaphore.WaitAsync();
        IReadOnlyList<AvailabilityItemRow>[] batches;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            batches = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => db.ClaimDueAvailabilityBatchAsync("claimer-" + i, Lease, Now, "stable", 16, deadline.Token)));
        }
        finally { semaphore.Release(); }
        var all = batches.SelectMany(b => b).ToArray();
        Assert.Equal(64, all.Length);
        Assert.Equal(all.Length, all.Select(Key).Distinct().Count());
        Assert.All(all, r => Assert.Equal(1, r.ProbeGeneration));
        foreach (var r in all) await db.ReleaseAvailabilityLeaseAsync(r, default);
        // Lock a real leading row; another connection must skip it, not block.
        await using (var tx = await _connection.BeginTransactionAsync())
        {
            await Sql("SELECT 1 FROM availability_items WHERE tmdb_id=6 AND type='movie' FOR UPDATE");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var skipped = await db.ClaimDueAvailabilityBatchAsync("skip", Lease, Now, "stable", 16, timeout.Token, "movie");
            Assert.DoesNotContain(skipped, r => r.TmdbId == 6);
            await tx.RollbackAsync();
            foreach (var r in skipped) await db.ReleaseAvailabilityLeaseAsync(r, default);
        }
        var first = await db.ClaimDueAvailabilityBatchAsync("expiry", TimeSpan.FromSeconds(1), Now, "stable", 32, default, "episode");
        Assert.NotEmpty(first);
        var recovered = new List<AvailabilityItemRow>();
        for (var i = 0; i < 10; i++) recovered.AddRange(await db.ClaimDueAvailabilityBatchAsync("recovered", Lease, Now.AddSeconds(2), "stable", 32, default, "episode"));
        Assert.Contains(recovered, r => first.Any(old => Key(old) == Key(r) && r.ProbeGeneration > old.ProbeGeneration));
        foreach (var old in first)
            Assert.False(await db.RescheduleAvailabilityTransientAsync(old, Now.AddDays(1), "stale", "stale generation", default));
        foreach (var r in recovered) await db.ReleaseAvailabilityLeaseAsync(r, default);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => db.ClaimDueAvailabilityBatchAsync("cancelled", Lease, Now, "stable", 4, canceled.Token));
        output.WriteLine("CONCURRENCY 4 claimers x16 disjoint; process write lock held; locked leader skipped; expiry reclaimed; stale completion fenced; episode cursor wraps; cancellation observed");
    }

    private async Task Worker(PhantomDb db)
    {
        // Missing metadata is an actual worker outcome (no indexer/network mock
        // claim implementation). Both types must be leased and rescheduled.
        var cfg = new PluginConfiguration { AvailabilityLeaseMinutes = 1, AvailabilityTransientRetryMinutes = 5 };
        var tmdb = Mock.Of<ITmdbClient>();
        var selector = new MagnetSelector(Array.Empty<IIndexerClient>(), new QualityScorer(NullLogger<QualityScorer>.Instance), NullLogger<MagnetSelector>.Instance, () => cfg);
        using var worker = new AvailabilityProbeWorker(db, selector,
            new TmdbExternalIdResolver(db, tmdb, NullLogger<TmdbExternalIdResolver>.Instance), tmdb,
            new ChannelStateProvider(db), NullLogger<AvailabilityProbeWorker>.Instance, () => cfg);
        Assert.Equal(4, await worker.ProbeAvailabilityBatchAsync(cfg, 4, default));
        Assert.Equal(2L, await Sql("SELECT count(DISTINCT type) FROM availability_items WHERE last_error_kind='missing_metadata'"));
        Assert.Equal(4L, await Sql("SELECT count(*) FROM availability_items WHERE last_error_kind='missing_metadata' AND lease_owner IS NULL AND lease_until IS NULL"));
        var semaphore = (SemaphoreSlim)typeof(PhantomDb).GetField("_writeLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(db)!;
        await semaphore.WaitAsync();
        try
        {
            using var cancellation = new CancellationTokenSource();
            var running = worker.ProbeAvailabilityBatchAsync(cfg, 4, cancellation.Token);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while ((long)(await Sql("SELECT count(*) FROM availability_items WHERE lease_owner LIKE 'availability-%'"))! < 4)
            {
                Assert.True(DateTime.UtcNow < deadline, "worker did not atomically acquire four leases");
                await Task.Delay(10);
            }
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running);
        }
        finally { semaphore.Release(); }
        Assert.Equal(0L, await Sql("SELECT count(*) FROM availability_items WHERE lease_owner LIKE 'availability-%'"));
        output.WriteLine("WORKER actual PostgreSQL claim -> metadata lookup -> transient reschedule; movie + episode; four leases released; cancellation during blocked completion releases entire batch");
    }

    private async Task PolicyChurn(PhantomDb db, DateTimeOffset clock)
    {
        // A stable already-admitted slice writes no catalogue rows. A change
        // advances, never resets, the persisted cursor while a sweep is active.
        Assert.Equal(0, await db.RescheduleAvailabilityPolicyAsync("stable", clock, default)); // already projected prefix, no stale policies
        var count = await db.RescheduleAvailabilityPolicyAsync("new-A", clock.AddSeconds(1), default);
        Assert.InRange(count, 1, 256);
        var cursor = (int)(await Sql("SELECT cursor_tmdb FROM availability_reschedule WHERE id=1"))!;
        using var restarted = PhantomDb.CreatePostgres(_dsn);
        count = await restarted.RescheduleAvailabilityPolicyAsync("new-B", clock.AddSeconds(2), default);
        Assert.InRange(count, 1, 256);
        Assert.True((int)(await Sql("SELECT cursor_tmdb FROM availability_reschedule WHERE id=1"))! > cursor);
        Assert.Equal(0, await restarted.RescheduleAvailabilityPolicyAsync("new-C", clock.AddSeconds(2), default));
        await ExplainVariants("stale-policy");
        output.WriteLine("POLICY persisted progress after restart/churn; <=256 changed/s; stale-policy hot claims remain indexed");
    }
}
