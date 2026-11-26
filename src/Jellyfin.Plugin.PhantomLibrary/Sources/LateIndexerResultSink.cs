using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Sources;

/// <summary>
/// p13-parallel-indexer-search: candidates for one item, re-ranked after an
/// indexer request that was still running at the search budget completed in the
/// background. <see cref="RankedCandidates"/> is the FULL ranked set (the
/// in-budget candidates plus every late arrival so far) so the cache rank order
/// stays coherent.
/// </summary>
public sealed record LateIndexerResult(
    int TmdbId,
    string Type,
    int? Season,
    int? Episode,
    IReadOnlyList<MagnetCandidate> RankedCandidates);

/// <summary>Receives late (post-budget) indexer results so they are never discarded.</summary>
public interface ILateIndexerResultSink
{
    Task WriteAsync(LateIndexerResult result, CancellationToken ct);
}

/// <summary>
/// Writes late indexer results into the P6 magnet cache (<c>source_candidates</c>),
/// keyed by the same item tuple + <see cref="PluginConfiguration.SourcePickerPreset"/>
/// the materialise / details-probe paths read, so the next materialise or details
/// probe of that item sees the slow indexer's releases without searching again.
/// Movie => season/episode -1 sentinels; episode => real season/episode (parity
/// with <see cref="MagnetCacheBuilder"/>).
/// </summary>
public sealed class MagnetCacheLateResultWriter : ILateIndexerResultSink
{
    /// <summary><c>source_candidates.source</c> value for rows written by this sink.</summary>
    public const string SourceTag = "late_indexer_result";

    private readonly PhantomDb _db;
    private readonly Func<PluginConfiguration> _configProvider;
    private readonly ILogger<MagnetCacheLateResultWriter> _logger;

    public MagnetCacheLateResultWriter(PhantomDb db, ILogger<MagnetCacheLateResultWriter> logger)
        : this(db, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal MagnetCacheLateResultWriter(
        PhantomDb db,
        ILogger<MagnetCacheLateResultWriter> logger,
        Func<PluginConfiguration> configProvider)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    public async Task WriteAsync(LateIndexerResult result, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.RankedCandidates.Count == 0)
        {
            return;
        }

        var cfg = _configProvider();
        var isEpisode = string.Equals(result.Type, "episode", StringComparison.OrdinalIgnoreCase);
        var season = isEpisode ? result.Season ?? -1 : -1;
        var episode = isEpisode ? result.Episode ?? -1 : -1;
        await _db.UpsertSourceCandidatesAsync(
            result.TmdbId,
            result.Type,
            season,
            episode,
            cfg.SourcePickerPreset,
            result.RankedCandidates,
            SourceTag,
            TimeSpan.FromHours(Math.Max(1, cfg.MagnetCacheTtlHours)),
            ct).ConfigureAwait(false);
        _logger.LogInformation(
            "Late indexer results cached: {Count} ranked candidates for {Type}/{Tmdb} s{Season}e{Episode}",
            result.RankedCandidates.Count, result.Type, result.TmdbId, season, episode);
    }
}
