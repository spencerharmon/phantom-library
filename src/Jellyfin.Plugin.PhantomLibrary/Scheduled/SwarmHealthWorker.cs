using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.State;
using Jellyfin.Plugin.PhantomLibrary.Swarm;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Scheduled;

/// <summary>
/// ROI Priority 0.5 (dht-swarm-health): keeps a REAL DHT swarm measurement for
/// every available item's candidate infohash and publishes it to the browse UI.
///
/// Each tick (<see cref="PluginConfiguration.SwarmHealthIntervalSeconds"/>):
///   1. takes up to <see cref="PluginConfiguration.SwarmHealthBatchSize"/> available
///      items whose candidate has no measurement newer than
///      <see cref="PluginConfiguration.SwarmHealthRecheckHours"/> (movies first, by
///      relevance — bounded by intent, never a catalogue sweep);
///   2. asks the swarm-health service (<see cref="PluginConfiguration.SwarmHealthServiceUrl"/>)
///      for each infohash's distinct DHT peers and stores the answers in
///      <c>swarm_health</c>;
/// and every <see cref="SyncEveryTicks"/> ticks writes each phantom movie/series
/// BaseItem's Peers value (CriticRating, see <see cref="SwarmHealthScore"/>) so the
/// web list view can sort by it. Disabled entirely while the service URL is empty.
/// </summary>
public sealed class SwarmHealthWorker : IHostedService, IDisposable
{
    /// <summary>BaseItem sync cadence, in measurement ticks.</summary>
    internal const int SyncEveryTicks = 10;

    /// <summary>Upper bound on BaseItem writes per sync pass (bounds write bursts).</summary>
    internal const int MaxBaseItemWritesPerSync = 2000;

    private readonly PhantomDb _db;
    private readonly SwarmHealthClient _client;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<SwarmHealthWorker> _logger;
    private readonly Func<PluginConfiguration> _configProvider;
    private Timer? _timer;
    private CancellationTokenSource? _stopping;
    private Task? _currentTick;
    private int _running;
    private int _ticks;

    public SwarmHealthWorker(
        PhantomDb db,
        SwarmHealthClient client,
        ILibraryManager libraryManager,
        ILogger<SwarmHealthWorker> logger)
        : this(db, client, libraryManager, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal SwarmHealthWorker(
        PhantomDb db,
        SwarmHealthClient client,
        ILibraryManager libraryManager,
        ILogger<SwarmHealthWorker> logger,
        Func<PluginConfiguration> configProvider)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var interval = TimeSpan.FromSeconds(_configProvider().SwarmHealthIntervalSeconds);
        _timer = new Timer(_ => _currentTick = TickAsync(_stopping.Token), null, interval, interval);
        _logger.LogInformation("Swarm-health worker started interval={Interval}s", interval.TotalSeconds);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _stopping?.Cancel();
        return _currentTick ?? Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _stopping?.Dispose();
    }

    private async Task TickAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            var cfg = _configProvider();
            if (string.IsNullOrWhiteSpace(cfg.SwarmHealthServiceUrl))
            {
                return;
            }

            await MeasureOnceAsync(cfg, ct).ConfigureAwait(false);
            if (Interlocked.Increment(ref _ticks) % SyncEveryTicks == 1)
            {
                await SyncBaseItemsAsync(cfg, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Swarm-health tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>Measure one batch of due candidates; returns the number of infohashes stored.</summary>
    internal async Task<int> MeasureOnceAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var due = await _db.GetSwarmHealthDueItemsAsync(
            cfg.SwarmHealthBatchSize,
            now.AddHours(-cfg.SwarmHealthRecheckHours),
            ct).ConfigureAwait(false);
        if (due.Count == 0)
        {
            return 0;
        }

        // Season packs share one infohash across many episodes; measure each once.
        var byHash = new Dictionary<string, SwarmHealthDueItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in due)
        {
            byHash.TryAdd(item.InfoHash, item);
        }

        IReadOnlyDictionary<string, int> peers;
        try
        {
            peers = await _client.MeasureAsync(cfg.SwarmHealthServiceUrl, byHash.Keys.ToList(), ct).ConfigureAwait(false);
            SwarmHealthMetrics.RecordRequest(ok: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            SwarmHealthMetrics.RecordRequest(ok: false);
            _logger.LogWarning(ex, "Swarm-health service request failed ({Count} infohashes)", byHash.Count);
            return 0;
        }

        var rows = new List<SwarmHealthMeasurement>(peers.Count);
        foreach (var (hash, count) in peers)
        {
            if (byHash.TryGetValue(hash, out var item))
            {
                rows.Add(new SwarmHealthMeasurement(hash, item.TmdbId, item.Type, item.Season, item.Episode, count, now));
            }
        }

        await _db.UpsertSwarmHealthAsync(rows, ct).ConfigureAwait(false);
        foreach (var group in rows.GroupBy(r => r.Type))
        {
            SwarmHealthMetrics.RecordMeasured(group.Key, group.Count(r => r.DhtPeers > 0), group.Count(r => r.DhtPeers <= 0));
        }

        _logger.LogDebug("Swarm-health measured {Measured}/{Asked} infohashes", rows.Count, byHash.Count);
        return rows.Count;
    }

    /// <summary>Write the Peers value onto phantom movie/series BaseItems; returns items written.</summary>
    internal async Task<int> SyncBaseItemsAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var fresh = DateTimeOffset.UtcNow.AddDays(-cfg.SwarmHealthRankWindowDays);
        var written = 0;
        foreach (var (channelId, kind, dbType, metricType) in new[]
                 {
                     (ChannelIds.Movies, BaseItemKind.Movie, "movie", "movie"),
                     (ChannelIds.Shows, BaseItemKind.Series, "episode", "series"),
                 })
        {
            ct.ThrowIfCancellationRequested();
            var peersByTmdb = await _db.GetSwarmPeersByTmdbAsync(dbType, fresh, ct).ConfigureAwait(false);
            SwarmHealthMetrics.SetTitles(metricType, peersByTmdb.Values.Count(p => p > 0), peersByTmdb.Values.Count(p => p <= 0));

            var items = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ChannelIds = new[] { channelId },
                IncludeItemTypes = new[] { kind },
                Recursive = true,
                EnableTotalRecordCount = false,
            });
            foreach (var item in items)
            {
                if (written >= MaxBaseItemWritesPerSync)
                {
                    break;
                }

                if (!ChannelItemId.TryParse(item.ExternalId, out var id) || id.TmdbId is not { } tmdb)
                {
                    continue;
                }

                var target = SwarmHealthScore.ToCriticRating(peersByTmdb.TryGetValue(tmdb, out var p) ? p : null);
                if (SwarmHealthScore.SameRating(item.CriticRating, target))
                {
                    continue;
                }

                item.CriticRating = target;
                await _libraryManager.UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
                written++;
            }
        }

        SwarmHealthMetrics.RecordBaseItemsUpdated(written);
        if (written > 0)
        {
            _logger.LogInformation("Swarm-health wrote Peers onto {Written} phantom BaseItems", written);
        }

        return written;
    }
}
