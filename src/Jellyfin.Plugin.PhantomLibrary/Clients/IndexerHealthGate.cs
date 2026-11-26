using System;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Diagnostics;
using Jellyfin.Plugin.PhantomLibrary.State;

namespace Jellyfin.Plugin.PhantomLibrary.Clients;

/// <summary>Database-backed admission, bounded waiting and recovery probing; no local rate-limit bucket.</summary>
public sealed class IndexerHealthGate
{
    private readonly IndexerHealthStore _store;
    private readonly int _intervalMs;

    public IndexerHealthGate(IndexerHealthStore store) : this(store, 1000) { }
    internal IndexerHealthGate(IndexerHealthStore store, int intervalMs)
    {
        _store = store;
        _intervalMs = intervalMs;
    }

    // Credentials, queries and colour identities must NOT split an upstream budget
    // or appear in metrics. Torrentio configuration paths share an origin budget.
    internal static string Key(string client, string baseUrl, int id = 0)
    {
        var uri = new Uri(baseUrl);
        var endpoint = uri.GetLeftPart(UriPartial.Authority).ToUpperInvariant()
            + (client == "Prowlarr" ? uri.AbsolutePath.TrimEnd('/') : string.Empty);
        return client + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(endpoint))) + ":" + id;
    }

    internal Task DisableUntilAsync(string key, DateTimeOffset until, CancellationToken ct)
        => _store.DisableUntilAsync(key, until, ct);

    internal async Task<T> RunAsync<T>(string key, string client, string indexer, bool interactive,
        Func<CancellationToken, Task<T>> request, CancellationToken ct, bool control = false, DateTimeOffset? deadline = null)
    {
        var owner = Guid.NewGuid().ToString("N");
        var waiting = Stopwatch.StartNew();
        // A small admission grace lets a newly arriving interactive request
        // preempt background work BEFORE it consumes a non-refundable token.
        if (!interactive && !control)
        {
            await Task.Delay(150, ct).ConfigureAwait(false);
        }

        while (true)
        {
            var decision = await _store.TryAcquireAsync(key, owner, interactive, control ? 15000 : _intervalMs, ct).ConfigureAwait(false);
            if (decision == "admitted")
            {
                break;
            }

            if (decision == "backoff" || (control && decision != "busy") || waiting.ElapsedMilliseconds >= 2000)
            {
                IndexerSearchMetrics.RecordHealth(client, indexer, "skip_" + decision, interactive, 0);
                throw new IndexerTransientException("Indexer admission deferred: " + decision) { HealthStatus = "skip_" + decision };
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        var sw = Stopwatch.StartNew();
        var status = "ok";
        long retry = 0;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var remainingMs = deadline is { } until ? (until - DateTimeOffset.UtcNow).TotalMilliseconds - 50 : 30000;
        bounded.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(remainingMs, 1, control ? 1000 : 30000)));
        using var monitoring = new CancellationTokenSource();
        var preempted = false;
        async Task MonitorAsync()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(200, monitoring.Token).ConfigureAwait(false);
                    if (await _store.ShouldYieldAsync(key, owner, monitoring.Token).ConfigureAwait(false))
                    {
                        preempted = true;
                        bounded.Cancel();
                        return;
                    }
                }
            }
            catch (OperationCanceledException) when (monitoring.IsCancellationRequested) { }
            catch
            {
                status = "error";
                bounded.Cancel();
                throw;
            }
        }

        var monitor = !interactive && !control ? MonitorAsync() : Task.CompletedTask;
        try
        {
            return await request(bounded.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (preempted && !ct.IsCancellationRequested)
        {
            status = "preempted";
            throw new IndexerTransientException("Background indexer request preempted by interactive demand") { HealthStatus = "skip_preempted" };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && (deadline is null || DateTimeOffset.UtcNow < deadline))
        {
            status = "cancelled";
            throw;
        }
        catch (OperationCanceledException)
        {
            status = "timeout";
            throw;
        }
        catch (IndexerTransientException ex)
        {
            status = ex.HealthStatus ?? "error";
            retry = ex.RetryAfterMs;
            throw;
        }
        catch (Exception)
        {
            status = "error";
            throw;
        }
        finally
        {
            // Caller abort must still release the fenced lease. DB failure is NOT
            // fail-open: propagate it, and let the persisted lease expire on crash.
            monitoring.Cancel();
            try
            {
                await monitor.ConfigureAwait(false);
            }
            finally
            {
                using var finish = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _store.CompleteAsync(key, owner, status, sw.ElapsedMilliseconds, retry, finish.Token, control ? 15000 : _intervalMs).ConfigureAwait(false);
                IndexerSearchMetrics.RecordHealth(client, indexer, status, interactive, sw.Elapsed.TotalMilliseconds);
            }
        }
    }

    internal static IndexerTransientException HttpFailure(HttpResponseMessage response, string client)
    {
        var after = response.Headers.RetryAfter;
        var delay = after?.Delta ?? (after?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.Zero);
        return new IndexerTransientException($"{client} returned HTTP {(int)response.StatusCode}")
        {
            HealthStatus = (int)response.StatusCode == 429 ? "429" : "error",
            RetryAfterMs = (long)Math.Max(0, delay.TotalMilliseconds),
        };
    }
}

/// <summary>Explicit background entry-point scope; captured into IndexerQuery before fan-out.</summary>
internal sealed class IndexerSearchScope : IDisposable
{
    private static readonly AsyncLocal<bool> Background = new();
    private readonly bool _previous = Background.Value;
    internal static bool IsInteractive => !Background.Value;
    internal IndexerSearchScope() => Background.Value = true;
    public void Dispose() => Background.Value = _previous;
}
