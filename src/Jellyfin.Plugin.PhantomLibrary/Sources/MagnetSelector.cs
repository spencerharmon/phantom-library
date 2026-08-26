using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
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
    string Indexer)
{
    public string? Title { get; init; }
}

public enum MagnetProbeOutcome
{
    Available,
    DefinitiveUnavailable,
    IndeterminateTransient,
    NoCapableIndexer,
}

public sealed record MagnetProbeResult(
    MagnetProbeOutcome Outcome,
    IReadOnlyList<MagnetCandidate> Candidates,
    string? ErrorKind,
    string? ErrorMessage)
{
    public static MagnetProbeResult Available(IReadOnlyList<MagnetCandidate> candidates)
        => new(MagnetProbeOutcome.Available, candidates, null, null);

    public static MagnetProbeResult DefinitiveUnavailable()
        => new(MagnetProbeOutcome.DefinitiveUnavailable, Array.Empty<MagnetCandidate>(), null, null);

    public static MagnetProbeResult NoCapableIndexer(string? message)
        => new(MagnetProbeOutcome.NoCapableIndexer, Array.Empty<MagnetCandidate>(), "no_capable_indexer", message);

    public static MagnetProbeResult Transient(string kind, string? message)
        => new(MagnetProbeOutcome.IndeterminateTransient, Array.Empty<MagnetCandidate>(), kind, message);
}

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
    private static readonly Regex EpisodeToken = new(@"\bS(?<s>\d{1,2})E(?<e>\d{1,3})\b|\b(?<s2>\d{1,2})x(?<e2>\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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

        var probe = await ProbeAsync(tmdbId, imdbId, type, season, episode, title, year, ct).ConfigureAwait(false);
        return probe.Outcome == MagnetProbeOutcome.Available ? probe.Candidates : Array.Empty<MagnetCandidate>();
    }

    public async Task<MagnetProbeResult> ProbeAsync(
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

        var enabled = _indexers.Where(i => i.IsEnabled).ToList();
        if (enabled.Count == 0)
        {
            return MagnetProbeResult.Transient("no_enabled_indexers", "No enabled indexers are configured");
        }

        var aggregated = new List<IndexerCandidate>();
        var failures = new List<string>();
        var abstentions = new List<string>();
        foreach (var indexer in enabled)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var hits = await indexer.SearchAsync(query, ct).ConfigureAwait(false);
                if (hits is { Count: > 0 })
                {
                    aggregated.AddRange(hits);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IndexerNotApplicableException ex)
            {
                // Abstention: the indexer cannot serve this query as-is (e.g. Torrentio
                // with no IMDB id). Not a failure and not transient — do not count it.
                abstentions.Add(indexer.Name);
                _logger.LogDebug(ex, "Indexer {Indexer} abstained (not applicable) for {Type}/{Tmdb}", indexer.Name, type, tmdbId);
            }
            catch (IndexerAuthException ex)
            {
                failures.Add($"{indexer.Name}:auth");
                _logger.LogWarning(ex, "Indexer {Indexer} returned auth failure for {Type}/{Tmdb}", indexer.Name, type, tmdbId);
            }
            catch (Exception ex)
            {
                failures.Add($"{indexer.Name}:transient");
                _logger.LogWarning(ex, "Indexer {Indexer} failed for {Type}/{Tmdb}", indexer.Name, type, tmdbId);
            }
        }

        if (aggregated.Count == 0)
        {
            if (failures.Count > 0)
            {
                return MagnetProbeResult.Transient("indexer_partial_or_total_failure", string.Join(";", failures));
            }

            if (abstentions.Count == enabled.Count)
            {
                // No enabled indexer could even serve this query (e.g. only Torrentio
                // enabled and no IMDB id). Not a definitive "unavailable" — no indexer ran.
                _logger.LogInformation(
                    "No capable indexer for {Type}/{Tmdb} s{Season} e{Episode}; all {N} enabled indexer(s) abstained",
                    type, tmdbId, season, episode, enabled.Count);
                return MagnetProbeResult.NoCapableIndexer(string.Join(";", abstentions));
            }

            _logger.LogInformation("No indexer returned candidates for {Type}/{Tmdb} s{Season} e{Episode}", type, tmdbId, season, episode);
            return MagnetProbeResult.DefinitiveUnavailable();
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
            if (failures.Count > 0)
            {
                return MagnetProbeResult.Transient("indexer_partial_failure_all_candidates_rejected", string.Join(";", failures));
            }

            _logger.LogInformation("Scorer rejected all {N} candidates for {Type}/{Tmdb}", aggregated.Count, type, tmdbId);
            return MagnetProbeResult.DefinitiveUnavailable();
        }

        var episodeRanked = string.Equals(type, "episode", StringComparison.OrdinalIgnoreCase)
            && season.HasValue
            && episode.HasValue
                ? ranked
                    .Select((candidate, index) => new { candidate, index })
                    .OrderByDescending(x => EpisodeSpecificityScore(x.candidate.Title, season.Value, episode.Value))
                    .ThenBy(x => x.index)
                    .Select(x => x.candidate)
                    .ToList()
                : ranked;

        var candidates = episodeRanked.Select(picked =>
        {
            var indexerLabel = !string.IsNullOrWhiteSpace(picked.IndexerName)
                ? picked.IndexerName!
                : (enabled.FirstOrDefault()?.Name ?? "unknown");

            return new MagnetCandidate(picked.Magnet, picked.InfoHash, picked.Size, picked.Seeders, indexerLabel)
            {
                Title = picked.Title,
            };
        }).ToList();

        return MagnetProbeResult.Available(candidates);
    }

    internal static int EpisodeSpecificityScore(string? title, int season, int episode)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return 0;
        }

        var score = 0;
        foreach (Match match in EpisodeToken.Matches(title))
        {
            var seasonText = match.Groups["s"].Success ? match.Groups["s"].Value : match.Groups["s2"].Value;
            var episodeText = match.Groups["e"].Success ? match.Groups["e"].Value : match.Groups["e2"].Value;
            if (!int.TryParse(seasonText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
                || !int.TryParse(episodeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e))
            {
                continue;
            }

            if (s == season && e == episode)
            {
                score += 1000;
            }
            else if (s == season)
            {
                score -= 100;
            }
            else
            {
                score -= 200;
            }
        }

        if (title.Contains("complete", StringComparison.OrdinalIgnoreCase)
            || title.Contains("season 1-", StringComparison.OrdinalIgnoreCase)
            || title.Contains("s01-s", StringComparison.OrdinalIgnoreCase)
            || title.Contains("s01 to s", StringComparison.OrdinalIgnoreCase))
        {
            score -= 50;
        }

        return score;
    }
}
