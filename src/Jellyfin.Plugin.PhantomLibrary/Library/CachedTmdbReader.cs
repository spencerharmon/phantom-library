using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Library;

/// <summary>
/// Wraps <see cref="ITmdbClient"/> recommendation-surface calls with a
/// PhantomDb-backed TTL cache (tmdb_cache table). Trending refreshes use
/// a 6h TTL; per-item similar/recommendations use a 24h TTL. Cached JSON
/// is the verbatim list of hits that the upstream method returned, so a
/// cache hit produces an identical IReadOnlyList&lt;TmdbSearchHit&gt; without
/// touching the network.
/// </summary>
public sealed class CachedTmdbReader
{
    /// <summary>Cache TTL for /trending/{type}/{window} responses.</summary>
    public static readonly TimeSpan TrendingTtl = TimeSpan.FromHours(6);

    /// <summary>Cache TTL for /{type}/{id}/similar and /{type}/{id}/recommendations responses.</summary>
    public static readonly TimeSpan SimilarRecommendationsTtl = TimeSpan.FromHours(24);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly ITmdbClient _tmdb;
    private readonly PhantomDb _db;
    private readonly ILogger<CachedTmdbReader> _logger;
    private readonly Func<PluginConfiguration> _configProvider;

    public CachedTmdbReader(ITmdbClient tmdb, PhantomDb db, ILogger<CachedTmdbReader> logger)
        : this(tmdb, db, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal CachedTmdbReader(ITmdbClient tmdb, PhantomDb db, ILogger<CachedTmdbReader> logger, Func<PluginConfiguration> configProvider)
    {
        _tmdb = tmdb;
        _db = db;
        _logger = logger;
        _configProvider = configProvider;
    }

    /// <summary>Public counters incremented on every fetch — handy for tests / log lines.</summary>
    public long HitCount { get; private set; }

    /// <summary>Public counters incremented on every fetch — handy for tests / log lines.</summary>
    public long MissCount { get; private set; }

    public Task<(IReadOnlyList<TmdbSearchHit> Hits, bool FromCache)> TrendingMoviesAsync(string window, string? language, CancellationToken ct)
        => FetchAsync(
            "trending/movie",
            new Dictionary<string, string?> { ["window"] = window },
            language,
            TrendingTtl,
            innerCt => _tmdb.GetTrendingMoviesAsync(window, language, innerCt),
            ct);

    public Task<(IReadOnlyList<TmdbSearchHit> Hits, bool FromCache)> TrendingSeriesAsync(string window, string? language, CancellationToken ct)
        => FetchAsync(
            "trending/tv",
            new Dictionary<string, string?> { ["window"] = window },
            language,
            TrendingTtl,
            innerCt => _tmdb.GetTrendingSeriesAsync(window, language, innerCt),
            ct);

    public Task<(IReadOnlyList<TmdbSearchHit> Hits, bool FromCache)> GetDiscoverMoviesAsync(int page, string? language, CancellationToken ct)
        => FetchAsync(
            "discover/movie",
            new Dictionary<string, string?> { ["page"] = page.ToString(CultureInfo.InvariantCulture) },
            language,
            DiscoverTtl(),
            innerCt => _tmdb.DiscoverMoviesAsync(page, language, innerCt),
            ct);

    public Task<(IReadOnlyList<TmdbSearchHit> Hits, bool FromCache)> GetDiscoverSeriesAsync(int page, string? language, CancellationToken ct)
        => FetchAsync(
            "discover/tv",
            new Dictionary<string, string?> { ["page"] = page.ToString(CultureInfo.InvariantCulture) },
            language,
            DiscoverTtl(),
            innerCt => _tmdb.DiscoverSeriesAsync(page, language, innerCt),
            ct);

    public Task<(IReadOnlyList<TmdbSearchHit> Hits, bool FromCache)> SimilarMoviesAsync(int tmdbId, string? language, CancellationToken ct)
        => FetchAsync(
            "movie/similar",
            new Dictionary<string, string?> { ["id"] = tmdbId.ToString(CultureInfo.InvariantCulture) },
            language,
            SimilarRecommendationsTtl,
            innerCt => _tmdb.GetSimilarMoviesAsync(tmdbId, language, innerCt),
            ct);

    public Task<(IReadOnlyList<TmdbSearchHit> Hits, bool FromCache)> SimilarSeriesAsync(int tmdbId, string? language, CancellationToken ct)
        => FetchAsync(
            "tv/similar",
            new Dictionary<string, string?> { ["id"] = tmdbId.ToString(CultureInfo.InvariantCulture) },
            language,
            SimilarRecommendationsTtl,
            innerCt => _tmdb.GetSimilarSeriesAsync(tmdbId, language, innerCt),
            ct);

    public Task<(IReadOnlyList<TmdbSearchHit> Hits, bool FromCache)> MovieRecommendationsAsync(int tmdbId, string? language, CancellationToken ct)
        => FetchAsync(
            "movie/recommendations",
            new Dictionary<string, string?> { ["id"] = tmdbId.ToString(CultureInfo.InvariantCulture) },
            language,
            SimilarRecommendationsTtl,
            innerCt => _tmdb.GetMovieRecommendationsAsync(tmdbId, language, innerCt),
            ct);

    public Task<(IReadOnlyList<TmdbSearchHit> Hits, bool FromCache)> SeriesRecommendationsAsync(int tmdbId, string? language, CancellationToken ct)
        => FetchAsync(
            "tv/recommendations",
            new Dictionary<string, string?> { ["id"] = tmdbId.ToString(CultureInfo.InvariantCulture) },
            language,
            SimilarRecommendationsTtl,
            innerCt => _tmdb.GetSeriesRecommendationsAsync(tmdbId, language, innerCt),
            ct);

    private TimeSpan DiscoverTtl()
    {
        var hours = _configProvider().DiscoverCacheTtlHours;
        return TimeSpan.FromHours(hours > 0 ? hours : 24);
    }

    private async Task<(IReadOnlyList<TmdbSearchHit> Hits, bool FromCache)> FetchAsync(
        string endpoint,
        IReadOnlyDictionary<string, string?> requestParams,
        string? language,
        TimeSpan ttl,
        Func<CancellationToken, Task<IReadOnlyList<TmdbSearchHit>>> upstream,
        CancellationToken ct)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? "en-US" : language;
        var paramsHash = ComputeParamsHash(requestParams);

        try
        {
            var cached = await _db.GetTmdbCacheAsync(endpoint, paramsHash, lang, ct).ConfigureAwait(false);
            if (cached is not null)
            {
                var deserialised = JsonSerializer.Deserialize<List<TmdbSearchHit>>(cached, JsonOpts)
                    ?? new List<TmdbSearchHit>();
                HitCount++;
                return (deserialised, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "tmdb_cache read failed for {Endpoint}; falling back to HTTP", endpoint);
        }

        var hits = await upstream(ct).ConfigureAwait(false);
        MissCount++;

        try
        {
            var json = JsonSerializer.Serialize(hits, JsonOpts);
            await _db.PutTmdbCacheAsync(endpoint, paramsHash, lang, json, ttl, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "tmdb_cache write failed for {Endpoint}; continuing", endpoint);
        }

        return (hits, false);
    }

    /// <summary>SHA-256 hex of canonical JSON (sorted keys) of the request parameters.</summary>
    public static string ComputeParamsHash(IReadOnlyDictionary<string, string?> requestParams)
    {
        ArgumentNullException.ThrowIfNull(requestParams);
        var sorted = requestParams
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var json = JsonSerializer.Serialize(sorted);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
