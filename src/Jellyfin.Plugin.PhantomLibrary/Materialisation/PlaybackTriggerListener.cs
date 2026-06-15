using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Stage-2.1 stub. Real playback trigger (channel-aware) rewritten in
/// Stage 4 per plan.
/// </summary>
public sealed class PlaybackTriggerListener : IHostedService
{
    private readonly ISessionManager _sessions;
    private readonly ILogger<PlaybackTriggerListener> _logger;

    public PlaybackTriggerListener(ISessionManager sessions, ILogger<PlaybackTriggerListener> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[PlaybackTrigger] stage-2.1 stub; rewritten in Stage 4");
        _ = _sessions;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
