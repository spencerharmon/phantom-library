using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;

namespace Jellyfin.Plugin.PhantomLibrary.Clients;

/// <summary>
/// Process-wide limiter for gostream operations that can trigger torrent metadata,
/// file-list, audio-probe, or FUSE I/O.
/// </summary>
public sealed class GostreamHeavyLimiter : IDisposable
{
    private readonly Func<PluginConfiguration> _configProvider;
    private readonly object _lock = new();
    private SemaphoreSlim _semaphore;
    private int _capacity;

    public GostreamHeavyLimiter()
        : this(() => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal GostreamHeavyLimiter(Func<PluginConfiguration> configProvider)
    {
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _capacity = EffectiveCapacity();
        _semaphore = new SemaphoreSlim(_capacity, _capacity);
    }

    public async ValueTask<IDisposable> AcquireAsync(CancellationToken ct)
    {
        var semaphore = CurrentSemaphore();
        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(semaphore);
    }

    private SemaphoreSlim CurrentSemaphore()
    {
        var desired = EffectiveCapacity();
        lock (_lock)
        {
            if (desired == _capacity)
            {
                return _semaphore;
            }

            // Dashboard-time operator edits may change capacity. Replacing the
            // semaphore avoids inventing permits on an already-contended limiter;
            // existing callers release their captured semaphore normally.
            _capacity = desired;
            _semaphore = new SemaphoreSlim(desired, desired);
            return _semaphore;
        }
    }

    private int EffectiveCapacity()
        => Math.Clamp(_configProvider().GostreamHeavyConcurrency, 1, 4);

    public void Dispose()
    {
        lock (_lock)
        {
            _semaphore.Dispose();
        }
    }

    private sealed class Lease : IDisposable
    {
        private readonly SemaphoreSlim _semaphore;
        private int _disposed;

        internal Lease(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _semaphore.Release();
            }
        }
    }
}
