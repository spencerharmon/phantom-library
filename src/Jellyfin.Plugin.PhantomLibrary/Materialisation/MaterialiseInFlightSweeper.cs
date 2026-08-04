using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Startup sweep of stale <c>materialise_in_flight</c> rows. A row in
/// that table normally lives for the duration of a single materialise
/// call; if the process was killed mid-call, the row leaks. Without a
/// sweeper the next attempt to materialise the same item would short-
/// circuit at the in-flight idempotency check forever.
///
/// Runs once at startup after a 15s grace delay (so the DI container
/// and ChannelManager have a chance to finish their own startup
/// work before we touch the DB). The age threshold is
/// <see cref="PluginConfiguration.MaterialiseInFlightStaleMinutes"/>;
///
/// Owner-aware (p4-phantomdb-multiwriter-safety-fixes,
/// docs/tasks/p4-phantomdb-multiwriter-audit.md): with N replicas
/// potentially sharing one <c>phantom.db</c>, a row younger than the
/// stale threshold can belong to a DIFFERENT, still-live replica's
/// in-flight materialise, not only to "an actively-running materialise
/// on this very process" as originally assumed. <see cref="PhantomDb"/>
/// stamps every claim with its own <see cref="PhantomDb.HostId"/>, so
/// this sweeper purges a row it OWNS at the normal (short) threshold,
/// but a row owned by ANY OTHER host (or a legacy NULL owner) only past
/// a much longer hard crash-recovery TTL
/// (<see cref="PluginConfiguration.MaterialiseInFlightForeignOwnerHardTtlMinutes"/>)
/// — so a live sibling replica's fresh lock is never silently stolen.
///
/// Plan §4.2.
/// </summary>
public sealed class MaterialiseInFlightSweeper : IHostedService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(15);

    private readonly PhantomDb _db;
    private readonly ILogger<MaterialiseInFlightSweeper> _logger;
    private readonly Func<PluginConfiguration> _configProvider;
    private CancellationTokenSource? _cts;

    public MaterialiseInFlightSweeper(
        PhantomDb db,
        ILogger<MaterialiseInFlightSweeper> logger)
        : this(db, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal MaterialiseInFlightSweeper(
        PhantomDb db,
        ILogger<MaterialiseInFlightSweeper> logger,
        Func<PluginConfiguration> configProvider)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _cts.Token;
        _ = Task.Run(() => RunOnceAsync(ct), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        return Task.CompletedTask;
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(StartupDelay, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var cfg = _configProvider();
            var threshold = TimeSpan.FromMinutes(Math.Max(1, cfg.MaterialiseInFlightStaleMinutes));
            var foreignThreshold = TimeSpan.FromMinutes(Math.Max(1, cfg.MaterialiseInFlightForeignOwnerHardTtlMinutes));
            var purged = await _db.PurgeStaleMaterialiseInFlightAsync(threshold, ct, foreignThreshold).ConfigureAwait(false);
            if (purged > 0)
            {
                _logger.LogInformation(
                    "Purged {N} stale materialise_in_flight rows on startup (own-host older than {Min}m; foreign/legacy-owner older than {ForeignMin}m)",
                    purged,
                    cfg.MaterialiseInFlightStaleMinutes,
                    cfg.MaterialiseInFlightForeignOwnerHardTtlMinutes);
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MaterialiseInFlightSweeper startup sweep failed");
        }
    }
}
