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
/// channel architecture (schema v8). Single writer, serialised via a
/// process-wide <see cref="SemaphoreSlim"/>; concurrent readers
/// permitted via separate short-lived connections.
///
/// Schema v9 is a clean break from the v5 file-on-disk schema (v6/v7/v8
/// were intermediate channel-arch revisions that never reached prod;
/// v9 adds the <c>tmdb_episode_cache</c> table the shows channel needs
/// for per-episode display metadata at refresh time). Per
/// AGENTS.md "No database migrations until v1.0", existing databases
/// at any pre-v9 user_version are HARD-REFUSED and the operator must
/// run <c>scripts/phantom-wipe.sh</c> before restart.
/// </summary>
public sealed class PhantomDb : IDisposable
{
    private const int CurrentSchemaVersion = 9;

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

        // version == 0: fresh / never-initialised DB. Create the v8 schema.
        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = SchemaV9Sql;
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

    private const string SchemaV9Sql = @"
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

    // ---- materialise_in_flight ----

    public async Task UpsertMaterialiseInFlightAsync(int tmdbId, string type, int season, int episode, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO materialise_in_flight
                (tmdb_id, type, season, episode, started_at)
                VALUES ($tmdb, $type, $season, $episode, $now)
                ON CONFLICT(tmdb_id, type, season, episode) DO UPDATE SET
                    started_at = excluded.started_at;";
            cmd.Parameters.AddWithValue("$tmdb", tmdbId);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$season", season);
            cmd.Parameters.AddWithValue("$episode", episode);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
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
