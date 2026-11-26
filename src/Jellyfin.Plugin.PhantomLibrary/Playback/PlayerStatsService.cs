using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Playback;

/// <summary>
/// p14-player-stats-overlay wire contract for
/// <c>GET /Plugins/PhantomLibrary/PlayerStats/Current</c> and
/// <c>GET /Plugins/PhantomLibrary/PlayerStats/{itemId}</c>. Property names are
/// pinned (camelCase) and every nullable measurement is ALWAYS serialised
/// (explicit <c>null</c> = "not known yet", never a fabricated 0) regardless of
/// the host's <c>WhenWritingNull</c> default. Never carries the gostream token,
/// base URL, stub/FUSE path or raw upstream error text.
/// </summary>
public sealed record PlayerStatsResponse
{
    /// <summary>Overall resolution outcome; see <see cref="PlayerStatsStatus"/>.</summary>
    [JsonPropertyName("status")] public required string Status { get; init; }

    /// <summary>True while a session of the caller is playing this item.</summary>
    [JsonPropertyName("playing")] public required bool Playing { get; init; }

    /// <summary>True when the item is a Phantom channel movie/episode (the only items the overlay renders for).</summary>
    [JsonPropertyName("isPhantom")] public required bool IsPhantom { get; init; }

    [JsonPropertyName("itemId")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? ItemId { get; init; }
    [JsonPropertyName("type")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Type { get; init; }
    [JsonPropertyName("tmdbId")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? TmdbId { get; init; }
    [JsonPropertyName("season")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? Season { get; init; }
    [JsonPropertyName("episode")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? Episode { get; init; }
    [JsonPropertyName("playSessionId")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? PlaySessionId { get; init; }
    [JsonPropertyName("mediaSourceId")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? MediaSourceId { get; init; }
    [JsonPropertyName("isPaused")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public bool? IsPaused { get; init; }
    [JsonPropertyName("positionTicks")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long? PositionTicks { get; init; }
    [JsonPropertyName("materialisedAt")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public DateTimeOffset? MaterialisedAt { get; init; }
    [JsonPropertyName("source")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public PlayerSourceInfo? Source { get; init; }
    [JsonPropertyName("stream")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public PlayerStreamInfo? Stream { get; init; }

    /// <summary>Server-suggested poll cadence (clamped 2000–5000 ms client-side).</summary>
    [JsonPropertyName("pollIntervalMs")] public int PollIntervalMs { get; init; } = PlayerStatsService.DefaultPollIntervalMs;
}

/// <summary>The plugin's own metadata about the source the item materialised from.</summary>
public sealed record PlayerSourceInfo
{
    /// <summary>Indexer release title (or the magnet <c>dn</c> when no candidate row survives).</summary>
    [JsonPropertyName("release")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Release { get; init; }

    /// <summary><c>candidate</c> (source_candidates title), <c>magnet_dn</c>, or null when unknown.</summary>
    [JsonPropertyName("releaseSource")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? ReleaseSource { get; init; }

    [JsonPropertyName("sizeBytes")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long? SizeBytes { get; init; }
    [JsonPropertyName("indexer")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? Indexer { get; init; }
    [JsonPropertyName("infoHash")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? InfoHash { get; init; }

    /// <summary>Seeders the indexer reported when this source was selected (not live).</summary>
    [JsonPropertyName("seedersAtSelection")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? SeedersAtSelection { get; init; }
}

/// <summary>Live gostream engine stats for the materialised info-hash (proxied verbatim; nulls preserved).</summary>
public sealed record PlayerStreamInfo
{
    [JsonPropertyName("state")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? State { get; init; }
    [JsonPropertyName("peers")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? Peers { get; init; }
    [JsonPropertyName("seeders")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public int? Seeders { get; init; }
    [JsonPropertyName("downloadRateBytesPerSec")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public double? DownloadRateBytesPerSec { get; init; }
    [JsonPropertyName("bytesCompleted")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long? BytesCompleted { get; init; }
    [JsonPropertyName("torrentSize")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long? TorrentSize { get; init; }
    [JsonPropertyName("readOffset")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long? ReadOffset { get; init; }
    [JsonPropertyName("bufferedAheadBytes")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public long? BufferedAheadBytes { get; init; }
    [JsonPropertyName("bufferedAheadSeconds")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public double? BufferedAheadSeconds { get; init; }
    [JsonPropertyName("lastReadAt")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public DateTimeOffset? LastReadAt { get; init; }
    [JsonPropertyName("torrentSampledAt")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public DateTimeOffset? TorrentSampledAt { get; init; }
    [JsonPropertyName("streamSampledAt")][JsonIgnore(Condition = JsonIgnoreCondition.Never)] public DateTimeOffset? StreamSampledAt { get; init; }
}

/// <summary>Playback context echoed into a <see cref="PlayerStatsResponse"/> (from the caller's live session, if any).</summary>
public sealed record PlayerPlaybackContext(bool Playing, string? PlaySessionId, string? MediaSourceId, bool? IsPaused, long? PositionTicks);

/// <summary>Closed set of <see cref="PlayerStatsResponse.Status"/> values.</summary>
public static class PlayerStatsStatus
{
    /// <summary>Source + live gostream stats resolved.</summary>
    public const string Ok = "ok";

    /// <summary>No session of the caller is playing anything (Current) / this item (per-item, non-admin).</summary>
    public const string NotPlaying = "not_playing";

    /// <summary>Item is real library / external / orphan content, not a phantom movie/episode.</summary>
    public const string NotPhantom = "not_phantom";

    /// <summary>Materialise is in flight; no source is bound yet.</summary>
    public const string Materialising = "materialising";

    /// <summary>Phantom item with no materialised_state row (still a phantom stub).</summary>
    public const string NotMaterialised = "not_materialised";

    /// <summary>Materialised, but no matching successful-source snapshot exists (including legacy state).</summary>
    public const string NoSource = "no_source";

    /// <summary>Source known but its info-hash is not a valid v1 BitTorrent hash.</summary>
    public const string InvalidHash = "invalid_hash";

    /// <summary>gostream answered 404: the hash is not loaded in the engine (expired/removed).</summary>
    public const string TorrentNotLoaded = "torrent_not_loaded";

    /// <summary>gostream unreachable, timed out, returned 5xx, or rejected the plugin's auth/request.</summary>
    public const string GostreamUnavailable = "gostream_unavailable";

    /// <summary>The requested item id does not resolve to a BaseItem.</summary>
    public const string ItemNotFound = "item_not_found";
}

/// <summary>
/// p14-player-stats-overlay. Maps a playing item to its materialised info-hash
/// (materialised_state → witnessed plugin_meta snapshot) plus source metadata
/// captured from the winning candidate and proxies gostream's
/// per-torrent <c>/api/library/stats</c> (gostream:phantom-player-stats-endpoint)
/// server-side, so the gostream token never reaches the browser.
///
/// Cost per call (the overlay polls every 2–5 s per open player): one cached
/// <see cref="ILibraryManager.GetItemById(Guid)"/>, ≤4 single-row indexed
/// SQL reads, ≤1 gostream GET bounded by <see cref="GostreamStatsTimeout"/>.
/// No writes, no TMDB/indexer calls, no catalogue scans.
/// </summary>
public sealed class PlayerStatsService
{
    /// <summary>Default overlay poll cadence; the task bound is 2–5 s.</summary>
    public const int DefaultPollIntervalMs = 3000;

    /// <summary>Upper bound on the proxied gostream call; the browser never overlaps requests.</summary>
    public static readonly TimeSpan GostreamStatsTimeout = TimeSpan.FromSeconds(4);

    private readonly ILibraryManager _libraryManager;
    private readonly PhantomDb _db;
    private readonly IGostreamClient _gostream;
    private readonly ILogger<PlayerStatsService> _logger;
    private readonly Func<PluginConfiguration> _configProvider;

    /// <summary>Initializes a new instance of the <see cref="PlayerStatsService"/> class.</summary>
    public PlayerStatsService(ILibraryManager libraryManager, PhantomDb db, IGostreamClient gostream, ILogger<PlayerStatsService> logger)
        : this(libraryManager, db, gostream, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal PlayerStatsService(
        ILibraryManager libraryManager,
        PhantomDb db,
        IGostreamClient gostream,
        ILogger<PlayerStatsService> logger,
        Func<PluginConfiguration> configProvider)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _gostream = gostream ?? throw new ArgumentNullException(nameof(gostream));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    /// <summary>Resolves source metadata + live gostream stats for <paramref name="itemId"/>.</summary>
    public async Task<PlayerStatsResponse> GetAsync(Guid itemId, PlayerPlaybackContext playback, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(playback);
        var idText = itemId.ToString("N", CultureInfo.InvariantCulture);
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return Base(PlayerStatsStatus.ItemNotFound, idText, playback, isPhantom: false);
        }

        if (!TryParsePlayable(item, out var parsed))
        {
            return Base(PlayerStatsStatus.NotPhantom, idText, playback, isPhantom: false);
        }

        var type = parsed.Kind == ChannelItemId.KindEpisode ? "episode" : "movie";
        var tmdb = parsed.TmdbId!.Value;
        var (sSentinel, eSentinel) = ChannelItemId.ToSentinels(parsed.Season, parsed.Episode);
        var ident = Base(PlayerStatsStatus.NotMaterialised, idText, playback, isPhantom: true) with
        {
            Type = type,
            TmdbId = tmdb,
            Season = parsed.Season,
            Episode = parsed.Episode,
        };

        var state = await _db.GetMaterialisedStateAsync(tmdb, type, sSentinel, eSentinel, ct).ConfigureAwait(false);
        if (state is null)
        {
            var inFlight = await _db.IsMaterialiseInFlightAsync(tmdb, type, sSentinel, eSentinel, ct).ConfigureAwait(false);
            return ident with { Status = inFlight ? PlayerStatsStatus.Materialising : PlayerStatsStatus.NotMaterialised };
        }

        ident = ident with { MaterialisedAt = state.MaterialisedAt };
        var entry = await _db.GetMaterialisedSourceAsync(state, ct).ConfigureAwait(false);
        if (entry is null)
        {
            return ident with { Status = PlayerStatsStatus.NoSource };
        }

        var hash = NormaliseInfoHash(entry.InfoHash) ?? NormaliseInfoHash(ExtractBtih(entry.Magnet));
        var title = string.IsNullOrWhiteSpace(entry.Title) ? null : entry.Title;
        string? releaseSource = title is null ? null : "candidate";
        if (title is null)
        {
            title = ExtractDisplayName(entry.Magnet);
            releaseSource = title is null ? null : "magnet_dn";
        }

        var source = new PlayerSourceInfo
        {
            Release = title,
            ReleaseSource = releaseSource,
            SizeBytes = entry.Size > 0 ? entry.Size : null,
            Indexer = string.IsNullOrWhiteSpace(entry.Indexer) ? null : entry.Indexer,
            InfoHash = hash,
            SeedersAtSelection = entry.Seeders >= 0 ? entry.Seeders : null,
        };
        ident = ident with { Source = source };
        if (hash is null)
        {
            return ident with { Status = PlayerStatsStatus.InvalidHash };
        }

        GostreamTorrentStats? stats;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(GostreamStatsTimeout);
            try
            {
                stats = await _gostream.GetTorrentStatsAsync(hash, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogDebug("gostream stats timed out for {Hash}", hash);
                return ident with { Status = PlayerStatsStatus.GostreamUnavailable };
            }
            catch (Exception ex) when (ex is GostreamException or HttpRequestException or InvalidOperationException or System.Text.Json.JsonException)
            {
                // Upstream text may carry internal hostnames; log it, never echo it to the browser.
                _logger.LogDebug(ex, "gostream stats failed for {Hash}", hash);
                return ident with { Status = PlayerStatsStatus.GostreamUnavailable };
            }
        }

        if (stats is null)
        {
            return ident with { Status = PlayerStatsStatus.TorrentNotLoaded };
        }

        return ident with
        {
            Status = PlayerStatsStatus.Ok,
            Stream = new PlayerStreamInfo
            {
                State = stats.StreamState,
                Peers = stats.Peers,
                Seeders = stats.Seeders,
                DownloadRateBytesPerSec = stats.DownloadRateBytesPerSec,
                BytesCompleted = stats.BytesCompleted,
                TorrentSize = stats.Size,
                ReadOffset = stats.ReadOffset,
                BufferedAheadBytes = stats.BufferedAheadBytes,
                BufferedAheadSeconds = stats.BufferedAheadSeconds,
                LastReadAt = stats.LastReadAt,
                TorrentSampledAt = stats.TorrentSampledAt,
                StreamSampledAt = stats.StreamSampledAt,
            },
        };
    }

    /// <summary>The "no player is open on this session" payload.</summary>
    public static PlayerStatsResponse NotPlaying()
        => new() { Status = PlayerStatsStatus.NotPlaying, Playing = false, IsPhantom = false };

    private static PlayerStatsResponse Base(string status, string itemId, PlayerPlaybackContext playback, bool isPhantom)
        => new()
        {
            Status = status,
            Playing = playback.Playing,
            IsPhantom = isPhantom,
            ItemId = itemId,
            PlaySessionId = playback.PlaySessionId,
            MediaSourceId = playback.MediaSourceId,
            IsPaused = playback.IsPaused,
            PositionTicks = playback.PositionTicks,
        };

    /// <summary>
    /// A phantom movie or episode channel item (external/orphan gostream-direct
    /// items and real library content are not phantom-materialised and carry no
    /// plugin-chosen source).
    /// </summary>
    private static bool TryParsePlayable(BaseItem item, out ChannelItemId parsed)
    {
        parsed = null!;
        if (!ChannelIds.IsPhantom(item.ChannelId)
            || item.Tags.Contains("external", StringComparer.OrdinalIgnoreCase)
            || !ChannelItemId.TryParse(item.ExternalId, out var id)
            || id.TmdbId is null)
        {
            return false;
        }

        if (id.Kind == ChannelItemId.KindMovie
            || (id.Kind == ChannelItemId.KindEpisode && id.Season.HasValue && id.Episode.HasValue))
        {
            parsed = id;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Lowercase 40-hex v1 info-hash (the only form gostream's stats endpoint
    /// accepts). Accepts 40-hex (any case) and 32-char base32; anything else → null.
    /// </summary>
    internal static string? NormaliseInfoHash(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var s = raw.Trim();
        if (s.Length == 40 && s.All(Uri.IsHexDigit))
        {
#pragma warning disable CA1308 // gostream's wire contract is lowercase hex.
            return s.ToLowerInvariant();
#pragma warning restore CA1308
        }

        if (s.Length == 32)
        {
            var bytes = DecodeBase32(s);
            return bytes is null ? null : Convert.ToHexStringLower(bytes);
        }

        return null;
    }

    /// <summary>The <c>xt=urn:btih:</c> value of a magnet URI, or null.</summary>
    internal static string? ExtractBtih(string? magnet)
    {
        foreach (var (key, value) in MagnetParams(magnet))
        {
            const string Prefix = "urn:btih:";
            if (string.Equals(key, "xt", StringComparison.OrdinalIgnoreCase)
                && value.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return value[Prefix.Length..];
            }
        }

        return null;
    }

    /// <summary>The magnet <c>dn</c> (display name) — the release name the indexer advertised.</summary>
    internal static string? ExtractDisplayName(string? magnet)
    {
        foreach (var (key, value) in MagnetParams(magnet))
        {
            if (string.Equals(key, "dn", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static System.Collections.Generic.IEnumerable<(string Key, string Value)> MagnetParams(string? magnet)
    {
        if (string.IsNullOrWhiteSpace(magnet))
        {
            yield break;
        }

        var q = magnet.IndexOf('?', StringComparison.Ordinal);
        if (q < 0)
        {
            yield break;
        }

        foreach (var part in magnet[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            string value;
            try
            {
                value = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
            }
            catch (UriFormatException)
            {
                continue;
            }

            yield return (part[..eq], value);
        }
    }

    private static byte[]? DecodeBase32(string s)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new byte[s.Length * 5 / 8];
        int buffer = 0, bits = 0, index = 0;
        foreach (var c in s.ToUpperInvariant())
        {
            var v = Alphabet.IndexOf(c, StringComparison.Ordinal);
            if (v < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                output[index++] = (byte)((buffer >> bits) & 0xFF);
            }
        }

        return index == output.Length ? output : null;
    }
}
