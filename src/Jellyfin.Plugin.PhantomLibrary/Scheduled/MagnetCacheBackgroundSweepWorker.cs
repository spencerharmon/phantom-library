using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Scheduled;

/// <summary>
/// ROI Priority 6, revised architecture item 2b
/// (p6-magnet-cache-background-sweep): the lowest-priority magnet-cache
/// lane. Mirrors <see cref="AvailabilityProbeWorker"/>'s breadth-first,
/// timer-driven pattern, but only ever ENQUEUES <c>magnet_cache_jobs</c>
/// rows — it never runs the Prowlarr fan-out itself (that is
/// <c>MagnetCacheBuilder</c>'s job, draining the queue in priority order).
///
/// Every enqueue uses <see cref="PhantomDb.BackgroundSweepMagnetCachePriority"/>
/// (0), the lowest lane in the magnet-cache priority scheme: a
/// <see cref="PhantomDb.OpportunisticMagnetCachePriority"/> (100) row
/// enqueued by a user-initiated touch is ALWAYS claimed first no matter how
/// large the background backlog has grown, because
/// <see cref="PhantomDb.ClaimNextMagnetCacheJobAsync"/> claims strictly
/// priority-first.
///
/// Yields to user activity exactly like <see cref="AvailabilityProbeWorker"/>:
/// if the user-activity marker was touched within
/// <see cref="PluginConfiguration.AvailabilityYieldToUserSeconds"/>, the tick
/// backs off to the slow interval without enqueuing anything, so the sweep
/// never competes with a live user session for the DB write lock.
///
/// TTL: <see cref="PhantomDb.GetAvailableItemsMissingFreshMagnetCacheAsync"/>
/// treats a <c>source_candidates</c> row whose <c>expires_at</c> has passed
/// as equivalent to no row at all, so a stale cached entry is re-enqueued
/// exactly like a never-cached one. Movie and episode tuples both flow
/// through the same query and enqueue path.
/// </summary>
public sealed class MagnetCacheBackgroundSweepWorker : IHostedService, IDisposable
{
    private readonly PhantomDb _db;
    private readonly ILogger<MagnetCacheBackgroundSweepWorker> _logger;
    private readonly Func<PluginConfiguration> _configProvider;
    private Timer? _timer;
    private CancellationTokenSource? _stopping;
    private Task? _currentTick;
    private int _running;

    public MagnetCacheBackgroundSweepWorker(
        PhantomDb db,
        ILogger<MagnetCacheBackgroundSweepWorker> logger)
        : this(db, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal MagnetCacheBackgroundSweepWorker(
        PhantomDb db,
        ILogger<MagnetCacheBackgroundSweepWorker> logger,
        Func<PluginConfiguration> configProvider)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cfg = _configProvider();
        var interval = TimeSpan.FromSeconds(Math.Max(1, cfg.MagnetCacheSweepMinIntervalSeconds));
        _timer = new Timer(_ => _currentTick = TickAsync(_stopping.Token), null, interval, interval);
        _logger.LogInformation(
            "Magnet-cache background sweep worker started interval={Interval}s enabled={Enabled}",
            interval.TotalSeconds,
            cfg.MagnetCacheSweepEnabled);
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

    private async Task TickAsync(CancellationToken serviceStopping)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            var cfg = _configProvider();
            if (!cfg.MagnetCacheSweepEnabled)
            {
                return;
            }

            // Yield to user-initiated work: back off at the slow interval
            // (never enqueuing) while a user-driven action recently touched
            // the shared activity marker, mirroring AvailabilityProbeWorker.
            var yieldWindow = Math.Max(0, cfg.AvailabilityYieldToUserSeconds);
            if (yieldWindow > 0)
            {
                var lastActivity = await _db.GetUserActivityAtAsync(serviceStopping).ConfigureAwait(false);
                if (lastActivity is { } activityAt
                    && DateTimeOffset.UtcNow - activityAt < TimeSpan.FromSeconds(yieldWindow))
                {
                    var backoff = TimeSpan.FromSeconds(Math.Max(1, cfg.MagnetCacheSweepMaxIntervalSeconds));
                    _timer?.Change(backoff, backoff);
                    _logger.LogDebug("Magnet-cache background sweep yielding to recent user activity");
                    return;
                }
            }

            var preset = string.IsNullOrWhiteSpace(cfg.SourcePickerPreset) ? "gostream-default" : cfg.SourcePickerPreset;
            var batch = Math.Max(1, cfg.MagnetCacheSweepBatchSize);
            var candidates = await _db.GetAvailableItemsMissingFreshMagnetCacheAsync(
                preset,
                batch,
                DateTimeOffset.UtcNow,
                serviceStopping).ConfigureAwait(false);

            foreach (var item in candidates)
            {
                serviceStopping.ThrowIfCancellationRequested();
                await _db.EnqueueMagnetCacheJobAsync(
                    item.TmdbId,
                    item.Type,
                    item.Season,
                    item.Episode,
                    preset,
                    PhantomDb.BackgroundSweepMagnetCachePriority,
                    serviceStopping).ConfigureAwait(false);
                _logger.LogInformation(
                    "Magnet-cache background sweep enqueued {Type}/{Tmdb} s{Season}e{Episode}",
                    item.Type, item.TmdbId, item.Season, item.Episode);
            }

            var anyWork = candidates.Count > 0;
            var nextDelay = TimeSpan.FromSeconds(Math.Max(1, anyWork ? cfg.MagnetCacheSweepMinIntervalSeconds : cfg.MagnetCacheSweepMaxIntervalSeconds));
            _timer?.Change(nextDelay, nextDelay);
        }
        catch (OperationCanceledException) when (serviceStopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Magnet-cache background sweep tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
