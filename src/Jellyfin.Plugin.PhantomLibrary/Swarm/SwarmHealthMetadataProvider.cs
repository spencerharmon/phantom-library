using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Scheduled;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Swarm;

/// <summary>
/// Keeps the DHT "Peers" score on phantom movie/series BaseItems across metadata
/// refreshes. Core's MetadataService replaces <c>CriticRating</c> with the remote
/// providers' value (null for phantoms) on a replace-all refresh, which wiped the
/// value the swarm-health sync wrote; a custom provider runs AFTER that merge on
/// every refresh, so the score is re-applied as part of the refresh itself.
///
/// The value is EXACTLY what the score sweep writes (<see cref="SwarmHealthScore.TargetFor"/>:
/// live min(peers,100) &gt; unmeasured 0.5 &gt; dead 0; orphan/non-TMDB titles 0.5), for
/// exactly the sweep's partitions (<see cref="SwarmHealthWorker.SyncPhases"/>: phantom
/// movies, series and orphan-series folders; never seasons, episodes or non-Phantom
/// items). The peer count comes from <see cref="SwarmHealthCache"/> (filled by the
/// worker's bounded batches) or, on a miss / stale entry, from ONE bounded indexed
/// lookup for this title — never a full <c>swarm_health</c> aggregate.
/// Inert while swarm health is disabled (empty service URL).
/// </summary>
public sealed class SwarmHealthMetadataProvider
    : ICustomMetadataProvider<Movie>, ICustomMetadataProvider<Series>, ICustomMetadataProvider<Folder>, IHasOrder
{
    private readonly Func<string, IReadOnlyCollection<int>, DateTimeOffset, CancellationToken, Task<IReadOnlyDictionary<int, int>>> _lookup;
    private readonly Func<PluginConfiguration> _config;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _logger;

    public SwarmHealthMetadataProvider(PhantomDb db, ILogger<SwarmHealthMetadataProvider> logger)
        : this(
            (db ?? throw new ArgumentNullException(nameof(db))).GetSwarmPeersForTmdbIdsAsync,
            () => Plugin.Instance?.Configuration ?? new PluginConfiguration(),
            () => DateTimeOffset.UtcNow,
            logger)
    {
    }

    internal SwarmHealthMetadataProvider(
        Func<string, IReadOnlyCollection<int>, DateTimeOffset, CancellationToken, Task<IReadOnlyDictionary<int, int>>> lookup,
        Func<PluginConfiguration> config,
        Func<DateTimeOffset> clock,
        ILogger logger)
    {
        _lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => "Phantom Library swarm health";

    /// <inheritdoc />
    public int Order => 1000;

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Movie item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        => ApplyAsync(item, cancellationToken);

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Series item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        => ApplyAsync(item, cancellationToken);

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Folder item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        => ApplyAsync(item, cancellationToken);

    /// <summary>Re-applies the Peers score to a phantom title; returns whether it changed.</summary>
    internal async Task<ItemUpdateType> ApplyAsync(BaseItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        var cfg = _config();
        if (string.IsNullOrWhiteSpace(cfg.SwarmHealthServiceUrl) || !IsSweptPartition(item))
        {
            return ItemUpdateType.None;
        }

        var (kind, tmdb) = SwarmHealthScore.Classify(item.ExternalId);
        if (kind == ScoredTitleKind.NotScored)
        {
            return ItemUpdateType.None;
        }

        var source = "orphan";
        var peersByTmdb = new Dictionary<int, int>();
        if (kind == ScoredTitleKind.Measurable)
        {
            var series = item.ChannelId == ChannelIds.Shows;
            var now = _clock();
            if (SwarmHealthCache.TryGet(series, tmdb, now.AddHours(-cfg.SwarmHealthRecheckHours), out var cached))
            {
                source = "cache";
            }
            else
            {
                source = "lookup";
                try
                {
                    var found = await _lookup(series ? "episode" : "movie", new[] { tmdb }, now.AddDays(-cfg.SwarmHealthRankWindowDays), ct).ConfigureAwait(false);
                    SwarmHealthCache.Record(series, new[] { tmdb }, found, now);
                    cached = found.TryGetValue(tmdb, out var p) ? p : null;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    // Never fail the core refresh: the sweep re-scores this title on its next pass.
                    SwarmHealthMetrics.RecordProviderApplied(source, "error");
                    _logger.LogWarning(ex, "Swarm-health provider could not look up {Item}; the score sweep will re-apply it", item.Id);
                    return ItemUpdateType.None;
                }
            }

            if (cached is { } measured)
            {
                peersByTmdb[tmdb] = measured;
            }
        }

        // Same policy function as the sweep (orphans -> unmeasured 0.5).
        if (SwarmHealthScore.TargetFor(item.ExternalId, peersByTmdb) is not { } target)
        {
            return ItemUpdateType.None;
        }

        if (SwarmHealthScore.SameRating(item.CriticRating, target))
        {
            SwarmHealthMetrics.RecordProviderApplied(source, "unchanged");
            return ItemUpdateType.None;
        }

        item.CriticRating = target;
        SwarmHealthMetrics.RecordProviderApplied(source, "restored");
        return ItemUpdateType.MetadataEdit;
    }

    /// <summary>Exactly the sweep's partitions: channel + concrete item type.</summary>
    private static bool IsSweptPartition(BaseItem item)
    {
        var typeName = item.GetType().FullName;
        foreach (var phase in SwarmHealthWorker.SyncPhases)
        {
            if (item.ChannelId == phase.ChannelId && string.Equals(typeName, phase.ItemType, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
