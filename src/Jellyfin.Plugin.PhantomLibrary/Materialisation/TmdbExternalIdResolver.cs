using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Read-through cache for TMDB → IMDb id lookups. Backed by the
/// <c>tmdb_external_ids</c> table on <see cref="PhantomDb"/>. Positive
/// hits are returned indefinitely (a documented <see
/// cref="PositiveCacheTtlDays"/> TTL applies in principle but is not
/// currently enforced — TMDB → IMDB associations are effectively
/// stable, and a re-fetch tick on the off chance of a correction is
/// expensive). Negative hits respect <see cref="NegativeCacheTtlHours"/>
/// so we don't hammer TMDB for items that genuinely have no IMDB
/// mapping.
///
/// Plan §4.1.
/// </summary>
public sealed class TmdbExternalIdResolver
{
    /// <summary>
    /// Window during which a cached null result short-circuits without
    /// re-fetching from TMDB.
    /// </summary>
    public const int NegativeCacheTtlHours = 24;

    /// <summary>
    /// Documented positive-cache lifetime. Currently not enforced — a
    /// positive lookup result is treated as permanent until the next
    /// schema wipe. Documented here so future tuning (e.g. periodic
    /// re-fetch to pick up TMDB corrections) has a starting point.
    /// </summary>
    public const int PositiveCacheTtlDays = 30;

    private readonly PhantomDb _db;
    private readonly ITmdbClient _tmdb;
    private readonly ILogger<TmdbExternalIdResolver> _logger;

    public TmdbExternalIdResolver(
        PhantomDb db,
        ITmdbClient tmdb,
        ILogger<TmdbExternalIdResolver> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tmdb = tmdb ?? throw new ArgumentNullException(nameof(tmdb));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Resolve the IMDb id for <paramref name="tmdbId"/>. <paramref name="type"/>
    /// is <c>"movie"</c> or <c>"series"</c> (episodes resolve via their
    /// series). Returns <c>null</c> when TMDB has no IMDb mapping; the
    /// negative result is cached for <see cref="NegativeCacheTtlHours"/>
    /// so repeated lookups don't repeatedly hit TMDB.
    /// </summary>
    public async Task<string?> GetImdbIdAsync(int tmdbId, string type, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        if (type != "movie" && type != "series")
        {
            throw new ArgumentException($"Unsupported type '{type}'; expected 'movie' or 'series'", nameof(type));
        }

        var cached = await _db.GetImdbIdAsync(tmdbId, type, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            if (cached.ImdbId is not null)
            {
                return cached.ImdbId;
            }

            if (DateTimeOffset.UtcNow - cached.FetchedAt < TimeSpan.FromHours(NegativeCacheTtlHours))
            {
                return null;
            }
        }

        string? imdbId;
        try
        {
            imdbId = type == "movie"
                ? await _tmdb.GetImdbIdForMovieAsync(tmdbId, ct).ConfigureAwait(false)
                : await _tmdb.GetImdbIdForSeriesAsync(tmdbId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Transient: don't poison the cache. The next call retries.
            _logger.LogWarning(
                ex,
                "TMDB external_ids fetch failed for {Type}/{Tmdb}; not caching",
                type,
                tmdbId);
            return null;
        }

        await _db.SetImdbIdAsync(tmdbId, type, imdbId, ct).ConfigureAwait(false);
        return imdbId;
    }
}
