using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Library;

/// <summary>
/// Default <see cref="IFavouriteRecommendationIngestor"/>. Fans a favourite
/// seed out to TMDB "similar" + "recommendations" via <see cref="CachedTmdbReader"/>
/// (24h cache) and writes the merged, de-duplicated, capped hits into the
/// append-only catalogue through <see cref="PhantomDb.UpsertCatalogueHitsAsync"/>
/// — the same write path <c>DiscoveryRefreshTask</c> uses, so new movie rows
/// enqueue availability probing and new series rows enqueue expansion.
/// </summary>
public sealed class FavouriteRecommendationIngestor : IFavouriteRecommendationIngestor
{
    /// <summary>
    /// <c>catalogue_items.source_mask</c> bit for rows discovered because a
    /// user favourited a related title. Distinct from <c>SourceTrending</c>(1)
    /// and <c>SourceDiscover</c>(2) so a favourite-seeded row is attributable.
    /// </summary>
    public const int SourceFavouriteRecommendation = 4;

    private const int DefaultMaxPerFavourite = 40;

    private readonly CachedTmdbReader _tmdb;
    private readonly PhantomDb _db;
    private readonly ILogger<FavouriteRecommendationIngestor> _logger;
    private readonly Func<PluginConfiguration?> _configurationProvider;

    public FavouriteRecommendationIngestor(
        CachedTmdbReader tmdb,
        PhantomDb db,
        ILogger<FavouriteRecommendationIngestor> logger)
        : this(tmdb, db, logger, () => Plugin.Instance?.Configuration)
    {
    }

    internal FavouriteRecommendationIngestor(
        CachedTmdbReader tmdb,
        PhantomDb db,
        ILogger<FavouriteRecommendationIngestor> logger,
        Func<PluginConfiguration?> configurationProvider)
    {
        _tmdb = tmdb;
        _db = db;
        _logger = logger;
        ArgumentNullException.ThrowIfNull(configurationProvider);
        _configurationProvider = configurationProvider;
    }

    /// <inheritdoc />
    public async Task<FavouriteRecommendationResult> IngestForFavouriteAsync(int tmdbId, string type, CancellationToken ct)
    {
        if (tmdbId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(tmdbId), tmdbId, "tmdbId must be positive.");
        }

        if (type != "movie" && type != "series")
        {
            throw new ArgumentException($"type must be 'movie' or 'series', got '{type}'.", nameof(type));
        }

        var config = _configurationProvider();
        if (!(config?.FavouriteRecommendationsEnabled ?? true))
        {
            _logger.LogDebug("Favourite recommendations disabled; skipping {Type} {Tmdb}", type, tmdbId);
            return FavouriteRecommendationResult.Disabled(tmdbId, type);
        }

        var maxPerFavourite = config?.FavouriteRecommendationsMaxPerFavourite ?? DefaultMaxPerFavourite;
        if (maxPerFavourite <= 0)
        {
            maxPerFavourite = DefaultMaxPerFavourite;
        }

        var language = string.IsNullOrWhiteSpace(config?.DiscoveryLanguage) ? null : config!.DiscoveryLanguage;

        IReadOnlyList<TmdbSearchHit> similar;
        IReadOnlyList<TmdbSearchHit> recommended;
        if (type == "movie")
        {
            (similar, _) = await _tmdb.SimilarMoviesAsync(tmdbId, language, ct).ConfigureAwait(false);
            (recommended, _) = await _tmdb.MovieRecommendationsAsync(tmdbId, language, ct).ConfigureAwait(false);
        }
        else
        {
            (similar, _) = await _tmdb.SimilarSeriesAsync(tmdbId, language, ct).ConfigureAwait(false);
            (recommended, _) = await _tmdb.SeriesRecommendationsAsync(tmdbId, language, ct).ConfigureAwait(false);
        }

        var candidatesConsidered = similar.Count + recommended.Count;
        var seenIds = new HashSet<int> { tmdbId };
        var rows = new List<TmdbMetadataRow>(Math.Min(candidatesConsidered, maxPerFavourite));
        foreach (var hit in Concat(similar, recommended))
        {
            ct.ThrowIfCancellationRequested();
            if (rows.Count >= maxPerFavourite)
            {
                break;
            }

            // Drop the seed itself and any id already taken from either list.
            if (!seenIds.Add(hit.Id))
            {
                continue;
            }

            var row = TmdbHitMapper.MapSearchHitToMetadata(hit, type);
            if (string.IsNullOrWhiteSpace(row.Title))
            {
                _logger.LogDebug("Skipping favourite-recommendation hit {Type}:{Tmdb} (no title)", type, hit.Id);
                continue;
            }

            rows.Add(row);
        }

        var write = await _db.UpsertCatalogueHitsAsync(rows, SourceFavouriteRecommendation, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "Favourite recommendations for {Type} {Tmdb}: candidates={Candidates} written={Seen} newCatalogue={Inserted} newAvailability={Avail} newSeriesExpansion={SeriesExpansion}",
            type,
            tmdbId,
            candidatesConsidered,
            write.Seen,
            write.Inserted,
            write.AvailabilityInserted,
            write.SeriesExpansionInserted);

        return new FavouriteRecommendationResult(
            TmdbId: tmdbId,
            Type: type,
            Enabled: true,
            CandidatesConsidered: candidatesConsidered,
            Seen: write.Seen,
            Inserted: write.Inserted,
            MetadataInserted: write.MetadataInserted,
            AvailabilityInserted: write.AvailabilityInserted,
            SeriesExpansionInserted: write.SeriesExpansionInserted);
    }

    private static IEnumerable<TmdbSearchHit> Concat(IReadOnlyList<TmdbSearchHit> a, IReadOnlyList<TmdbSearchHit> b)
    {
        foreach (var hit in a)
        {
            yield return hit;
        }

        foreach (var hit in b)
        {
            yield return hit;
        }
    }
}
