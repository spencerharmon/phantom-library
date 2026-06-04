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

public sealed record PhantomItemRow
{
    public int? TmdbId { get; init; }
    public string? ImdbId { get; init; }
    public required string Type { get; init; }
    public required PhantomItemState State { get; init; }
    public required DateTimeOffset FirstSeen { get; init; }
    public required DateTimeOffset LastTouched { get; init; }
    public bool EvictionProtected { get; init; }
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

        if (version >= 1)
        {
            return;
        }

        using var tx = conn.BeginTransaction();
        using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = SchemaV1Sql;
            cmd.ExecuteNonQuery();
        }

        using (var sv = conn.CreateCommand())
        {
            sv.Transaction = tx;
            sv.CommandText = "PRAGMA user_version = 1;";
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
                (item_guid, tmdb_id, imdb_id, type, state, first_seen, last_touched, eviction_protected)
                VALUES ($guid,$tmdb,$imdb,$type,$state,$first,$last,$prot)
                ON CONFLICT(item_guid) DO UPDATE SET
                    tmdb_id=excluded.tmdb_id,
                    imdb_id=excluded.imdb_id,
                    type=excluded.type,
                    state=excluded.state,
                    last_touched=excluded.last_touched,
                    eviction_protected=excluded.eviction_protected;";
            cmd.Parameters.AddWithValue("$guid", jellyfinItemId.ToString("N"));
            cmd.Parameters.AddWithValue("$tmdb", (object?)row.TmdbId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$imdb", (object?)row.ImdbId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$type", row.Type);
            cmd.Parameters.AddWithValue("$state", row.State.ToString());
            cmd.Parameters.AddWithValue("$first", row.FirstSeen.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$last", row.LastTouched.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$prot", row.EvictionProtected ? 1 : 0);
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
        cmd.CommandText = @"SELECT tmdb_id, imdb_id, type, state, first_seen, last_touched, eviction_protected
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
        };
    }

    // ---- user_prefs / autopilot_state: M7/M8 ----
    // TODO(M7/M8): implement real accessors. Returning defaults here so M4
    // callers don't break when these tables are referenced.

    private static void BindKey(SqliteCommand cmd, MagnetCacheKey key)
    {
        cmd.Parameters.AddWithValue("$tmdb", key.TmdbId ?? 0);
        cmd.Parameters.AddWithValue("$imdb", key.ImdbId ?? string.Empty);
        cmd.Parameters.AddWithValue("$type", key.Type);
        cmd.Parameters.AddWithValue("$season", key.Season ?? 0);
        cmd.Parameters.AddWithValue("$episode", key.Episode ?? 0);
    }
}
