using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Stage-2.1 stub. Real listener (channel-aware favourite + playback
/// hand-off) rewritten in Stage 4 per plan.
/// </summary>
public sealed class UserDataSavedListener : IHostedService
{
    private readonly IUserDataManager _userData;
    private readonly ILogger<UserDataSavedListener> _logger;

    public UserDataSavedListener(IUserDataManager userData, ILogger<UserDataSavedListener> logger)
    {
        _userData = userData;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[UserDataSavedListener] stage-2.1 stub; rewritten in Stage 4");
        _ = _userData;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
