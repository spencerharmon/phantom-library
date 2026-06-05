using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary;

/// <summary>
/// Plugin startup glue: extracts the splash + verifies the phantom stub
/// directory tree, then binds the phantom dir into the operator-configured
/// gostream-movies / gostream-shows CollectionFolders. Runs once at host
/// startup; subsequent runs are operator-triggered (config change).
/// </summary>
internal sealed class PhantomBootstrapService : IHostedService
{
    private readonly IPhantomStubManager _stubs;
    private readonly IPhantomCollectionFolderBinder _binder;
    private readonly ILogger<PhantomBootstrapService> _logger;

    public PhantomBootstrapService(
        IPhantomStubManager stubs,
        IPhantomCollectionFolderBinder binder,
        ILogger<PhantomBootstrapService> logger)
    {
        _stubs = stubs;
        _binder = binder;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Fire-and-forget: must not block host startup, and the library
        // subsystem (GetUserRootFolder) is not guaranteed ready right at
        // StartAsync — give it a short head start, then bootstrap.
        _ = Task.Run(() => RunAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task RunAsync(CancellationToken ct)
    {
        try
        {
            // Wait briefly for the library subsystem to settle. The binder
            // needs GetUserRootFolder() to return populated children.
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            await _stubs.BootstrapAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[PhantomBootstrap] stub bootstrap failed — phantom creation will be skipped until fixed.");
            // Continue to binder anyway; it is harmless if no stubs exist.
        }

        // Initial bind.
        await SafeBindAsync(ct).ConfigureAwait(false);

        // Install event-driven watchdog. ItemUpdated fires when ANY
        // BaseItem (including our gostream CollectionFolders) is
        // saved. The watchdog re-patches if the save dropped our
        // phantom path — closes the race with FolderMetadataService
        // image-provider saves that capture a stale snapshot.
        try { _binder.InstallWatchdog(); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PhantomBootstrap] watchdog install failed; falling back to periodic re-bind only");
        }

        // Periodic re-bind. Belt-and-braces: the watchdog should
        // catch race overwrites, the periodic re-bind catches
        // anything the watchdog misses (e.g. plugin restart of
        // gostream-shows binding while a save was in flight on
        // gostream-movies). No-ops cheaply once the binding is
        // correct.
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromMinutes(5), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            await SafeBindAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task SafeBindAsync(CancellationToken ct)
    {
        try
        {
            await _binder.BindAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "[PhantomBootstrap] CollectionFolder bind failed — phantoms may not be visible in browse.");
        }
    }
}
