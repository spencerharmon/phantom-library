using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Scheduled;

/// <summary>
/// ROI Priority 9 fix (ttfb-magnet-cache-drain-worker): the missing CONSUMER
/// of the <c>magnet_cache_jobs</c> queue. Both producers already exist — the
/// opportunistic enqueue in <c>PhantomSourceManager.EnqueueOpportunisticMagnetCacheJobAsync</c>
/// (priority=100, on user interaction) and <see cref="MagnetCacheBackgroundSweepWorker"/>
/// (priority=0, background) — but nothing ever drained the queue, so every
/// enqueued row (including a high-priority opportunistic row created the
/// instant a user opens an item's info panel) sat pending forever and the
/// "opportunistic source-location on user interaction" ROI direction had ZERO
/// effect on materialise TTFB.
///
/// This worker mirrors <see cref="MagnetCacheBackgroundSweepWorker"/>'s and
/// <see cref="AvailabilityProbeWorker"/>'s timer-driven, user-yielding pattern,
/// but instead of ENQUEUEing it CLAIMS + processes: each tick it loops
/// <see cref="MagnetCacheBuilder.ProcessNextAsync"/> (bounded by
/// <see cref="PluginConfiguration.MagnetCacheDrainMaxJobsPerTick"/>), which
/// claims the single highest-priority pending job atomically
/// (<see cref="PhantomDb.ClaimNextMagnetCacheJobAsync"/>, priority-first) and
/// runs the full Prowlarr fan-out for it, writing the result into
/// <c>source_candidates</c>. Movie AND episode tuples both flow through the
/// identical claim → fan-out → write path in <c>ProcessNextAsync</c>.
///
/// Contract preservation:
///  - P6 cache-first: this worker only WRITES <c>source_candidates</c>; a cache
///    HIT at materialise time still takes zero probes. It never touches the
///    materialise read path.
///  - "Never compete with a live user session for the DB write lock": exactly
///    like the sweep worker, it yields (backs off at the slow interval without
///    claiming anything) while a user-activity marker was touched within
///    <see cref="PluginConfiguration.AvailabilityYieldToUserSeconds"/>.
///  - Claim/lease safety: <c>ClaimNextMagnetCacheJobAsync</c>'s atomic claim
///    already guards against this worker and a synchronous materialise
///    cache-miss build racing for the same item tuple (worst case: one
///    redundant wasted probe).
/// </summary>
public sealed class MagnetCacheDrainWorker : IHostedService, IDisposable
{
    private readonly MagnetCacheBuilder _builder;
    private readonly PhantomDb _db;
    private readonly ILogger<MagnetCacheDrainWorker> _logger;
    private readonly Func<PluginConfiguration> _configProvider;
    private Timer? _timer;
    private CancellationTokenSource? _stopping;
    private Task? _currentTick;
    private int _running;

    public MagnetCacheDrainWorker(
        MagnetCacheBuilder builder,
        PhantomDb db,
        ILogger<MagnetCacheDrainWorker> logger)
        : this(builder, db, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal MagnetCacheDrainWorker(
        MagnetCacheBuilder builder,
        PhantomDb db,
        ILogger<MagnetCacheDrainWorker> logger,
        Func<PluginConfiguration> configProvider)
    {
        _builder = builder ?? throw new ArgumentNullException(nameof(builder));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cfg = _configProvider();
        var interval = TimeSpan.FromSeconds(Math.Max(1, cfg.MagnetCacheDrainMinIntervalSeconds));
        _timer = new Timer(_ => _currentTick = TickAsync(_stopping.Token), null, interval, interval);
        _logger.LogInformation(
            "Magnet-cache drain worker started interval={Interval}s enabled={Enabled} maxPerTick={MaxPerTick}",
            interval.TotalSeconds,
            cfg.MagnetCacheDrainEnabled,
            cfg.MagnetCacheDrainMaxJobsPerTick);
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

    /// <summary>
    /// Drain up to <c>MagnetCacheDrainMaxJobsPerTick</c> pending jobs in
    /// priority order. Exposed internal so the regression test can drive a
    /// single deterministic tick without waiting on the timer. Returns the
    /// number of jobs actually claimed and processed this tick.
    /// </summary>
    internal async Task<int> DrainOnceAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var maxJobs = Math.Max(1, cfg.MagnetCacheDrainMaxJobsPerTick);
        var processed = 0;
        for (var i = 0; i < maxJobs; i++)
        {
            ct.ThrowIfCancellationRequested();
            var result = await _builder.ProcessNextAsync(ct).ConfigureAwait(false);
            if (result is null)
            {
                break; // queue empty
            }

            processed++;
            _logger.LogInformation(
                "Magnet-cache drain processed {Type}/{Tmdb} s{Season}e{Episode} -> {Count} candidates{Error}",
                result.Job.Type,
                result.Job.TmdbId,
                result.Job.Season,
                result.Job.Episode,
                result.CandidateCount,
                result.Error is null ? string.Empty : $" (error={result.Error})");
        }

        return processed;
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
            if (!cfg.MagnetCacheDrainEnabled)
            {
                return;
            }

            // Yield to user-initiated work: back off at the slow interval
            // (never claiming) while a user-driven action recently touched
            // the shared activity marker, mirroring the sweep/probe workers.
            var yieldWindow = Math.Max(0, cfg.AvailabilityYieldToUserSeconds);
            if (yieldWindow > 0)
            {
                var lastActivity = await _db.GetUserActivityAtAsync(serviceStopping).ConfigureAwait(false);
                if (lastActivity is { } activityAt
                    && DateTimeOffset.UtcNow - activityAt < TimeSpan.FromSeconds(yieldWindow))
                {
                    var backoff = TimeSpan.FromSeconds(Math.Max(1, cfg.MagnetCacheDrainMaxIntervalSeconds));
                    _timer?.Change(backoff, backoff);
                    _logger.LogDebug("Magnet-cache drain yielding to recent user activity");
                    return;
                }
            }

            var processed = await DrainOnceAsync(cfg, serviceStopping).ConfigureAwait(false);

            // If we hit the per-tick cap the queue likely still has work, so
            // stay fast; if we drained fewer than the cap the queue is empty,
            // so slow down until the next enqueue.
            var maxJobs = Math.Max(1, cfg.MagnetCacheDrainMaxJobsPerTick);
            var moreLikely = processed >= maxJobs;
            var nextDelay = TimeSpan.FromSeconds(Math.Max(1, moreLikely
                ? cfg.MagnetCacheDrainMinIntervalSeconds
                : cfg.MagnetCacheDrainMaxIntervalSeconds));
            _timer?.Change(nextDelay, nextDelay);
        }
        catch (OperationCanceledException) when (serviceStopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Magnet-cache drain tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }
}
