using System.Diagnostics;
using Jellyfin.Plugin.PhantomLibrary.State.Db;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using E = Jellyfin.Plugin.PhantomLibrary.State.Db.RowExpression;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

// DDL fault injection and backend termination use shared database catalogs.
// Do not overlap unrelated fixture teardown with these tests' CIC snapshot waits.
// Racing colors remain truly concurrent within the explicit race tests below.
[CollectionDefinition("Row projection DDL", DisableParallelization = true)]
public sealed class RowProjectionCollection;

[Collection("Row projection DDL")]
public sealed class RowProjectionExpandTests(ITestOutputHelper output) : IAsyncLifetime
{
    private readonly string _schema = "projection_" + Guid.NewGuid().ToString("N");
    private string _dsn = null!;
    private NpgsqlConnection _db = null!;
    private string _before = null!;
    private static readonly string[] Tables = ["movie", "episode"];
    private const string Unique = "CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS build_idx ON build (id)";
    private static readonly string[] Indexes = Tables.Select(t => $"CREATE INDEX CONCURRENTLY IF NOT EXISTS {t}_schedule ON {t} (bucket, due, id)").ToArray();

    // This is an illustrative fixture, NOT the P0 scheduling schema/policy.
    private static AdditiveRowProjection Projection(string table, int version = 1, string outputName = "bucket") => new(table + "_projection", version, table,
        new RowProjectionColumn(outputName, -1,
            E.Choose(E.GreaterThan(E.NumberColumn("priority"), E.Number(0)), E.Number(0),
                E.Choose(E.And(E.Equal(E.TextColumn("status"), E.Text("unknown")), E.IsNull(E.NumberColumn("checked_at"))), E.Number(1), E.Number(2)))),
        new RowProjectionColumn("due", 0, E.Greatest(E.Coalesce(E.NumberColumn("next_check_at"), E.Number(0)), E.Coalesce(E.NumberColumn("lease_until"), E.Number(0)))));
    private static AdditiveRowProjection[] Projections() => Tables.Select(t => Projection(t)).ToArray();

    public async Task InitializeAsync()
    {
        var dsn = Environment.GetEnvironmentVariable("PHANTOM_TEST_POSTGRES_DSN");
        Assert.False(string.IsNullOrWhiteSpace(dsn), "PostgreSQL DSN REQUIRED; no optional skip");
        _dsn = new NpgsqlConnectionStringBuilder(dsn) { SearchPath = _schema, MaxPoolSize = 5 }.ConnectionString;
        _db = await OpenAsync();
        await SqlAsync($"CREATE SCHEMA {_schema}");
        foreach (var table in Tables)
        {
            await SqlAsync($"CREATE TABLE {table} (id bigint PRIMARY KEY, season int, episode int, priority int NOT NULL DEFAULT 0, status text NOT NULL DEFAULT 'unknown', checked_at bigint, next_check_at bigint, lease_until bigint, title text); INSERT INTO {table} (id,season,episode,next_check_at,title) VALUES (1,1,1,100,'one'),(2,1,2,200,'two')");
        }

        await SqlAsync("CREATE TABLE build (id bigint); INSERT INTO build VALUES (1),(1)");
        _before = await SnapshotAsync();
        output.WriteLine("PostgreSQL " + await ScalarAsync("SHOW server_version"));
    }

    public async Task DisposeAsync()
    {
        if (_db is null) return;
        try
        {
            var after = await SnapshotAsync();
            output.WriteLine($"LEGACY before={_before} after={after} movie=2 episode=2 (includes ctid/xmin; no rewrite)");
            Assert.Equal(_before, after);
        }
        finally
        {
            await SqlAsync($"DROP SCHEMA {_schema} CASCADE");
            // Each fixture has a distinct SearchPath/pool. Keep pooled callers in
            // the tests, but retire only this fixture's idle sockets on teardown.
            NpgsqlConnection.ClearPool(_db);
            await _db.DisposeAsync();
        }
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var c = new NpgsqlConnection(_dsn);
        await c.OpenAsync();
        return c;
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection c, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, c) { CommandTimeout = 20 };
        var result = await cmd.ExecuteScalarAsync();
        return result is DBNull ? null : result;
    }
    private Task<object?> ScalarAsync(string sql) => ScalarAsync(_db, sql);
    private async Task SqlAsync(string sql) => _ = await ScalarAsync(sql);
    private async Task<string> SnapshotAsync() => (string)(await ScalarAsync("SELECT md5(" + string.Join(" || ", Tables.Select(t => $"(SELECT json_agg(r ORDER BY id)::text FROM (SELECT id,season,episode,priority,status,checked_at,next_check_at,lease_until,title,ctid::text,xmin::text FROM {t}) r)")) + ")"))!;
    private Task<bool> ApplyAsync(string name = "projection", string[]? sql = null) => SchemaExpandMigrator.ApplyAsync(_db, name, sql ?? Indexes, Projections(), default);
    private Task<bool> ReceiptAsync(string name = "projection") => SchemaExpandMigrator.IsAppliedAsync(_db, name, default);
    private async Task<string> OidsAsync() => (string)(await ScalarAsync($"SELECT string_agg(p.oid::text,',' ORDER BY p.proname) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='{_schema}'"))!;
    private async Task WaitAsync(string sql)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            if ((bool?)await ScalarAsync(sql) == true) return;
            await Task.Delay(20);
        }
        throw new TimeoutException(sql);
    }
    private async Task NoLocksAsync(string name)
    {
        await WaitAsync($"SELECT NOT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name='phantom-expand:{name}')");
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM pg_locks l JOIN pg_stat_activity a ON a.pid=l.pid WHERE a.application_name='phantom-expand:{name}'"));
    }

    [Fact]
    public async Task LiteralDefaultsNoRewriteAndReplayValidatesShape()
    {
        var files = await ScalarAsync($"SELECT string_agg(relfilenode::text,',' ORDER BY relname) FROM pg_class WHERE oid IN ('{_schema}.movie'::regclass,'{_schema}.episode'::regclass)");
        Assert.True(await ApplyAsync());
        Assert.Equal(_before, await SnapshotAsync());
        Assert.Equal(files, await ScalarAsync($"SELECT string_agg(relfilenode::text,',' ORDER BY relname) FROM pg_class WHERE oid IN ('{_schema}.movie'::regclass,'{_schema}.episode'::regclass)"));
        foreach (var table in Tables)
        {
            Assert.Equal(2L, await ScalarAsync($"SELECT count(*) FROM {table} WHERE bucket=-1 AND due=0"));
            Assert.Equal(true, await ScalarAsync($"SELECT indisvalid AND indisready AND indislive FROM pg_index WHERE indexrelid='{_schema}.{table}_schedule'::regclass"));
        }
        var oids = await OidsAsync();
        Assert.False(await ApplyAsync());
        Assert.Equal(oids, await OidsAsync());
        Assert.True(await ReceiptAsync());
        await NoLocksAsync("projection");
        output.WriteLine("CATALOG default-only rows, unchanged relfilenodes/ctid/xmin, valid concurrent indexes, replay retains function OIDs=" + oids);
    }

    [Theory]
    [InlineData("movie", false)]
    [InlineData("movie", true)]
    [InlineData("episode", false)]
    [InlineData("episode", true)]
    public async Task LegacyAndNewWritesMaintainOrderingAndDueImmediately(string table, bool newWriter)
    {
        Assert.True(await ApplyAsync());
        await using var tx = await _db.BeginTransactionAsync();
        var columns = newWriter ? ",bucket,due" : "";
        var extra = newWriter ? ",999,999" : "";
        await SqlAsync($"INSERT INTO {table}(id,season,episode,next_check_at,lease_until{columns}) VALUES (10,1,10,100,NULL{extra}),(11,1,11,100,50{extra}),(12,1,12,100,300{extra}),(13,1,13,NULL,NULL{extra})");
        Assert.Equal("10:1:100,11:1:100,12:1:300,13:1:0", await ScalarAsync($"SELECT string_agg(id||':'||bucket||':'||due,',' ORDER BY id) FROM {table} WHERE id>=10"));
        await SqlAsync($"UPDATE {table} SET priority=5 WHERE id=10");
        Assert.Equal(0L, await ScalarAsync($"SELECT bucket FROM {table} WHERE id=10"));
        await SqlAsync($"UPDATE {table} SET lease_until=500 WHERE id=10");
        Assert.Equal(500L, await ScalarAsync($"SELECT due FROM {table} WHERE id=10"));
        await SqlAsync($"UPDATE {table} SET priority=0,status='available',checked_at=120,next_check_at=600,lease_until=NULL WHERE id=10");
        Assert.Equal("2:600", await ScalarAsync($"SELECT bucket||':'||due FROM {table} WHERE id=10"));
        await SqlAsync($"UPDATE {table} SET status='unknown',checked_at=NULL WHERE id=10");
        Assert.Equal(1L, await ScalarAsync($"SELECT bucket FROM {table} WHERE id=10"));
        await SqlAsync($"UPDATE {table} SET checked_at=150 WHERE id=10");
        Assert.Equal(2L, await ScalarAsync($"SELECT bucket FROM {table} WHERE id=10"));
        await SqlAsync($"UPDATE {table} SET bucket=999,due=999 WHERE id=10");
        Assert.Equal("2:600", await ScalarAsync($"SELECT bucket||':'||due FROM {table} WHERE id=10"));
        // An unrelated update must not become an implicit backfill.
        await SqlAsync($"UPDATE {table} SET title='changed' WHERE id=1");
        Assert.Equal(-1L, await ScalarAsync($"SELECT bucket FROM {table} WHERE id=1"));
        // ON CONFLICT (the legacy upsert path) fires both INSERT and UPDATE correctly.
        await SqlAsync($"INSERT INTO {table}(id,priority,next_check_at) VALUES (10,7,900) ON CONFLICT(id) DO UPDATE SET priority=EXCLUDED.priority,next_check_at=EXCLUDED.next_check_at");
        Assert.Equal("0:900", await ScalarAsync($"SELECT bucket||':'||due FROM {table} WHERE id=10"));
        await tx.RollbackAsync();
        output.WriteLine($"WRITER {table} new={newWriter}: INSERT/upsert/acquire/complete/priority/status/checked_at; null, expired, future lease; output spoof overwritten; unrelated update not backfilled");
    }

    [Fact]
    public async Task FreshAndExpandedSchemasHaveEquivalentShape()
    {
        Assert.True(await ApplyAsync());
        var fresh = _schema + "_fresh";
        await SqlAsync($"CREATE SCHEMA {fresh}");
        try
        {
            await using var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_dsn) { SearchPath = fresh, Pooling = false }.ConnectionString);
            await c.OpenAsync();
            // A fresh builder installs the same closed definition, not a parallel function body.
            foreach (var table in Tables)
                await ScalarAsync(c, $"CREATE TABLE {table} (LIKE {_schema}.{table} INCLUDING DEFAULTS); ALTER TABLE {table} DROP COLUMN bucket, DROP COLUMN due");
            Assert.True(await SchemaExpandMigrator.ApplyAsync(c, "projection", Indexes, Projections(), default));
            async Task<string> Shape(NpgsqlConnection db) => (string)(await ScalarAsync(db, @"SELECT jsonb_build_object(
'columns',(SELECT jsonb_agg(jsonb_build_array(table_name,column_name,data_type,is_nullable,column_default) ORDER BY table_name,column_name) FROM information_schema.columns WHERE table_schema=current_schema() AND table_name IN ('movie','episode')),
'functions',(SELECT jsonb_agg(jsonb_build_array(proname,prosrc,provolatile,prosecdef,proconfig) ORDER BY proname) FROM pg_proc WHERE pronamespace=current_schema()::regnamespace),
'indexes',(SELECT jsonb_agg(jsonb_build_array(c.relname,pg_get_indexdef(i.indexrelid,1,false),pg_get_indexdef(i.indexrelid,2,false),pg_get_indexdef(i.indexrelid,3,false),i.indisvalid) ORDER BY c.relname) FROM pg_index i JOIN pg_class c ON c.oid=i.indexrelid WHERE c.relnamespace=current_schema()::regnamespace AND c.relname LIKE '%_schedule'),
 'triggers',(SELECT jsonb_agg(jsonb_build_array(tgname,tgtype,tgenabled,p.proname) ORDER BY tgname) FROM pg_trigger t JOIN pg_proc p ON p.oid=t.tgfoid WHERE p.pronamespace=current_schema()::regnamespace))::text"))!;
            Assert.Equal(await Shape(_db), await Shape(c));
            output.WriteLine("FRESH/EXPANDED: equal column/default, function/body/config, trigger/event, valid index/key shapes");
        }
        finally { await SqlAsync($"DROP SCHEMA {fresh} CASCADE"); }
    }

    [Fact]
    public async Task OldRawHelperRefusesTheLegitimateCompiledProjection()
    {
        var p = Projection("movie");
        var raw = $"CREATE FUNCTION {p.FunctionName}() RETURNS trigger LANGUAGE plpgsql IMMUTABLE AS '{p.Body.Replace("'", "''", StringComparison.Ordinal)}'";
        await Assert.ThrowsAsync<NonAdditiveSchemaChangeException>(() => SchemaExpandMigrator.ApplyAsync(_db, "old", new[] { raw }, default));
        Assert.Null(await ScalarAsync("SELECT to_regclass('phantom_expand_migrations')::text"));
        Assert.True(await ApplyAsync());
        output.WriteLine("REGRESSION old string-only helper refuses actual compiled projection; closed typed overload installs it");
    }

    [Theory]
    [InlineData("ALTER TABLE movie ADD COLUMN bucket bigint DEFAULT -1")]
    [InlineData("ALTER TABLE movie ADD COLUMN bucket bigint DEFAULT -1; ALTER TABLE movie ALTER COLUMN bucket SET DEFAULT random()")]
    [InlineData("CREATE FUNCTION movie_projection_v1_fn() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RETURN NEW; END'")]
    public async Task ExistingObjectsAreNeverAdoptedEvenIfCompatible(string collision)
    {
        await SqlAsync(collision);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync());
        Assert.False(await ReceiptAsync());
        Assert.Null(await ScalarAsync("SELECT to_regclass('movie_schedule')::text"));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM phantom_expand_row_projections"));
    }

    [Theory]
    [InlineData("ALTER TABLE movie DISABLE TRIGGER movie_projection_v1_tr")]
    [InlineData("ALTER TABLE movie ENABLE ALWAYS TRIGGER movie_projection_v1_tr")]
    [InlineData("DROP TRIGGER movie_projection_v1_tr ON movie")]
    [InlineData("ALTER TABLE movie DROP COLUMN due CASCADE")]
    [InlineData("ALTER FUNCTION movie_projection_v1_fn() VOLATILE")]
    [InlineData("ALTER FUNCTION movie_projection_v1_fn() SECURITY DEFINER")]
    [InlineData("ALTER FUNCTION movie_projection_v1_fn() SET search_path TO public")]
    [InlineData("CREATE OR REPLACE FUNCTION movie_projection_v1_fn() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN NEW.priority := 999; RETURN NEW; END'")]
    [InlineData("DROP FUNCTION movie_projection_v1_fn() CASCADE")]
    [InlineData("ALTER TABLE movie ALTER COLUMN bucket SET DEFAULT 42")]
    [InlineData("ALTER TABLE movie ALTER COLUMN due SET NOT NULL")]
    [InlineData("DROP TRIGGER movie_projection_v1_tr ON movie; CREATE TRIGGER movie_projection_v1_tr BEFORE INSERT ON episode FOR EACH ROW EXECUTE FUNCTION movie_projection_v1_fn()")]
    public async Task ReceiptDoesNotHideCatalogueDriftAndNoDefinitionIsReplaced(string drift)
    {
        Assert.True(await ApplyAsync());
        await SqlAsync(drift);
        Assert.True(await ReceiptAsync());
        var functions = await OidsAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync());
        Assert.Equal(functions, await OidsAsync());
        Assert.False(await ReceiptAsync());
        await NoLocksAsync("projection");
        output.WriteLine("DRIFT refused without replace: " + drift);
    }

    [Fact]
    public async Task ExternalIdenticalReplacementOidIsNotOwned()
    {
        Assert.True(await ApplyAsync());
        var body = Projection("movie").Body.Replace("'", "''", StringComparison.Ordinal);
        await SqlAsync($"DROP FUNCTION movie_projection_v1_fn() CASCADE; CREATE FUNCTION movie_projection_v1_fn() RETURNS trigger LANGUAGE plpgsql IMMUTABLE SECURITY INVOKER SET search_path TO pg_catalog AS '{body}'; CREATE TRIGGER movie_projection_v1_tr BEFORE INSERT OR UPDATE OF bucket,checked_at,due,lease_until,next_check_at,priority,status ON movie FOR EACH ROW EXECUTE FUNCTION movie_projection_v1_fn()");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync());
        Assert.False(await ReceiptAsync());
    }

    [Fact]
    public async Task FutureDefinitionOtherOwnerAndOmissionAreRefused()
    {
        Assert.True(await SchemaExpandMigrator.ApplyAsync(_db, "future", Indexes, Tables.Select(t => Projection(t, 2)).ToArray(), default));
        var oids = await OidsAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync("future"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ApplyAsync("other"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaExpandMigrator.ApplyAsync(_db, "future", Indexes, default));
        Assert.Equal(oids, await OidsAsync());
        Assert.False(await ReceiptAsync("future"));
        Assert.False(await ReceiptAsync("other"));
    }

    [Fact]
    public async Task LegacyColumnMutationWrongSourceAndForeignTargetsAreRefusedAtomically()
    {
        foreach (var p in new[] {
            new AdditiveRowProjection("bad",1,"movie", new RowProjectionColumn("priority",0,E.Number(1))),
            new AdditiveRowProjection("bad",1,"movie", new RowProjectionColumn("bad",0,E.NumberColumn("status"))),
            new AdditiveRowProjection("bad",1,"missing", new RowProjectionColumn("bad",0,E.Number(1))) })
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaExpandMigrator.ApplyAsync(_db, "bad", Array.Empty<string>(), new[] { Projection("episode"), p }, default));
            Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM information_schema.columns WHERE table_schema='{_schema}' AND column_name IN ('bucket','due','bad')"));
        }
        Assert.Throws<ArgumentException>(() => new AdditiveRowProjection("bad",1,"public.movie",new RowProjectionColumn("bad",0,E.Number(1))));
        Assert.Throws<ArgumentException>(() => new AdditiveRowProjection("bad",1,"movie",new RowProjectionColumn("priority",0,E.NumberColumn("priority"))));
        Assert.Throws<ArgumentException>(() => new AdditiveRowProjection("bad",1,"movie",new RowProjectionColumn("bad",0,E.Text("arbitrary SQL"))));
        Assert.Throws<ArgumentException>(() => E.NumberColumn("priority); UPDATE movie SET priority=9;--"));
        Assert.False(await ReceiptAsync("bad"));
    }

    [Theory]
    [InlineData("DO $$ BEGIN NULL; END $$")]
    [InlineData("ALTER TABLE movie ADD COLUMN IF NOT EXISTS bad bigint DEFAULT random()")]
    [InlineData("ALTER TABLE movie ADD COLUMN IF NOT EXISTS bad bigint GENERATED ALWAYS AS (priority) STORED")]
    [InlineData("CREATE TRIGGER bad BEFORE UPDATE ON movie FOR EACH ROW EXECUTE FUNCTION bad()")]
    public async Task TypedApiDoesNotWidenTheRawSqlGrammar(string sql)
    {
        await Assert.ThrowsAsync<NonAdditiveSchemaChangeException>(() => ApplyAsync("unsafe", new[] { sql }));
        Assert.Null(await ScalarAsync("SELECT to_regclass('phantom_expand_migrations')::text"));
    }

    [Fact]
    public async Task TransactionalFailureRollsBackColumnsFunctionTriggerAndOwnership()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaExpandMigrator.ApplyAsync(_db, "rollback", Array.Empty<string>(), new[] { Projection("movie"), Projection("absent") }, default));
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM information_schema.columns WHERE table_schema='{_schema}' AND column_name='bucket'"));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM phantom_expand_row_projections"));
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM pg_proc WHERE pronamespace='{_schema}'::regnamespace"));
        Assert.False(await ReceiptAsync("rollback"));
    }

    [Fact]
    public async Task InvalidConcurrentSuffixKeepsAtomicPrefixAndRestartRepairs()
    {
        var sql = Indexes.Append(Unique).ToArray();
        var failure = await Assert.ThrowsAsync<PostgresException>(() => ApplyAsync("failure", sql));
        Assert.Equal("23505", failure.SqlState);
        Assert.False(await ReceiptAsync("failure"));
        Assert.Equal(2L, await ScalarAsync("SELECT count(*) FROM phantom_expand_row_projections"));
        await using (var tx = await _db.BeginTransactionAsync())
        {
            foreach (var table in Tables)
            {
                await SqlAsync($"INSERT INTO {table}(id,next_check_at,lease_until) VALUES (100,25,50)");
                Assert.Equal("1:50", await ScalarAsync($"SELECT bucket||':'||due FROM {table} WHERE id=100"));
            }
            await tx.RollbackAsync();
        }
        var oids = await OidsAsync();
        var invalid = await ScalarAsync("SELECT 'build_idx'::regclass::oid::bigint");
        await SqlAsync("TRUNCATE build; INSERT INTO build VALUES (1),(2)");
        Assert.True(await ApplyAsync("failure", sql));
        Assert.Equal(oids, await OidsAsync());
        Assert.NotEqual(invalid, await ScalarAsync("SELECT 'build_idx'::regclass::oid::bigint"));
        Assert.Equal(true, await ScalarAsync("SELECT indisvalid FROM pg_index WHERE indexrelid='build_idx'::regclass"));
        Assert.False(await ApplyAsync("failure", sql));
        output.WriteLine("PHASE SQLSTATE 23505: atomic prefix live for both legacy writers, no receipt; restart repairs invalid index only");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrBackendLossAfterPrefixCannotLeakLocksOrReceipt(bool crash)
    {
        await SqlAsync("TRUNCATE build; INSERT INTO build VALUES (1),(2)");
        await using var blocker = await OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await ScalarAsync(blocker, "UPDATE build SET id=id WHERE id=1");
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var sql = Indexes.Append(Unique).ToArray();
        await using var caller = await OpenAsync();
        var apply = SchemaExpandMigrator.ApplyAsync(caller, "interrupt", sql, Projections(), cancel.Token);
        // Observe on a separate connection, including the initial current_schema read.
        await WaitAsync($"SELECT to_regclass('{_schema}.build_idx') IS NOT NULL");
        Assert.Equal(2L, await ScalarAsync("SELECT count(*) FROM phantom_expand_row_projections"));
        Assert.False(await ReceiptAsync("interrupt"));
        if (crash)
        {
            Assert.Equal(true, await ScalarAsync("SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name='phantom-expand:interrupt'"));
            await Assert.ThrowsAnyAsync<NpgsqlException>(() => apply);
        }
        else
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
        }
        await NoLocksAsync("interrupt");
        Assert.False(await ReceiptAsync("interrupt"));
        await tx.RollbackAsync();
        Assert.True(await ApplyAsync("interrupt", sql));
        Assert.True(await ReceiptAsync("interrupt"));
        Assert.Equal(1, await ScalarAsync("SELECT 1"));
        output.WriteLine($"INTERRUPT crash={crash}: committed projection, absent receipt, closed unpooled session, restart valid");
    }

    [Fact]
    public async Task FinalSuffixVerificationRefusesConcurrentProjectionDrift()
    {
        await SqlAsync("TRUNCATE build; INSERT INTO build VALUES (1),(2)");
        await using var blocker = await OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await ScalarAsync(blocker, "UPDATE build SET id=id WHERE id=1");
        await using var caller = await OpenAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var apply = SchemaExpandMigrator.ApplyAsync(caller, "late-drift", new[] { Unique }, Projections(), timeout.Token);
        await WaitAsync($"SELECT to_regclass('{_schema}.build_idx') IS NOT NULL");
        await SqlAsync("ALTER TABLE movie DISABLE TRIGGER movie_projection_v1_tr");
        await tx.RollbackAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => apply);
        Assert.False(await ReceiptAsync("late-drift"));
        await NoLocksAsync("late-drift");
        output.WriteLine("FINAL VERIFY: concurrent external trigger disable during suffix detected, completed receipt refused");
    }

    [Fact]
    public async Task FullBigintLiteralRangeDoesNotBreakLegacyWrites()
    {
        var projection = new AdditiveRowProjection("bounds", 1, "movie",
            new RowProjectionColumn("minimum", long.MinValue, E.Number(long.MinValue)),
            new RowProjectionColumn("maximum", long.MaxValue, E.Number(long.MaxValue)));
        Assert.True(await SchemaExpandMigrator.ApplyAsync(_db, "bounds", Array.Empty<string>(), new[] { projection }, default));
        Assert.Equal(long.MinValue, await ScalarAsync("SELECT minimum FROM movie WHERE id=1"));
        Assert.Equal(long.MaxValue, await ScalarAsync("SELECT maximum FROM movie WHERE id=1"));
        await using var tx = await _db.BeginTransactionAsync();
        await SqlAsync("INSERT INTO movie (id) VALUES (10)");
        Assert.Equal(long.MinValue, await ScalarAsync("SELECT minimum FROM movie WHERE id=10"));
        Assert.Equal(long.MaxValue, await ScalarAsync("SELECT maximum FROM movie WHERE id=10"));
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task TransactionOnlyProjectionTreatsSqlLikeTextAsData()
    {
        const string literal = "unknown'; UPDATE movie SET priority=999; -- $$";
        var projection = new AdditiveRowProjection("literal", 1, "movie", new RowProjectionColumn("literal_result", -1,
            E.Choose(E.Equal(E.TextColumn("status"), E.Text(literal)), E.Number(7), E.Number(8))));
        Assert.True(await SchemaExpandMigrator.ApplyAsync(_db, "literal", Array.Empty<string>(), new[] { projection }, default));
        Assert.True(await ReceiptAsync("literal"));
        Assert.False(await SchemaExpandMigrator.ApplyAsync(_db, "literal", Array.Empty<string>(), new[] { projection }, default));
        await using var tx = await _db.BeginTransactionAsync();
        await using var cmd = new NpgsqlCommand("INSERT INTO movie (id,status) VALUES (10,@status) RETURNING literal_result", _db);
        cmd.Parameters.AddWithValue("status", literal);
        Assert.Equal(7L, await cmd.ExecuteScalarAsync());
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM movie WHERE priority=999"));
        await tx.RollbackAsync();
        output.WriteLine("CLOSED COMPILER: SQL-like literal remains data, transaction-only receipt/replay verified");
    }

    [Fact]
    public async Task CancellationInsidePrefixRollsBackAllProjectionObjects()
    {
        await using var blocker = await OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await ScalarAsync(blocker, "SELECT count(*) FROM episode");
        await using var caller = await OpenAsync();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var apply = SchemaExpandMigrator.ApplyAsync(caller, "prefixcancel", Indexes, Projections(), cancel.Token);
        await WaitAsync("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name='phantom-expand:prefixcancel' AND wait_event_type='Lock' AND query LIKE '%episode%')");
        // Movie projection has been installed but is still invisible to old writers.
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM information_schema.columns WHERE table_schema='{_schema}' AND column_name='bucket'"));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);
        await NoLocksAsync("prefixcancel");
        Assert.False(await ReceiptAsync("prefixcancel"));
        Assert.Equal(0L, await ScalarAsync("SELECT count(*) FROM phantom_expand_row_projections"));
        Assert.Equal(0L, await ScalarAsync($"SELECT count(*) FROM pg_proc WHERE pronamespace='{_schema}'::regnamespace"));
        await tx.RollbackAsync();
        Assert.True(await ApplyAsync("prefixcancel"));
        output.WriteLine("CANCEL prefix: invisible partial movie projection rolled back, zero functions/ownership, restart installs both");
    }

    [Fact]
    public async Task RacingColorsAndCancelledWaiterUseOneOwner()
    {
        await SqlAsync("TRUNCATE build; INSERT INTO build VALUES (1),(2)");
        await using var blocker = await OpenAsync();
        await using var tx = await blocker.BeginTransactionAsync();
        await ScalarAsync(blocker, "UPDATE build SET id=id WHERE id=1");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var sql = Indexes.Append(Unique).ToArray();
        await using var first = await OpenAsync();
        var a = SchemaExpandMigrator.ApplyAsync(first, "race", sql, Projections(), timeout.Token);
        await WaitAsync($"SELECT to_regclass('{_schema}.build_idx') IS NOT NULL");
        await using var other = await OpenAsync();
        var b = SchemaExpandMigrator.ApplyAsync(other, "race", sql, Projections(), timeout.Token);
        await using var waiting = await OpenAsync();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var c = SchemaExpandMigrator.ApplyAsync(waiting, "waiter", sql, Projections(), cancel.Token);
        await WaitAsync("SELECT count(*)=3 FROM pg_stat_activity WHERE application_name IN ('phantom-expand:race','phantom-expand:waiter')");
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c);
        await NoLocksAsync("waiter");
        Assert.False(b.IsCompleted);
        await tx.RollbackAsync();
        var results = await Task.WhenAll(a,b);
        Assert.Equal(1, results.Count(v => v));
        Assert.Equal(2L, await ScalarAsync("SELECT count(*) FROM phantom_expand_row_projections"));
        await NoLocksAsync("race");
        output.WriteLine("RACE applied=1 replay=1; cancelled waiter closed; one owner per projection, no advisory locks in pool");
    }
}
