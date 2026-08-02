using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    public string ValidationPolicyVersion { get; init; } = "legacy";
}

public sealed record SourceCandidateRow(
    int TmdbId,
    string Type,
    int Season,
    int Episode,
    string Preset,
    string Magnet,
    string InfoHash,
    string Indexer,
    string Title,
    int? Seeders,
    long? Size,
    int Rank,
    string Source,
    DateTimeOffset FetchedAt,
    DateTimeOffset ExpiresAt,
    string ValidationStatus = "unknown",
    string? ValidationReason = null,
    DateTimeOffset? ValidatedAt = null,
    DateTimeOffset? ValidationExpiresAt = null,
    long? ValidationDurationMs = null,
    string ValidationPolicyVersion = "unknown",
    long? SelectedFileId = null,
    string? SelectedFilePath = null,
    long? SelectedFileSize = null);


public sealed record SourceCandidateValidationUpdate(
    int TmdbId,
    string Type,
    int Season,
    int Episode,
    string Preset,
    string Magnet,
    string ValidationStatus,
    string? ValidationReason,
    DateTimeOffset? ValidatedAt,
    DateTimeOffset? ValidationExpiresAt,
    long? ValidationDurationMs,
    string ValidationPolicyVersion,
    long? SelectedFileId,
    string? SelectedFilePath,
    long? SelectedFileSize);

public sealed record BulkMaterialiseRequestRow(
    string RequestId,
    string UserId,
    string ParentExternalId,
    string ParentKind,
    int TmdbId,
    int Season,
    string Trigger,
    string Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset UpdatedAt,
    string? LastError,
    DateTimeOffset? LastUnfavoritedAt,
    int Generation);

public sealed record BulkMaterialiseItemRow(
    string RequestId,
    int TmdbId,
    string Type,
    int Season,
    int Episode,
    string Status,
    int Generation,
    string? ClaimToken,
    int Attempts,
    DateTimeOffset NextRunAt,
    DateTimeOffset UpdatedAt,
    string? LastError);

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

public sealed record SeasonAvailabilitySummary(int KnownCount, int PlayableCount, int UnknownCount, int UnavailableCount);

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
    DateTimeOffset FetchedAt,
    int? RuntimeMinutes = null);

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
/// The three per-user Phantom toggles persisted in <c>user_prefs</c> (one
/// row per Jellyfin user). A user with no row falls back to
/// <see cref="Defaults"/> (all on): only explicit toggle choices are stored,
/// so an absent row means "this user has never changed a toggle", never a
/// denial. Favourite state is <b>not</b> stored here — it is read live from
/// Jellyfin's own <c>UserData</c>.
///
/// <list type="bullet">
/// <item><c>ProtectFavourites</c> — this user's favourites pin the shared
///   materialised file against idle eviction (the server-wide master switch
///   in <c>PluginConfiguration.ProtectFavourites</c> still applies).</item>
/// <item><c>ShowPhantoms</c> — not-yet-materialised phantom titles appear in
///   this user's channel browse. Persisted here for the show/hide surface;
///   the channel-visible wiring lands in the dependent show/hide + rig
///   tasks (its cache is not per-user keyed — see the m14-per-user eval).</item>
/// <item><c>AllowEager</c> — this user's own interactions (favouriting,
///   playback progress) may trigger eager source probing / materialise.</item>
/// </list>
/// </summary>
public sealed record UserPrefs(bool ProtectFavourites, bool ShowPhantoms, bool AllowEager)
{
    /// <summary>The all-on defaults applied when a user has no <c>user_prefs</c> row.</summary>
    public static UserPrefs Defaults { get; } = new(true, true, true);
}

/// <summary>
/// Row of the <c>user_hidden_items</c> table: one catalogue title a user has
/// explicitly hidden from their channel browse. <see cref="Type"/> is
/// <c>movie</c> or <c>series</c> — hiding is title-level, so an episode is
/// hidden iff its parent series is.
/// </summary>
public sealed record HiddenItemRow(int TmdbId, string Type, DateTimeOffset HiddenAt);

/// <summary>
/// SQLite-backed persistence for the plugin's private state under the
/// channel architecture (schema v16). Single writer, serialised via a
/// process-wide <see cref="SemaphoreSlim"/>; concurrent readers
/// permitted via separate short-lived connections.
///
/// Schema v14 is a clean break from the v5 file-on-disk schema (v6/v7/v8
/// were intermediate channel-arch revisions that never reached prod;
/// v9 adds the <c>tmdb_episode_cache</c> table the shows channel needs
/// for per-episode display metadata at refresh time; v10 adds
/// <c>magnet_failure_cache</c> so rejected pack candidates do not
/// block viable alternatives; v11 adds append-only catalogue and
/// availability scheduler state; v12 adds ranked source candidates;
/// v13 persists movie runtime minutes for resume eligibility; v14 adds source validation state, failure policy versions, and a durable bulk materialise queue; v15 adds the persistent gostream path→tmdb resolution cache; v16 adds the two <b>additive</b>
/// per-user preference tables <c>user_prefs</c> (one row per Jellyfin
/// user carrying the per-user toggles) and <c>user_hidden_items</c>
/// (a user's per-item hidden set) — the foundation for REQ-M14-PER-USER
/// (branch B). Favourite state is <b>not</b> stored here; it is read
/// live from Jellyfin's own <c>UserData</c>. Per
/// AGENTS.md "No database migrations until v1.0", existing databases
/// at any pre-v16 user_version are HARD-REFUSED and the operator must
/// wipe (<c>scripts/phantom-wipe.sh --commit</c>) before restart.
/// </summary>
public sealed class PhantomDb : IDisposable
{
    public const int CurrentSchemaVersion = 16;

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
            // HARD-REFUSE: pre-v1.0 = wipe-and-rebuild, no migrations.
            throw new InvalidOperationException(
                $"Phantom Library schema is at version {version}; this build requires" + Environment.NewLine
                + $"version {CurrentSchemaVersion}. Pre-v1.0 has no migrations." + Environment.NewLine
                + "Stop Jellyfin, run" + Environment.NewLine
                + "`sudo bash scripts/phantom-wipe.sh --commit`, then restart.");
        }

        if (version > CurrentSchemaVersion)
        {
            // Operator downgraded the plugin against a newer DB. Also unsafe.
            throw new InvalidOperationException(
                $"Phantom Library schema is at version {version}; this build only knows about"
                + $" version {CurrentSchemaVersion}. Downgrade is not supported. Wipe and rebuild.");
        }

        // version == 0: fresh / never-initialised DB. Create the current schema.
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
    validation_policy_version TEXT NOT NULL DEFAULT 'legacy',
    PRIMARY KEY (tmdb_id, imdb_id, type, season, episode, preset, magnet)
);
CREATE INDEX IF NOT EXISTS idx_magnet_failure_cache_retry_after
    ON magnet_failure_cache(retry_after);

-- Ranked source candidates cached from availability probes and source
-- details page probes. No FK by operator-approved design (2026-06-23):
-- candidates are keyed by the same stable item tuple as availability /
-- materialised state and may exist before/after either row.
CREATE TABLE IF NOT EXISTS source_candidates (
    tmdb_id    INTEGER NOT NULL,
    type       TEXT NOT NULL,
    season     INTEGER NOT NULL DEFAULT -1,
    episode    INTEGER NOT NULL DEFAULT -1,
    preset     TEXT NOT NULL DEFAULT '',
    magnet     TEXT NOT NULL,
    info_hash  TEXT NOT NULL,
    indexer    TEXT NOT NULL,
    title      TEXT NOT NULL,
    seeders    INTEGER,
    size       INTEGER,
    rank       INTEGER NOT NULL,
    source     TEXT NOT NULL,
    fetched_at INTEGER NOT NULL,
    expires_at INTEGER NOT NULL,
    validation_status TEXT NOT NULL DEFAULT 'unknown',
    validation_reason TEXT,
    validated_at INTEGER,
    validation_expires_at INTEGER,
    validation_duration_ms INTEGER,
    validation_policy_version TEXT NOT NULL DEFAULT 'unknown',
    selected_file_id INTEGER,
    selected_file_path TEXT,
    selected_file_size INTEGER,
    PRIMARY KEY (tmdb_id, type, season, episode, preset, magnet)
);
CREATE INDEX IF NOT EXISTS idx_source_candidates_item_rank
    ON source_candidates(tmdb_id, type, season, episode, preset, rank);
CREATE INDEX IF NOT EXISTS idx_source_candidates_validation
    ON source_candidates(tmdb_id, type, season, episode, preset, validation_status, rank);
CREATE INDEX IF NOT EXISTS idx_source_candidates_expiry
    ON source_candidates(expires_at);
CREATE INDEX IF NOT EXISTS idx_source_candidates_hash
    ON source_candidates(info_hash);

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

-- Durable bulk favourite materialisation requests. No FK to item table by
-- operator-approved no-FK schema design; request/item relationship is
-- enforced by composite keys in code.
CREATE TABLE IF NOT EXISTS bulk_materialise_requests (
    request_id TEXT PRIMARY KEY,
    user_id TEXT NOT NULL,
    parent_external_id TEXT NOT NULL,
    parent_kind TEXT NOT NULL,
    tmdb_id INTEGER NOT NULL,
    season INTEGER NOT NULL DEFAULT -1,
    trigger TEXT NOT NULL,
    status TEXT NOT NULL,
    requested_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    last_error TEXT,
    last_unfavorited_at INTEGER,
    generation INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_bulk_materialise_requests_status
    ON bulk_materialise_requests(status, updated_at);
CREATE UNIQUE INDEX IF NOT EXISTS idx_bulk_materialise_requests_active_parent
    ON bulk_materialise_requests(user_id, parent_external_id)
    WHERE status IN ('pending','running');

CREATE TABLE IF NOT EXISTS bulk_materialise_items (
    request_id TEXT NOT NULL,
    tmdb_id INTEGER NOT NULL,
    type TEXT NOT NULL,
    season INTEGER NOT NULL,
    episode INTEGER NOT NULL,
    status TEXT NOT NULL,
    generation INTEGER NOT NULL DEFAULT 0,
    claim_token TEXT,
    attempts INTEGER NOT NULL DEFAULT 0,
    next_run_at INTEGER NOT NULL,
    updated_at INTEGER NOT NULL,
    last_error TEXT,
    PRIMARY KEY (request_id, tmdb_id, type, season, episode)
);
CREATE INDEX IF NOT EXISTS idx_bulk_materialise_items_due
    ON bulk_materialise_items(status, next_run_at);
CREATE INDEX IF NOT EXISTS idx_bulk_materialise_items_episode
    ON bulk_materialise_items(tmdb_id, type, season, episode);

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
    runtime_minutes  INTEGER,
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

-- v15 persistent gostream FUSE-path -> tmdb_id resolution cache. Without
-- this, channel cold-browse re-runs TMDB search per orphan file every
-- restart (movies ~40s, shows ~5.3s). 'kind' is 'movie' or 'series'.
CREATE TABLE IF NOT EXISTS gostream_path_tmdb (
    path        TEXT PRIMARY KEY,
    kind        TEXT NOT NULL,
    tmdb_id     INTEGER NOT NULL,
    resolved_at INTEGER NOT NULL
);

-- v16 per-user preferences (additive; REQ-M14-PER-USER branch B). One row
-- per Jellyfin user holding that user's Phantom toggles. Favourites are NOT
-- stored here — favourite state is read live from Jellyfin's own UserData;
-- this table only persists the explicit per-user toggle choices. A user with
-- no row falls back to the code-level defaults (all toggles on); the backend
-- task interprets an absent row, the schema only persists explicit writes.
-- protect_favourites / show_phantoms / allow_eager are the three toggles the
-- (removed, now being revived) per-user admin surface exposed.
CREATE TABLE IF NOT EXISTS user_prefs (
    user_id            TEXT NOT NULL PRIMARY KEY,   -- Jellyfin user GUID (canonical string form)
    protect_favourites INTEGER NOT NULL DEFAULT 1 CHECK(protect_favourites IN (0,1)),
    show_phantoms      INTEGER NOT NULL DEFAULT 1 CHECK(show_phantoms IN (0,1)),
    allow_eager        INTEGER NOT NULL DEFAULT 1 CHECK(allow_eager IN (0,1)),
    updated_at         INTEGER NOT NULL
);

-- v12 per-user hidden set (additive; REQ-M14-PER-USER branch B, Surface 3).
-- The set of catalogue titles a specific user has explicitly hidden from
-- their channel browse. A separate table from user_prefs because it is a
-- multi-row set per user (0..N hidden titles) rather than a single toggle
-- row. Keyed (user_id, tmdb_id, type) to match the catalogue's identity;
-- type is 'movie' or 'series' (hiding is title-level, matching the movie/TV
-- visibility queries — episodes are not independently hidden).
CREATE TABLE IF NOT EXISTS user_hidden_items (
    user_id   TEXT NOT NULL,
    tmdb_id   INTEGER NOT NULL,
    type      TEXT NOT NULL CHECK(type IN ('movie','series')),
    hidden_at INTEGER NOT NULL,
    PRIMARY KEY (user_id, tmdb_id, type)
);
CREATE INDEX IF NOT EXISTS idx_user_hidden_items_user
    ON user_hidden_items(user_id);
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
        cmd.CommandText = @"SELECT info_hash, reason, failed_at, retry_after, validation_policy_version
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
            ValidationPolicyVersion = r.GetString(4),
        };
    }

    public async Task<MagnetFailureEntry?> GetMagnetFailureAsync(
        MagnetFailureKey key,
        string currentValidationPolicyVersion,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentValidationPolicyVersion);
        var failure = await GetMagnetFailureAsync(key, ct).ConfigureAwait(false);
        return IsCurrentFailure(failure, currentValidationPolicyVersion) ? failure : null;
    }

    public async Task<MagnetFailureEntry?> GetMagnetFailureByInfoHashAsync(
        MagnetCacheKey key,
        string infoHash,
        string currentValidationPolicyVersion,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(infoHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentValidationPolicyVersion);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT info_hash, reason, failed_at, retry_after, validation_policy_version
            FROM magnet_failure_cache
            WHERE tmdb_id=$tmdb
              AND imdb_id=$imdb
              AND type=$type
              AND season=$season
              AND episode=$episode
              AND preset=$preset
              AND lower(info_hash)=lower($hash)
            ORDER BY retry_after DESC
            LIMIT 1;";
        BindKey(cmd, key);
        cmd.Parameters.AddWithValue("$preset", key.Preset);
        cmd.Parameters.AddWithValue("$hash", infoHash);

        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return null;
        }

        var failure = new MagnetFailureEntry
        {
            InfoHash = r.GetString(0),
            Reason = r.GetString(1),
            FailedAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2)),
            RetryAfter = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(3)),
            ValidationPolicyVersion = r.GetString(4),
        };
        return IsCurrentFailure(failure, currentValidationPolicyVersion) ? failure : null;
    }

    private static bool IsCurrentFailure(MagnetFailureEntry? failure, string currentValidationPolicyVersion)
    {
        if (failure is null || DateTimeOffset.UtcNow >= failure.RetryAfter)
        {
            return false;
        }

        return !IsPolicySensitiveFailureReason(failure.Reason)
            || string.Equals(failure.ValidationPolicyVersion, currentValidationPolicyVersion, StringComparison.Ordinal);
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
                (tmdb_id, imdb_id, type, season, episode, preset, magnet, info_hash, reason, failed_at, retry_after, validation_policy_version)
                VALUES ($tmdb,$imdb,$type,$season,$episode,$preset,$magnet,$hash,$reason,$failed,$retry,$policy);";
            BindKey(cmd, new MagnetCacheKey(key.TmdbId, key.ImdbId, key.Type, key.Season, key.Episode, key.Preset));
            cmd.Parameters.AddWithValue("$preset", key.Preset);
            cmd.Parameters.AddWithValue("$magnet", key.Magnet);
            cmd.Parameters.AddWithValue("$hash", entry.InfoHash);
            cmd.Parameters.AddWithValue("$reason", entry.Reason);
            cmd.Parameters.AddWithValue("$failed", entry.FailedAt.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$retry", entry.RetryAfter.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$policy", entry.ValidationPolicyVersion);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> DeleteMagnetFailuresAsync(MagnetCacheKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"DELETE FROM magnet_failure_cache
                WHERE tmdb_id=$tmdb
                  AND imdb_id=$imdb
                  AND type=$type
                  AND season=$season
                  AND episode=$episode
                  AND preset=$preset;";
            BindKey(cmd, key);
            cmd.Parameters.AddWithValue("$preset", key.Preset);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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

    private static bool IsPolicySensitiveFailureReason(string reason)
        => string.Equals(reason, "target_episode_not_found", StringComparison.Ordinal)
           || string.Equals(reason, "no_valid_files", StringComparison.Ordinal)
           || string.Equals(reason, "fuse_path_missing", StringComparison.Ordinal)
           || string.Equals(reason, "no_english_audio", StringComparison.Ordinal)
           || string.Equals(reason, "no_main_english_audio", StringComparison.Ordinal)
           || string.Equals(reason, "audio_probe_unsupported_format", StringComparison.Ordinal);

    // ---- source_candidates ----

    public async Task<IReadOnlyList<SourceCandidateRow>> ListSourceCandidatesAsync(
        int tmdbId,
        string type,
        int season,
        int episode,
        string preset,
        bool includeExpired,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(preset);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT tmdb_id,type,season,episode,preset,magnet,info_hash,indexer,title,seeders,size,rank,source,fetched_at,expires_at,
                   validation_status,validation_reason,validated_at,validation_expires_at,validation_duration_ms,validation_policy_version,
                   selected_file_id,selected_file_path,selected_file_size
            FROM source_candidates
            WHERE tmdb_id=$tmdb AND type=$type AND season=$season AND episode=$episode AND preset=$preset
              AND ($includeExpired=1 OR expires_at >= $now)
            ORDER BY rank ASC, seeders DESC, size DESC;";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$season", season);
        cmd.Parameters.AddWithValue("$episode", episode);
        cmd.Parameters.AddWithValue("$preset", preset);
        cmd.Parameters.AddWithValue("$includeExpired", includeExpired ? 1 : 0);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var list = new List<SourceCandidateRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadSourceCandidate(r));
        }

        return list;
    }

    public async Task UpsertSourceCandidatesAsync(
        int tmdbId,
        string type,
        int season,
        int episode,
        string preset,
        IReadOnlyList<Jellyfin.Plugin.PhantomLibrary.Sources.MagnetCandidate> candidates,
        string source,
        TimeSpan ttl,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (candidates.Count == 0)
        {
            return;
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
            var now = DateTimeOffset.UtcNow;
            var expires = now.Add(ttl <= TimeSpan.Zero ? TimeSpan.FromHours(1) : ttl);
            var rank = 0;
            foreach (var candidate in candidates)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(candidate.Magnet) || string.IsNullOrWhiteSpace(candidate.InfoHash))
                {
                    continue;
                }

                rank++;
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = (SqliteTransaction)tx;
                cmd.CommandText = @"INSERT INTO source_candidates
                    (tmdb_id,type,season,episode,preset,magnet,info_hash,indexer,title,seeders,size,rank,source,fetched_at,expires_at)
                    VALUES ($tmdb,$type,$season,$episode,$preset,$magnet,$hash,$indexer,$title,$seeders,$size,$rank,$source,$fetched,$expires)
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
                cmd.Parameters.AddWithValue("$tmdb", tmdbId);
                cmd.Parameters.AddWithValue("$type", type);
                cmd.Parameters.AddWithValue("$season", season);
                cmd.Parameters.AddWithValue("$episode", episode);
                cmd.Parameters.AddWithValue("$preset", preset);
                cmd.Parameters.AddWithValue("$magnet", candidate.Magnet);
                cmd.Parameters.AddWithValue("$hash", candidate.InfoHash);
                cmd.Parameters.AddWithValue("$indexer", candidate.Indexer);
                cmd.Parameters.AddWithValue("$title", candidate.Title ?? string.Empty);
                cmd.Parameters.AddWithValue("$seeders", candidate.Seeders);
                cmd.Parameters.AddWithValue("$size", candidate.Size);
                cmd.Parameters.AddWithValue("$rank", rank);
                cmd.Parameters.AddWithValue("$source", source);
                cmd.Parameters.AddWithValue("$fetched", now.ToUnixTimeSeconds());
                cmd.Parameters.AddWithValue("$expires", expires.ToUnixTimeSeconds());
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpdateSourceCandidateValidationAsync(SourceCandidateValidationUpdate update, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(update.Type);
        ArgumentNullException.ThrowIfNull(update.Preset);
        ArgumentException.ThrowIfNullOrWhiteSpace(update.Magnet);
        ArgumentException.ThrowIfNullOrWhiteSpace(update.ValidationStatus);
        ArgumentException.ThrowIfNullOrWhiteSpace(update.ValidationPolicyVersion);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE source_candidates
                SET validation_status=$status,
                    validation_reason=$reason,
                    validated_at=$validated,
                    validation_expires_at=$validationExpires,
                    validation_duration_ms=$duration,
                    validation_policy_version=$policy,
                    selected_file_id=$fileId,
                    selected_file_path=$filePath,
                    selected_file_size=$fileSize
                WHERE tmdb_id=$tmdb AND type=$type AND season=$season AND episode=$episode AND preset=$preset AND magnet=$magnet;";
            cmd.Parameters.AddWithValue("$tmdb", update.TmdbId);
            cmd.Parameters.AddWithValue("$type", update.Type);
            cmd.Parameters.AddWithValue("$season", update.Season);
            cmd.Parameters.AddWithValue("$episode", update.Episode);
            cmd.Parameters.AddWithValue("$preset", update.Preset);
            cmd.Parameters.AddWithValue("$magnet", update.Magnet);
            cmd.Parameters.AddWithValue("$status", update.ValidationStatus);
            cmd.Parameters.AddWithValue("$reason", (object?)update.ValidationReason ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$validated", update.ValidatedAt.HasValue ? update.ValidatedAt.Value.ToUnixTimeSeconds() : DBNull.Value);
            cmd.Parameters.AddWithValue("$validationExpires", update.ValidationExpiresAt.HasValue ? update.ValidationExpiresAt.Value.ToUnixTimeSeconds() : DBNull.Value);
            cmd.Parameters.AddWithValue("$duration", (object?)update.ValidationDurationMs ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$policy", update.ValidationPolicyVersion);
            cmd.Parameters.AddWithValue("$fileId", (object?)update.SelectedFileId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$filePath", (object?)update.SelectedFilePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fileSize", (object?)update.SelectedFileSize ?? DBNull.Value);
            var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            if (affected != 1)
            {
                throw new InvalidOperationException("source_candidates validation update matched no row");
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> ClearSourceCandidateValidationAsync(
        int tmdbId,
        string type,
        int season,
        int episode,
        string preset,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentNullException.ThrowIfNull(preset);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE source_candidates
                SET validation_status='unknown',
                    validation_reason=NULL,
                    validated_at=NULL,
                    validation_expires_at=NULL,
                    validation_duration_ms=NULL,
                    validation_policy_version='unknown',
                    selected_file_id=NULL,
                    selected_file_path=NULL,
                    selected_file_size=NULL
                WHERE tmdb_id=$tmdb AND type=$type AND season=$season AND episode=$episode AND preset=$preset;";
            cmd.Parameters.AddWithValue("$tmdb", tmdbId);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$season", season);
            cmd.Parameters.AddWithValue("$episode", episode);
            cmd.Parameters.AddWithValue("$preset", preset);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static SourceCandidateRow ReadSourceCandidate(SqliteDataReader r)
        => new(
            r.GetInt32(0),
            r.GetString(1),
            r.GetInt32(2),
            r.GetInt32(3),
            r.GetString(4),
            r.GetString(5),
            r.GetString(6),
            r.GetString(7),
            r.GetString(8),
            r.IsDBNull(9) ? null : r.GetInt32(9),
            r.IsDBNull(10) ? null : r.GetInt64(10),
            r.GetInt32(11),
            r.GetString(12),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(13)),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(14)),
            r.GetString(15),
            r.IsDBNull(16) ? null : r.GetString(16),
            r.IsDBNull(17) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(17)),
            r.IsDBNull(18) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(18)),
            r.IsDBNull(19) ? null : r.GetInt64(19),
            r.GetString(20),
            r.IsDBNull(21) ? null : r.GetInt64(21),
            r.IsDBNull(22) ? null : r.GetString(22),
            r.IsDBNull(23) ? null : r.GetInt64(23));

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
                    cmd.CommandText = @"INSERT INTO tmdb_metadata
                        (tmdb_id, type, title, year, overview, poster_url, backdrop_url,
                         genres_json, official_rating, community_rating, original_title, runtime_minutes, fetched_at)
                        VALUES ($tmdb,$type,$title,$year,$overview,$poster,$backdrop,
                                $genres,$rating,$community,$origtitle,$runtime,$fetched)
                        ON CONFLICT(tmdb_id, type) DO UPDATE SET
                            runtime_minutes=excluded.runtime_minutes,
                            genres_json=COALESCE(tmdb_metadata.genres_json, excluded.genres_json),
                            official_rating=COALESCE(tmdb_metadata.official_rating, excluded.official_rating),
                            community_rating=COALESCE(tmdb_metadata.community_rating, excluded.community_rating),
                            original_title=COALESCE(tmdb_metadata.original_title, excluded.original_title),
                            overview=COALESCE(tmdb_metadata.overview, excluded.overview),
                            poster_url=COALESCE(tmdb_metadata.poster_url, excluded.poster_url),
                            backdrop_url=COALESCE(tmdb_metadata.backdrop_url, excluded.backdrop_url)
                        WHERE tmdb_metadata.runtime_minutes IS NULL
                          AND excluded.runtime_minutes IS NOT NULL;";
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
        cmd.Parameters.AddWithValue("$runtime", (object?)row.RuntimeMinutes ?? DBNull.Value);
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

    public async Task MarkAvailabilityAvailableAsync(int tmdbId, string type, int season, int episode, MagnetCacheEntry? candidate, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        var now = DateTimeOffset.UtcNow;
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO availability_items
                    (tmdb_id, type, season, episode, status, checked_at, next_check_at,
                     candidate_magnet, candidate_info_hash, candidate_size, candidate_seeders,
                     candidate_indexer, candidate_source, last_error_kind, last_error_message,
                     lease_owner, lease_until)
                VALUES ($tmdb,$type,$season,$episode,'available',$checked,$next,
                    $magnet,$hash,$size,$seeders,$indexer,$source,NULL,NULL,NULL,NULL)
                ON CONFLICT(tmdb_id, type, season, episode) DO UPDATE SET
                    status='available',
                    checked_at=excluded.checked_at,
                    next_check_at=excluded.next_check_at,
                    candidate_magnet=COALESCE(excluded.candidate_magnet, availability_items.candidate_magnet),
                    candidate_info_hash=COALESCE(excluded.candidate_info_hash, availability_items.candidate_info_hash),
                    candidate_size=COALESCE(excluded.candidate_size, availability_items.candidate_size),
                    candidate_seeders=COALESCE(excluded.candidate_seeders, availability_items.candidate_seeders),
                    candidate_indexer=COALESCE(excluded.candidate_indexer, availability_items.candidate_indexer),
                    candidate_source=COALESCE(excluded.candidate_source, availability_items.candidate_source),
                    last_error_kind=NULL,
                    last_error_message=NULL,
                    lease_owner=NULL,
                    lease_until=NULL;";
            cmd.Parameters.AddWithValue("$tmdb", tmdbId);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$season", season);
            cmd.Parameters.AddWithValue("$episode", episode);
            cmd.Parameters.AddWithValue("$checked", now.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$next", now.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$magnet", (object?)candidate?.Magnet ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$hash", (object?)candidate?.InfoHash ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$size", (object?)candidate?.Size ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$seeders", (object?)candidate?.Seeders ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$indexer", (object?)candidate?.Indexer ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$source", (object?)candidate?.Source ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
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
                   m.genres_json,m.official_rating,m.community_rating,m.original_title,m.fetched_at,m.runtime_minutes,
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
            MaterialisedStateRow? mat = r.IsDBNull(13) ? null : new MaterialisedStateRow(
                r.GetInt32(13), r.GetString(14), r.GetInt32(15), r.GetInt32(16), r.GetString(17), r.GetString(18), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(19)));
            AvailabilityItemRow? av = r.IsDBNull(20) ? null : ReadAvailability(r, 20);
            list.Add(new VisibleMovieRow(meta, mat, av));
        }

        return list;
    }

    public async Task<IReadOnlyList<VisibleSeriesRow>> ListVisibleSeriesRowsAsync(int minAvailableEpisodes, CancellationToken ct)
    {
        minAvailableEpisodes = Math.Max(1, minAvailableEpisodes);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT m.tmdb_id,m.type,m.title,m.year,m.overview,m.poster_url,m.backdrop_url,
                   m.genres_json,m.official_rating,m.community_rating,m.original_title,m.fetched_at,m.runtime_minutes,
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
            LEFT JOIN (
                SELECT tmdb_id, COUNT(*) AS display_count FROM (
                    SELECT tmdb_id, season, episode FROM availability_items
                    WHERE type='episode' AND status='available'
                    UNION
                    SELECT tmdb_id, season, episode FROM materialised_state
                    WHERE type='episode'
                ) GROUP BY tmdb_id
            ) display ON display.tmdb_id=m.tmdb_id
            WHERE m.type='series' AND COALESCE(display.display_count,0) >= $min
            ORDER BY m.fetched_at DESC;";
        cmd.Parameters.AddWithValue("$min", minAvailableEpisodes);
        var list = new List<VisibleSeriesRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new VisibleSeriesRow(
                ReadTmdbMetadata(r, 0),
                Convert.ToInt32(r.GetInt64(13)),
                Convert.ToInt32(r.GetInt64(14))));
        }

        return list;
    }

    public Task<IReadOnlyList<VisibleSeriesRow>> ListVisibleSeriesRowsAsync(CancellationToken ct)
        => ListVisibleSeriesRowsAsync(1, ct);

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

    public async Task<IReadOnlyList<(int SeriesTmdbId, int Season, int Episode)>> ListDisplayEpisodeIdsForVisibleSeriesAsync(int minAvailableEpisodes, CancellationToken ct)
    {
        minAvailableEpisodes = Math.Max(1, minAvailableEpisodes);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"WITH visible_series AS (
                SELECT tmdb_id FROM (
                    SELECT tmdb_id, season, episode FROM availability_items
                    WHERE type='episode' AND status='available'
                    UNION
                    SELECT tmdb_id, season, episode FROM materialised_state
                    WHERE type='episode'
                )
                GROUP BY tmdb_id
                HAVING COUNT(*) >= $min
            )
            SELECT series_tmdb_id, season, episode FROM tmdb_episode_cache
                WHERE series_tmdb_id IN (SELECT tmdb_id FROM visible_series)
            UNION
            SELECT tmdb_id, season, episode FROM availability_items
                WHERE type='episode' AND tmdb_id IN (SELECT tmdb_id FROM visible_series)
            UNION
            SELECT tmdb_id, season, episode FROM materialised_state
                WHERE type='episode' AND tmdb_id IN (SELECT tmdb_id FROM visible_series)
            ORDER BY series_tmdb_id, season, episode;";
        cmd.Parameters.AddWithValue("$min", minAvailableEpisodes);
        var list = new List<(int SeriesTmdbId, int Season, int Episode)>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add((r.GetInt32(0), r.GetInt32(1), r.GetInt32(2)));
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

    public async Task<SeasonAvailabilitySummary> GetSeasonAvailabilitySummaryAsync(int seriesTmdbId, int season, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"WITH
            known AS (
                SELECT season, episode FROM tmdb_episode_cache
                WHERE series_tmdb_id=$series AND season=$season
                UNION
                SELECT season, episode FROM availability_items
                WHERE tmdb_id=$series AND type='episode' AND season=$season
                UNION
                SELECT season, episode FROM materialised_state
                WHERE tmdb_id=$series AND type='episode' AND season=$season
            ),
            playable AS (
                SELECT season, episode FROM availability_items
                WHERE tmdb_id=$series AND type='episode' AND season=$season AND status='available'
                UNION
                SELECT season, episode FROM materialised_state
                WHERE tmdb_id=$series AND type='episode' AND season=$season
            ),
            unavailable_only AS (
                SELECT season, episode FROM availability_items
                WHERE tmdb_id=$series AND type='episode' AND season=$season AND status='unavailable'
                EXCEPT
                SELECT season, episode FROM playable
            ),
            resolved AS (
                SELECT season, episode FROM playable
                UNION
                SELECT season, episode FROM unavailable_only
            )
            SELECT
                (SELECT COUNT(*) FROM known),
                (SELECT COUNT(*) FROM playable),
                (SELECT COUNT(*) FROM (SELECT season, episode FROM known EXCEPT SELECT season, episode FROM resolved)),
                (SELECT COUNT(*) FROM unavailable_only);";
        cmd.Parameters.AddWithValue("$series", seriesTmdbId);
        cmd.Parameters.AddWithValue("$season", season);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return new SeasonAvailabilitySummary(0, 0, 0, 0);
        }

        return new SeasonAvailabilitySummary(
            Convert.ToInt32(r.GetInt64(0)),
            Convert.ToInt32(r.GetInt64(1)),
            Convert.ToInt32(r.GetInt64(2)),
            Convert.ToInt32(r.GetInt64(3)));
    }

    public async Task<bool> IsSeriesVisibleAsync(int seriesTmdbId, int minAvailableEpisodes, CancellationToken ct)
    {
        minAvailableEpisodes = Math.Max(1, minAvailableEpisodes);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*) FROM (
                SELECT season, episode FROM availability_items
                WHERE tmdb_id=$series AND type='episode' AND status='available'
                UNION
                SELECT season, episode FROM materialised_state
                WHERE tmdb_id=$series AND type='episode'
            );";
        cmd.Parameters.AddWithValue("$series", seriesTmdbId);
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt64(v, CultureInfo.InvariantCulture) >= minAvailableEpisodes;
    }

    public async Task<AvailabilityItemRow?> GetAvailabilityItemAsync(int tmdbId, string type, int season, int episode, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT tmdb_id,type,season,episode,status,checked_at,next_check_at,
                   candidate_magnet,candidate_info_hash,candidate_size,candidate_seeders,
                   candidate_indexer,candidate_source,probe_generation,lease_owner
            FROM availability_items
            WHERE tmdb_id=$tmdb AND type=$type AND season=$season AND episode=$episode
            LIMIT 1;";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$season", season);
        cmd.Parameters.AddWithValue("$episode", episode);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadAvailability(r) : null;
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

    // ---- user_prefs (per-user toggles) ----

    /// <summary>
    /// Read a user's toggles. A user with no <c>user_prefs</c> row falls back
    /// to <see cref="UserPrefs.Defaults"/> (all on) — an absent row means the
    /// user has never changed a toggle, not that anything is denied.
    /// </summary>
    public async Task<UserPrefs> GetUserPrefsAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT protect_favourites, show_phantoms, allow_eager
            FROM user_prefs WHERE user_id=$uid LIMIT 1;";
        cmd.Parameters.AddWithValue("$uid", UserKey(userId));
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await r.ReadAsync(ct).ConfigureAwait(false))
        {
            return UserPrefs.Defaults;
        }

        return new UserPrefs(r.GetInt32(0) != 0, r.GetInt32(1) != 0, r.GetInt32(2) != 0);
    }

    /// <summary>
    /// Upsert a user's toggles, replacing any existing row for the user.
    /// </summary>
    public async Task UpsertUserPrefsAsync(Guid userId, UserPrefs prefs, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(prefs);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO user_prefs
                    (user_id, protect_favourites, show_phantoms, allow_eager, updated_at)
                    VALUES ($uid, $pf, $sp, $ae, $now)
                ON CONFLICT(user_id) DO UPDATE SET
                    protect_favourites = excluded.protect_favourites,
                    show_phantoms      = excluded.show_phantoms,
                    allow_eager        = excluded.allow_eager,
                    updated_at         = excluded.updated_at;";
            cmd.Parameters.AddWithValue("$uid", UserKey(userId));
            cmd.Parameters.AddWithValue("$pf", prefs.ProtectFavourites ? 1 : 0);
            cmd.Parameters.AddWithValue("$sp", prefs.ShowPhantoms ? 1 : 0);
            cmd.Parameters.AddWithValue("$ae", prefs.AllowEager ? 1 : 0);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // ---- user_hidden_items (per-user hidden set) ----

    /// <summary>
    /// Hide a catalogue title for a user. Idempotent — re-hiding just refreshes
    /// <c>hidden_at</c>. <paramref name="type"/> is <c>movie</c> or
    /// <c>series</c> (title-level; episodes follow their parent series).
    /// </summary>
    public async Task AddHiddenItemAsync(Guid userId, int tmdbId, string type, CancellationToken ct)
    {
        var normType = NormalizeHiddenType(type);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO user_hidden_items (user_id, tmdb_id, type, hidden_at)
                    VALUES ($uid, $tmdb, $type, $now)
                ON CONFLICT(user_id, tmdb_id, type) DO UPDATE SET
                    hidden_at = excluded.hidden_at;";
            cmd.Parameters.AddWithValue("$uid", UserKey(userId));
            cmd.Parameters.AddWithValue("$tmdb", tmdbId);
            cmd.Parameters.AddWithValue("$type", normType);
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// Un-hide a title for a user. Idempotent — a no-op if not currently hidden.
    /// </summary>
    public async Task RemoveHiddenItemAsync(Guid userId, int tmdbId, string type, CancellationToken ct)
    {
        var normType = NormalizeHiddenType(type);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"DELETE FROM user_hidden_items
                WHERE user_id=$uid AND tmdb_id=$tmdb AND type=$type;";
            cmd.Parameters.AddWithValue("$uid", UserKey(userId));
            cmd.Parameters.AddWithValue("$tmdb", tmdbId);
            cmd.Parameters.AddWithValue("$type", normType);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    /// True iff the user has explicitly hidden this (tmdb_id, type) title.
    /// </summary>
    public async Task<bool> IsItemHiddenAsync(Guid userId, int tmdbId, string type, CancellationToken ct)
    {
        var normType = NormalizeHiddenType(type);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT 1 FROM user_hidden_items
            WHERE user_id=$uid AND tmdb_id=$tmdb AND type=$type LIMIT 1;";
        cmd.Parameters.AddWithValue("$uid", UserKey(userId));
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", normType);
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return v is not null and not DBNull;
    }

    /// <summary>
    /// All titles a user has hidden, most-recently-hidden first.
    /// </summary>
    public async Task<IReadOnlyList<HiddenItemRow>> ListHiddenItemsAsync(Guid userId, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT tmdb_id, type, hidden_at FROM user_hidden_items
            WHERE user_id=$uid ORDER BY hidden_at DESC, tmdb_id;";
        cmd.Parameters.AddWithValue("$uid", UserKey(userId));
        var list = new List<HiddenItemRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(new HiddenItemRow(r.GetInt32(0), r.GetString(1), DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(2))));
        }

        return list;
    }

    private async Task<HashSet<int>> HiddenTmdbIdsAsync(Guid userId, string type, CancellationToken ct)
    {
        var normType = NormalizeHiddenType(type);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT tmdb_id FROM user_hidden_items
            WHERE user_id=$uid AND type=$type;";
        cmd.Parameters.AddWithValue("$uid", UserKey(userId));
        cmd.Parameters.AddWithValue("$type", normType);
        var set = new HashSet<int>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            set.Add(r.GetInt32(0));
        }

        return set;
    }

    // ---- per-user visibility (composition over the server-wide queries) ----
    //
    // These filter the server-wide visibility results by the user's hidden set.
    // They are deliberately NOT wired into the cached channel-browse path (that
    // cache is not keyed per user — see docs/plans/channel-architecture.md and
    // the m14-per-user eval Surface 3); the per-user show/hide surface + rig
    // task consume them. Hiding is title-level: an episode is visible to a user
    // iff its parent series is both server-visible and not hidden by that user.

    public async Task<IReadOnlyList<VisibleMovieRow>> ListVisibleMovieRowsAsync(Guid userId, CancellationToken ct)
    {
        var baseRows = await ListVisibleMovieRowsAsync(ct).ConfigureAwait(false);
        var hidden = await HiddenTmdbIdsAsync(userId, "movie", ct).ConfigureAwait(false);
        if (hidden.Count == 0)
        {
            return baseRows;
        }

        var list = new List<VisibleMovieRow>(baseRows.Count);
        foreach (var row in baseRows)
        {
            if (!hidden.Contains(row.Metadata.TmdbId))
            {
                list.Add(row);
            }
        }

        return list;
    }

    public async Task<IReadOnlyList<VisibleSeriesRow>> ListVisibleSeriesRowsAsync(Guid userId, int minAvailableEpisodes, CancellationToken ct)
    {
        var baseRows = await ListVisibleSeriesRowsAsync(minAvailableEpisodes, ct).ConfigureAwait(false);
        var hidden = await HiddenTmdbIdsAsync(userId, "series", ct).ConfigureAwait(false);
        if (hidden.Count == 0)
        {
            return baseRows;
        }

        var list = new List<VisibleSeriesRow>(baseRows.Count);
        foreach (var row in baseRows)
        {
            if (!hidden.Contains(row.Metadata.TmdbId))
            {
                list.Add(row);
            }
        }

        return list;
    }

    public Task<IReadOnlyList<VisibleSeriesRow>> ListVisibleSeriesRowsAsync(Guid userId, CancellationToken ct)
        => ListVisibleSeriesRowsAsync(userId, 1, ct);

    public async Task<bool> IsSeriesVisibleAsync(Guid userId, int seriesTmdbId, int minAvailableEpisodes, CancellationToken ct)
    {
        if (await IsItemHiddenAsync(userId, seriesTmdbId, "series", ct).ConfigureAwait(false))
        {
            return false;
        }

        return await IsSeriesVisibleAsync(seriesTmdbId, minAvailableEpisodes, ct).ConfigureAwait(false);
    }

    public async Task<bool> IsEpisodeVisibleAsync(Guid userId, int seriesTmdbId, int season, int episode, CancellationToken ct)
    {
        // Hiding is title-level: an episode disappears for a user exactly when
        // its parent series is hidden. Otherwise defer to server-wide episode
        // visibility.
        if (await IsItemHiddenAsync(userId, seriesTmdbId, "series", ct).ConfigureAwait(false))
        {
            return false;
        }

        return await IsEpisodeVisibleAsync(seriesTmdbId, season, episode, ct).ConfigureAwait(false);
    }

    private static string UserKey(Guid userId)
        => userId.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>
    /// Validate and normalise a hidden-item type to the canonical
    /// <c>movie</c>/<c>series</c> tokens the <c>user_hidden_items.type</c>
    /// CHECK constraint accepts. Throws for anything else so a bad caller fails
    /// loudly instead of writing a row that can never match a query.
    /// </summary>
    private static string NormalizeHiddenType(string type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
#pragma warning disable CA1308 // Canonical hidden-item tokens are lowercase ('movie'/'series'), not identifiers used for round-trip display.
        return type.ToLowerInvariant() switch
#pragma warning restore CA1308
        {
            "movie" => "movie",
            "series" => "series",
            _ => throw new ArgumentException(
                "Hidden-item type must be 'movie' or 'series', got: " + type, nameof(type)),
        };
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
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(offset + 11)),
            r.IsDBNull(offset + 12) ? null : r.GetInt32(offset + 12));
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

    /// <summary>
    /// Attempts to claim the materialise_in_flight row for the given
    /// tuple. If no row exists, inserts one and returns true. If a row
    /// already exists, the claim is granted (the existing row is
    /// stolen and its started_at bumped to now) only when that row's
    /// started_at is OLDER than <paramref name="staleThreshold"/> —
    /// i.e. it can only belong to a materialise that crashed mid-
    /// flight and leaked the row (the process's own finally-block
    /// delete never ran), never to a genuinely still-running
    /// materialise, which by definition has a fresh started_at. This
    /// makes the leaked-claim recovery inline and deterministic
    /// (no reliance on <see cref="MaterialiseInFlightSweeper"/>'s
    /// startup-only sweep, which a claim younger than the threshold
    /// at sweep time would otherwise survive indefinitely). A fresh
    /// (non-stale) existing row still blocks the caller, exactly as
    /// before — this only widens the reclaim path for provably-leaked
    /// rows.
    /// </summary>
    /// <param name="staleThreshold">
    /// Age past which an existing row is presumed leaked and
    /// reclaimable inline. Pass <see cref="TimeSpan.MaxValue"/> (or
    /// omit) to disable inline reclaim and preserve the original
    /// insert-or-ignore-only behaviour.
    /// </param>
    public async Task<bool> TryInsertMaterialiseInFlightAsync(int tmdbId, string type, int season, int episode, CancellationToken ct, TimeSpan? staleThreshold = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var cutoff = staleThreshold.HasValue
                ? now.Subtract(staleThreshold.Value).ToUnixTimeSeconds()
                : long.MinValue;

            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO materialise_in_flight
                (tmdb_id, type, season, episode, started_at)
                VALUES ($tmdb, $type, $season, $episode, $now)
                ON CONFLICT (tmdb_id, type, season, episode)
                DO UPDATE SET started_at = excluded.started_at
                WHERE materialise_in_flight.started_at < $cutoff;";
            cmd.Parameters.AddWithValue("$tmdb", tmdbId);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$season", season);
            cmd.Parameters.AddWithValue("$episode", episode);
            cmd.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
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
        var started = await GetMaterialiseInFlightStartedAtAsync(tmdbId, type, season, episode, ct).ConfigureAwait(false);
        return started.HasValue;
    }

    public async Task<DateTimeOffset?> GetMaterialiseInFlightStartedAtAsync(int tmdbId, string type, int season, int episode, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT started_at FROM materialise_in_flight
            WHERE tmdb_id=$tmdb AND type=$type AND season=$season AND episode=$episode
            LIMIT 1;";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$season", season);
        cmd.Parameters.AddWithValue("$episode", episode);
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (v is null || v is DBNull)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(Convert.ToInt64(v, CultureInfo.InvariantCulture));
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

    public async Task<int> CountOtherMaterialisedReferencesAsync(
        int tmdbId,
        string type,
        int season,
        int episode,
        string stubPath,
        string? infoHash,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(stubPath);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT COUNT(*)
            FROM materialised_state ms
            WHERE NOT (ms.tmdb_id=$tmdb AND ms.type=$type AND ms.season=$season AND ms.episode=$episode)
              AND (
                    ms.stub_path=$stub
                    OR (
                        $hash <> ''
                        AND EXISTS (
                            SELECT 1
                            FROM magnet_cache mc
                            WHERE mc.tmdb_id=ms.tmdb_id
                              AND mc.type=ms.type
                              AND mc.season=CASE WHEN ms.season < 0 THEN 0 ELSE ms.season END
                              AND mc.episode=CASE WHEN ms.episode < 0 THEN 0 ELSE ms.episode END
                              AND mc.info_hash=$hash
                        )
                    )
                  );";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$season", season);
        cmd.Parameters.AddWithValue("$episode", episode);
        cmd.Parameters.AddWithValue("$stub", stubPath);
        cmd.Parameters.AddWithValue("$hash", string.IsNullOrWhiteSpace(infoHash) ? string.Empty : infoHash);
        var count = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(count, CultureInfo.InvariantCulture);
    }

    // ---- bulk_materialise_* ----

    public static string ComputeBulkMaterialiseRequestId(string userId, string parentExternalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentExternalId);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(userId + ":" + parentExternalId));
        return Convert.ToHexStringLower(bytes);
    }

    public async Task UpsertBulkMaterialiseRequestAsync(BulkMaterialiseRequestRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO bulk_materialise_requests
                (request_id,user_id,parent_external_id,parent_kind,tmdb_id,season,trigger,status,requested_at,updated_at,last_error,last_unfavorited_at,generation)
                VALUES ($request,$user,$parent,$kind,$tmdb,$season,$trigger,$status,$requested,$updated,$error,$unfav,$generation)
                ON CONFLICT(request_id) DO UPDATE SET
                    user_id=excluded.user_id,
                    parent_external_id=excluded.parent_external_id,
                    parent_kind=excluded.parent_kind,
                    tmdb_id=excluded.tmdb_id,
                    season=excluded.season,
                    trigger=excluded.trigger,
                    status=excluded.status,
                    updated_at=excluded.updated_at,
                    last_error=excluded.last_error,
                    last_unfavorited_at=excluded.last_unfavorited_at,
                    generation=excluded.generation;";
            BindBulkRequest(cmd, row);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<BulkMaterialiseRequestRow?> GetBulkMaterialiseRequestAsync(string requestId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT request_id,user_id,parent_external_id,parent_kind,tmdb_id,season,trigger,status,requested_at,updated_at,last_error,last_unfavorited_at,generation
            FROM bulk_materialise_requests WHERE request_id=$request LIMIT 1;";
        cmd.Parameters.AddWithValue("$request", requestId);
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await r.ReadAsync(ct).ConfigureAwait(false) ? ReadBulkRequest(r) : null;
    }

    public async Task UpsertBulkMaterialiseItemAsync(BulkMaterialiseItemRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(row);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO bulk_materialise_items
                (request_id,tmdb_id,type,season,episode,status,generation,claim_token,attempts,next_run_at,updated_at,last_error)
                VALUES ($request,$tmdb,$type,$season,$episode,$status,$generation,$claim,$attempts,$next,$updated,$error)
                ON CONFLICT(request_id,tmdb_id,type,season,episode) DO UPDATE SET
                    status=excluded.status,
                    generation=excluded.generation,
                    claim_token=excluded.claim_token,
                    attempts=excluded.attempts,
                    next_run_at=excluded.next_run_at,
                    updated_at=excluded.updated_at,
                    last_error=excluded.last_error;";
            BindBulkItem(cmd, row);
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<BulkMaterialiseItemRow>> ListBulkMaterialiseItemsAsync(string requestId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT request_id,tmdb_id,type,season,episode,status,generation,claim_token,attempts,next_run_at,updated_at,last_error
            FROM bulk_materialise_items WHERE request_id=$request ORDER BY season, episode;";
        cmd.Parameters.AddWithValue("$request", requestId);
        var list = new List<BulkMaterialiseItemRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadBulkItem(r));
        }

        return list;
    }

    public async Task<IReadOnlyList<BulkMaterialiseItemRow>> PeekDueBulkMaterialiseItemsAsync(DateTimeOffset now, int limit, CancellationToken ct)
    {
        if (limit <= 0)
        {
            return Array.Empty<BulkMaterialiseItemRow>();
        }

        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT request_id,tmdb_id,type,season,episode,status,generation,claim_token,attempts,next_run_at,updated_at,last_error
            FROM bulk_materialise_items
            WHERE status IN ('pending','retry') AND next_run_at <= $now
            ORDER BY next_run_at ASC, updated_at ASC
            LIMIT $limit;";
        cmd.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$limit", limit);
        var list = new List<BulkMaterialiseItemRow>();
        await using var r = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await r.ReadAsync(ct).ConfigureAwait(false))
        {
            list.Add(ReadBulkItem(r));
        }

        return list;
    }

    public async Task<bool> TryClaimBulkMaterialiseItemAsync(
        string requestId,
        int tmdbId,
        string type,
        int season,
        int episode,
        int generation,
        string claimToken,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE bulk_materialise_items
                SET status='running', claim_token=$claim, attempts=attempts+1, updated_at=$now
                WHERE request_id=$request AND tmdb_id=$tmdb AND type=$type
                  AND season=$season AND episode=$episode AND generation=$generation
                  AND status IN ('pending','retry') AND next_run_at <= $now;";
            cmd.Parameters.AddWithValue("$request", requestId);
            cmd.Parameters.AddWithValue("$tmdb", tmdbId);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$season", season);
            cmd.Parameters.AddWithValue("$episode", episode);
            cmd.Parameters.AddWithValue("$generation", generation);
            cmd.Parameters.AddWithValue("$claim", claimToken);
            cmd.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> CompleteBulkMaterialiseItemAsync(
        string requestId,
        int tmdbId,
        string type,
        int season,
        int episode,
        int generation,
        string claimToken,
        string status,
        DateTimeOffset nextRunAt,
        string? lastError,
        DateTimeOffset now,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(claimToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE bulk_materialise_items
                SET status=$status, claim_token=NULL, next_run_at=$next, updated_at=$now, last_error=$error
                WHERE request_id=$request AND tmdb_id=$tmdb AND type=$type
                  AND season=$season AND episode=$episode AND generation=$generation
                  AND claim_token=$claim;";
            cmd.Parameters.AddWithValue("$request", requestId);
            cmd.Parameters.AddWithValue("$tmdb", tmdbId);
            cmd.Parameters.AddWithValue("$type", type);
            cmd.Parameters.AddWithValue("$season", season);
            cmd.Parameters.AddWithValue("$episode", episode);
            cmd.Parameters.AddWithValue("$generation", generation);
            cmd.Parameters.AddWithValue("$claim", claimToken);
            cmd.Parameters.AddWithValue("$status", status);
            cmd.Parameters.AddWithValue("$next", nextRunAt.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$error", (object?)lastError ?? DBNull.Value);
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<int> ResetStaleBulkMaterialiseItemsAsync(TimeSpan staleAge, DateTimeOffset now, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"UPDATE bulk_materialise_items
                SET status='retry', claim_token=NULL, next_run_at=$now, updated_at=$now, last_error='stale_running_reset'
                WHERE status='running' AND updated_at < $cutoff;";
            cmd.Parameters.AddWithValue("$now", now.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$cutoff", now.Subtract(staleAge).ToUnixTimeSeconds());
            return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static void BindBulkRequest(SqliteCommand cmd, BulkMaterialiseRequestRow row)
    {
        cmd.Parameters.AddWithValue("$request", row.RequestId);
        cmd.Parameters.AddWithValue("$user", row.UserId);
        cmd.Parameters.AddWithValue("$parent", row.ParentExternalId);
        cmd.Parameters.AddWithValue("$kind", row.ParentKind);
        cmd.Parameters.AddWithValue("$tmdb", row.TmdbId);
        cmd.Parameters.AddWithValue("$season", row.Season);
        cmd.Parameters.AddWithValue("$trigger", row.Trigger);
        cmd.Parameters.AddWithValue("$status", row.Status);
        cmd.Parameters.AddWithValue("$requested", row.RequestedAt.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$updated", row.UpdatedAt.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$error", (object?)row.LastError ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$unfav", row.LastUnfavoritedAt.HasValue ? row.LastUnfavoritedAt.Value.ToUnixTimeSeconds() : DBNull.Value);
        cmd.Parameters.AddWithValue("$generation", row.Generation);
    }

    private static BulkMaterialiseRequestRow ReadBulkRequest(SqliteDataReader r)
        => new(
            r.GetString(0),
            r.GetString(1),
            r.GetString(2),
            r.GetString(3),
            r.GetInt32(4),
            r.GetInt32(5),
            r.GetString(6),
            r.GetString(7),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(8)),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(9)),
            r.IsDBNull(10) ? null : r.GetString(10),
            r.IsDBNull(11) ? null : DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(11)),
            r.GetInt32(12));

    private static void BindBulkItem(SqliteCommand cmd, BulkMaterialiseItemRow row)
    {
        cmd.Parameters.AddWithValue("$request", row.RequestId);
        cmd.Parameters.AddWithValue("$tmdb", row.TmdbId);
        cmd.Parameters.AddWithValue("$type", row.Type);
        cmd.Parameters.AddWithValue("$season", row.Season);
        cmd.Parameters.AddWithValue("$episode", row.Episode);
        cmd.Parameters.AddWithValue("$status", row.Status);
        cmd.Parameters.AddWithValue("$generation", row.Generation);
        cmd.Parameters.AddWithValue("$claim", (object?)row.ClaimToken ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$attempts", row.Attempts);
        cmd.Parameters.AddWithValue("$next", row.NextRunAt.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$updated", row.UpdatedAt.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$error", (object?)row.LastError ?? DBNull.Value);
    }

    private static BulkMaterialiseItemRow ReadBulkItem(SqliteDataReader r)
        => new(
            r.GetString(0),
            r.GetInt32(1),
            r.GetString(2),
            r.GetInt32(3),
            r.GetInt32(4),
            r.GetString(5),
            r.GetInt32(6),
            r.IsDBNull(7) ? null : r.GetString(7),
            r.GetInt32(8),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(9)),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(10)),
            r.IsDBNull(11) ? null : r.GetString(11));

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
                 genres_json, official_rating, community_rating, original_title, runtime_minutes, fetched_at)
                VALUES ($tmdb,$type,$title,$year,$overview,$poster,$backdrop,
                        $genres,$rating,$community,$origtitle,$runtime,$fetched);";
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
            cmd.Parameters.AddWithValue("$runtime", (object?)row.RuntimeMinutes ?? DBNull.Value);
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
                   genres_json, official_rating, community_rating, original_title, fetched_at, runtime_minutes
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
                   genres_json, official_rating, community_rating, original_title, fetched_at, runtime_minutes
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

    public async Task<int?> GetGostreamPathTmdbAsync(string path, string kind, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"SELECT tmdb_id FROM gostream_path_tmdb WHERE path=$path AND kind=$kind LIMIT 1;";
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$kind", kind);
        var v = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        if (v is null || v is DBNull)
        {
            return null;
        }

        return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task UpsertGostreamPathTmdbAsync(string path, string kind, int tmdbId, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var conn = await OpenAsync(ct).ConfigureAwait(false);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT OR REPLACE INTO gostream_path_tmdb (path, kind, tmdb_id, resolved_at)
                VALUES ($path,$kind,$tmdb,$at);";
            cmd.Parameters.AddWithValue("$path", path);
            cmd.Parameters.AddWithValue("$kind", kind);
            cmd.Parameters.AddWithValue("$tmdb", tmdbId);
            cmd.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
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
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(11)),
            r.IsDBNull(12) ? null : r.GetInt32(12));
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
