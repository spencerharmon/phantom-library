using System;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Diagnostics;

internal sealed class PerfScope : IDisposable
{
    private readonly ILogger _logger;
    private readonly string _operation;
    private readonly long _start;
    private readonly long _allocStart;
    private readonly Func<string>? _summary;
    private bool _disposed;

    public PerfScope(ILogger logger, string operation, Func<string>? summary = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _operation = string.IsNullOrWhiteSpace(operation) ? throw new ArgumentException("operation required", nameof(operation)) : operation;
        _summary = summary;
        _start = Stopwatch.GetTimestamp();
        _allocStart = GC.GetTotalAllocatedBytes(precise: false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var elapsed = Stopwatch.GetElapsedTime(_start);
        var allocated = GC.GetTotalAllocatedBytes(precise: false) - _allocStart;
        var summary = _summary?.Invoke();
        if (string.IsNullOrWhiteSpace(summary))
        {
            _logger.LogInformation("Perf {Operation} elapsedMs={ElapsedMs} allocatedBytes={AllocatedBytes}", _operation, elapsed.TotalMilliseconds, allocated);
        }
        else
        {
            _logger.LogInformation("Perf {Operation} elapsedMs={ElapsedMs} allocatedBytes={AllocatedBytes} {Summary}", _operation, elapsed.TotalMilliseconds, allocated, summary);
        }
    }
}
