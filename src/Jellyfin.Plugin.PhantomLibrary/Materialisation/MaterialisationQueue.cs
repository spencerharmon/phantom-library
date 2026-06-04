using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

public interface IMaterialisationQueue
{
    event EventHandler<MaterialisationLifecycleEvent>? LifecycleChanged;
    void EnqueueUser(Guid jellyfinItemId, MaterialiseTrigger trigger);
    void EnqueueEager(Guid jellyfinItemId);
    int PendingUserCount { get; }
    int PendingEagerCount { get; }
}

/// <summary>
/// Two-lane bounded channel queue. User-triggered work is drained
/// preferentially over eager pre-resolve work; <see cref="PluginConfiguration.MaterialisationConcurrencyGlobal"/>
/// workers consume both lanes.
/// </summary>
public sealed class MaterialisationQueue : IMaterialisationQueue, IHostedService, IDisposable
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MaterialisationQueue> _logger;
    private readonly Func<PluginConfiguration> _configProvider;

    private readonly Channel<QueueItem> _userLane;
    private readonly Channel<QueueItem> _eagerLane;
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();

    // Per-indexer concurrency caps. Keyed by indexer name. Populated lazily.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _indexerLimits = new();

    private CancellationTokenSource? _cts;
    private Task[]? _workers;

    public MaterialisationQueue(IServiceProvider services, ILogger<MaterialisationQueue> logger)
        : this(services, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    public MaterialisationQueue(
        IServiceProvider services,
        ILogger<MaterialisationQueue> logger,
        Func<PluginConfiguration> configProvider)
    {
        _services = services;
        _logger = logger;
        _configProvider = configProvider;

        var cfg = _configProvider();
        var userCap = Math.Max(8, cfg.MaterialisationConcurrencyGlobal * 4);
        var eagerCap = Math.Max(8, cfg.EagerResolveMaxConcurrent * 8);

        _userLane = Channel.CreateBounded<QueueItem>(new BoundedChannelOptions(userCap)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
        });
        _eagerLane = Channel.CreateBounded<QueueItem>(new BoundedChannelOptions(eagerCap)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    public event EventHandler<MaterialisationLifecycleEvent>? LifecycleChanged;

    public int PendingUserCount => _userLane.Reader.Count;
    public int PendingEagerCount => _eagerLane.Reader.Count;

    private void FireQueued(Guid id)
    {
        try
        {
            LifecycleChanged?.Invoke(this, new MaterialisationLifecycleEvent(
                id, MaterialisationLifecyclePhase.Queued, null));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "LifecycleChanged handler threw for {Id} (Queued)", id);
        }
    }

    public void EnqueueUser(Guid id, MaterialiseTrigger trigger)
    {
        if (id == Guid.Empty) return;
        if (!_inFlight.TryAdd(id, 0))
        {
            _logger.LogDebug("Enqueue user {Id}: deduped (already in flight)", id);
            return;
        }

        if (!_userLane.Writer.TryWrite(new QueueItem(id, trigger)))
        {
            _logger.LogWarning("User lane full; dropping enqueue for {Id}", id);
            _inFlight.TryRemove(id, out _);
            return;
        }

        FireQueued(id);
    }

    public void EnqueueEager(Guid id)
    {
        if (id == Guid.Empty) return;
        if (!_inFlight.TryAdd(id, 0))
        {
            return;
        }

        if (!_eagerLane.Writer.TryWrite(new QueueItem(id, MaterialiseTrigger.PreResolve)))
        {
            _logger.LogDebug("Eager lane full; dropping enqueue for {Id}", id);
            _inFlight.TryRemove(id, out _);
            return;
        }

        FireQueued(id);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var cfg = _configProvider();
        var n = Math.Max(1, cfg.MaterialisationConcurrencyGlobal);
        _cts = new CancellationTokenSource();
        _workers = new Task[n];
        for (var i = 0; i < n; i++)
        {
            _workers[i] = Task.Run(() => WorkerLoopAsync(_cts.Token));
        }

        _logger.LogInformation("MaterialisationQueue started with {N} workers", n);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _userLane.Writer.TryComplete();
        _eagerLane.Writer.TryComplete();
        _cts?.Cancel();
        if (_workers is not null)
        {
            var drainDeadline = Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
            var workersDone = Task.WhenAll(_workers);
            await Task.WhenAny(workersDone, drainDeadline).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _cts?.Dispose();
        foreach (var s in _indexerLimits.Values)
        {
            s.Dispose();
        }
    }

    private async Task WorkerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            QueueItem item;
            try
            {
                item = await DequeueAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ChannelClosedException)
            {
                return;
            }

            try
            {
                using var scope = _services.CreateScope();
                var materialiser = scope.ServiceProvider.GetRequiredService<IMaterialiser>();
                await materialiser.MaterialiseAsync(item.Id, item.Trigger, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Materialise worker errored for {Id}", item.Id);
            }
            finally
            {
                _inFlight.TryRemove(item.Id, out _);
            }
        }
    }

    private async Task<QueueItem> DequeueAsync(CancellationToken ct)
    {
        // Prefer user lane; if empty, take eager. We block on whichever has
        // data using WaitToReadAsync.
        while (true)
        {
            if (_userLane.Reader.TryRead(out var u))
            {
                return u;
            }

            if (_eagerLane.Reader.TryRead(out var e))
            {
                return e;
            }

            var userWait = _userLane.Reader.WaitToReadAsync(ct).AsTask();
            var eagerWait = _eagerLane.Reader.WaitToReadAsync(ct).AsTask();
            var winner = await Task.WhenAny(userWait, eagerWait).ConfigureAwait(false);
            // unwrap so we surface cancellation/closure exceptions
            await winner.ConfigureAwait(false);
        }
    }

    internal SemaphoreSlim GetIndexerLimit(string indexerName)
    {
        var cap = Math.Max(1, _configProvider().MaterialisationConcurrencyPerIndexer);
        return _indexerLimits.GetOrAdd(indexerName, _ => new SemaphoreSlim(cap, cap));
    }

    private readonly record struct QueueItem(Guid Id, MaterialiseTrigger Trigger);
}
