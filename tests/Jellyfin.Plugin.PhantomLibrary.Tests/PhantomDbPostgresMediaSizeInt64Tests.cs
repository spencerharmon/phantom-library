using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Jellyfin.Plugin.PhantomLibrary.State;
using Jellyfin.Plugin.PhantomLibrary.State.Db;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// Real-PostgreSQL persistence integration for <c>postgres-media-size-int64-expand</c>.
/// PostgreSQL <c>INTEGER</c> is 32-bit, so a real 5 GiB movie/episode size threw
/// 22003 "integer out of range" in <see cref="PhantomDb.UpsertSourceCandidatesAsync"/>.
/// The v23 EXPAND adds BIGINT <c>*_bytes</c> companions, dual-writes, and resolves
/// reads via <see cref="PhantomDb.ResolvedSizeSql"/> so a pre-v23 (legacy) writer
/// sharing the database keeps working.
///
/// Driven by <c>scripts/tests/postgres-media-size-int64.test.sh</c>, which provisions
/// an isolated PostgreSQL and sets <c>PHANTOM_TEST_POSTGRES_DSN</c> plus
/// <c>PHANTOM_TEST_POSTGRES_REQUIRED=1</c>. With REQUIRED set, a missing DSN FAILS
/// the tests (no silent skip); without it (a plain <c>dotnet test</c> on a host with
/// no Postgres) the tests return immediately, like the sibling Postgres suites.
///
/// The <c>Legacy*Sql</c> constants are the pre-v23 statements copied verbatim from
/// <c>PhantomDb.cs</c> at the parent commit (b04395f2) — they stand in for the
/// still-running older color during the blue/green overlap window.
/// </summary>
public sealed class PhantomDbPostgresMediaSizeInt64Tests : IAsyncLifetime
{
    /// <summary>5 GiB — a real movie/episode size well past int32 (2 GiB - 1).</summary>
    private const long FiveGiB = 5L * 1024 * 1024 * 1024;

    private const long SixGiB = 6L * 1024 * 1024 * 1024;

    private const long SevenHundredMiB = 700L * 1024 * 1024;

    private const string MigrationName = "v22_v23_media_size_bigint_companions";

    // ---- pre-v23 (legacy writer / reader) SQL, verbatim from the parent commit ----

    private const string LegacyUpsertSourceCandidateSql = @"INSERT INTO source_candidates
                    (tmdb_id,type,season,episode,preset,magnet,info_hash,indexer,title,seeders,size,rank,source,fetched_at,expires_at)
                    VALUES (@tmdb,@type,@season,@episode,@preset,@magnet,@hash,@indexer,@title,@seeders,@size,@rank,@source,@fetched,@expires)
                    ON CONFLICT(tmdb_id,type,season,episode,preset,magnet) DO UPDATE SET
                        info_hash=excluded.info_hash,
                        indexer=excluded.indexer,
                        title=excluded.title,
                        seeders=excluded.seeders,
                        size=excluded.size,
                        rank=excluded.rank,
                        source=excluded.source,
                        fetched_at=excluded.fetched_at,
                        expires_at=excluded.expires_at;";

    private const string LegacyListSourceCandidatesSql = @"SELECT tmdb_id,type,season,episode,preset,magnet,info_hash,indexer,title,seeders,size,rank,source,fetched_at,expires_at,
                   validation_status,validation_reason,validated_at,validation_expires_at,validation_duration_ms,validation_policy_version,
                   selected_file_id,selected_file_path,selected_file_size
            FROM source_candidates
            WHERE tmdb_id=@tmdb AND type=@type AND season=@season AND episode=@episode AND preset=@preset
              AND (@includeExpired=1 OR expires_at >= @now)
            ORDER BY rank ASC, seeders DESC, size DESC;";

    private const string LegacyCompleteAvailabilitySql = @"UPDATE availability_items SET
                    status=@status,
                    checked_at=@checked,
                    next_check_at=@next,
                    candidate_magnet=@magnet,
                    candidate_info_hash=@hash,
                    candidate_size=@size,
                    candidate_seeders=@seeders,
                    candidate_indexer=@indexer,
                    candidate_source=@source,
                    probe_policy_hash=@policy,
                    last_error_kind=@errKind,
                    last_error_message=@errMsg,
                    lease_owner=NULL,
                    lease_until=NULL,
                    attempt_count=0,
                    negative_streak=@negStreak
                WHERE tmdb_id=@tmdb AND type=@type AND season=@season AND episode=@episode
                  AND lease_owner=@owner AND probe_generation=@generation;";

    // PostgresDbProvider.TranslateUpsertSql output for the legacy magnet_cache
    // "INSERT OR REPLACE" (pre-v23 column list, no size_bytes).
    private const string LegacyPutMagnetSql = @"INSERT INTO magnet_cache (tmdb_id, imdb_id, type, season, episode, preset, magnet, info_hash, size, seeders, indexer, cached_at, ttl_seconds, source)
        VALUES (@tmdb,@imdb,@type,@season,@episode,@preset,@magnet,@hash,@size,@seeders,@indexer,@cached,@ttl,@source)
        ON CONFLICT (tmdb_id, imdb_id, type, season, episode, preset) DO UPDATE SET magnet = EXCLUDED.magnet, info_hash = EXCLUDED.info_hash, size = EXCLUDED.size, seeders = EXCLUDED.seeders, indexer = EXCLUDED.indexer, cached_at = EXCLUDED.cached_at, ttl_seconds = EXCLUDED.ttl_seconds, source = EXCLUDED.source;";

    private static readonly string? Dsn = Environment.GetEnvironmentVariable("PHANTOM_TEST_POSTGRES_DSN");

    private static readonly bool Required =
        string.Equals(Environment.GetEnvironmentVariable("PHANTOM_TEST_POSTGRES_REQUIRED"), "1", StringComparison.Ordinal);

    private readonly ITestOutputHelper _output;
    private readonly string _schema = "phantom_size_it_" + Guid.NewGuid().ToString("N");
    private string? _connectionString;

    public PhantomDbPostgresMediaSizeInt64Tests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static bool Enabled
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Dsn))
            {
                return true;
            }

            if (Required)
            {
                throw new InvalidOperationException(
                    "PHANTOM_TEST_POSTGRES_REQUIRED=1 but PHANTOM_TEST_POSTGRES_DSN is unset — refusing to skip.");
            }

            return false;
        }
    }

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(Dsn))
        {
            return;
        }

        _connectionString = new NpgsqlConnectionStringBuilder(Dsn) { SearchPath = _schema }.ToString();
        await ExecAsync(Dsn!, $"CREATE SCHEMA IF NOT EXISTS {_schema};");
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(Dsn))
        {
            return;
        }

        await ExecAsync(Dsn!, $"DROP SCHEMA IF EXISTS {_schema} CASCADE;");
    }

    // ------------------------------------------------------------------
    // Fresh schema
    // ------------------------------------------------------------------

    [Fact]
    public async Task FreshSchema_HasBigintCompanions_AndLegacyIntegerColumnsUntouched()
    {
        if (!Enabled)
        {
            return;
        }

        using (var db = NewDb())
        {
            await db.CountCatalogueItemsAsync("movie", null, default);
        }

        Assert.Equal(PhantomDb.CurrentSchemaVersion, await ReadVersionAsync());
        Assert.Equal(23, PhantomDb.CurrentSchemaVersion);

        // Companions are BIGINT and nullable.
        Assert.Equal(("bigint", "YES"), await ColumnTypeAsync("source_candidates", "size_bytes"));
        Assert.Equal(("bigint", "YES"), await ColumnTypeAsync("source_candidates", "selected_file_size_bytes"));
        Assert.Equal(("bigint", "YES"), await ColumnTypeAsync("availability_items", "candidate_size_bytes"));
        Assert.Equal(("bigint", "YES"), await ColumnTypeAsync("magnet_cache", "size_bytes"));

        // Legacy columns stay INTEGER (expand never retypes / drops).
        Assert.Equal(("integer", "YES"), await ColumnTypeAsync("source_candidates", "size"));
        Assert.Equal(("integer", "YES"), await ColumnTypeAsync("source_candidates", "selected_file_size"));
        Assert.Equal(("integer", "YES"), await ColumnTypeAsync("availability_items", "candidate_size"));
        Assert.Equal(("integer", "NO"), await ColumnTypeAsync("magnet_cache", "size"));
    }

    // ------------------------------------------------------------------
    // >= 5 GiB round trips: source candidates (movie + episode)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(603, "movie", -1, -1)]
    [InlineData(1399, "episode", 1, 3)]
    public async Task SourceCandidates_FiveGiB_RoundTrip(int tmdb, string type, int season, int episode)
    {
        if (!Enabled)
        {
            return;
        }

        using var db = NewDb();
        var candidates = new[]
        {
            new MagnetCandidate("magnet:?xt=urn:btih:" + new string('a', 40), new string('a', 40), FiveGiB, 120, "prowlarr") { Title = "Big.2160p" },
            new MagnetCandidate("magnet:?xt=urn:btih:" + new string('b', 40), new string('b', 40), SevenHundredMiB, 80, "prowlarr") { Title = "Small.720p" },
        };

        // Without the fix this throws PostgresException 22003 (integer out of range).
        await db.UpsertSourceCandidatesAsync(tmdb, type, season, episode, "1080p", candidates, "availability", TimeSpan.FromHours(6), default);

        var rows = await db.ListSourceCandidatesAsync(tmdb, type, season, episode, "1080p", includeExpired: false, default);
        Assert.Equal(2, rows.Count);
        Assert.Equal(FiveGiB, rows.Single(r => r.InfoHash == new string('a', 40)).Size);
        Assert.Equal(SevenHundredMiB, rows.Single(r => r.InfoHash == new string('b', 40)).Size);

        // Selected-file size (>= 5 GiB) round-trips through validation update.
        await db.UpdateSourceCandidateValidationAsync(
            new SourceCandidateValidationUpdate(
                tmdb, type, season, episode, "1080p", candidates[0].Magnet,
                "valid", null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), 42, "v1",
                SelectedFileId: 0, SelectedFilePath: "Big.2160p/Big.2160p.mkv", SelectedFileSize: SixGiB),
            default);
        rows = await db.ListSourceCandidatesAsync(tmdb, type, season, episode, "1080p", includeExpired: false, default);
        var big = rows.Single(r => r.InfoHash == new string('a', 40));
        Assert.Equal(FiveGiB, big.Size);
        Assert.Equal(SixGiB, big.SelectedFileSize);

        // Raw storage: legacy INTEGER holds the overflow sentinel (never a
        // clamp/truncation), the BIGINT companion holds the exact value.
        await using var conn = await OpenRawAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT size, size_bytes, selected_file_size, selected_file_size_bytes FROM source_candidates WHERE tmdb_id=@t AND info_hash=@h;";
        cmd.Parameters.AddWithValue("t", tmdb);
        cmd.Parameters.AddWithValue("h", new string('a', 40));
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        Assert.Equal(-1, r.GetInt32(0));
        Assert.Equal(FiveGiB, r.GetInt64(1));
        Assert.Equal(-1, r.GetInt32(2));
        Assert.Equal(SixGiB, r.GetInt64(3));

        // Clear drops both selected-file columns.
        await db.ClearSourceCandidateValidationAsync(tmdb, type, season, episode, "1080p", default);
        rows = await db.ListSourceCandidatesAsync(tmdb, type, season, episode, "1080p", includeExpired: false, default);
        Assert.Null(rows.Single(x => x.InfoHash == new string('a', 40)).SelectedFileSize);
        Assert.Equal(FiveGiB, rows.Single(x => x.InfoHash == new string('a', 40)).Size);
    }

    // ------------------------------------------------------------------
    // >= 5 GiB round trips: magnet cache (movie + episode)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(603, "movie", null, null)]
    [InlineData(1399, "episode", 1, 3)]
    public async Task MagnetCache_FiveGiB_RoundTrip(int tmdb, string type, int? season, int? episode)
    {
        if (!Enabled)
        {
            return;
        }

        using var db = NewDb();
        var key = new MagnetCacheKey(tmdb, null, type, season, episode, "1080p");
        await db.PutCachedMagnetAsync(key, Entry(FiveGiB, 'c'), default);
        Assert.Equal(FiveGiB, (await db.GetCachedMagnetAsync(key, default))!.Size);

        // Upsert path (ON CONFLICT DO UPDATE) with a different big size.
        await db.PutCachedMagnetAsync(key, Entry(SixGiB, 'c'), default);
        Assert.Equal(SixGiB, (await db.GetCachedMagnetAsync(key, default))!.Size);

        // Small value still dual-writes identically.
        await db.PutCachedMagnetAsync(key, Entry(SevenHundredMiB, 'c'), default);
        Assert.Equal(SevenHundredMiB, (await db.GetCachedMagnetAsync(key, default))!.Size);
    }

    // ------------------------------------------------------------------
    // >= 5 GiB availability round trips + generation-fenced completion
    // ------------------------------------------------------------------

    [Fact]
    public async Task Availability_MovieAndEpisode_FiveGiB_RoundTrip_ViaMarkAvailable()
    {
        if (!Enabled)
        {
            return;
        }

        using var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        await db.UpsertCatalogueHitsAsync(new[] { Meta(603, "movie") }, 1, now, default);

        await db.MarkAvailabilityAvailableAsync(603, "movie", -1, -1, Entry(FiveGiB, 'd'), default);
        await db.MarkAvailabilityAvailableAsync(1399, "episode", 1, 3, Entry(SixGiB, 'e'), default);

        Assert.Equal(FiveGiB, (await db.GetAvailabilityItemAsync(603, "movie", -1, -1, default))!.CandidateSize);
        Assert.Equal(SixGiB, (await db.GetAvailabilityItemAsync(1399, "episode", 1, 3, default))!.CandidateSize);

        // Browse read path (aliased a.candidate_size) resolves the 64-bit value too.
        var visible = await db.ListAllMovieRowsAsync(default);
        Assert.Equal(FiveGiB, visible.Single(v => v.Metadata.TmdbId == 603).Availability!.CandidateSize);

        // A candidate-less re-mark keeps the existing (COALESCE) 64-bit size.
        await db.MarkAvailabilityAvailableAsync(603, "movie", -1, -1, null, default);
        Assert.Equal(FiveGiB, (await db.GetAvailabilityItemAsync(603, "movie", -1, -1, default))!.CandidateSize);

        // A new small candidate replaces it exactly.
        await db.MarkAvailabilityAvailableAsync(603, "movie", -1, -1, Entry(SevenHundredMiB, 'f'), default);
        Assert.Equal(SevenHundredMiB, (await db.GetAvailabilityItemAsync(603, "movie", -1, -1, default))!.CandidateSize);
    }

    [Theory]
    [InlineData(603, "movie", -1, -1)]
    [InlineData(1399, "episode", 2, 5)]
    public async Task Availability_GenerationFencedCompletion_FiveGiB(int tmdb, string type, int season, int episode)
    {
        if (!Enabled)
        {
            return;
        }

        using var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        // Seed an availability row, then make it due by forcing next_check_at into the past.
        await db.MarkAvailabilityAvailableAsync(tmdb, type, season, episode, null, default);
        await ExecAsync(_connectionString!, "UPDATE availability_items SET next_check_at = 0;");

        var lease = await db.ClaimDueAvailabilityAsync("worker-a", TimeSpan.FromMinutes(5), now, "policy-1", default, type);
        Assert.NotNull(lease);
        Assert.Equal(tmdb, lease!.TmdbId);
        Assert.Equal(type, lease.Type);

        // A stale generation (superseded lease) must NOT complete or write the size.
        var stale = lease with { ProbeGeneration = lease.ProbeGeneration - 1 };
        Assert.False(await db.CompleteAvailabilityProbeAsync(
            stale, "available", now, now.AddDays(1), "policy-1", Entry(SixGiB, 'g'), null, null, default));
        Assert.Null((await db.GetAvailabilityItemAsync(tmdb, type, season, episode, default))!.CandidateSize);

        // The current-generation lease completes with the exact >= 5 GiB size.
        Assert.True(await db.CompleteAvailabilityProbeAsync(
            lease, "available", now, now.AddDays(1), "policy-1", Entry(FiveGiB, 'h'), null, null, default));
        var row = (await db.GetAvailabilityItemAsync(tmdb, type, season, episode, default))!;
        Assert.Equal("available", row.Status);
        Assert.Equal(FiveGiB, row.CandidateSize);
        Assert.Null(row.LeaseOwner);

        // Replaying the same (now released) lease is fenced out and changes nothing.
        Assert.False(await db.CompleteAvailabilityProbeAsync(
            lease, "available", now, now.AddDays(1), "policy-1", Entry(SixGiB, 'i'), null, null, default));
        Assert.Equal(FiveGiB, (await db.GetAvailabilityItemAsync(tmdb, type, season, episode, default))!.CandidateSize);
    }

    // ------------------------------------------------------------------
    // Legacy (pre-v23) writer / reader overlap on the expanded schema
    // ------------------------------------------------------------------

    [Fact]
    public async Task LegacyWriterOverlap_SourceCandidates_LegacyValueWinsOverStaleCompanion()
    {
        if (!Enabled)
        {
            return;
        }

        using var db = NewDb();
        var magnet = "magnet:?xt=urn:btih:" + new string('j', 40);
        await db.UpsertSourceCandidatesAsync(
            603, "movie", -1, -1, "1080p",
            new[] { new MagnetCandidate(magnet, new string('j', 40), FiveGiB, 10, "prowlarr") { Title = "Big" } },
            "availability", TimeSpan.FromHours(6), default);

        // 1. Legacy READER on a new-writer 5 GiB row: no exception, sees the
        //    non-positive "unknown" sentinel (never a wrong positive size).
        await using (var conn = await OpenRawAsync())
        {
            var legacyRead = await LegacyReadSizeAsync(conn, 603, "movie", -1, -1);
            Assert.Equal(-1L, legacyRead);
        }

        // 2. Legacy WRITER cannot store a 5 GiB size in the INTEGER column
        //    (documents the original 22003 failure the expand works around).
        await using (var conn = await OpenRawAsync())
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(
                () => LegacyUpsertCandidateAsync(conn, 603, "movie", -1, -1, magnet, FiveGiB));
            Assert.Equal("22003", ex.SqlState);
        }

        // 3. Legacy WRITER overwrites the same candidate with a small size: its
        //    value wins; the stale 5 GiB companion is no longer witnessed.
        await using (var conn = await OpenRawAsync())
        {
            await LegacyUpsertCandidateAsync(conn, 603, "movie", -1, -1, magnet, SevenHundredMiB);
        }

        var rows = await db.ListSourceCandidatesAsync(603, "movie", -1, -1, "1080p", includeExpired: false, default);
        Assert.Equal(SevenHundredMiB, rows.Single().Size);

        // 4. Legacy WRITER inserts a brand-new episode candidate (companion NULL):
        //    the new reader sees the legacy value unchanged.
        await using (var conn = await OpenRawAsync())
        {
            await LegacyUpsertCandidateAsync(conn, 1399, "episode", 1, 3, "magnet:?xt=urn:btih:" + new string('k', 40), 1_500_000_000L);
        }

        var ep = await db.ListSourceCandidatesAsync(1399, "episode", 1, 3, "1080p", includeExpired: false, default);
        Assert.Equal(1_500_000_000L, ep.Single().Size);

        // 5. The new writer re-upserts 5 GiB over the legacy row -> 5 GiB again.
        await db.UpsertSourceCandidatesAsync(
            603, "movie", -1, -1, "1080p",
            new[] { new MagnetCandidate(magnet, new string('j', 40), FiveGiB, 10, "prowlarr") { Title = "Big" } },
            "availability", TimeSpan.FromHours(6), default);
        rows = await db.ListSourceCandidatesAsync(603, "movie", -1, -1, "1080p", includeExpired: false, default);
        Assert.Equal(FiveGiB, rows.Single().Size);
    }

    [Fact]
    public async Task LegacyWriterOverlap_Availability_LegacyCompletionClearsOrReplacesStaleCompanion()
    {
        if (!Enabled)
        {
            return;
        }

        using var db = NewDb();
        await db.MarkAvailabilityAvailableAsync(603, "movie", -1, -1, Entry(FiveGiB, 'l'), default);
        await db.MarkAvailabilityAvailableAsync(1399, "episode", 1, 3, Entry(FiveGiB, 'm'), default);
        await ExecAsync(_connectionString!, "UPDATE availability_items SET next_check_at = 0;");

        // Legacy writer completes the MOVIE as unavailable (candidate NULL):
        // the new reader must report NULL, not the stale 5 GiB companion.
        var movieLease = await db.ClaimDueAvailabilityAsync("old-color", TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow, "p", default, "movie");
        Assert.NotNull(movieLease);
        await using (var conn = await OpenRawAsync())
        {
            Assert.Equal(1, await LegacyCompleteAsync(conn, movieLease!, "unavailable", null, null));
        }

        var movie = (await db.GetAvailabilityItemAsync(603, "movie", -1, -1, default))!;
        Assert.Equal("unavailable", movie.Status);
        Assert.Null(movie.CandidateMagnet);
        Assert.Null(movie.CandidateSize);

        // Legacy writer completes the EPISODE with a different, small candidate:
        // its value wins over the stale companion.
        var epLease = await db.ClaimDueAvailabilityAsync("old-color", TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow, "p", default, "episode");
        Assert.NotNull(epLease);
        await using (var conn = await OpenRawAsync())
        {
            Assert.Equal(1, await LegacyCompleteAsync(conn, epLease!, "available", "magnet:?xt=urn:btih:" + new string('n', 40), SevenHundredMiB));
        }

        Assert.Equal(SevenHundredMiB, (await db.GetAvailabilityItemAsync(1399, "episode", 1, 3, default))!.CandidateSize);

        // Legacy writer generation fence still holds on the expanded schema:
        // replaying its released lease affects 0 rows.
        await using (var conn = await OpenRawAsync())
        {
            Assert.Equal(0, await LegacyCompleteAsync(conn, epLease!, "available", "magnet:?xt=urn:btih:" + new string('o', 40), 1L));
        }

        Assert.Equal(SevenHundredMiB, (await db.GetAvailabilityItemAsync(1399, "episode", 1, 3, default))!.CandidateSize);
    }

    [Fact]
    public async Task LegacyWriterOverlap_MagnetCache_LegacyReplaceWins()
    {
        if (!Enabled)
        {
            return;
        }

        using var db = NewDb();
        var key = new MagnetCacheKey(603, null, "movie", null, null, "1080p");
        await db.PutCachedMagnetAsync(key, Entry(FiveGiB, 'p'), default);

        await using (var conn = await OpenRawAsync())
        {
            await LegacyPutMagnetAsync(conn, 603, "movie", 0, 0, SevenHundredMiB);
        }

        Assert.Equal(SevenHundredMiB, (await db.GetCachedMagnetAsync(key, default))!.Size);

        // Legacy writer on a key the new writer never touched (companion NULL).
        await using (var conn = await OpenRawAsync())
        {
            await LegacyPutMagnetAsync(conn, 1399, "episode", 1, 3, 1_000_000_000L);
        }

        Assert.Equal(1_000_000_000L, (await db.GetCachedMagnetAsync(new MagnetCacheKey(1399, null, "episode", 1, 3, "1080p"), default))!.Size);
    }

    // ------------------------------------------------------------------
    // Prior-schema (v22) expand: clone rehearsal + idempotency
    // ------------------------------------------------------------------

    [Fact]
    public async Task PriorSchemaV22_SelfExpands_PreservesData_AndIsIdempotent()
    {
        if (!Enabled)
        {
            return;
        }

        // Build a fresh schema, then roll it back to the exact v22 shape.
        using (var seed = NewDb())
        {
            await seed.CountCatalogueItemsAsync("movie", null, default);
        }

        await ExecAsync(_connectionString!, @"
ALTER TABLE source_candidates DROP COLUMN size_bytes;
ALTER TABLE source_candidates DROP COLUMN selected_file_size_bytes;
ALTER TABLE availability_items DROP COLUMN candidate_size_bytes;
ALTER TABLE magnet_cache DROP COLUMN size_bytes;
UPDATE phantom_schema_meta SET version = 22;");
        Assert.Null(await ColumnTypeOrNullAsync("source_candidates", "size_bytes"));

        // Seed v22 data with the LEGACY writers (the clone's data shape).
        await using (var conn = await OpenRawAsync())
        {
            await LegacyUpsertCandidateAsync(conn, 603, "movie", -1, -1, "magnet:?xt=urn:btih:" + new string('q', 40), 1_900_000_000L);
            await LegacyUpsertCandidateAsync(conn, 1399, "episode", 1, 3, "magnet:?xt=urn:btih:" + new string('r', 40), 900_000_000L);
            await LegacyPutMagnetAsync(conn, 603, "movie", 0, 0, 1_234_567_890L);
        }

        await ExecAsync(_connectionString!, @"
INSERT INTO availability_items (tmdb_id,type,season,episode,status,next_check_at,candidate_magnet,candidate_size)
VALUES (603,'movie',-1,-1,'available',0,'m1',1900000000), (1399,'episode',1,3,'available',0,'m2',900000000);");

        // PREDICTED (pre-expand): per-table row counts + legacy-column checksums.
        // The expand is DDL-only, so the prediction is: identical counts and
        // checksums afterwards, and 0 rows touched by any data update.
        var predicted = await SnapshotAsync();
        foreach (var kv in predicted)
        {
            _output.WriteLine($"REHEARSAL predicted {kv.Key} = {kv.Value}");
        }

        await using (var conn = await OpenRawAsync())
        {
            Assert.False(await SchemaExpandMigrator.IsAppliedAsync(conn, MigrationName, default));
        }

        // A v23 build opening the v22 DB self-applies the expand (no hard refuse).
        using (var db = NewDb())
        {
            var rows = await db.ListSourceCandidatesAsync(603, "movie", -1, -1, "1080p", includeExpired: false, default);
            Assert.Equal(1_900_000_000L, rows.Single().Size); // legacy value read unchanged, no backfill
            Assert.Equal(1_900_000_000L, (await db.GetAvailabilityItemAsync(603, "movie", -1, -1, default))!.CandidateSize);
            Assert.Equal(1_234_567_890L, (await db.GetCachedMagnetAsync(new MagnetCacheKey(603, null, "movie", null, null, "1080p"), default))!.Size);
            Assert.Equal(900_000_000L, (await db.ListSourceCandidatesAsync(1399, "episode", 1, 3, "1080p", false, default)).Single().Size);

            // And the expanded DB now accepts 5 GiB.
            await db.UpsertSourceCandidatesAsync(
                1399, "episode", 1, 3, "1080p",
                new[] { new MagnetCandidate("magnet:?xt=urn:btih:" + new string('s', 40), new string('s', 40), FiveGiB, 3, "prowlarr") },
                "availability", TimeSpan.FromHours(6), default);
            Assert.Contains(
                (await db.ListSourceCandidatesAsync(1399, "episode", 1, 3, "1080p", false, default)),
                r => r.Size == FiveGiB);
            await ExecAsync(_connectionString!, "DELETE FROM source_candidates WHERE info_hash = '" + new string('s', 40) + "';");
        }

        Assert.Equal(PhantomDb.CurrentSchemaVersion, await ReadVersionAsync());
        Assert.Equal(("bigint", "YES"), await ColumnTypeAsync("source_candidates", "size_bytes"));
        Assert.Equal(("bigint", "YES"), await ColumnTypeAsync("source_candidates", "selected_file_size_bytes"));
        Assert.Equal(("bigint", "YES"), await ColumnTypeAsync("availability_items", "candidate_size_bytes"));
        Assert.Equal(("bigint", "YES"), await ColumnTypeAsync("magnet_cache", "size_bytes"));
        Assert.Equal(("integer", "YES"), await ColumnTypeAsync("source_candidates", "size"));

        // ACTUAL (post-expand): identical counts/checksums; companions all NULL
        // (no data update was performed by the expand).
        var actual = await SnapshotAsync();
        foreach (var kv in actual)
        {
            _output.WriteLine($"REHEARSAL actual    {kv.Key} = {kv.Value}");
        }

        Assert.Equal(predicted, actual);
        Assert.Equal(0L, await ScalarLongAsync("SELECT COUNT(*) FROM source_candidates WHERE size_bytes IS NOT NULL OR selected_file_size_bytes IS NOT NULL;"));
        Assert.Equal(0L, await ScalarLongAsync("SELECT COUNT(*) FROM availability_items WHERE candidate_size_bytes IS NOT NULL;"));
        Assert.Equal(0L, await ScalarLongAsync("SELECT COUNT(*) FROM magnet_cache WHERE size_bytes IS NOT NULL;"));

        // Idempotency: (a) the recorded migration is a no-op on re-apply;
        await using (var conn = await OpenRawAsync())
        {
            Assert.True(await SchemaExpandMigrator.IsAppliedAsync(conn, MigrationName, default));
            Assert.False(await SchemaExpandMigrator.ApplyAsync(conn, MigrationName, PhantomDb.GetExpandMigrationStatements(MigrationName), default));
        }

        // (b) the raw guarded DDL re-runs harmlessly (peer color racing it);
        foreach (var stmt in PhantomDb.GetExpandMigrationStatements(MigrationName))
        {
            await ExecAsync(_connectionString!, stmt);
        }

        // (c) a second behind-version open re-expands as a no-op.
        await ExecAsync(_connectionString!, "UPDATE phantom_schema_meta SET version = 22;");
        using (var again = NewDb())
        {
            await again.CountCatalogueItemsAsync("movie", null, default);
        }

        Assert.Equal(PhantomDb.CurrentSchemaVersion, await ReadVersionAsync());
        Assert.Equal(predicted, await SnapshotAsync());
        Assert.Equal(1L, await ScalarLongAsync($"SELECT COUNT(*) FROM phantom_expand_migrations WHERE name = '{MigrationName}';"));

        // The statements are classified additive (expand-safe) by the gate.
        SchemaExpandMigrator.EnsureAdditiveOnly(PhantomDb.GetExpandMigrationStatements(MigrationName));
    }

    [Fact]
    public async Task FreshSchema_MatchesExpandedSchema_ForSizeCompanions()
    {
        if (!Enabled)
        {
            return;
        }

        using (var fresh = NewDb())
        {
            await fresh.CountCatalogueItemsAsync("movie", null, default);
        }

        var tables = new[] { "source_candidates", "availability_items", "magnet_cache" };
        var freshShape = new List<string>();
        foreach (var t in tables)
        {
            freshShape.AddRange(await ColumnShapeAsync(t));
        }

        await ExecAsync(_connectionString!, @"
ALTER TABLE source_candidates DROP COLUMN size_bytes;
ALTER TABLE source_candidates DROP COLUMN selected_file_size_bytes;
ALTER TABLE availability_items DROP COLUMN candidate_size_bytes;
ALTER TABLE magnet_cache DROP COLUMN size_bytes;
UPDATE phantom_schema_meta SET version = 22;");

        using (var expanded = NewDb())
        {
            await expanded.CountCatalogueItemsAsync("movie", null, default);
        }

        var expandedShape = new List<string>();
        foreach (var t in tables)
        {
            expandedShape.AddRange(await ColumnShapeAsync(t));
        }

        Assert.Equal(freshShape, expandedShape);
    }

    [Fact]
    public async Task ForwardTolerant_NewerSchemaVersion_StillRoundTripsFiveGiB()
    {
        if (!Enabled)
        {
            return;
        }

        using (var seed = NewDb())
        {
            await seed.CountCatalogueItemsAsync("movie", null, default);
        }

        // A newer color has already stamped v24 and added an unknown extra
        // column; this build must neither refuse nor rewrite the stamp.
        await ExecAsync(_connectionString!, $@"
ALTER TABLE source_candidates ADD COLUMN IF NOT EXISTS future_col TEXT;
UPDATE phantom_schema_meta SET version = {PhantomDb.CurrentSchemaVersion + 1};");

        using var db = NewDb();
        await db.UpsertSourceCandidatesAsync(
            603, "movie", -1, -1, "1080p",
            new[] { new MagnetCandidate("magnet:?xt=urn:btih:" + new string('t', 40), new string('t', 40), FiveGiB, 1, "prowlarr") },
            "availability", TimeSpan.FromHours(6), default);
        Assert.Equal(FiveGiB, (await db.ListSourceCandidatesAsync(603, "movie", -1, -1, "1080p", false, default)).Single().Size);
        Assert.Equal(PhantomDb.CurrentSchemaVersion + 1, await ReadVersionAsync());
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static MagnetCacheEntry Entry(long size, char hashChar) => new()
    {
        Magnet = "magnet:?xt=urn:btih:" + new string(hashChar, 40),
        InfoHash = new string(hashChar, 40),
        Size = size,
        Seeders = 9,
        Indexer = "prowlarr",
        CachedAt = DateTimeOffset.UtcNow,
        Ttl = TimeSpan.FromHours(1),
        Source = "eager",
    };

    private static TmdbMetadataRow Meta(int tmdb, string type) => new(
        TmdbId: tmdb, Type: type, Title: "T" + tmdb, Year: 2020, Overview: null, PosterUrl: null, BackdropUrl: null,
        Genres: null, OfficialRating: null, CommunityRating: null, OriginalTitle: null, FetchedAt: DateTimeOffset.UtcNow);

    private PhantomDb NewDb() => PhantomDb.CreatePostgres(_connectionString!);

    private async Task<NpgsqlConnection> OpenRawAsync()
    {
        var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<long> ScalarLongAsync(string sql)
    {
        await using var conn = await OpenRawAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }

    private async Task<int> ReadVersionAsync()
        => (int)await ScalarLongAsync("SELECT version FROM phantom_schema_meta LIMIT 1;");

    private async Task<(string Type, string Nullable)?> ColumnTypeOrNullAsync(string table, string column)
    {
        await using var conn = await OpenRawAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT data_type, is_nullable FROM information_schema.columns WHERE table_schema = current_schema() AND table_name=@t AND column_name=@c;";
        cmd.Parameters.AddWithValue("t", table);
        cmd.Parameters.AddWithValue("c", column);
        await using var r = await cmd.ExecuteReaderAsync();
        return await r.ReadAsync() ? (r.GetString(0), r.GetString(1)) : null;
    }

    private async Task<(string Type, string Nullable)> ColumnTypeAsync(string table, string column)
        => await ColumnTypeOrNullAsync(table, column)
            ?? throw new InvalidOperationException($"column {table}.{column} missing");

    private async Task<List<string>> ColumnShapeAsync(string table)
    {
        await using var conn = await OpenRawAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT column_name || ':' || data_type || ':' || is_nullable || ':' || COALESCE(column_default,'') FROM information_schema.columns WHERE table_schema = current_schema() AND table_name=@t ORDER BY column_name;";
        cmd.Parameters.AddWithValue("t", table);
        var list = new List<string>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(table + "." + r.GetString(0));
        }

        return list;
    }

    private async Task<SortedDictionary<string, string>> SnapshotAsync()
    {
        var snap = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, sql) in new[]
        {
            ("source_candidates", "SELECT COUNT(*)::text || '/' || COALESCE(md5(string_agg(tmdb_id||'|'||type||'|'||season||'|'||episode||'|'||magnet||'|'||COALESCE(size::text,'N')||'|'||COALESCE(selected_file_size::text,'N'), ',' ORDER BY tmdb_id,type,season,episode,magnet)),'') FROM source_candidates"),
            ("availability_items", "SELECT COUNT(*)::text || '/' || COALESCE(md5(string_agg(tmdb_id||'|'||type||'|'||season||'|'||episode||'|'||status||'|'||COALESCE(candidate_size::text,'N'), ',' ORDER BY tmdb_id,type,season,episode)),'') FROM availability_items"),
            ("magnet_cache", "SELECT COUNT(*)::text || '/' || COALESCE(md5(string_agg(tmdb_id||'|'||type||'|'||season||'|'||episode||'|'||size::text, ',' ORDER BY tmdb_id,type,season,episode)),'') FROM magnet_cache"),
        })
        {
            await using var conn = await OpenRawAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            snap[name] = (string)(await cmd.ExecuteScalarAsync())!;
        }

        return snap;
    }

    private static async Task<long?> LegacyReadSizeAsync(NpgsqlConnection conn, int tmdb, string type, int season, int episode)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = LegacyListSourceCandidatesSql;
        cmd.Parameters.AddWithValue("tmdb", tmdb);
        cmd.Parameters.AddWithValue("type", type);
        cmd.Parameters.AddWithValue("season", season);
        cmd.Parameters.AddWithValue("episode", episode);
        cmd.Parameters.AddWithValue("preset", "1080p");
        cmd.Parameters.AddWithValue("includeExpired", 1);
        cmd.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        // Same accessor the legacy ReadSourceCandidate used.
        return r.IsDBNull(10) ? null : r.GetInt64(10);
    }

    private static async Task LegacyUpsertCandidateAsync(NpgsqlConnection conn, int tmdb, string type, int season, int episode, string magnet, long size)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = LegacyUpsertSourceCandidateSql;
        var now = DateTimeOffset.UtcNow;
        cmd.Parameters.AddWithValue("tmdb", tmdb);
        cmd.Parameters.AddWithValue("type", type);
        cmd.Parameters.AddWithValue("season", season);
        cmd.Parameters.AddWithValue("episode", episode);
        cmd.Parameters.AddWithValue("preset", "1080p");
        cmd.Parameters.AddWithValue("magnet", magnet);
        cmd.Parameters.AddWithValue("hash", magnet[^40..]);
        cmd.Parameters.AddWithValue("indexer", "prowlarr");
        cmd.Parameters.AddWithValue("title", "legacy");
        cmd.Parameters.AddWithValue("seeders", 5);
        cmd.Parameters.AddWithValue("size", size); // long, exactly as the legacy writer bound MagnetCandidate.Size
        cmd.Parameters.AddWithValue("rank", 1);
        cmd.Parameters.AddWithValue("source", "availability");
        cmd.Parameters.AddWithValue("fetched", now.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("expires", now.AddHours(6).ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<int> LegacyCompleteAsync(NpgsqlConnection conn, AvailabilityItemRow lease, string status, string? magnet, long? size)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = LegacyCompleteAvailabilitySql;
        var now = DateTimeOffset.UtcNow;
        cmd.Parameters.AddWithValue("negStreak", 0);
        cmd.Parameters.AddWithValue("status", status);
        cmd.Parameters.AddWithValue("checked", now.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("next", now.AddDays(1).ToUnixTimeSeconds());
        cmd.Parameters.Add(new NpgsqlParameter("magnet", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)magnet ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("hash", NpgsqlTypes.NpgsqlDbType.Text) { Value = (object?)magnet?[^40..] ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("size", NpgsqlTypes.NpgsqlDbType.Bigint) { Value = (object?)size ?? DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("seeders", NpgsqlTypes.NpgsqlDbType.Integer) { Value = magnet is null ? DBNull.Value : 4 });
        cmd.Parameters.Add(new NpgsqlParameter("indexer", NpgsqlTypes.NpgsqlDbType.Text) { Value = magnet is null ? DBNull.Value : "prowlarr" });
        cmd.Parameters.Add(new NpgsqlParameter("source", NpgsqlTypes.NpgsqlDbType.Text) { Value = magnet is null ? DBNull.Value : "eager" });
        cmd.Parameters.AddWithValue("policy", "p");
        cmd.Parameters.Add(new NpgsqlParameter("errKind", NpgsqlTypes.NpgsqlDbType.Text) { Value = DBNull.Value });
        cmd.Parameters.Add(new NpgsqlParameter("errMsg", NpgsqlTypes.NpgsqlDbType.Text) { Value = DBNull.Value });
        cmd.Parameters.AddWithValue("tmdb", lease.TmdbId);
        cmd.Parameters.AddWithValue("type", lease.Type);
        cmd.Parameters.AddWithValue("season", lease.Season);
        cmd.Parameters.AddWithValue("episode", lease.Episode);
        cmd.Parameters.AddWithValue("owner", lease.LeaseOwner ?? string.Empty);
        cmd.Parameters.AddWithValue("generation", lease.ProbeGeneration);
        return await cmd.ExecuteNonQueryAsync();
    }

    private static async Task LegacyPutMagnetAsync(NpgsqlConnection conn, int tmdb, string type, int season, int episode, long size)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = LegacyPutMagnetSql;
        cmd.Parameters.AddWithValue("tmdb", tmdb);
        cmd.Parameters.AddWithValue("imdb", string.Empty);
        cmd.Parameters.AddWithValue("type", type);
        cmd.Parameters.AddWithValue("season", season);
        cmd.Parameters.AddWithValue("episode", episode);
        cmd.Parameters.AddWithValue("preset", "1080p");
        cmd.Parameters.AddWithValue("magnet", "magnet:?xt=urn:btih:" + new string('z', 40));
        cmd.Parameters.AddWithValue("hash", new string('z', 40));
        cmd.Parameters.AddWithValue("size", size);
        cmd.Parameters.AddWithValue("seeders", 3);
        cmd.Parameters.AddWithValue("indexer", "prowlarr");
        cmd.Parameters.AddWithValue("cached", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("ttl", 3600L);
        cmd.Parameters.AddWithValue("source", "eager");
        await cmd.ExecuteNonQueryAsync();
    }
}
