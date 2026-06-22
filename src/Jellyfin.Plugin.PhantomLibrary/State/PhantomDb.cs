using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.PhantomLibrary.State;

public sealed record MagnetCacheKey(int? TmdbId, string? ImdbId, string Type, int? Season, int? Episode, string Preset);

public sealed record MagnetCacheEntry
{
    public required string Magnet { get; init; }
    public required string InfoHash { get; init; }
    public required long Size { get; init; }
    public required int Seeders { get; init; }
    public required string Indexer { get; init; }
    public required DateTimeOffset CachedAt { get; init; }
    public required TimeSpan Ttl { get; init; }
    public required string Source { get; init; } // "eager" | "user"
}

public sealed record UnavailableKey(int? TmdbId, string? ImdbId, string Type, int? Season, int? Episode);

public sealed record MagnetFailureKey(int? TmdbId, string? ImdbId, string Type, int? Season, int? Episode, string Preset, string Magnet);

public sealed record MagnetFailureEntry
{
    public required string InfoHash { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset FailedAt { get; init; }
    public required DateTimeOffset RetryAfter { get; init; }
}

/// <summary>
/// Row of the <c>discovery_cache</c> table. Movies and series only;
/// season/episode level discovery is not tracked here (those come
/// from TMDB on-demand during channel-folder browse).
/// </summary>
public sealed record DiscoveryCacheRow(
    int TmdbId,
    string Type,
    DateTimeOffset DiscoveredAt,
    DateTimeOffset LastRefreshed);

public sealed record CatalogueHitWriteResult(int Seen, int Inserted, int MetadataInserted, int MetadataSkipped, int AvailabilityInserted, int SeriesExpansionInserted);

public sealed record AvailabilityItemRow(
    int TmdbId,
    string Type,
    int Season,
    int Episode,
    string Status,
    DateTimeOffset? CheckedAt,
    DateTimeOffset NextCheckAt,
    string? CandidateMagnet,
    string? CandidateInfoHash,
    long? CandidateSize,
    int? CandidateSeeders,
    string? CandidateIndexer,
    string? CandidateSource,
    int ProbeGeneration,
    string? LeaseOwner);

public sealed record VisibleMovieRow(
    TmdbMetadataRow Metadata,
    MaterialisedStateRow? Materialised,
    AvailabilityItemRow? Availability);

public sealed record VisibleSeriesRow(TmdbMetadataRow Metadata, int AvailableEpisodeCount, int MaterialisedEpisodeCount);

public sealed record VisibleSeasonRow(int SeriesTmdbId, int Season, int AvailableEpisodeCount, int MaterialisedEpisodeCount);

public sealed record DueSeriesExpansionRow(int SeriesTmdbId, int ProbeGeneration, string LeaseOwner);

/// <summary>
/// Row of the <c>materialised_state</c> table. <c>Season</c> and
/// <c>Episode</c> use the <c>-1</c> sentinel for movies (per critic v2
/// BLOCKER 3 — SQLite treats NULL as distinct in UNIQUE/PK, so we use
/// a real integer sentinel instead). Callers above the DB layer pass
/// <c>null</c> for movies and the <see cref="ChannelItemId"/> codec
/// converts at the boundary.
/// </summary>
public sealed record MaterialisedStateRow(
    int TmdbId,
    string Type,
    int Season,
    int Episode,
    string StubPath,
    string FusePath,
    DateTimeOffset MaterialisedAt);

/// <summary>
/// Row of the <c>tmdb_external_ids</c> table. <see cref="ImdbId"/>
/// nullable: a row with null ImdbId is a negative-cache entry (TMDB
/// returned no external IMDb id) — the TTL window is interpreted by
/// the resolver layer, not by the DB.
/// </summary>
public sealed record TmdbExternalIdRow(string? ImdbId, DateTimeOffset FetchedAt);

/// <summary>
/// Row of the <c>tmdb_metadata</c> table. Per-(tmdb_id, type) cached
/// metadata used to synthesise <c>ChannelItemInfo</c>s in the channel
/// browse pipeline without re-hitting TMDB on every render. Warmed by
/// <c>DiscoveryRefreshTask</c> for every (tmdb_id, type) it discovers.
/// </summary>
public sealed record TmdbMetadataRow(
    int TmdbId,
    string Type,
    string Title,
    int? Year,
    string? Overview,
    string? PosterUrl,
    string? BackdropUrl,
    string[]? Genres,
    string? OfficialRating,
    double? CommunityRating,
    string? OriginalTitle,
    DateTimeOffset FetchedAt);

/// <summary>
/// Row of the <c>tmdb_episode_cache</c> table. Per-(series_tmdb_id,
/// season, episode) cached title/overview/still/airdate/runtime warmed
/// by the shows-channel browse path (Stage 5.1) so the
/// IChannelItemRefresh path (post-flight materialise refresh) can
/// rebuild the episode ChannelItemInfo without re-hitting TMDB.
/// </summary>
public sealed record TmdbEpisodeRow(
    int SeriesTmdbId,
    int Season,
    int Episode,
    string Title,
    string? Overview,
    string? StillUrl,
    string? AirDate,
    int? RuntimeMinutes,
    DateTimeOffset FetchedAt);

/// <summary>
/// SQLite-backed persistence for the plugin's private state under the
/// channel architecture (schema v11). Single writer, serialised via a
/// process-wide <see cref="SemaphoreSlim"/>; concurrent readers
/// permitted via separate short-lived connections.
///
/// Schema v11 is a clean break from the v5 file-on-disk schema (v6/v7/v8
/// were intermediate channel-arch revisions that never reached prod;
/// v9 adds the <c>tmdb_episode_cache</c> table the shows channel needs
/// for per-episode display metadata at refresh time; v10 adds
/// <c>magnet_failure_cache</c> so rejected pack candidates do not
/// block viable alternatives; v11 adds append-only catalogue and
/// availability scheduler state). Per
/// AGENTS.md "No database migrations until v1.0", existing databases
/// at any pre-v11 user_version are HARD-REFUSED and the operator must
/// run <c>scripts/phantom-wipe.sh</c> before restart.
/// </summary>
public sealed class PhantomDb : IDisposable
{
    private const int CurrentSchemaVersion = 11;

    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private int _schemaEnsured;

    public PhantomDb(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            throw new ArgumentException("dbPath required", nameof(dbPath));
        }

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var b = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        };
        _connectionString = b.ToString();
    }

    public void Dispose()
    {
        _writeLock.Dispose();
        SqliteConnection.ClearAllPools();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        if (Interlocked.CompareExchange(ref _schemaEnsured, 1, 0) == 0)
        {
            try
            {
                EnsureSchema(conn);
            }
            catch
            {
                Interlocked.Exchange(ref _schemaEnsured, 0);
                conn.Dispose();
                throw;
            }
        }

        return conn;
    }

    private static void EnsureSchema(SqliteConnection conn)
    {
        using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            pragma.ExecuteNonQuery();
        }

        int version;
        using (var v = conn.CreateCommand())
        {
            v.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt32(v.ExecuteScalar() ?? 0,
                System.Globalization.CultureInfo.InvariantCulture);
        }

        if (version == CurrentSchemaVersion)
        {
            return;
        }

        if (version > 0 && version < CurrentSchemaVersion)
        {
            // HARD-REFUSE: pre-v1.0 the plugin does not ship migrations.
            // Operator must wipe and rebuild. Per AGENTS.md
            // "No database migrations until v1.0" + critic v2 BLOCKER 2.
            throw new InvalidOperationException(
                $"Phantom Library schema is at version {version}; this build requires" + Environment.NewLine
                + $"version {CurrentSchemaVersion}. Pre-v1.0 the plugin does not ship migrations — see" + Environment.NewLine
                + "AGENTS.md \"No database migrations until v1.0\". Stop Jellyfin, run" + Environment.NewLine
                + "`sudo bash scripts/phantom-wipe.sh --commit`, then restart.");
        }

        if (version > CurrentSchemaVersion)
        {
            // Operator downgraded the plugin against a newer DB. Also unsafe.
            throw new InvalidOperationException(
                $"Phantom Library schema is at version {version}; this build only knows about"
                + $" version {CurrentSchemaVersion}. Downgrade is not supported. Wipe and rebuild.");
        }

        // version == 0: fresh / never-initialised DB. Create the v11 schema.
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = SchemaV10Sql;
            cmd.ExecuteNonQuery();
        }

        using (var sv = conn.CreateCommand())
        {
            sv.Transaction = tx;
            sv.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion};";
            sv.ExecuteNonQuery();
        }

        tx.Commit();
    }

    private const string SchemaV10Sql = @"
-- Channel-arch discovery: trending + similar-of-favourited TMDB ids,
-- one row per (tmdb_id, type). Synthesised into ChannelItemInfos by
-- PhantomMoviesChannel / PhantomShowsChannel at browse time.
CREATE TABLE IF NOT EXISTS discovery_cache (
    tmdb_id        INTEGER NOT NULL,
    type           TEXT NOT NULL,        -- 'movie' or 'series'
    discovered_at  INTEGER NOT NULL,
    last_refreshed INTEGER NOT NULL,
    PRIMARY KEY (tmdb_id, type)
);
CREATE INDEX IF NOT EXISTS idx_discovery_cache_last_refreshed
    ON discovery_cache(last_refreshed);

-- v11 append-only catalogue. Discovery/recommendation surfaces feed this
-- table; absence from a later TMDB response does not delete a row.
CREATE TABLE IF NOT EXISTS catalogue_items (
    tmdb_id       INTEGER NOT NULL,
    type          TEXT NOT NULL CHECK(type IN ('movie','series')),
    first_seen_at INTEGER NOT NULL,
    last_seen_at  INTEGER NOT NULL,
    source_mask   INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (tmdb_id, type)
);
CREATE INDEX IF NOT EXISTS idx_catalogue_items_type_last_seen
    ON catalogue_items(type, last_seen_at);

-- Series expansion state. Separate from catalogue_items so expansion has
-- its own due/lease lifecycle.
CREATE TABLE IF NOT EXISTS series_expansion_state (
    series_tmdb_id   INTEGER PRIMARY KEY,
    last_expanded_at INTEGER,
    next_expand_at   INTEGER NOT NULL,
    lease_owner      TEXT,
    lease_until      INTEGER,
    probe_generation INTEGER NOT NULL DEFAULT 0,
    last_error_kind  TEXT,
    last_error_message TEXT
);
CREATE INDEX IF NOT EXISTS idx_series_expansion_due
    ON series_expansion_state(next_expand_at, lease_until);

-- Episode catalogue derived from TMDB season payloads. Availability is
-- tracked separately because search source state changes independently
-- from TMDB's episode list.
CREATE TABLE IF NOT EXISTS series_episode_catalogue (
    series_tmdb_id INTEGER NOT NULL,
    episode_tmdb_id INTEGER NOT NULL,
    season INTEGER NOT NULL CHECK(season >= 0),
    episode INTEGER NOT NULL CHECK(episode > 0),
    air_date TEXT,
    first_seen_at INTEGER NOT NULL,
    last_seen_at INTEGER NOT NULL,
    PRIMARY KEY (series_tmdb_id, season, episode)
);
CREATE INDEX IF NOT EXISTS idx_series_episode_catalogue_episode_tmdb
    ON series_episode_catalogue(episode_tmdb_id);

-- Probe scheduler state. Materialised rows and real gostream files remain
-- visible regardless of this table; availability only gates unmaterialised
-- phantom visibility.
CREATE TABLE IF NOT EXISTS availability_items (
    tmdb_id INTEGER NOT NULL,
    type TEXT NOT NULL CHECK(type IN ('movie','episode')),
    season INTEGER NOT NULL DEFAULT -1,
    episode INTEGER NOT NULL DEFAULT -1,
    status TEXT NOT NULL CHECK(status IN ('unknown','available','unavailable')),
    checked_at INTEGER,
    next_check_at INTEGER NOT NULL,
    candidate_magnet TEXT,
    candidate_info_hash TEXT,
    candidate_size INTEGER,
    candidate_seeders INTEGER,
    candidate_indexer TEXT,
    candidate_source TEXT,
    probe_policy_hash TEXT,
    last_error_kind TEXT,
    last_error_message TEXT,
    lease_owner TEXT,
    lease_until INTEGER,
    probe_generation INTEGER NOT NULL DEFAULT 0,
    attempt_count INTEGER NOT NULL DEFAULT 0,
    CHECK ((type='movie' AND season=-1 AND episode=-1) OR (type='episode' AND season>=0 AND episode>0)),
    PRIMARY KEY (tmdb_id, type, season, episode)
);
CREATE INDEX IF NOT EXISTS idx_availability_due
    ON availability_items(next_check_at, lease_until, status);
CREATE INDEX IF NOT EXISTS idx_availability_status_type
    ON availability_items(status, type, tmdb_id, season, episode);

-- Channel-arch materialised state: one row per (tmdb_id, type, season,
-- episode). Movies use sentinel season=-1, episode=-1; series episodes
-- use the real season/episode integers. The sentinel scheme (per critic
-- v2 BLOCKER 3) sidesteps SQLite's quirky UNIQUE-on-NULL semantics.
CREATE TABLE IF NOT EXISTS materialised_state (
    tmdb_id         INTEGER NOT NULL,
    type            TEXT NOT NULL,        -- 'movie' or 'episode'
    season          INTEGER NOT NULL DEFAULT -1,
    episode         INTEGER NOT NULL DEFAULT -1,
    stub_path       TEXT NOT NULL,
    fuse_path       TEXT NOT NULL,
    materialised_at INTEGER NOT NULL,
    PRIMARY KEY (tmdb_id, type, season, episode)
);
CREATE INDEX IF NOT EXISTS idx_materialised_state_type
    ON materialised_state(type);
CREATE INDEX IF NOT EXISTS idx_materialised_state_materialised_at
    ON materialised_state(materialised_at);

-- Channel-arch in-flight tracking: a row exists for the duration of an
-- in-progress Materialiser run. Same (tmdb_id, type, season, episode)
-- shape + sentinel discipline as materialised_state. Deleted by the
-- Materialiser's finally block; stale rows (process crashed mid-flight)
-- are swept by Stage 4's startup sweep via
-- PurgeStaleMaterialiseInFlightAsync.
CREATE TABLE IF NOT EXISTS materialise_in_flight (
    tmdb_id    INTEGER NOT NULL,
    type       TEXT NOT NULL,
    season     INTEGER NOT NULL DEFAULT -1,
    episode    INTEGER NOT NULL DEFAULT -1,
    started_at INTEGER NOT NULL,
    PRIMARY KEY (tmdb_id, type, season, episode)
);

-- Channel-arch external-id cache: TMDB → IMDb lookup result, plus
-- negative-cache rows (imdb_id NULL). TTL interpretation is the
-- resolver layer's responsibility; this table just stores
-- (key → optional value + fetched-at).
CREATE TABLE IF NOT EXISTS tmdb_external_ids (
    tmdb_id    INTEGER NOT NULL,
    type       TEXT NOT NULL,    -- 'movie' or 'series'
    imdb_id    TEXT,             -- NULL = negative-cache row
    fetched_at INTEGER NOT NULL,
    PRIMARY KEY (tmdb_id, type)
);

-- Surviving table from v3: cached TMDB endpoint responses. Reused by
-- channel item synthesis.
CREATE TABLE IF NOT EXISTS tmdb_cache (
    endpoint     TEXT NOT NULL,
    params_hash  TEXT NOT NULL,
    language     TEXT NOT NULL,
    response_json TEXT NOT NULL,
    cached_at    INTEGER NOT NULL,
    ttl_seconds  INTEGER NOT NULL,
    PRIMARY KEY (endpoint, params_hash, language)
);
CREATE INDEX IF NOT EXISTS idx_tmdb_cache_expiry
    ON tmdb_cache(cached_at, ttl_seconds);

-- Surviving table from v1: per-source magnet cache.
CREATE TABLE IF NOT EXISTS magnet_cache (
    tmdb_id     INTEGER NOT NULL DEFAULT 0,
    imdb_id     TEXT NOT NULL DEFAULT '',
    type        TEXT NOT NULL,
    season      INTEGER NOT NULL DEFAULT 0,
    episode     INTEGER NOT NULL DEFAULT 0,
    preset      TEXT NOT NULL DEFAULT '',
    magnet      TEXT NOT NULL,
    info_hash   TEXT NOT NULL,
    size        INTEGER NOT NULL,
    seeders     INTEGER NOT NULL,
    indexer     TEXT NOT NULL,
    cached_at   INTEGER NOT NULL,
    ttl_seconds INTEGER NOT NULL,
    source      TEXT NOT NULL,
    PRIMARY KEY (tmdb_id, imdb_id, type, season, episode, preset)
);

-- Candidate-level negative cache: a specific magnet failed for a
-- specific materialisation key. This is distinct from
-- unavailable_marker, which means no acceptable candidate exists for
-- the item at all.
CREATE TABLE IF NOT EXISTS magnet_failure_cache (
    tmdb_id     INTEGER NOT NULL DEFAULT 0,
    imdb_id     TEXT NOT NULL DEFAULT '',
    type        TEXT NOT NULL,
    season      INTEGER NOT NULL DEFAULT 0,
    episode     INTEGER NOT NULL DEFAULT 0,
    preset      TEXT NOT NULL DEFAULT '',
    magnet      TEXT NOT NULL,
    info_hash   TEXT NOT NULL,
    reason      TEXT NOT NULL,
    failed_at   INTEGER NOT NULL,
    retry_after INTEGER NOT NULL,
    PRIMARY KEY (tmdb_id, imdb_id, type, season, episode, preset, magnet)
);
CREATE INDEX IF NOT EXISTS idx_magnet_failure_cache_retry_after
    ON magnet_failure_cache(retry_after);

-- Surviving table from v1: indexers returned nothing for a key; back
-- off until retry_after.
CREATE TABLE IF NOT EXISTS unavailable_marker (
    tmdb_id     INTEGER NOT NULL DEFAULT 0,
    imdb_id     TEXT NOT NULL DEFAULT '',
    type        TEXT NOT NULL,
    season      INTEGER NOT NULL DEFAULT 0,
    episode     INTEGER NOT NULL DEFAULT 0,
    marked_at   INTEGER NOT NULL,
    retry_after INTEGER NOT NULL,
    PRIMARY KEY (tmdb_id, imdb_id, type, season, episode)
);

-- Surviving table from v5: key/value store for one-shot migration
-- markers and similar small metadata that needs to outlive plugin
-- restarts.
CREATE TABLE IF NOT EXISTS plugin_meta (
    key   TEXT PRIMARY KEY,
    value TEXT NOT NULL
);

-- Channel-arch per-(tmdb_id, type) metadata cache. One row per
-- discoverable item; the channel browse pipeline reads this to
-- synthesise ChannelItemInfo without hitting TMDB on every render.
-- Warmed by DiscoveryRefreshTask (Stage 3.1). Genres are stored as a
-- JSON array of strings so we can round-trip them without a side
-- table.
CREATE TABLE IF NOT EXISTS tmdb_metadata (
    tmdb_id          INTEGER NOT NULL,
    type             TEXT NOT NULL,           -- 'movie' or 'series'
    title            TEXT NOT NULL,
    year             INTEGER,
    overview         TEXT,
    poster_url       TEXT,
    backdrop_url     TEXT,
    genres_json      TEXT,                    -- JSON array of strings
    official_rating  TEXT,
    community_rating REAL,
    original_title   TEXT,
    fetched_at       INTEGER NOT NULL,
    PRIMARY KEY (tmdb_id, type)
);
CREATE INDEX IF NOT EXISTS idx_tmdb_metadata_fetched_at
    ON tmdb_metadata(fetched_at);

-- Channel-arch per-episode metadata cache. One row per
-- (series_tmdb_id, season, episode). Warmed lazily by the shows
-- channel browse path (Stage 5.1); read by BuildEpisodeItemAsync on
-- the IChannelItemRefresh post-flight path so materialise post-flight
-- doesn't re-hit TMDB to know the episode's title/overview/still.
CREATE TABLE IF NOT EXISTS tmdb_episode_cache (
    series_tmdb_id  INTEGER NOT NULL,
    season          INTEGER NOT NULL,
    episode         INTEGER NOT NULL,
    title           TEXT NOT NULL,
    overview        TEXT,
    still_url       TEXT,
    air_date        TEXT,
    runtime_minutes INTEGER,
    fetched_at      INTEGER NOT NULL,
    PRIMARY KEY (series_tmdb_id, season, episode)
);
CREATE INDEX IF NOT EXISTS idx_tmdb_episode_cache_fetched_at
    ON tmdb_episode_cache(fetched_at);
";

    // ---- magnet_cache ----

    public async Task<MagnetCacheEntry?> GetCachedMagnetAsync(MagnetCacheKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT magnet, info_hash, size, seeders, indexer, cached_at, ttl_seconds, source
            FROM magnet_cache
            WHERE tmdb_id = $tmdb
              AND imdb_id = $imdb
              AND type = $type
              AND season = $season
              AND episode = $episode
              AND preset = $preset
            LIMIT 1;";
        BindKey(cmd, key);
        cmd.Parameters.AddWithValue("$preset", key.Preset);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var cachedAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(5));
        var ttlSec = r.GetInt64(6);
        if (DateTimeOffset.UtcNow > cachedAt + TimeSpan.FromSeconds(ttlSec))
        {
            return null;
        }

        return new MagnetCacheEntry
        {
            Magnet = r.GetString(0),
            InfoHash = r.GetString(1),
            Size = r.GetInt64(2),
            Seeders = r.GetInt32(3),
            Indexer = r.GetString(4),
            CachedAt = cachedAt,
            Ttl = TimeSpan.FromSeconds(ttlSec),
            Source = r.GetString(7),
        };
    }

    public async Task PutCachedMagnetAsync(MagnetCacheKey key, MagnetCacheEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(entry);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT OR REPLACE INTO magnet_cache
                (tmdb_id, imdb_id, type, season, episode, preset, magnet, info_hash, size, seeders, indexer, cached_at, ttl_seconds, source)
                VALUES ($tmdb,$imdb,$type,$season,$episode,$preset,$magnet,$hash,$size,$seeders,$indexer,$cached,$ttl,$source);";
            BindKey(cmd, key);
            cmd.Parameters.AddWithValue("$preset", key.Preset);
            cmd.Parameters.AddWithValue("$magnet", entry.Magnet);
            cmd.Parameters.AddWithValue("$hash", entry.InfoHash);
            cmd.Parameters.AddWithValue("$size", entry.Size);
            cmd.Parameters.AddWithValue("$seeders", entry.Seeders);
            cmd.Parameters.AddWithValue("$indexer", entry.Indexer);
            cmd.Parameters.AddWithValue("$cached", entry.CachedAt.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$ttl", (long)entry.Ttl.TotalSeconds);
            cmd.Parameters.AddWithValue("$source", entry.Source);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteCachedMagnetAsync(MagnetCacheKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"DELETE FROM magnet_cache
                WHERE tmdb_id=$tmdb
                  AND imdb_id=$imdb
                  AND type=$type
                  AND season=$season
                  AND episode=$episode
                  AND preset=$preset;";
            BindKey(cmd, key);
            cmd.Parameters.AddWithValue("$preset", key.Preset);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ---- magnet_failure_cache ----

    public async Task<MagnetFailureEntry?> GetMagnetFailureAsync(MagnetFailureKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT info_hash, reason, failed_at, retry_after
            FROM magnet_failure_cache
            WHERE tmdb_id=$tmdb
              AND imdb_id=$imdb
              AND type=$type
              AND season=$season
              AND episode=$episode
              AND preset=$preset
              AND magnet=$magnet
            LIMIT 1;";
        BindKey(cmd, new MagnetCacheKey(key.TmdbId, key.ImdbId, key.Type, key.Season, key.Episode, key.Preset));
        cmd.Parameters.AddWithValue("$preset", key.Preset);
        cmd.Parameters.AddWithValue("$magnet", key.Magnet);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var retryAfter = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3));
        if (DateTimeOffset.UtcNow >= retryAfter)
        {
            return null;
        }

        return new MagnetFailureEntry
        {
            InfoHash = r.GetString(0),
            Reason = r.GetString(1),
            FailedAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2)),
            RetryAfter = retryAfter,
        };
    }

    public async Task MarkMagnetFailedAsync(MagnetFailureKey key, MagnetFailureEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(entry);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT OR REPLACE INTO magnet_failure_cache
                (tmdb_id, imdb_id, type, season, episode, preset, magnet, info_hash, reason, failed_at, retry_after)
                VALUES ($tmdb,$imdb,$type,$season,$episode,$preset,$magnet,$hash,$reason,$failed,$retry);";
            BindKey(cmd, new MagnetCacheKey(key.TmdbId, key.ImdbId, key.Type, key.Season, key.Episode, key.Preset));
            cmd.Parameters.AddWithValue("$preset", key.Preset);
            cmd.Parameters.AddWithValue("$magnet", key.Magnet);
            cmd.Parameters.AddWithValue("$hash", entry.InfoHash);
            cmd.Parameters.AddWithValue("$reason", entry.Reason);
            cmd.Parameters.AddWithValue("$failed", entry.FailedAt.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$retry", entry.RetryAfter.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> PurgeExpiredMagnetFailuresAsync(CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM magnet_failure_cache WHERE retry_after < $now;";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ---- unavailable_marker ----

    /// <summary>
    /// Returns the marker's <c>retry_after</c> instant if the key is
    /// currently marked unavailable, or <c>null</c> if no live marker
    /// exists.
    /// </summary>
    public async Task<DateTimeOffset?> IsMarkedUnavailableAsync(UnavailableKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT retry_after FROM unavailable_marker
            WHERE tmdb_id=$tmdb
              AND imdb_id=$imdb
              AND type=$type
              AND season=$season
              AND episode=$episode
            LIMIT 1;";
        BindKey(cmd, new MagnetCacheKey(key.TmdbId, key.ImdbId, key.Type, key.Season, key.Episode, string.Empty));
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (v is null || v is DBNull)
        {
            return null;
        }

        var retryAfter = DateTimeOffset.FromUnixTimeSeconds(
            Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture));
        return DateTimeOffset.UtcNow < retryAfter ? retryAfter : null;
    }

    public async Task MarkUnavailableAsync(UnavailableKey key, TimeSpan retryAfter, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT OR REPLACE INTO unavailable_marker (tmdb_id, imdb_id, type, season, episode, marked_at, retry_after)
                VALUES ($tmdb,$imdb,$type,$season,$episode,$marked,$retry);";
            BindKey(cmd, new MagnetCacheKey(key.TmdbId, key.ImdbId, key.Type, key.Season, key.Episode, string.Empty));
            var now = DateTimeOffset.UtcNow;
            cmd.Parameters.AddWithValue("$marked", now.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$retry", now.Add(retryAfter).ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteUnavailableAsync(UnavailableKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"DELETE FROM unavailable_marker
                WHERE tmdb_id=$tmdb AND imdb_id=$imdb AND type=$type AND season=$season AND episode=$episode;";
            BindKey(cmd, new MagnetCacheKey(key.TmdbId, key.ImdbId, key.Type, key.Season, key.Episode, string.Empty));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> PurgeExpiredUnavailableMarkersAsync(CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM unavailable_marker WHERE retry_after < $now;";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ---- tmdb_cache ----

    /// <summary>
    /// Returns cached TMDB response JSON for (endpoint, paramsHash,
    /// language) if present and unexpired, else null.
    /// </summary>
    public async Task<string?> GetTmdbCacheAsync(string endpoint, string paramsHash, string language, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(paramsHash);
        ArgumentNullException.ThrowIfNull(language);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT response_json, cached_at, ttl_seconds FROM tmdb_cache
            WHERE endpoint=$ep AND params_hash=$ph AND language=$lang LIMIT 1;";
        cmd.Parameters.AddWithValue("$ep", endpoint);
        cmd.Parameters.AddWithValue("$ph", paramsHash);
        cmd.Parameters.AddWithValue("$lang", language);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var json = r.GetString(0);
        var cachedAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1));
        var ttlSec = r.GetInt64(2);
        if (DateTimeOffset.UtcNow > cachedAt + TimeSpan.FromSeconds(ttlSec))
        {
            return null;
        }

        return json;
    }

    public async Task PutTmdbCacheAsync(string endpoint, string paramsHash, string language, string responseJson, TimeSpan ttl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(paramsHash);
        ArgumentNullException.ThrowIfNull(language);
        ArgumentNullException.ThrowIfNull(responseJson);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT OR REPLACE INTO tmdb_cache
                (endpoint, params_hash, language, response_json, cached_at, ttl_seconds)
                VALUES ($ep,$ph,$lang,$json,$cached,$ttl);";
            cmd.Parameters.AddWithValue("$ep", endpoint);
            cmd.Parameters.AddWithValue("$ph", paramsHash);
            cmd.Parameters.AddWithValue("$lang", language);
            cmd.Parameters.AddWithValue("$json", responseJson);
            cmd.Parameters.AddWithValue("$cached", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$ttl", (long)ttl.TotalSeconds);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> PurgeExpiredTmdbCacheAsync(CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM tmdb_cache WHERE (cached_at + ttl_seconds) < $now;";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ---- plugin_meta (key/value store) ----

    public async Task<string?> GetMetaAsync(string key, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM plugin_meta WHERE key=$k LIMIT 1;";
        cmd.Parameters.AddWithValue("$k", key);
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v is null or DBNull ? null : (string)v;
    }

    public async Task SetMetaAsync(string key, string value, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(value);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO plugin_meta(key, value) VALUES($k, $v)
                ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ---- discovery_cache ----

    /// <summary>
    /// Upsert a (tmdb_id, type) row. On insert, sets both
    /// <c>discovered_at</c> and <c>last_refreshed</c> to now; on
    /// update, refreshes only <c>last_refreshed</c> (so the first-seen
    /// timestamp is preserved for "newly discovered" UI surfacing).
    /// </summary>
    public async Task UpsertDiscoveryCacheAsync(int tmdbId, string type, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO discovery_cache
                (tmdb_id, type, discovered_at, last_refreshed)
                VALUES ($tmdb, $type, $now, $now)
                ON CONFLICT(tmdb_id, type) DO UPDATE SET
                    last_refreshed = excluded.last_refreshed;";
            cmd.Parameters.AddWithValue("$tmdb", tmdbId);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<DiscoveryCacheRow>> ListDiscoveryCacheAsync(string type, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT tmdb_id, type, discovered_at, last_refreshed
            FROM discovery_cache WHERE type=$type
            ORDER BY discovered_at DESC;";
        cmd.Parameters.AddWithValue("$type", type);
        var list = new List<DiscoveryCacheRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new DiscoveryCacheRow(
                r.GetInt32(0),
                r.GetString(1),
                DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2)),
                DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3))));
        }

        return list;
    }

    /// <summary>
    /// TTL-only purge: deletes discovery_cache rows whose
    /// <c>last_refreshed</c> is older than <paramref name="ttl"/>.
    /// Returns the number of rows deleted.
    ///
    /// <para><paramref name="protectFavourited"/> is currently ignored
    /// at this layer. The favourite-protection requirement (Stage 3.1
    /// of the channel-handoff plan) needs an <c>ILibraryManager</c>
    /// lookup, which doesn't belong inside the DB layer. Callers that
    /// need favourite-protection should two-pass it:
    /// <see cref="ListDiscoveryCacheAsync"/> → filter favourites in C#
    /// → <see cref="DeleteDiscoveryCacheRowAsync"/> per kept row.</para>
    /// </summary>
    public async Task<int> PurgeStaleDiscoveryAsync(TimeSpan ttl, bool protectFavourited, CancellationToken ct)
    {
        _ = protectFavourited;
        // TODO(stage-3.1): wire favourited-protection via ILibraryManager lookup
        // in the DiscoveryRefreshTask (two-pass: list → filter → delete-per-row)
        // rather than pushing the dependency into PhantomDb.
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM discovery_cache WHERE last_refreshed < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff",
                DateTimeOffset.UtcNow.Subtract(ttl).ToUnixTimeSeconds());
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Per-row delete helper for the Stage 3.1 two-pass favourite-protected
    /// purge. Provided so DiscoveryRefreshTask can call this in a loop
    /// after filtering the listed rows in C# against ILibraryManager.
    /// </summary>
    public async Task DeleteDiscoveryCacheRowAsync(int tmdbId, string type, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM discovery_cache WHERE tmdb_id=$tmdb AND type=$type;";
            cmd.Parameters.AddWithValue("$tmdb", tmdbId);
            cmd.Parameters.AddWithValue("$type", type);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ---- catalogue / availability v11 ----

    public async Task<int> CountCatalogueItemsAsync(string type, int? sourceMask, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sourceMask.HasValue
            ? "SELECT COUNT(*) FROM catalogue_items WHERE type=$type AND (source_mask & $mask) != 0;"
            : "SELECT COUNT(*) FROM catalogue_items WHERE type=$type;";
        cmd.Parameters.AddWithValue("$type", type);
        if (sourceMask.HasValue)
        {
            cmd.Parameters.AddWithValue("$mask", sourceMask.Value);
        }

        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<CatalogueHitWriteResult> UpsertCatalogueHitsAsync(IReadOnlyList<TmdbMetadataRow> rows, int sourceMask, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(rows);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            var seen = 0;
            var inserted = 0;
            var metadataInserted = 0;
            var metadataSkipped = 0;
            var availabilityInserted = 0;
            var seriesExpansionInserted = 0;
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (row.Type != "movie" && row.Type != "series")
                {
                    throw new ArgumentException($"Unsupported catalogue type '{row.Type}'", nameof(rows));
                }

                if (string.IsNullOrWhiteSpace(row.Title))
                {
                    continue;
                }

                seen++;
                var nowUnix = now.ToUnixTimeSeconds();
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = (SqliteTransaction)tx;
                    cmd.CommandText = @"INSERT OR IGNORE INTO catalogue_items
                        (tmdb_id, type, first_seen_at, last_seen_at, source_mask)
                        VALUES ($tmdb,$type,$now,$now,$mask);";
                    cmd.Parameters.AddWithValue("$tmdb", row.TmdbId);
                    cmd.Parameters.AddWithValue("$type", row.Type);
                    cmd.Parameters.AddWithValue("$now", nowUnix);
                    cmd.Parameters.AddWithValue("$mask", sourceMask);
                    inserted += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = (SqliteTransaction)tx;
                    cmd.CommandText = @"UPDATE catalogue_items
                        SET last_seen_at=$now, source_mask=(source_mask | $mask)
                        WHERE tmdb_id=$tmdb AND type=$type;";
                    cmd.Parameters.AddWithValue("$tmdb", row.TmdbId);
                    cmd.Parameters.AddWithValue("$type", row.Type);
                    cmd.Parameters.AddWithValue("$now", nowUnix);
                    cmd.Parameters.AddWithValue("$mask", sourceMask);
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                var metaChange = 0;
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = (SqliteTransaction)tx;
                    cmd.CommandText = @"INSERT OR IGNORE INTO tmdb_metadata
                        (tmdb_id, type, title, year, overview, poster_url, backdrop_url,
                         genres_json, official_rating, community_rating, original_title, fetched_at)
                        VALUES ($tmdb,$type,$title,$year,$overview,$poster,$backdrop,
                                $genres,$rating,$community,$origtitle,$fetched);";
                    BindMetadata(cmd, row);
                    metaChange = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                if (metaChange > 0)
                {
                    metadataInserted += metaChange;
                }
                else
                {
                    metadataSkipped++;
                }

                if (row.Type == "movie")
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.Transaction = (SqliteTransaction)tx;
                    cmd.CommandText = @"INSERT OR IGNORE INTO availability_items
                        (tmdb_id, type, season, episode, status, next_check_at)
                        VALUES ($tmdb,'movie',-1,-1,'unknown',$now);";
                    cmd.Parameters.AddWithValue("$tmdb", row.TmdbId);
                    cmd.Parameters.AddWithValue("$now", nowUnix);
                    availabilityInserted += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.Transaction = (SqliteTransaction)tx;
                    cmd.CommandText = @"INSERT OR IGNORE INTO series_expansion_state
                        (series_tmdb_id, next_expand_at) VALUES ($tmdb,$now);";
                    cmd.Parameters.AddWithValue("$tmdb", row.TmdbId);
                    cmd.Parameters.AddWithValue("$now", nowUnix);
                    seriesExpansionInserted += await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return new CatalogueHitWriteResult(seen, inserted, metadataInserted, metadataSkipped, availabilityInserted, seriesExpansionInserted);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void BindMetadata(SqliteCommand cmd, TmdbMetadataRow row)
    {
        cmd.Parameters.AddWithValue("$tmdb", row.TmdbId);
        cmd.Parameters.AddWithValue("$type", row.Type);
        cmd.Parameters.AddWithValue("$title", row.Title);
        cmd.Parameters.AddWithValue("$year", (object?)row.Year ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$overview", (object?)row.Overview ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$poster", (object?)row.PosterUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$backdrop", (object?)row.BackdropUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$genres", row.Genres is null ? (object)DBNull.Value : System.Text.Json.JsonSerializer.Serialize(row.Genres));
        cmd.Parameters.AddWithValue("$rating", (object?)row.OfficialRating ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$community", (object?)row.CommunityRating ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$origtitle", (object?)row.OriginalTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fetched", row.FetchedAt.ToUnixTimeSeconds());
    }

    public async Task<AvailabilityItemRow?> ClaimDueAvailabilityAsync(string owner, TimeSpan leaseDuration, DateTimeOffset now, string policyHash, CancellationToken ct, string? preferredType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyHash);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            var sqliteTx = (SqliteTransaction)tx;
            var preferredEpisode = string.Equals(preferredType, "episode", StringComparison.Ordinal);

            AvailabilityItemRow? row;
            if (preferredEpisode)
            {
                var cursor = await GetLongMetaInTransactionAsync(conn, sqliteTx, "availability.cursor.episode_series", ct).ConfigureAwait(false);
                row = await TryReadDueEpisodeAfterCursorAsync(conn, sqliteTx, now, policyHash, cursor, ct).ConfigureAwait(false)
                    ?? await TryReadDueEpisodeAfterCursorAsync(conn, sqliteTx, now, policyHash, null, ct).ConfigureAwait(false);
            }
            else
            {
                row = await TryReadDueAvailabilityAsync(conn, sqliteTx, now, policyHash, preferredType, ct).ConfigureAwait(false);
            }

            if (row is null)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return null;
            }

            var generation = row.ProbeGeneration + 1;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = sqliteTx;
                cmd.CommandText = @"UPDATE availability_items
                    SET lease_owner=$owner, lease_until=$until, probe_generation=$gen, attempt_count=attempt_count+1
                    WHERE tmdb_id=$tmdb AND type=$type AND season=$season AND episode=$episode
                      AND probe_generation=$oldGen
                      AND (lease_until IS NULL OR lease_until < $now);";
                cmd.Parameters.AddWithValue("$owner", owner);
                cmd.Parameters.AddWithValue("$until", now.Add(leaseDuration).ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$gen", generation);
                cmd.Parameters.AddWithValue("$oldGen", row.ProbeGeneration);
                cmd.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$tmdb", row.TmdbId);
                cmd.Parameters.AddWithValue("$type", row.Type);
                cmd.Parameters.AddWithValue("$season", row.Season);
                cmd.Parameters.AddWithValue("$episode", row.Episode);
                if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                {
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                    return null;
                }
            }

            if (preferredEpisode)
            {
                await SetMetaInTransactionAsync(conn, sqliteTx, "availability.cursor.episode_series", row.TmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return row with { ProbeGeneration = generation, LeaseOwner = owner };
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<AvailabilityItemRow?> TryReadDueAvailabilityAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        DateTimeOffset now,
        string policyHash,
        string? preferredType,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"SELECT tmdb_id,type,season,episode,status,checked_at,next_check_at,
                   candidate_magnet,candidate_info_hash,candidate_size,candidate_seeders,
                   candidate_indexer,candidate_source,probe_generation,lease_owner
            FROM availability_items
            WHERE (next_check_at <= $now OR probe_policy_hash IS NULL OR probe_policy_hash <> $policy)
              AND (lease_until IS NULL OR lease_until < $now)
              AND ($preferred IS NULL OR type=$preferred)
            ORDER BY CASE WHEN checked_at IS NULL THEN 0 WHEN status='available' THEN 1 WHEN status='unavailable' THEN 2 ELSE 3 END,
                     next_check_at ASC
            LIMIT 1;";
        cmd.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$policy", policyHash);
        cmd.Parameters.AddWithValue("$preferred", (object?)preferredType ?? DBNull.Value);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadAvailability(r) : null;
    }

    private static async Task<AvailabilityItemRow?> TryReadDueEpisodeAfterCursorAsync(
        SqliteConnection conn,
        SqliteTransaction tx,
        DateTimeOffset now,
        string policyHash,
        long? cursor,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"SELECT tmdb_id,type,season,episode,status,checked_at,next_check_at,
                   candidate_magnet,candidate_info_hash,candidate_size,candidate_seeders,
                   candidate_indexer,candidate_source,probe_generation,lease_owner
            FROM availability_items
            WHERE type='episode'
              AND ($cursor IS NULL OR tmdb_id > $cursor)
              AND (next_check_at <= $now OR probe_policy_hash IS NULL OR probe_policy_hash <> $policy)
              AND (lease_until IS NULL OR lease_until < $now)
            ORDER BY tmdb_id ASC,
                     CASE WHEN checked_at IS NULL THEN 0 WHEN status='available' THEN 1 WHEN status='unavailable' THEN 2 ELSE 3 END,
                     season ASC,
                     episode ASC
            LIMIT 1;";
        cmd.Parameters.AddWithValue("$cursor", (object?)cursor ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$policy", policyHash);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadAvailability(r) : null;
    }

    private static async Task<long?> GetLongMetaInTransactionAsync(SqliteConnection conn, SqliteTransaction tx, string key, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT value FROM plugin_meta WHERE key=$k LIMIT 1;";
        cmd.Parameters.AddWithValue("$k", key);
        var value = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (value is null or DBNull)
        {
            return null;
        }

        return long.TryParse((string)value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    private static async Task SetMetaInTransactionAsync(SqliteConnection conn, SqliteTransaction tx, string key, string value, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = @"INSERT INTO plugin_meta(key, value) VALUES($k, $v)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> CompleteAvailabilityProbeAsync(
        AvailabilityItemRow lease,
        string status,
        DateTimeOffset checkedAt,
        DateTimeOffset nextCheckAt,
        string policyHash,
        MagnetCacheEntry? candidate,
        string? errorKind,
        string? errorMessage,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        if (status != "unknown" && status != "available" && status != "unavailable")
        {
            throw new ArgumentException($"Unsupported availability status '{status}'", nameof(status));
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE availability_items SET
                    status=$status,
                    checked_at=$checked,
                    next_check_at=$next,
                    candidate_magnet=$magnet,
                    candidate_info_hash=$hash,
                    candidate_size=$size,
                    candidate_seeders=$seeders,
                    candidate_indexer=$indexer,
                    candidate_source=$source,
                    probe_policy_hash=$policy,
                    last_error_kind=$errKind,
                    last_error_message=$errMsg,
                    lease_owner=NULL,
                    lease_until=NULL
                WHERE tmdb_id=$tmdb AND type=$type AND season=$season AND episode=$episode
                  AND lease_owner=$owner AND probe_generation=$generation;";
            cmd.Parameters.AddWithValue("$status", status);
            cmd.Parameters.AddWithValue("$checked", checkedAt.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$next", nextCheckAt.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$magnet", (object?)candidate?.Magnet ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hash", (object?)candidate?.InfoHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$size", (object?)candidate?.Size ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$seeders", (object?)candidate?.Seeders ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$indexer", (object?)candidate?.Indexer ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source", (object?)candidate?.Source ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$policy", policyHash);
            cmd.Parameters.AddWithValue("$errKind", (object?)SanitizeError(errorKind, 64) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$errMsg", (object?)SanitizeError(errorMessage, 512) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tmdb", lease.TmdbId);
            cmd.Parameters.AddWithValue("$type", lease.Type);
            cmd.Parameters.AddWithValue("$season", lease.Season);
            cmd.Parameters.AddWithValue("$episode", lease.Episode);
            cmd.Parameters.AddWithValue("$owner", lease.LeaseOwner ?? string.Empty);
            cmd.Parameters.AddWithValue("$generation", lease.ProbeGeneration);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> RescheduleAvailabilityTransientAsync(AvailabilityItemRow lease, DateTimeOffset nextCheckAt, string errorKind, string? errorMessage, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE availability_items SET
                    next_check_at=$next,
                    last_error_kind=$errKind,
                    last_error_message=$errMsg,
                    lease_owner=NULL,
                    lease_until=NULL
                WHERE tmdb_id=$tmdb AND type=$type AND season=$season AND episode=$episode
                  AND lease_owner=$owner AND probe_generation=$generation;";
            cmd.Parameters.AddWithValue("$next", nextCheckAt.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$errKind", SanitizeError(errorKind, 64));
            cmd.Parameters.AddWithValue("$errMsg", (object?)SanitizeError(errorMessage, 512) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tmdb", lease.TmdbId);
            cmd.Parameters.AddWithValue("$type", lease.Type);
            cmd.Parameters.AddWithValue("$season", lease.Season);
            cmd.Parameters.AddWithValue("$episode", lease.Episode);
            cmd.Parameters.AddWithValue("$owner", lease.LeaseOwner ?? string.Empty);
            cmd.Parameters.AddWithValue("$generation", lease.ProbeGeneration);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string? SanitizeError(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var cleaned = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    public async Task<DueSeriesExpansionRow?> ClaimDueSeriesExpansionAsync(string owner, TimeSpan leaseDuration, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            int? seriesTmdb = null;
            var oldGeneration = 0;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = @"SELECT series_tmdb_id, probe_generation FROM series_expansion_state
                    WHERE next_expand_at <= $now AND (lease_until IS NULL OR lease_until < $now)
                    ORDER BY CASE WHEN last_expanded_at IS NULL THEN 0 ELSE 1 END, next_expand_at ASC
                    LIMIT 1;";
                cmd.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
                await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await r.ReadAsync(ct).ConfigureAwait(false))
                {
                    seriesTmdb = r.GetInt32(0);
                    oldGeneration = r.GetInt32(1);
                }
            }

            if (!seriesTmdb.HasValue)
            {
                await tx.CommitAsync(ct).ConfigureAwait(false);
                return null;
            }

            var generation = oldGeneration + 1;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = @"UPDATE series_expansion_state
                    SET lease_owner=$owner, lease_until=$until, probe_generation=$gen
                    WHERE series_tmdb_id=$tmdb AND probe_generation=$oldGen
                      AND (lease_until IS NULL OR lease_until < $now);";
                cmd.Parameters.AddWithValue("$owner", owner);
                cmd.Parameters.AddWithValue("$until", now.Add(leaseDuration).ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$gen", generation);
                cmd.Parameters.AddWithValue("$oldGen", oldGeneration);
                cmd.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$tmdb", seriesTmdb.Value);
                if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                {
                    await tx.CommitAsync(ct).ConfigureAwait(false);
                    return null;
                }
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return new DueSeriesExpansionRow(seriesTmdb.Value, generation, owner);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> CompleteSeriesExpansionAsync(
        DueSeriesExpansionRow lease,
        IReadOnlyList<TmdbEpisodeRow> episodes,
        IReadOnlyDictionary<(int Season, int Episode), (int EpisodeTmdbId, string? AirDate)> episodeIds,
        DateTimeOffset now,
        DateTimeOffset nextExpandAt,
        TimeSpan releaseDelay,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ArgumentNullException.ThrowIfNull(episodes);
        ArgumentNullException.ThrowIfNull(episodeIds);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            foreach (var row in episodes)
            {
                ct.ThrowIfCancellationRequested();
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = (SqliteTransaction)tx;
                    cmd.CommandText = @"INSERT OR IGNORE INTO tmdb_episode_cache
                        (series_tmdb_id, season, episode, title, overview, still_url, air_date, runtime_minutes, fetched_at)
                        VALUES ($series,$season,$episode,$title,$overview,$still,$air,$runtime,$fetched);";
                    cmd.Parameters.AddWithValue("$series", row.SeriesTmdbId);
                    cmd.Parameters.AddWithValue("$season", row.Season);
                    cmd.Parameters.AddWithValue("$episode", row.Episode);
                    cmd.Parameters.AddWithValue("$title", row.Title);
                    cmd.Parameters.AddWithValue("$overview", (object?)row.Overview ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$still", (object?)row.StillUrl ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$air", (object?)row.AirDate ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$runtime", (object?)row.RuntimeMinutes ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$fetched", row.FetchedAt.ToUnixTimeSeconds());
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                episodeIds.TryGetValue((row.Season, row.Episode), out var ids);
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = (SqliteTransaction)tx;
                    cmd.CommandText = @"INSERT INTO series_episode_catalogue
                        (series_tmdb_id, episode_tmdb_id, season, episode, air_date, first_seen_at, last_seen_at)
                        VALUES ($series,$epTmdb,$season,$episode,$air,$now,$now)
                        ON CONFLICT(series_tmdb_id, season, episode) DO UPDATE SET
                            episode_tmdb_id=excluded.episode_tmdb_id,
                            air_date=excluded.air_date,
                            last_seen_at=excluded.last_seen_at;";
                    cmd.Parameters.AddWithValue("$series", row.SeriesTmdbId);
                    cmd.Parameters.AddWithValue("$epTmdb", ids.EpisodeTmdbId);
                    cmd.Parameters.AddWithValue("$season", row.Season);
                    cmd.Parameters.AddWithValue("$episode", row.Episode);
                    cmd.Parameters.AddWithValue("$air", (object?)ids.AirDate ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                var nextCheck = ComputeEpisodeNextCheck(row.AirDate, now, releaseDelay);
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = (SqliteTransaction)tx;
                    cmd.CommandText = @"INSERT OR IGNORE INTO availability_items
                        (tmdb_id, type, season, episode, status, next_check_at)
                        VALUES ($series,'episode',$season,$episode,'unknown',$next);";
                    cmd.Parameters.AddWithValue("$series", row.SeriesTmdbId);
                    cmd.Parameters.AddWithValue("$season", row.Season);
                    cmd.Parameters.AddWithValue("$episode", row.Episode);
                    cmd.Parameters.AddWithValue("$next", nextCheck.ToUnixTimeSeconds());
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = @"UPDATE series_expansion_state SET
                        last_expanded_at=$now,
                        next_expand_at=$next,
                        lease_owner=NULL,
                        lease_until=NULL,
                        last_error_kind=NULL,
                        last_error_message=NULL
                    WHERE series_tmdb_id=$series AND lease_owner=$owner AND probe_generation=$generation;";
                cmd.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$next", nextExpandAt.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$series", lease.SeriesTmdbId);
                cmd.Parameters.AddWithValue("$owner", lease.LeaseOwner);
                cmd.Parameters.AddWithValue("$generation", lease.ProbeGeneration);
                if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) != 1)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return false;
                }
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static DateTimeOffset ComputeEpisodeNextCheck(string? airDate, DateTimeOffset now, TimeSpan releaseDelay)
    {
        if (!string.IsNullOrWhiteSpace(airDate)
            && DateTimeOffset.TryParse(airDate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed)
            && parsed > now)
        {
            return parsed.Add(releaseDelay);
        }

        return now;
    }

    public async Task<bool> FailSeriesExpansionAsync(DueSeriesExpansionRow lease, DateTimeOffset nextExpandAt, string errorKind, string? errorMessage, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE series_expansion_state SET
                    next_expand_at=$next,
                    lease_owner=NULL,
                    lease_until=NULL,
                    last_error_kind=$kind,
                    last_error_message=$message
                WHERE series_tmdb_id=$series AND lease_owner=$owner AND probe_generation=$generation;";
            cmd.Parameters.AddWithValue("$next", nextExpandAt.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$kind", SanitizeError(errorKind, 64));
            cmd.Parameters.AddWithValue("$message", (object?)SanitizeError(errorMessage, 512) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$series", lease.SeriesTmdbId);
            cmd.Parameters.AddWithValue("$owner", lease.LeaseOwner);
            cmd.Parameters.AddWithValue("$generation", lease.ProbeGeneration);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<VisibleMovieRow>> ListVisibleMovieRowsAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT m.tmdb_id,m.type,m.title,m.year,m.overview,m.poster_url,m.backdrop_url,
                   m.genres_json,m.official_rating,m.community_rating,m.original_title,m.fetched_at,
                   ms.tmdb_id,ms.type,ms.season,ms.episode,ms.stub_path,ms.fuse_path,ms.materialised_at,
                   a.tmdb_id,a.type,a.season,a.episode,a.status,a.checked_at,a.next_check_at,
                   a.candidate_magnet,a.candidate_info_hash,a.candidate_size,a.candidate_seeders,
                   a.candidate_indexer,a.candidate_source,a.probe_generation,a.lease_owner
            FROM tmdb_metadata m
            LEFT JOIN materialised_state ms ON ms.tmdb_id=m.tmdb_id AND ms.type='movie'
            LEFT JOIN availability_items a ON a.tmdb_id=m.tmdb_id AND a.type='movie' AND a.season=-1 AND a.episode=-1
            WHERE m.type='movie' AND (ms.tmdb_id IS NOT NULL OR a.status='available')
            ORDER BY COALESCE(ms.materialised_at, m.fetched_at) DESC;";
        var list = new List<VisibleMovieRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var meta = ReadTmdbMetadata(r, 0);
            MaterialisedStateRow? mat = r.IsDBNull(12) ? null : new MaterialisedStateRow(
                r.GetInt32(12), r.GetString(13), r.GetInt32(14), r.GetInt32(15), r.GetString(16), r.GetString(17), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(18)));
            AvailabilityItemRow? av = r.IsDBNull(19) ? null : ReadAvailability(r, 19);
            list.Add(new VisibleMovieRow(meta, mat, av));
        }

        return list;
    }

    public async Task<IReadOnlyList<VisibleSeriesRow>> ListVisibleSeriesRowsAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT m.tmdb_id,m.type,m.title,m.year,m.overview,m.poster_url,m.backdrop_url,
                   m.genres_json,m.official_rating,m.community_rating,m.original_title,m.fetched_at,
                   COALESCE(av.available_count,0), COALESCE(mat.materialised_count,0)
            FROM tmdb_metadata m
            LEFT JOIN (
                SELECT tmdb_id, COUNT(*) AS available_count FROM availability_items
                WHERE type='episode' AND status='available'
                GROUP BY tmdb_id
            ) av ON av.tmdb_id=m.tmdb_id
            LEFT JOIN (
                SELECT tmdb_id, COUNT(*) AS materialised_count FROM materialised_state
                WHERE type='episode'
                GROUP BY tmdb_id
            ) mat ON mat.tmdb_id=m.tmdb_id
            WHERE m.type='series' AND (COALESCE(av.available_count,0) > 0 OR COALESCE(mat.materialised_count,0) > 0)
            ORDER BY m.fetched_at DESC;";
        var list = new List<VisibleSeriesRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new VisibleSeriesRow(
                ReadTmdbMetadata(r, 0),
                Convert.ToInt32(r.GetInt64(12)),
                Convert.ToInt32(r.GetInt64(13))));
        }

        return list;
    }

    public async Task<IReadOnlyList<VisibleSeasonRow>> ListVisibleSeasonsAsync(int seriesTmdbId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT season,
                   SUM(CASE WHEN source='available' THEN 1 ELSE 0 END) AS available_count,
                   SUM(CASE WHEN source='materialised' THEN 1 ELSE 0 END) AS materialised_count
            FROM (
                SELECT season, 'available' AS source FROM availability_items
                WHERE tmdb_id=$series AND type='episode' AND status='available'
                UNION ALL
                SELECT season, 'materialised' AS source FROM materialised_state
                WHERE tmdb_id=$series AND type='episode'
            )
            GROUP BY season
            ORDER BY season;";
        cmd.Parameters.AddWithValue("$series", seriesTmdbId);
        var list = new List<VisibleSeasonRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new VisibleSeasonRow(seriesTmdbId, r.GetInt32(0), Convert.ToInt32(r.GetInt64(1)), Convert.ToInt32(r.GetInt64(2))));
        }

        return list;
    }

    public async Task<IReadOnlyList<(int SeriesTmdbId, int Season, int Episode)>> ListVisibleEpisodeIdsAsync(CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT tmdb_id, season, episode FROM availability_items
                WHERE type='episode' AND status='available'
            UNION
            SELECT tmdb_id, season, episode FROM materialised_state
                WHERE type='episode'
            ORDER BY tmdb_id, season, episode;";
        var list = new List<(int SeriesTmdbId, int Season, int Episode)>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add((r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));
        }

        return list;
    }

    public async Task<bool> IsEpisodeVisibleAsync(int seriesTmdbId, int season, int episode, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT 1 FROM materialised_state
                WHERE tmdb_id=$series AND type='episode' AND season=$season AND episode=$episode
            UNION ALL
            SELECT 1 FROM availability_items
                WHERE tmdb_id=$series AND type='episode' AND season=$season AND episode=$episode AND status='available'
            LIMIT 1;";
        cmd.Parameters.AddWithValue("$series", seriesTmdbId);
        cmd.Parameters.AddWithValue("$season", season);
        cmd.Parameters.AddWithValue("$episode", episode);
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v is not null and not DBNull;
    }

    private static TmdbMetadataRow ReadTmdbMetadata(SqliteDataReader r, int offset)
    {
        string[]? genres = null;
        if (!r.IsDBNull(offset + 7))
        {
            try
            {
                genres = System.Text.Json.JsonSerializer.Deserialize<string[]>(r.GetString(offset + 7));
            }
            catch (System.Text.Json.JsonException)
            {
                genres = null;
            }
        }

        return new TmdbMetadataRow(
            r.GetInt32(offset),
            r.GetString(offset + 1),
            r.GetString(offset + 2),
            r.IsDBNull(offset + 3) ? null : r.GetInt32(offset + 3),
            r.IsDBNull(offset + 4) ? null : r.GetString(offset + 4),
            r.IsDBNull(offset + 5) ? null : r.GetString(offset + 5),
            r.IsDBNull(offset + 6) ? null : r.GetString(offset + 6),
            genres,
            r.IsDBNull(offset + 8) ? null : r.GetString(offset + 8),
            r.IsDBNull(offset + 9) ? null : r.GetDouble(offset + 9),
            r.IsDBNull(offset + 10) ? null : r.GetString(offset + 10),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(offset + 11)));
    }

    private static AvailabilityItemRow ReadAvailability(SqliteDataReader r, int offset = 0)
        => new(
            r.GetInt32(offset),
            r.GetString(offset + 1),
            r.GetInt32(offset + 2),
            r.GetInt32(offset + 3),
            r.GetString(offset + 4),
            r.IsDBNull(offset + 5) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(offset + 5)),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(offset + 6)),
            r.IsDBNull(offset + 7) ? null : r.GetString(offset + 7),
            r.IsDBNull(offset + 8) ? null : r.GetString(offset + 8),
            r.IsDBNull(offset + 9) ? null : r.GetInt64(offset + 9),
            r.IsDBNull(offset + 10) ? null : r.GetInt32(offset + 10),
            r.IsDBNull(offset + 11) ? null : r.GetString(offset + 11),
            r.IsDBNull(offset + 12) ? null : r.GetString(offset + 12),
            r.GetInt32(offset + 13),
            r.IsDBNull(offset + 14) ? null : r.GetString(offset + 14));

    // ---- materialise_in_flight ----

    public async Task<bool> TryInsertMaterialiseInFlightAsync(int tmdbId, string type, int season, int episode, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT OR IGNORE INTO materialise_in_flight
                (tmdb_id, type, season, episode, started_at)
                VALUES ($tmdb, $type, $season, $episode, $now);";
            cmd.Parameters.AddWithValue("$tmdb", tmdbId);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$season", season);
            cmd.Parameters.AddWithValue("$episode", episode);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpsertMaterialiseInFlightAsync(int tmdbId, string type, int season, int episode, CancellationToken ct)
    {
        _ = await TryInsertMaterialiseInFlightAsync(tmdbId, type, season, episode, ct).ConfigureAwait(false);
    }

    public async Task DeleteMaterialiseInFlightAsync(int tmdbId, string type, int season, int episode, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"DELETE FROM materialise_in_flight
                WHERE tmdb_id=$tmdb AND type=$type AND season=$season AND episode=$episode;";
            cmd.Parameters.AddWithValue("$tmdb", tmdbId);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$season", season);
            cmd.Parameters.AddWithValue("$episode", episode);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> IsMaterialiseInFlightAsync(int tmdbId, string type, int season, int episode, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT 1 FROM materialise_in_flight
            WHERE tmdb_id=$tmdb AND type=$type AND season=$season AND episode=$episode
            LIMIT 1;";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$season", season);
        cmd.Parameters.AddWithValue("$episode", episode);
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v is not null and not DBNull;
    }

    /// <summary>
    /// Purges materialise_in_flight rows older than
    /// <paramref name="threshold"/>. Returns the count of rows deleted
    /// so the Stage 4 startup sweeper can log it.
    /// </summary>
    public async Task<int> PurgeStaleMaterialiseInFlightAsync(TimeSpan threshold, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM materialise_in_flight WHERE started_at < $cutoff;";
            cmd.Parameters.AddWithValue("$cutoff",
                DateTimeOffset.UtcNow.Subtract(threshold).ToUnixTimeSeconds());
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ---- materialised_state ----

    /// <summary>
    /// Inserts a materialised_state row. Uses INSERT OR REPLACE so a
    /// re-materialise of the same (tmdb, type, season, episode) tuple
    /// overwrites the previous path pair atomically rather than
    /// throwing — re-materialise IS the expected upsert path (e.g.
    /// gostream re-cached an evicted file under a new stub path).
    /// </summary>
    public async Task InsertMaterialisedStateAsync(int tmdbId, string type, int season, int episode, string stubPath, string fusePath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(stubPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(fusePath);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT OR REPLACE INTO materialised_state
                (tmdb_id, type, season, episode, stub_path, fuse_path, materialised_at)
                VALUES ($tmdb, $type, $season, $episode, $stub, $fuse, $now);";
            cmd.Parameters.AddWithValue("$tmdb", tmdbId);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$season", season);
            cmd.Parameters.AddWithValue("$episode", episode);
            cmd.Parameters.AddWithValue("$stub", stubPath);
            cmd.Parameters.AddWithValue("$fuse", fusePath);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<MaterialisedStateRow?> GetMaterialisedStateAsync(int tmdbId, string type, int season, int episode, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT tmdb_id, type, season, episode, stub_path, fuse_path, materialised_at
            FROM materialised_state
            WHERE tmdb_id=$tmdb AND type=$type AND season=$season AND episode=$episode
            LIMIT 1;";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$season", season);
        cmd.Parameters.AddWithValue("$episode", episode);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new MaterialisedStateRow(
            r.GetInt32(0),
            r.GetString(1),
            r.GetInt32(2),
            r.GetInt32(3),
            r.GetString(4),
            r.GetString(5),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(6)));
    }

    public async Task<IReadOnlyList<MaterialisedStateRow>> ListMaterialisedStateAsync(string type, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT tmdb_id, type, season, episode, stub_path, fuse_path, materialised_at
            FROM materialised_state WHERE type=$type
            ORDER BY materialised_at DESC;";
        cmd.Parameters.AddWithValue("$type", type);
        var list = new List<MaterialisedStateRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new MaterialisedStateRow(
                r.GetInt32(0),
                r.GetString(1),
                r.GetInt32(2),
                r.GetInt32(3),
                r.GetString(4),
                r.GetString(5),
                DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(6))));
        }

        return list;
    }

    public async Task DeleteMaterialisedStateAsync(int tmdbId, string type, int season, int episode, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"DELETE FROM materialised_state
                WHERE tmdb_id=$tmdb AND type=$type AND season=$season AND episode=$episode;";
            cmd.Parameters.AddWithValue("$tmdb", tmdbId);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$season", season);
            cmd.Parameters.AddWithValue("$episode", episode);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ---- tmdb_external_ids ----

    /// <summary>
    /// Reads the cached (tmdb_id, type) → imdb_id mapping. Returns
    /// <c>null</c> if no row exists. A returned row with
    /// <c>ImdbId == null</c> is a negative-cache entry; callers that
    /// care about negative-cache TTL must inspect <c>FetchedAt</c>.
    /// </summary>
    public async Task<TmdbExternalIdRow?> GetImdbIdAsync(int tmdbId, string type, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT imdb_id, fetched_at FROM tmdb_external_ids
            WHERE tmdb_id=$tmdb AND type=$type LIMIT 1;";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var imdbId = r.IsDBNull(0) ? null : r.GetString(0);
        var fetchedAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(1));
        return new TmdbExternalIdRow(imdbId, fetchedAt);
    }

    /// <summary>
    /// Upserts the (tmdb_id, type) → imdb_id mapping. A null
    /// <paramref name="imdbId"/> writes a negative-cache row.
    /// </summary>
    public async Task SetImdbIdAsync(int tmdbId, string type, string? imdbId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO tmdb_external_ids
                (tmdb_id, type, imdb_id, fetched_at)
                VALUES ($tmdb, $type, $imdb, $now)
                ON CONFLICT(tmdb_id, type) DO UPDATE SET
                    imdb_id = excluded.imdb_id,
                    fetched_at = excluded.fetched_at;";
            cmd.Parameters.AddWithValue("$tmdb", tmdbId);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$imdb", (object?)imdbId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ---- tmdb_metadata ----

    /// <summary>
    /// Upserts a (tmdb_id, type) metadata row. INSERT OR REPLACE
    /// semantics: the latest fetch wins. <paramref name="row"/>'s
    /// <see cref="TmdbMetadataRow.Genres"/> is serialised as a JSON
    /// array; null is stored as SQL NULL.
    /// </summary>
    public async Task UpsertTmdbMetadataAsync(TmdbMetadataRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.Type);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.Title);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT OR REPLACE INTO tmdb_metadata
                (tmdb_id, type, title, year, overview, poster_url, backdrop_url,
                 genres_json, official_rating, community_rating, original_title, fetched_at)
                VALUES ($tmdb,$type,$title,$year,$overview,$poster,$backdrop,
                        $genres,$rating,$community,$origtitle,$fetched);";
            cmd.Parameters.AddWithValue("$tmdb", row.TmdbId);
            cmd.Parameters.AddWithValue("$type", row.Type);
            cmd.Parameters.AddWithValue("$title", row.Title);
            cmd.Parameters.AddWithValue("$year", (object?)row.Year ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$overview", (object?)row.Overview ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$poster", (object?)row.PosterUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$backdrop", (object?)row.BackdropUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$genres",
                row.Genres is null
                    ? (object)DBNull.Value
                    : System.Text.Json.JsonSerializer.Serialize(row.Genres));
            cmd.Parameters.AddWithValue("$rating", (object?)row.OfficialRating ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$community", (object?)row.CommunityRating ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$origtitle", (object?)row.OriginalTitle ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fetched", row.FetchedAt.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<TmdbMetadataRow?> FindTmdbMetadataByTitleYearAsync(string type, string title, int? year, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT tmdb_id, type, title, year, overview, poster_url, backdrop_url,
                   genres_json, official_rating, community_rating, original_title, fetched_at
            FROM tmdb_metadata
            WHERE type=$type AND ($year IS NULL OR year=$year)
            ORDER BY fetched_at DESC;";
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$year", (object?)year ?? DBNull.Value);
        var wanted = NormalizeTitle(title);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = ReadTmdbMetadata(r);
            if (string.Equals(NormalizeTitle(row.Title), wanted, StringComparison.Ordinal)
                || string.Equals(NormalizeTitle(row.OriginalTitle), wanted, StringComparison.Ordinal))
            {
                return row;
            }
        }

        return null;
    }

    public async Task<TmdbMetadataRow?> GetTmdbMetadataAsync(int tmdbId, string type, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT tmdb_id, type, title, year, overview, poster_url, backdrop_url,
                   genres_json, official_rating, community_rating, original_title, fetched_at
            FROM tmdb_metadata WHERE tmdb_id=$tmdb AND type=$type LIMIT 1;";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return ReadTmdbMetadata(r);
    }

    private static TmdbMetadataRow ReadTmdbMetadata(SqliteDataReader r)
    {
        string[]? genres = null;
        if (!r.IsDBNull(7))
        {
            try
            {
                genres = System.Text.Json.JsonSerializer.Deserialize<string[]>(r.GetString(7));
            }
            catch (System.Text.Json.JsonException)
            {
                genres = null;
            }
        }

        return new TmdbMetadataRow(
            r.GetInt32(0),
            r.GetString(1),
            r.GetString(2),
            r.IsDBNull(3) ? null : r.GetInt32(3),
            r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            genres,
            r.IsDBNull(8) ? null : r.GetString(8),
            r.IsDBNull(9) ? null : r.GetDouble(9),
            r.IsDBNull(10) ? null : r.GetString(10),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(11)));
    }

    private static string NormalizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var sb = new System.Text.StringBuilder(title.Length);
        foreach (var ch in title)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToUpperInvariant(ch));
            }
        }

        return sb.ToString();
    }

    // ---- tmdb_episode_cache ----

    /// <summary>
    /// Upserts a (series_tmdb_id, season, episode) row. INSERT OR
    /// REPLACE semantics so the latest fetch overwrites the prior cached
    /// title/overview/still without throwing on conflict.
    /// </summary>
    public async Task UpsertTmdbEpisodeAsync(TmdbEpisodeRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.Title);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT OR REPLACE INTO tmdb_episode_cache
                (series_tmdb_id, season, episode, title, overview, still_url,
                 air_date, runtime_minutes, fetched_at)
                VALUES ($series, $season, $episode, $title, $overview, $still,
                        $air, $runtime, $fetched);";
            cmd.Parameters.AddWithValue("$series", row.SeriesTmdbId);
            cmd.Parameters.AddWithValue("$season", row.Season);
            cmd.Parameters.AddWithValue("$episode", row.Episode);
            cmd.Parameters.AddWithValue("$title", row.Title);
            cmd.Parameters.AddWithValue("$overview", (object?)row.Overview ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$still", (object?)row.StillUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$air", (object?)row.AirDate ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$runtime", (object?)row.RuntimeMinutes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fetched", row.FetchedAt.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<TmdbEpisodeRow?> GetTmdbEpisodeAsync(int seriesTmdbId, int season, int episode, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT series_tmdb_id, season, episode, title, overview, still_url,
                   air_date, runtime_minutes, fetched_at
            FROM tmdb_episode_cache
            WHERE series_tmdb_id=$series AND season=$season AND episode=$episode
            LIMIT 1;";
        cmd.Parameters.AddWithValue("$series", seriesTmdbId);
        cmd.Parameters.AddWithValue("$season", season);
        cmd.Parameters.AddWithValue("$episode", episode);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return ReadEpisodeRow(r);
    }

    /// <summary>
    /// Lists all cached episodes for a season ordered by episode
    /// number ascending. Empty list when the season has not been
    /// warmed yet.
    /// </summary>
    public async Task<IReadOnlyList<TmdbEpisodeRow>> ListEpisodesForSeasonAsync(int seriesTmdbId, int season, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT series_tmdb_id, season, episode, title, overview, still_url,
                   air_date, runtime_minutes, fetched_at
            FROM tmdb_episode_cache
            WHERE series_tmdb_id=$series AND season=$season
            ORDER BY episode ASC;";
        cmd.Parameters.AddWithValue("$series", seriesTmdbId);
        cmd.Parameters.AddWithValue("$season", season);
        var list = new List<TmdbEpisodeRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadEpisodeRow(r));
        }

        return list;
    }

    private static TmdbEpisodeRow ReadEpisodeRow(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        return new TmdbEpisodeRow(
            r.GetInt32(0),
            r.GetInt32(1),
            r.GetInt32(2),
            r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5),
            r.IsDBNull(6) ? null : r.GetString(6),
            r.IsDBNull(7) ? null : r.GetInt32(7),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(8)));
    }

    private static void BindKey(SqliteCommand cmd, MagnetCacheKey key)
    {
        cmd.Parameters.AddWithValue("$tmdb", key.TmdbId ?? 0);
        cmd.Parameters.AddWithValue("$imdb", key.ImdbId ?? string.Empty);
        cmd.Parameters.AddWithValue("$type", key.Type);
        cmd.Parameters.AddWithValue("$season", key.Season ?? 0);
        cmd.Parameters.AddWithValue("$episode", key.Episode ?? 0);
    }
}
