using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Stage-2.1 stub. Legacy stub-based sweep deleted with the rest of the
/// phantom-stub architecture; rewrite per plan §6.1 lands in Stage 6.1.
/// </summary>
public sealed class EvictionSweeper : IHostedService
{
    private readonly ILogger<EvictionSweeper> _logger;

    public EvictionSweeper(ILogger<EvictionSweeper> logger)
    {
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("[Eviction] stage-2.1 stub; rewritten in Stage 6.1");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
