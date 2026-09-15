using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Scheduled;

/// <summary>
/// home-shelves-basitem-warmup: keeps Jellyfin's channel-item BaseItem cache
/// populated for the two phantom channels so the Home shelves
/// (<c>PhantomLibraryShelvesController</c>) always resolve their cards.
///
/// WHY THIS EXISTS. The shelves map each curated row member to its navigable
/// Jellyfin <c>BaseItem</c> guid and DROP any member core has not yet wrapped
/// (so <c>#/details?id=&lt;guid&gt;</c> always resolves). Jellyfin only creates
/// / refreshes those channel BaseItems lazily, when a channel FOLDER is browsed
/// (<c>IChannelManager.GetChannelItemsInternal</c>) or the built-in "Refresh
/// Channels" task runs; and it INVALIDATES them whenever the channel's
/// <c>DataVersion</c> bumps (which the availability/materialise workers do
/// frequently). Since the curated-rows redesign dropped the browsable category
/// folders and routes users to the shelves instead, nothing browses the
/// channels any more — so the BaseItem cache decays to near-empty and rails
/// collapse (whole media types vanish). Observed live: series BaseItems fell
/// 9,585 → 7 with no browse traffic, and a single root browse restored them.
///
/// This worker reproduces exactly that root browse — the same
/// <c>GetChannelItemsInternal</c> call path the <c>/Channels/{id}/Items</c> API
/// uses — on a timer and at startup, for both phantom channels. It wraps the
/// FULL root each channel emits (no <c>Limit</c>: the limit only bounds the
/// returned page, wrapping persists every enumerated item), so every shelf
/// member stays navigable. Bounded work — two channel enumerations per tick,
/// never an O(catalogue) per-user scan.
/// </summary>
public sealed class ChannelBaseItemWarmupWorker : IHostedService, IDisposable
{
    private readonly IChannelManager _channelManager;
    private readonly ILogger<ChannelBaseItemWarmupWorker> _logger;
    private readonly Func<PluginConfiguration> _configProvider;
    private Timer? _timer;
    private CancellationTokenSource? _stopping;
    private Task? _currentTick;
    private int _running;

    public ChannelBaseItemWarmupWorker(
        IChannelManager channelManager,
        ILogger<ChannelBaseItemWarmupWorker> logger)
        : this(channelManager, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal ChannelBaseItemWarmupWorker(
        IChannelManager channelManager,
        ILogger<ChannelBaseItemWarmupWorker> logger,
        Func<PluginConfiguration> configProvider)
    {
        _channelManager = channelManager ?? throw new ArgumentNullException(nameof(channelManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cfg = _configProvider();
        var startupDelay = TimeSpan.FromSeconds(Math.Max(0, cfg.ChannelWarmupStartupDelaySeconds));
        var interval = TimeSpan.FromMinutes(Math.Max(1, cfg.ChannelWarmupIntervalMinutes));
        _timer = new Timer(_ => _currentTick = TickAsync(_stopping.Token), null, startupDelay, interval);
        _logger.LogInformation(
            "Channel BaseItem warmup worker started startupDelay={StartupDelay}s interval={Interval}min enabled={Enabled}",
            startupDelay.TotalSeconds,
            interval.TotalMinutes,
            cfg.ChannelWarmupEnabled);
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
        // Re-entrancy guard: a slow enumeration must never overlap the next tick.
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_configProvider().ChannelWarmupEnabled)
            {
                return;
            }

            var wrapped = await WarmAsync(serviceStopping).ConfigureAwait(false);
            _logger.LogInformation("Channel BaseItem warmup wrapped {Wrapped} root items across phantom channels", wrapped);
        }
        catch (OperationCanceledException) when (serviceStopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Channel BaseItem warmup tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>
    /// Wrap+persist the full root of each phantom channel, exactly as the
    /// <c>/Channels/{id}/Items</c> browse does. Returns the total number of root
    /// items wrapped (summed across both channels).
    /// </summary>
    internal async Task<int> WarmAsync(CancellationToken ct)
    {
        var total = 0;
        foreach (var channelId in new[] { ChannelIds.Movies, ChannelIds.Shows })
        {
            ct.ThrowIfCancellationRequested();
            var query = new InternalItemsQuery
            {
                ChannelIds = new[] { channelId },
                ParentId = Guid.Empty,   // channel root — parentItem resolves to the channel itself
                EnableTotalRecordCount = false,
            };

            var result = await _channelManager
                .GetChannelItemsInternal(query, new Progress<double>(), ct)
                .ConfigureAwait(false);
            total += result.Items.Count;
            _logger.LogDebug("Channel {ChannelId} warmup wrapped {Count} root items", channelId, result.Items.Count);
        }

        return total;
    }
}
