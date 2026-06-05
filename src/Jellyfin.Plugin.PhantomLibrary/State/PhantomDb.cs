using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.PhantomLibrary.State;

public enum PhantomItemState
{
    Phantom,
    Virtual,
    Materialised,
    Unavailable,
}

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

public sealed record MaterialisationLogEntry
{
    public required DateTimeOffset Timestamp { get; init; }
    public Guid? ItemGuid { get; init; }
    public required string Trigger { get; init; }
    public required long DurationMs { get; init; }
    public required string Outcome { get; init; }
    public string? Error { get; init; }
    public string? Indexer { get; init; }
    public string? InfoHash { get; init; }
}

/// <summary>Per-user, per-series autopilot tracking row (autopilot_state table).</summary>
public sealed record AutopilotStateRow
{
    public required Guid UserId { get; init; }
    public required string SeriesImdb { get; init; }
    public int? LastPlayedSeason { get; init; }
    public int? LastPlayedEpisode { get; init; }
    public int? NextMaterialisedSeason { get; init; }
    public int? NextMaterialisedEpisode { get; init; }
    public int? PrefetchCursorSeason { get; init; }
    public int? PrefetchCursorEpisode { get; init; }
    public required long UpdatedAt { get; init; }
}

public sealed record PhantomItemRow
{
    public int? TmdbId { get; init; }
    public string? ImdbId { get; init; }
    public required string Type { get; init; }
    public required PhantomItemState State { get; init; }
    public required DateTimeOffset FirstSeen { get; init; }
    public required DateTimeOffset LastTouched { get; init; }
    public bool EvictionProtected { get; init; }
    public string? OriginalOverview { get; init; }
}

/// <summary>
/// SQLite-backed persistence for the plugin's private state. Single
/// writer, serialised via a process-wide <see cref="SemaphoreSlim"/>;
/// concurrent readers permitted via separate short-lived connections.
/// </summary>
public sealed class PhantomDb : IDisposable
{
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

    private const int CurrentSchemaVersion = 3;

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
            version = Convert.ToInt32(v.ExecuteScalar() ?? 0, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (version >= CurrentSchemaVersion)
        {
            return;
        }

        using var tx = conn.BeginTransaction();

        if (version < 1)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = SchemaV1Sql;
            cmd.ExecuteNonQuery();
        }

        if (version < 2)
        {
            // v2: add original_overview to phantom_items so PhantomStatusDecorator
            // can round-trip the user-visible Overview after stamping its prefix.
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = "ALTER TABLE phantom_items ADD COLUMN original_overview TEXT;";
            cmd.ExecuteNonQuery();
        }

        if (version < 3)
        {
            // v3: tmdb_cache table for SuggestionsContributor (M6). Additive.
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = SchemaV3Sql;
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

    private const string SchemaV1Sql = @"
CREATE TABLE IF NOT EXISTS phantom_items (
    item_guid TEXT PRIMARY KEY,
    tmdb_id INTEGER,
    imdb_id TEXT,
    type TEXT NOT NULL,
    state TEXT NOT NULL,
    first_seen INTEGER NOT NULL,
    last_touched INTEGER NOT NULL,
    eviction_protected INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS magnet_cache (
    tmdb_id INTEGER NOT NULL DEFAULT 0,
    imdb_id TEXT NOT NULL DEFAULT '',
    type TEXT NOT NULL,
    season INTEGER NOT NULL DEFAULT 0,
    episode INTEGER NOT NULL DEFAULT 0,
    preset TEXT NOT NULL DEFAULT '',
    magnet TEXT NOT NULL,
    info_hash TEXT NOT NULL,
    size INTEGER NOT NULL,
    seeders INTEGER NOT NULL,
    indexer TEXT NOT NULL,
    cached_at INTEGER NOT NULL,
    ttl_seconds INTEGER NOT NULL,
    source TEXT NOT NULL,
    PRIMARY KEY (tmdb_id, imdb_id, type, season, episode, preset)
);
CREATE TABLE IF NOT EXISTS materialisation_log (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts INTEGER NOT NULL,
    item_guid TEXT,
    trigger TEXT NOT NULL,
    duration_ms INTEGER NOT NULL,
    outcome TEXT NOT NULL,
    error TEXT,
    indexer TEXT,
    info_hash TEXT
);
CREATE TABLE IF NOT EXISTS unavailable_marker (
    tmdb_id INTEGER NOT NULL DEFAULT 0,
    imdb_id TEXT NOT NULL DEFAULT '',
    type TEXT NOT NULL,
    season INTEGER NOT NULL DEFAULT 0,
    episode INTEGER NOT NULL DEFAULT 0,
    marked_at INTEGER NOT NULL,
    retry_after INTEGER NOT NULL,
    PRIMARY KEY (tmdb_id, imdb_id, type, season, episode)
);
CREATE TABLE IF NOT EXISTS user_prefs (
    user_id TEXT PRIMARY KEY,
    protect_favourites INTEGER NOT NULL DEFAULT 1,
    show_phantoms INTEGER NOT NULL DEFAULT 1,
    allow_eager INTEGER NOT NULL DEFAULT 1
);
CREATE TABLE IF NOT EXISTS autopilot_state (
    user_id TEXT NOT NULL,
    series_imdb TEXT NOT NULL,
    last_played_season INTEGER,
    last_played_episode INTEGER,
    next_materialised_season INTEGER,
    next_materialised_episode INTEGER,
    prefetch_cursor_season INTEGER,
    prefetch_cursor_episode INTEGER,
    updated_at INTEGER NOT NULL,
    PRIMARY KEY (user_id, series_imdb)
);
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

    public async Task<bool> IsMarkedUnavailableAsync(UnavailableKey key, CancellationToken ct)
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
            return false;
        }

        var retryAfter = DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture));
        return DateTimeOffset.UtcNow < retryAfter;
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

    // ---- materialisation_log ----

    public async Task LogMaterialisationAsync(MaterialisationLogEntry entry, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO materialisation_log
                (ts, item_guid, trigger, duration_ms, outcome, error, indexer, info_hash)
                VALUES ($ts,$guid,$trigger,$dur,$outcome,$err,$idx,$hash);";
            cmd.Parameters.AddWithValue("$ts", entry.Timestamp.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$guid", (object?)entry.ItemGuid?.ToString("N") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$trigger", entry.Trigger);
            cmd.Parameters.AddWithValue("$dur", entry.DurationMs);
            cmd.Parameters.AddWithValue("$outcome", entry.Outcome);
            cmd.Parameters.AddWithValue("$err", (object?)entry.Error ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$idx", (object?)entry.Indexer ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hash", (object?)entry.InfoHash ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ---- phantom_items ----

    public async Task UpsertPhantomItemAsync(Guid jellyfinItemId, PhantomItemRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO phantom_items
                (item_guid, tmdb_id, imdb_id, type, state, first_seen, last_touched, eviction_protected, original_overview)
                VALUES ($guid,$tmdb,$imdb,$type,$state,$first,$last,$prot,$orig)
                ON CONFLICT(item_guid) DO UPDATE SET
                    tmdb_id=excluded.tmdb_id,
                    imdb_id=excluded.imdb_id,
                    type=excluded.type,
                    state=excluded.state,
                    last_touched=excluded.last_touched,
                    eviction_protected=excluded.eviction_protected,
                    original_overview=excluded.original_overview;";
            cmd.Parameters.AddWithValue("$guid", jellyfinItemId.ToString("N"));
            cmd.Parameters.AddWithValue("$tmdb", (object?)row.TmdbId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$imdb", (object?)row.ImdbId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$type", row.Type);
            cmd.Parameters.AddWithValue("$state", row.State.ToString());
            cmd.Parameters.AddWithValue("$first", row.FirstSeen.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$last", row.LastTouched.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$prot", row.EvictionProtected ? 1 : 0);
            cmd.Parameters.AddWithValue("$orig", (object?)row.OriginalOverview ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<PhantomItemRow?> GetPhantomItemAsync(Guid jellyfinItemId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT tmdb_id, imdb_id, type, state, first_seen, last_touched, eviction_protected, original_overview
            FROM phantom_items WHERE item_guid=$guid LIMIT 1;";
        cmd.Parameters.AddWithValue("$guid", jellyfinItemId.ToString("N"));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        return new PhantomItemRow
        {
            TmdbId = r.IsDBNull(0) ? null : r.GetInt32(0),
            ImdbId = r.IsDBNull(1) ? null : r.GetString(1),
            Type = r.GetString(2),
            State = Enum.Parse<PhantomItemState>(r.GetString(3)),
            FirstSeen = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(4)),
            LastTouched = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(5)),
            EvictionProtected = r.GetInt32(6) != 0,
            OriginalOverview = r.IsDBNull(7) ? null : r.GetString(7),
        };
    }

    /// <summary>
    /// Stores the unmodified Overview text for the item, leaving every
    /// other column alone. Only writes on first call (when the existing
    /// stored value is NULL) so that repeated invocations during the
    /// materialising → ready transitions never overwrite the
    /// genuinely-original copy with a previously-decorated one.
    /// </summary>
    public async Task<string?> RememberOriginalOverviewAsync(
        Guid jellyfinItemId, string? overview, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using (var read = conn.CreateCommand())
            {
                read.CommandText = "SELECT original_overview FROM phantom_items WHERE item_guid=$guid LIMIT 1;";
                read.Parameters.AddWithValue("$guid", jellyfinItemId.ToString("N"));
                var existing = await read.ExecuteScalarAsync(ct).ConfigureAwait(false);
                if (existing is not null && existing is not DBNull)
                {
                    return (string)existing;
                }
            }

            // Either no row, or row with NULL original_overview. Upsert.
            await using (var write = conn.CreateCommand())
            {
                write.CommandText = @"INSERT INTO phantom_items
                    (item_guid, tmdb_id, imdb_id, type, state, first_seen, last_touched, eviction_protected, original_overview)
                    VALUES ($guid, NULL, NULL, 'unknown', 'Virtual', $now, $now, 0, $orig)
                    ON CONFLICT(item_guid) DO UPDATE SET
                        original_overview = COALESCE(phantom_items.original_overview, excluded.original_overview);";
                write.Parameters.AddWithValue("$guid", jellyfinItemId.ToString("N"));
                write.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                write.Parameters.AddWithValue("$orig", (object?)overview ?? DBNull.Value);
                await write.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            return overview;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Returns the stored original overview (if any) and clears it from
    /// the row in the same transaction — used when the decorator restores
    /// the user-visible Overview after a Finished phase.
    /// </summary>
    public async Task<string?> TakeOriginalOverviewAsync(Guid jellyfinItemId, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            string? value;
            await using (var read = conn.CreateCommand())
            {
                read.CommandText = "SELECT original_overview FROM phantom_items WHERE item_guid=$guid LIMIT 1;";
                read.Parameters.AddWithValue("$guid", jellyfinItemId.ToString("N"));
                var existing = await read.ExecuteScalarAsync(ct).ConfigureAwait(false);
                value = existing is null or DBNull ? null : (string)existing;
            }

            if (value is not null)
            {
                await using var clear = conn.CreateCommand();
                clear.CommandText = "UPDATE phantom_items SET original_overview = NULL WHERE item_guid=$guid;";
                clear.Parameters.AddWithValue("$guid", jellyfinItemId.ToString("N"));
                await clear.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            return value;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ---- user_prefs / autopilot_state ----

    /// <summary>Reads autopilot state for (user, series_imdb) or returns null if absent.</summary>
    public async Task<AutopilotStateRow?> GetAutopilotStateAsync(Guid userId, string seriesImdb, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(seriesImdb);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT last_played_season, last_played_episode,
            next_materialised_season, next_materialised_episode,
            prefetch_cursor_season, prefetch_cursor_episode, updated_at
            FROM autopilot_state
            WHERE user_id=$uid AND series_imdb=$imdb LIMIT 1;";
        cmd.Parameters.AddWithValue("$uid", userId.ToString("N"));
        cmd.Parameters.AddWithValue("$imdb", seriesImdb);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        static int? NullableInt(Microsoft.Data.Sqlite.SqliteDataReader r, int ord) => r.IsDBNull(ord) ? null : r.GetInt32(ord);
        return new AutopilotStateRow
        {
            UserId = userId,
            SeriesImdb = seriesImdb,
            LastPlayedSeason = NullableInt(r, 0),
            LastPlayedEpisode = NullableInt(r, 1),
            NextMaterialisedSeason = NullableInt(r, 2),
            NextMaterialisedEpisode = NullableInt(r, 3),
            PrefetchCursorSeason = NullableInt(r, 4),
            PrefetchCursorEpisode = NullableInt(r, 5),
            UpdatedAt = r.GetInt64(6),
        };
    }

    /// <summary>Upserts an autopilot_state row.</summary>
    public async Task UpsertAutopilotStateAsync(AutopilotStateRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentException.ThrowIfNullOrWhiteSpace(row.SeriesImdb);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO autopilot_state
                (user_id, series_imdb, last_played_season, last_played_episode,
                 next_materialised_season, next_materialised_episode,
                 prefetch_cursor_season, prefetch_cursor_episode, updated_at)
                VALUES ($uid,$imdb,$lps,$lpe,$nms,$nme,$pcs,$pce,$updated)
                ON CONFLICT(user_id, series_imdb) DO UPDATE SET
                    last_played_season=excluded.last_played_season,
                    last_played_episode=excluded.last_played_episode,
                    next_materialised_season=excluded.next_materialised_season,
                    next_materialised_episode=excluded.next_materialised_episode,
                    prefetch_cursor_season=excluded.prefetch_cursor_season,
                    prefetch_cursor_episode=excluded.prefetch_cursor_episode,
                    updated_at=excluded.updated_at;";
            cmd.Parameters.AddWithValue("$uid", row.UserId.ToString("N"));
            cmd.Parameters.AddWithValue("$imdb", row.SeriesImdb);
            cmd.Parameters.AddWithValue("$lps", (object?)row.LastPlayedSeason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$lpe", (object?)row.LastPlayedEpisode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$nms", (object?)row.NextMaterialisedSeason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$nme", (object?)row.NextMaterialisedEpisode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pcs", (object?)row.PrefetchCursorSeason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$pce", (object?)row.PrefetchCursorEpisode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$updated", row.UpdatedAt);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ---- user_prefs / autopilot_state: M7 ----
    // TODO(M7): implement user_prefs accessors.

    private const string SchemaV3Sql = @"
CREATE TABLE IF NOT EXISTS tmdb_cache (
    endpoint TEXT NOT NULL,
    params_hash TEXT NOT NULL,
    language TEXT NOT NULL,
    response_json TEXT NOT NULL,
    cached_at INTEGER NOT NULL,
    ttl_seconds INTEGER NOT NULL,
    PRIMARY KEY (endpoint, params_hash, language)
);
CREATE INDEX IF NOT EXISTS idx_tmdb_cache_expiry ON tmdb_cache(cached_at, ttl_seconds);
";

    // ---- tmdb_cache ----

    /// <summary>
    /// Returns cached TMDB response JSON for (endpoint, paramsHash, language) if present and unexpired, else null.
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

    /// <summary>Writes or replaces a cached TMDB response.</summary>
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

    /// <summary>Removes expired tmdb_cache rows. Returns count deleted.</summary>
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

    private static void BindKey(SqliteCommand cmd, MagnetCacheKey key)
    {
        cmd.Parameters.AddWithValue("$tmdb", key.TmdbId ?? 0);
        cmd.Parameters.AddWithValue("$imdb", key.ImdbId ?? string.Empty);
        cmd.Parameters.AddWithValue("$type", key.Type);
        cmd.Parameters.AddWithValue("$season", key.Season ?? 0);
        cmd.Parameters.AddWithValue("$episode", key.Episode ?? 0);
    }
}
