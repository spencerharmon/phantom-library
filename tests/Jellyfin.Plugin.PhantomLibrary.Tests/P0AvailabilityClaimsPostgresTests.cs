using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.PhantomLibrary.State;
using Jellyfin.Plugin.PhantomLibrary.State.Db;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// Required PostgreSQL acceptance, not an opt-in/no-op integration test. Each test
/// owns a random schema; the DSN must permit CREATE SCHEMA on a disposable server.
/// Run scripts/tests/p0-availability-claims.test.sh for the complete gate including rig parity.
/// </summary>
public sealed class P0AvailabilityClaimsPostgresTests : IAsyncLifetime
{
    private readonly string _schema = "p0_availability_" + Guid.NewGuid().ToString("N");
    private string _dsn = string.Empty;
    private string _scopedDsn = string.Empty;
    private bool _created;
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    public async Task InitializeAsync()
    {
        _dsn = Environment.GetEnvironmentVariable("PHANTOM_TEST_POSTGRES_DSN") ?? string.Empty;
        Assert.False(string.IsNullOrWhiteSpace(_dsn),
            "PostgreSQL is REQUIRED. Run scripts/tests/p0-availability-claims.test.sh to provision it, or set PHANTOM_TEST_POSTGRES_DSN.");
        _scopedDsn = new NpgsqlConnectionStringBuilder(_dsn)
        {
            SearchPath = _schema,
            Pooling = false,
            CommandTimeout = 180,
            ApplicationName = "p0-availability-acceptance",
        }.ToString();
        await using var connection = new NpgsqlConnection(_dsn);
        await connection.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE SCHEMA {_schema};", connection);
        await create.ExecuteNonQueryAsync();
        _created = true;
    }

    public async Task DisposeAsync()
    {
        if (!_created)
        {
            return;
        }

        await using var connection = new NpgsqlConnection(_dsn);
        await connection.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP SCHEMA {_schema} CASCADE;", connection);
        drop.CommandTimeout = 180;
        await drop.ExecuteNonQueryAsync();
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(_scopedDsn);
        await connection.OpenAsync();
        return connection;
    }

    private async Task<object?> SqlAsync(string sql)
    {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }

    private async Task<PhantomDb> NewDbAsync()
    {
        var db = PhantomDb.CreatePostgres(_scopedDsn);
        await db.SetMetaAsync("p0-test", "1", default);
        Assert.True(db.UsesPostgresAvailabilityBatch);
        return db;
    }

    private Task SeedAsync(int count, string type = "movie", string policy = "A", bool due = true)
        => SqlAsync($"""
            INSERT INTO availability_items(tmdb_id,type,season,episode,status,checked_at,next_check_at,probe_policy_hash)
            SELECT n,'{type}',{(type == "movie" ? -1 : 1)},{(type == "movie" ? -1 : 1)},
                'unavailable',{Now.ToUnixTimeSeconds() - 3600},{Now.AddDays(due ? -1 : 1).ToUnixTimeSeconds()},'{policy}'
            FROM generate_series(1,{count}) n;
            """);

    private static Task<IReadOnlyList<AvailabilityItemRow>> ClaimAsync(
        PhantomDb db, string owner = "owner", string? type = null, DateTimeOffset? now = null, string policy = "A", int count = 32)
        => db.ClaimDueAvailabilityBatchAsync(owner, Lease, now ?? Now, policy, count, default, type);

    [Fact]
    public async Task EmptySparseAndStalePolicies_RespectPriorityBucketsAndBounds()
    {
        using var db = await NewDbAsync();
        Assert.Empty(await ClaimAsync(db));
        await SeedAsync(1000, due: false);
        await SqlAsync($"""
            UPDATE availability_items SET next_check_at={Now.ToUnixTimeSeconds()-1} WHERE tmdb_id=999;
            UPDATE availability_items SET probe_policy_hash=NULL,priority=100 WHERE tmdb_id=998;
            UPDATE availability_items SET probe_policy_hash='old',priority=50 WHERE tmdb_id=997;
            """);
        var rows = await ClaimAsync(db, type: "movie");
        Assert.Equal(new[] { 998, 997, 999 }, rows.Select(r => r.TmdbId));
        Assert.All(rows, row => { Assert.Equal(1, row.ProbeGeneration); Assert.Equal(1, row.AttemptCount); });
        Assert.Empty(await ClaimAsync(db));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ClaimAsync(db, count: 33));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ClaimAsync(db, count: 0));

        await SqlAsync("TRUNCATE availability_items;");
        await SeedAsync(4);
        await SqlAsync("""
            UPDATE availability_items SET checked_at=NULL WHERE tmdb_id=4;
            UPDATE availability_items SET status='available' WHERE tmdb_id=3;
            UPDATE availability_items SET status='unknown' WHERE tmdb_id=1;
            """);
        Assert.Equal(new[] { 4, 3, 2, 1 }, (await ClaimAsync(db)).Select(r => r.TmdbId));
    }

    [Theory]
    [InlineData("movie")]
    [InlineData("episode")]
    public async Task ConcurrentClaimers_AreDisjoint_AndExpiryRenewalReleaseAreFenced(string type)
    {
        using var a = await NewDbAsync();
        using var b = await NewDbAsync();
        await SeedAsync(64, type);
        var batches = await Task.WhenAll(ClaimAsync(a, "A", type), ClaimAsync(b, "B", type));
        Assert.All(batches, batch => Assert.Equal(32, batch.Count));
        Assert.Equal(64, batches.SelectMany(batch => batch).Select(r => (r.TmdbId, r.Type, r.Season, r.Episode)).Distinct().Count());
        Assert.Empty(await ClaimAsync(a, "C", type));
        var original = batches[0][0];
        Assert.False(await b.RenewAvailabilityLeaseAsync(original with { LeaseOwner = "intruder" }, Lease, Now, default));
        Assert.True(await a.RenewAvailabilityLeaseAsync(original, Lease, Now.AddSeconds(60), default));
        Assert.Empty(await ClaimAsync(b, "C", type, Now.Add(Lease)));
        var expired = await ClaimAsync(b, "C", type, Now.Add(Lease).AddSeconds(1));
        Assert.DoesNotContain(expired, r => r.TmdbId == original.TmdbId);
        Assert.NotEmpty(expired);
        var stale = batches.SelectMany(batch => batch).Single(r => r.TmdbId == expired[0].TmdbId);
        Assert.False(await a.RenewAvailabilityLeaseAsync(stale, Lease, Now.Add(Lease).AddSeconds(1), default));
        Assert.False(await a.ReleaseAvailabilityLeaseAsync(stale, default, unconsumed: true));
        Assert.False(await a.CompleteAvailabilityProbeAsync(stale, "available", Now, Now.AddDays(7), "A", null, null, null, default));
        Assert.False(await a.RescheduleAvailabilityTransientAsync(stale, Now.AddDays(7), "late", null, default));
        Assert.True(await b.ReleaseAvailabilityLeaseAsync(expired[0], default, unconsumed: true));
        Assert.False(await b.ReleaseAvailabilityLeaseAsync(expired[0], default, unconsumed: true));
        Assert.Equal(1, Convert.ToInt32(await SqlAsync($"SELECT attempt_count FROM availability_items WHERE tmdb_id={expired[0].TmdbId};"), CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task RowLockContention_SkipsLockedClaimWithoutDuplicatingIt()
    {
        using var db = await NewDbAsync();
        await ClaimAsync(db); // Park the empty policy sweep before taking a row lock.
        await SeedAsync(2);
        await using var locked = await OpenAsync();
        await using var tx = await locked.BeginTransactionAsync();
        await using var rowLock = new NpgsqlCommand("SELECT 1 FROM availability_items WHERE tmdb_id=1 FOR UPDATE;", locked, tx);
        await rowLock.ExecuteScalarAsync();
        var rows = await ClaimAsync(db, count: 2).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Assert.Single(rows).TmdbId);
        await tx.RollbackAsync();
        Assert.Equal(1, Assert.Single(await ClaimAsync(db)).TmdbId);
    }

    [Fact]
    public async Task EpisodeCursor_WrapsBeyond64Series_DespiteDominantSeriesBacklog()
    {
        using var db = await NewDbAsync();
        await SeedAsync(96, "episode");
        await SqlAsync("""
            INSERT INTO availability_items(tmdb_id,type,season,episode,status,next_check_at)
            SELECT 1,'episode',1,n,'unknown',0 FROM generate_series(2,10000) n;
            UPDATE availability_scheduler_state SET episode_cursor=80;
            """);
        var seen = new HashSet<int>();
        for (var batch = 0; batch < 3; batch++)
        {
            var rows = await ClaimAsync(db, "cursor", "episode");
            Assert.Equal(32, rows.Count);
            Assert.Equal(32, rows.Select(r => r.TmdbId).Distinct().Count());
            foreach (var row in rows)
            {
                Assert.True(seen.Add(row.TmdbId), $"series {row.TmdbId} repeated before completing a cursor cycle");
                Assert.True(await db.CompleteAvailabilityProbeAsync(row, "unavailable", Now, Now.AddDays(7), "A", null, null, null, default));
            }
        }

        Assert.Equal(Enumerable.Range(1, 96), seen.Order());
    }

    [Fact]
    public async Task AlternatingPolicies_FinishSweepWithoutCursorReset_AndLateOldWritersBecomeDue()
    {
        using var db = await NewDbAsync();
        await SeedAsync(1025, due: false);
        // Start A then finish it despite alternating requests from two deployed policies.
        long previous = -2147483649;
        for (var page = 0; page < 5; page++)
        {
            await ClaimAsync(db, policy: page % 2 == 0 ? "A" : "B");
            var cursor = Convert.ToInt64(await SqlAsync("SELECT cursor_tmdb FROM availability_scheduler_state;"), CultureInfo.InvariantCulture);
            Assert.True(cursor >= previous);
            previous = cursor;
        }

        Assert.Equal(true, await SqlAsync("SELECT sweep_complete FROM availability_scheduler_state;"));
        Assert.Equal("A", await SqlAsync("SELECT policy_hash FROM availability_scheduler_state;"));
        for (var round = 0; round < 4; round++)
        {
            var target = round % 2 == 0 ? "B" : "A";
            var at = Now.AddSeconds((round + 1) * 61);
            previous = -2147483649;
            for (var page = 0; page < 5; page++)
            {
                var rows = await ClaimAsync(db, policy: page % 2 == 0 ? target : (target == "A" ? "B" : "A"), now: at);
                var cursor = Convert.ToInt64(await SqlAsync("SELECT cursor_tmdb FROM availability_scheduler_state;"), CultureInfo.InvariantCulture);
                Assert.True(cursor >= previous);
                previous = cursor;
                foreach (var row in rows)
                {
                    Assert.True(await db.CompleteAvailabilityProbeAsync(row, "unavailable", at, at.AddDays(7), target, null, null, null, default));
                }
            }

            Assert.Equal(target, await SqlAsync("SELECT policy_hash FROM availability_scheduler_state;"));
            Assert.Equal(true, await SqlAsync("SELECT sweep_complete FROM availability_scheduler_state;"));
            // A shipped old writer knows none of the scheduler columns. Its late
            // completion must still invalidate a row BEHIND the completed cursor.
            await SqlAsync($"""
                UPDATE availability_items SET lease_owner=NULL,lease_until=NULL,priority=100,
                    probe_policy_hash='{(target == "A" ? "B" : "A")}',next_check_at={Now.AddDays(10).ToUnixTimeSeconds()}
                WHERE tmdb_id=1;
                """);
            Assert.Equal(true, await SqlAsync("SELECT scheduler_ready FROM availability_items WHERE tmdb_id=1;"));
            Assert.Equal(1, (await ClaimAsync(db, policy: target, now: at))[0].TmdbId);
        }
    }

    [Fact]
    public async Task OldSchemaExpand_IsIdempotent_PreservesRowsVersionAndLegacyWriters()
    {
        await CreateLegacySchemaAsync();
        await SeedAsync(3);
        var before = await SqlAsync("SELECT pg_relation_filenode('availability_items'::regclass);");
        await using (var connection = await OpenAsync())
        {
            await AvailabilitySchedulerSchema.EnsureAsync(connection, default);
            await AvailabilitySchedulerSchema.EnsureAsync(connection, default);
        }

        Assert.Equal(before, await SqlAsync("SELECT pg_relation_filenode('availability_items'::regclass);"));
        Assert.Equal(3L, await SqlAsync("SELECT count(*) FROM availability_items;"));
        Assert.Equal(22, await SqlAsync("SELECT version FROM phantom_schema_meta;"));
        Assert.Equal(4L, await SqlAsync("SELECT count(*) FROM pg_index WHERE indrelid='availability_items'::regclass AND indisvalid AND indexrelid::regclass::text LIKE 'idx_availability_scheduler_%';"));
        using var db = await NewDbAsync();
        Assert.Equal(3, (await ClaimAsync(db)).Count);
        await SqlAsync($"""
            INSERT INTO availability_items(tmdb_id,type,season,episode,status,next_check_at)
            VALUES(4,'movie',-1,-1,'unknown',0) ON CONFLICT DO NOTHING;
            UPDATE availability_items SET lease_owner='legacy',lease_until={Now.AddMinutes(5).ToUnixTimeSeconds()},
                probe_generation=probe_generation+1,attempt_count=attempt_count+1 WHERE tmdb_id=4;
            """);
        Assert.Empty(await ClaimAsync(db));
        await SqlAsync("UPDATE availability_items SET lease_owner=NULL,lease_until=NULL,probe_policy_hash=NULL,next_check_at=0 WHERE tmdb_id=4;");
        Assert.Equal(4, Assert.Single(await ClaimAsync(db)).TmdbId);
    }

    private async Task CreateLegacySchemaAsync()
    {
        // The base v22 DDL is still the shipped legacy shape. The feature expand
        // must work on that shape, not a hand-maintained miniature test schema.
        var ddl = (string)(typeof(PhantomDb).GetField("SchemaV10Sql", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetRawConstantValue() ?? throw new InvalidOperationException("Missing production base schema"));
        Assert.DoesNotContain("scheduler_ready", ddl, StringComparison.Ordinal);
        await SqlAsync(ddl);
        await SqlAsync("CREATE TABLE phantom_schema_meta(version INTEGER NOT NULL); INSERT INTO phantom_schema_meta VALUES(22);");
    }

    [Fact]
    public async Task FourMillionRows_ProductionPlans_NoSeqScan_AndEveryExecutionUnderFiveMilliseconds()
    {
        var evidence = Environment.GetEnvironmentVariable("PHANTOM_P0_EVIDENCE_DIR");
        Assert.False(string.IsNullOrWhiteSpace(evidence), "PHANTOM_P0_EVIDENCE_DIR is required to retain EXPLAIN evidence.");
        Directory.CreateDirectory(evidence!);
        await CreateLegacySchemaAsync();
        Console.WriteLine("P0: inserting exactly 4,000,000 legacy availability rows");
        await SqlAsync($"""
            INSERT INTO availability_items(tmdb_id,type,season,episode,status,checked_at,next_check_at,probe_policy_hash)
            SELECT n,'movie',-1,-1,'unavailable',{Now.ToUnixTimeSeconds()},{Now.AddDays(7).ToUnixTimeSeconds()},'A'
            FROM generate_series(1,2000000) n;
            INSERT INTO availability_items(tmdb_id,type,season,episode,status,checked_at,next_check_at,probe_policy_hash)
            SELECT CASE WHEN n<=1000000 THEN 1 ELSE 2+(n-1000001)/1000 END,'episode',1,
                CASE WHEN n<=1000000 THEN n ELSE 1+(n-1000001)%1000 END,
                'unavailable',{Now.ToUnixTimeSeconds()},{Now.AddDays(7).ToUnixTimeSeconds()},'A'
            FROM generate_series(1,2000000) n;
            ANALYZE availability_items;
            """);
        Assert.Equal(4000000L, await SqlAsync("SELECT count(*) FROM availability_items;"));
        var failures = new List<string>();
        await CaptureLegacyAsync("empty", evidence!);
        await using (var connection = await OpenAsync())
        {
            await AvailabilitySchedulerSchema.EnsureAsync(connection, default);
        }

        Console.WriteLine("P0: hydrating scheduler through the production trigger, with normal planner settings");
        await SqlAsync("UPDATE availability_scheduler_state SET policy_hash='A',pending_policy_hash='A',sweep_complete=TRUE;");
        await SqlAsync("UPDATE availability_items SET next_check_at=next_check_at; ANALYZE availability_items; ANALYZE availability_scheduler_state;");
        await using (var connection = await OpenAsync())
        {
            await using var settings = new NpgsqlCommand("""
                SELECT json_build_object('version',version(),'settings',
                    (SELECT json_object_agg(name,setting) FROM pg_settings WHERE name LIKE 'enable_%' OR name IN
                    ('shared_buffers','effective_cache_size','work_mem','random_page_cost','seq_page_cost','jit','max_parallel_workers_per_gather')),
                    'rows',(SELECT count(*) FROM availability_items),'table_bytes',pg_total_relation_size('availability_items'))::text;
                """, connection);
            await File.WriteAllTextAsync(Path.Combine(evidence!, "postgres-settings.json"), (string)(await settings.ExecuteScalarAsync())!);
            await using var planner = new NpgsqlCommand("SHOW enable_seqscan;", connection);
            Assert.Equal("on", await planner.ExecuteScalarAsync());
        }

        foreach (var scenario in new[] { "empty", "sparse", "null-policy", "stale-policy", "lease-expired", "cursor-wrap" })
        {
            Console.WriteLine($"P0: retaining before/after plans for {scenario}");
            await SqlAsync($"""
                UPDATE availability_items SET next_check_at={Now.AddDays(7).ToUnixTimeSeconds()},probe_policy_hash='A',
                    lease_owner=NULL,lease_until=NULL WHERE (type='movie' AND tmdb_id<=96) OR (type='episode' AND tmdb_id<=96 AND episode=1);
                UPDATE availability_scheduler_state SET episode_cursor={(scenario == "cursor-wrap" ? 80 : -1)};
                """);
            var mutation = scenario switch
            {
                "sparse" or "cursor-wrap" => $"next_check_at={Now.ToUnixTimeSeconds()-1}",
                "null-policy" => "probe_policy_hash=NULL",
                "stale-policy" => "probe_policy_hash='old'",
                "lease-expired" => $"next_check_at=0,lease_owner='expired',lease_until={Now.ToUnixTimeSeconds()-1}",
                _ => null,
            };
            if (mutation is not null)
            {
                await SqlAsync($"UPDATE availability_items SET {mutation} WHERE (type='movie' AND tmdb_id<=96) OR (type='episode' AND tmdb_id<=96 AND episode=1);");
            }

            if (scenario != "empty")
            {
                await CaptureLegacyAsync(scenario, evidence!);
            }

            await CaptureProductionAsync(scenario, evidence!, failures);
        }

        await File.WriteAllTextAsync(Path.Combine(evidence!, "plan-failures.json"), JsonSerializer.Serialize(failures, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private async Task CaptureLegacyAsync(string scenario, string evidence)
    {
        foreach (var type in new string?[] { null, "movie", "episode" })
        {
            await using var connection = await OpenAsync();
            await using var command = new NpgsqlCommand("""
                EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)
                SELECT tmdb_id,type,season,episode,status,checked_at,next_check_at,
                    candidate_magnet,candidate_info_hash,candidate_size,candidate_seeders,
                    candidate_indexer,candidate_source,probe_generation,lease_owner,attempt_count,negative_streak
                FROM availability_items
                WHERE (next_check_at<=@now OR probe_policy_hash IS NULL OR probe_policy_hash<>@policy)
                    AND (lease_until IS NULL OR lease_until<@now)
                """ + (type == "episode" ? " AND type='episode' AND (@cursor IS NULL OR tmdb_id>@cursor)" : " AND (@preferred IS NULL OR type=@preferred)") +
                (type == "episode" ? " ORDER BY priority DESC,tmdb_id,CASE WHEN checked_at IS NULL THEN 0 WHEN status='available' THEN 1 WHEN status='unavailable' THEN 2 ELSE 3 END,season,episode LIMIT 1;" :
                    " ORDER BY priority DESC,CASE WHEN checked_at IS NULL THEN 0 WHEN status='available' THEN 1 WHEN status='unavailable' THEN 2 ELSE 3 END,next_check_at LIMIT 1;"), connection);
            command.Parameters.AddWithValue("now", Now.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("policy", "A");
            if (type == "episode")
            {
                command.Parameters.AddWithValue("cursor", NpgsqlTypes.NpgsqlDbType.Bigint, scenario == "cursor-wrap" ? 80L : DBNull.Value);
            }
            else
            {
                command.Parameters.AddWithValue("preferred", NpgsqlTypes.NpgsqlDbType.Text, (object?)type ?? DBNull.Value);
            }

            await File.WriteAllTextAsync(Path.Combine(evidence, $"before-{scenario}-{type ?? "fallback"}.json"), (string)(await command.ExecuteScalarAsync())!);
            await File.WriteAllTextAsync(Path.Combine(evidence, $"before-{scenario}-{type ?? "fallback"}.sql"), command.CommandText);
        }
    }

    private async Task CaptureProductionAsync(string scenario, string evidence, List<string> failures)
    {
        foreach (var variant in new[] { "fallback", "movie", "episode", "promotion", "sweep-active", "sweep-complete" })
        {
            await using var connection = await OpenAsync();
            await using var tx = await connection.BeginTransactionAsync();
            await using (var setup = new NpgsqlCommand($"""
                SELECT set_config('phantom.availability_now','{Now.ToUnixTimeSeconds()}',true);
                UPDATE availability_scheduler_state SET sweep_complete={(variant == "sweep-active" ? "FALSE" : "TRUE")},
                    cursor_tmdb=-2147483649,cursor_type='',cursor_season=-1,cursor_episode=-1;
                """, connection, tx))
            {
                await setup.ExecuteNonQueryAsync();
            }

            await using var command = variant switch
            {
                "promotion" => PostgresAvailabilityScheduler.CreatePromotionCommand(connection, tx, Now),
                "sweep-active" or "sweep-complete" => PostgresAvailabilityScheduler.CreateSweepCommand(connection, tx, "A", Now),
                _ => PostgresAvailabilityScheduler.CreateClaimCommand(connection, tx, "explain", Lease, Now, 32, variant == "fallback" ? null : variant),
            };
            await File.WriteAllTextAsync(Path.Combine(evidence, $"after-{scenario}-{variant}.sql"), command.CommandText);
            // The production sweep is a two-statement command. EXPLAIN each actual
            // statement in order in the SAME transaction; never explain a substitute SELECT.
            var statements = command.CommandText.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(variant.StartsWith("sweep", StringComparison.Ordinal) ? 2 : 1, statements.Length);
            for (var index = 0; index < statements.Length; index++)
            {
                command.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + statements[index];
                var json = (string)(await command.ExecuteScalarAsync())!;
                var name = $"after-{scenario}-{variant}-{index}";
                await File.WriteAllTextAsync(Path.Combine(evidence, name + ".json"), json);
                using var document = JsonDocument.Parse(json);
                var root = document.RootElement[0];
                var milliseconds = root.GetProperty("Execution Time").GetDouble();
                if (milliseconds >= 5)
                {
                    failures.Add($"{name}: execution {milliseconds.ToString(CultureInfo.InvariantCulture)}ms >= 5ms");
                }

                InspectPlan(root.GetProperty("Plan"), name, failures);
            }

            await tx.RollbackAsync();
        }
    }

    private static void InspectPlan(JsonElement node, string name, List<string> failures)
    {
        if (node.TryGetProperty("Node Type", out var type) && type.GetString()!.Contains("Seq Scan", StringComparison.Ordinal))
        {
            failures.Add($"{name}: forbidden {type.GetString()} on {node.GetProperty("Relation Name").GetString()}");
        }

        if (node.TryGetProperty("Plans", out var children))
        {
            foreach (var child in children.EnumerateArray())
            {
                InspectPlan(child, name, failures);
            }
        }
    }
}
