using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Sources;

/// <summary>
/// Chosen magnet candidate returned by <see cref="MagnetSelector"/>.
/// Identical shape to the cached row in <c>magnet_cache</c> minus the
/// cache bookkeeping, so callers can flow this straight into a
/// <c>GostreamAddRequest</c> or into the cache.
/// </summary>
public sealed record MagnetCandidate(
    string Magnet,
    string InfoHash,
    long Size,
    int Seeders,
    string Indexer);

/// <summary>
/// Aggregates results from every registered <see cref="IIndexerClient"/>
/// and picks the best release per the configured <see cref="QualityScorer"/>
/// + seeder/size floors. Replaces the pre-channel-arch in-line magnet
/// selection inside <c>Materialiser.cs</c>; tuple-shaped so the
/// channel-arch materialiser can call it without a BaseItem.
///
/// Plan §4.2.0.
/// </summary>
public sealed class MagnetSelector
{
    private readonly IEnumerable<IIndexerClient> _indexers;
    private readonly QualityScorer _scorer;
    private readonly Func<PluginConfiguration> _configProvider;
    private readonly ILogger<MagnetSelector> _logger;

    public MagnetSelector(
        IEnumerable<IIndexerClient> indexers,
        QualityScorer scorer,
        ILogger<MagnetSelector> logger)
        : this(indexers, scorer, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    // Test-friendly ctor: lets tests inject a synthetic configuration
    // without spinning up Plugin.Instance.
    internal MagnetSelector(
        IEnumerable<IIndexerClient> indexers,
        QualityScorer scorer,
        ILogger<MagnetSelector> logger,
        Func<PluginConfiguration> configProvider)
    {
        _indexers = indexers ?? throw new ArgumentNullException(nameof(indexers));
        _scorer = scorer ?? throw new ArgumentNullException(nameof(scorer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    /// <summary>
    /// Picks the best magnet for the given identifiers, or returns
    /// <c>null</c> if no indexer returned an acceptable candidate.
    /// Errors from individual indexers are logged and skipped; the
    /// remaining indexers' results are still scored.
    /// </summary>
    public async Task<MagnetCandidate?> SelectAsync(
        int tmdbId,
        string? imdbId,
        string type,
        int? season,
        int? episode,
        string title,
        int? year,
        CancellationToken ct)
    {
        var ranked = await SelectRankedAsync(tmdbId, imdbId, type, season, episode, title, year, ct)
            .ConfigureAwait(false);
        return ranked.Count > 0 ? ranked[0] : null;
    }

    /// <summary>
    /// Returns all acceptable magnets in preference order. Errors from
    /// individual indexers are logged and skipped; the remaining indexers'
    /// results are still scored.
    /// </summary>
    public async Task<IReadOnlyList<MagnetCandidate>> SelectRankedAsync(
        int tmdbId,
        string? imdbId,
        string type,
        int? season,
        int? episode,
        string title,
        int? year,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        var query = new IndexerQuery
        {
            Type = type,
            Tmdb = tmdbId,
            Imdb = type == "movie" ? imdbId : null,
            SeriesImdb = type == "episode" ? imdbId : null,
            Title = title,
            Year = year,
            Season = season,
            Episode = episode,
        };

        var aggregated = new List<IndexerCandidate>();
        foreach (var indexer in _indexers)
        {
            ct.ThrowIfCancellationRequested();
            if (!indexer.IsEnabled)
            {
                continue;
            }

            try
            {
                var hits = await indexer.SearchAsync(query, ct).ConfigureAwait(false);
                if (hits is null || hits.Count == 0)
                {
                    continue;
                }

                aggregated.AddRange(hits);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IndexerAuthException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Indexer {Indexer} returned auth failure for {Type}/{Tmdb}; skipping",
                    indexer.Name,
                    type,
                    tmdbId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Indexer {Indexer} failed for {Type}/{Tmdb}; skipping",
                    indexer.Name,
                    type,
                    tmdbId);
            }
        }

        if (aggregated.Count == 0)
        {
            _logger.LogInformation(
                "No indexer returned candidates for {Type}/{Tmdb} s{Season} e{Episode}",
                type,
                tmdbId,
                season,
                episode);
            return Array.Empty<MagnetCandidate>();
        }

        var cfg = _configProvider();
        var ranked = _scorer.RankCandidates(
            aggregated,
            cfg.QualityPreset,
            cfg.MinSeeders,
            cfg.MinSizeGb1080p,
            cfg.MinSizeGb4K,
            cfg.ResolutionFallbackOrder,
            cfg.SeederWeight,
            cfg.PreferredResolution);

        if (ranked.Count == 0)
        {
            _logger.LogInformation(
                "Scorer rejected all {N} candidates for {Type}/{Tmdb}",
                aggregated.Count,
                type,
                tmdbId);
            return Array.Empty<MagnetCandidate>();
        }

        return ranked.Select(picked =>
        {
            var indexerLabel = !string.IsNullOrWhiteSpace(picked.IndexerName)
                ? picked.IndexerName!
                : (_indexers.FirstOrDefault()?.Name ?? "unknown");

            return new MagnetCandidate(
                picked.Magnet,
                picked.InfoHash,
                picked.Size,
                picked.Seeders,
                indexerLabel);
        }).ToList();
    }
}
