using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Dto;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Querying;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Scheduled;

/// <summary>
/// Keeps Jellyfin's per-user channel-item disk cache warm so the one-time full
/// channel-folder re-sync happens in the BACKGROUND rather than on a user's first
/// browse.
///
/// Jellyfin caches a channel folder's provider result on disk keyed by the channel's
/// <c>IChannel.DataVersion</c>; on a cache miss it re-syncs the entire folder into the
/// library database on the read path. On the PostgreSQL backend that re-sync is ~13k
/// tiny queries (tens of seconds) because each is a network round-trip. The DataVersion
/// coalescer (<see cref="Channels.ChannelStateProvider"/>) already bounds how OFTEN a
/// miss can happen (to once per its window); this worker moves the miss itself off the
/// interactive path by driving the same channel listing on a timer:
///   * an initial warm shortly after startup, so the first interactive browse is warm; and
///   * a periodic warm within the coalescing window, so a DataVersion change is absorbed
///     here before a user hits it.
///
/// Warming simply calls <see cref="IChannelManager.GetChannelItems(InternalItemsQuery, CancellationToken)"/>
/// -- the exact path an interactive browse takes -- for every user (the cache is per-user)
/// and every Phantom channel. When DataVersion has not changed the call returns from the
/// warm cache (cheap); it only does the expensive re-sync when there is genuinely new
/// content, and then off the user path. It is best-effort: any failure is logged and
/// swallowed so it can never disrupt startup or an interactive request.
/// </summary>
public sealed class ChannelPrewarmWorker : IHostedService, IDisposable
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly IChannelManager _channelManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<ChannelPrewarmWorker> _logger;
    private readonly Func<PluginConfiguration> _configProvider;
    private Timer? _timer;
    private CancellationTokenSource? _stopping;
    private Task? _currentTick;
    private int _running;
    private bool _loggedFirstWarm;

    public ChannelPrewarmWorker(
        IChannelManager channelManager,
        IUserManager userManager,
        ILogger<ChannelPrewarmWorker> logger)
        : this(channelManager, userManager, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal ChannelPrewarmWorker(
        IChannelManager channelManager,
        IUserManager userManager,
        ILogger<ChannelPrewarmWorker> logger,
        Func<PluginConfiguration> configProvider)
    {
        _channelManager = channelManager ?? throw new ArgumentNullException(nameof(channelManager));
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timer = new Timer(_ => _currentTick = TickAsync(_stopping.Token), null, InitialDelay, Interval);
        _logger.LogInformation(
            "Channel prewarm worker started initialDelay={Initial}s interval={Interval}s",
            InitialDelay.TotalSeconds,
            Interval.TotalSeconds);
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
        // Skip if a previous tick is still running (a cold re-sync can outlast the interval).
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_configProvider().ChannelPrewarmEnabled || ct.IsCancellationRequested)
            {
                return;
            }

            var users = _userManager.GetUsers();
            var warmed = 0;
            var swAll = Stopwatch.StartNew();

            foreach (var user in users)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                QueryResult<Channel> channels;
                try
                {
                    channels = await _channelManager
                        .GetChannelsInternalAsync(new ChannelQuery { UserId = user.Id })
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Channel prewarm: listing channels failed for user {User}", user.Username);
                    continue;
                }

                foreach (var channel in channels.Items)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    if (!IsPhantomChannel(channel.Name))
                    {
                        continue;
                    }

                    try
                    {
                        var query = new InternalItemsQuery(user)
                        {
                            ChannelIds = new[] { channel.Id },
                            StartIndex = 0,
                            Limit = 1,
                            DtoOptions = new DtoOptions(false)
                        };

                        var sw = Stopwatch.StartNew();
                        await _channelManager.GetChannelItems(query, ct).ConfigureAwait(false);
                        sw.Stop();
                        warmed++;
                        _logger.LogDebug(
                            "Channel prewarm: warmed '{Channel}' for user {User} in {Ms}ms",
                            channel.Name,
                            user.Username,
                            sw.ElapsedMilliseconds);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Channel prewarm: warming '{Channel}' for user {User} failed", channel.Name, user.Username);
                    }
                }
            }

            if (!_loggedFirstWarm && warmed > 0)
            {
                _loggedFirstWarm = true;
                _logger.LogInformation(
                    "Channel prewarm: initial warm complete ({Warmed} channel/user pairs in {Ms}ms); interactive browses now hit the warm cache",
                    warmed,
                    swAll.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Channel prewarm tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static bool IsPhantomChannel(string? name)
        => name is not null && name.StartsWith("Phantom ", StringComparison.Ordinal);
}
