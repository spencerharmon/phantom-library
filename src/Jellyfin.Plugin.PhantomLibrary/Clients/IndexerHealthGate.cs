using System;
using System.Data.Common;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Diagnostics;
using Jellyfin.Plugin.PhantomLibrary.State;

namespace Jellyfin.Plugin.PhantomLibrary.Clients;

/// <summary>
/// Shared, per-upstream admission. No process-local token bucket: every request,
/// including each Prowlarr subquery, spends the same database-backed budget.
/// Priority preempts WAITING background work, never refunds an already-sent request.
/// </summary>
public sealed class IndexerHealthGate(PhantomDb db)
{
    internal const int QueueBudgetMs = 3000;

    internal static string Key(string client, string baseUrl, int id = 0)
    {
        var uri = new Uri(baseUrl);
        // Torrentio filter/config paths share one upstream quota. Prowlarr IDs
        // are scoped to the gateway (including a reverse-proxy base path).
        var scope = uri.GetLeftPart(UriPartial.Authority).ToUpperInvariant()
            + (client == "Prowlarr" ? uri.AbsolutePath.TrimEnd('/') : string.Empty);
        return client + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))) + ":" + id;
    }

    internal Task DisableAsync(string baseUrl, int id, DateTimeOffset until, CancellationToken ct)
        => db.DisableIndexerUntilAsync(Key("Prowlarr", baseUrl, id), until, ct);

    internal async Task<T> ExecuteAsync<T>(string key, string client, string indexer, IndexerPriority priority,
        Func<CancellationToken, Task<T>> action, CancellationToken ct, CancellationToken? searchCancellation = null)
    {
        try
        {
            return await ExecuteCoreAsync(key, client, indexer, priority, action, ct, searchCancellation ?? ct).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            // A DB outage must not bypass the shared budget or become definitive unavailable.
            throw new IndexerTransientException("Indexer health database unavailable", ex);
        }
    }

    private async Task<T> ExecuteCoreAsync<T>(string key, string client, string indexer, IndexerPriority priority,
        Func<CancellationToken, Task<T>> action, CancellationToken ct, CancellationToken searchCancellation)
    {
        var interactive = priority == IndexerPriority.Interactive;
        await db.RegisterIndexerAsync(key, interactive, QueueBudgetMs + 250, ct).ConfigureAwait(false);
        var queued = Stopwatch.StartNew();
        var owner = Guid.NewGuid().ToString("N");
        // Give an arriving interactive request a chance to reserve the next slot.
        if (!interactive)
        {
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        string admission;
        while ((admission = await db.TryAcquireIndexerAsync(key, owner, interactive,
            client == "Torrentio" ? 2000 : 1000, ct).ConfigureAwait(false)) != "admitted")
        {
            if (admission == "skipped_backoff" || queued.ElapsedMilliseconds >= QueueBudgetMs)
            {
                await db.SkipIndexerAsync(key, admission, ct).ConfigureAwait(false);
                IndexerSearchMetrics.RecordHealth(client, indexer, admission, priority.ToString(), 0, 0);
                throw new IndexerTransientException($"{indexer}: {admission}") { HealthStatus = admission };
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
        }

        var watch = Stopwatch.StartNew();
        var status = "ok";
        long retryMs = 0;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Must end before the 60-second crash-recovery lease can be reassigned.
        deadline.CancelAfter(TimeSpan.FromSeconds(55));
        try
        {
            return await action(deadline.Token).ConfigureAwait(false);
        }
        catch (IndexerTransientException ex)
        {
            status = ex.HttpStatus == 429 ? "429" : "error";
            retryMs = (long)Math.Max(0, ex.RetryAfter.TotalMilliseconds);
            throw;
        }
        catch (IndexerAuthException)
        {
            status = "auth";
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            status = searchCancellation.IsCancellationRequested ? "cancelled" : "timeout";
            throw;
        }
        catch (OperationCanceledException ex)
        {
            status = "timeout";
            throw new IndexerTransientException("Indexer request deadline exceeded", ex);
        }
        catch (Exception)
        {
            status = "error";
            throw;
        }
        finally
        {
            // Cancellation must release this owner's lease, not leave the other
            // colour locked out. Bounded independent cleanup; errors are not hidden.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await db.CompleteIndexerAsync(key, owner, status, watch.ElapsedMilliseconds, retryMs, cleanup.Token).ConfigureAwait(false);
            IndexerSearchMetrics.RecordHealth(client, indexer, status, priority.ToString(), watch.Elapsed.TotalMilliseconds, retryMs);
        }
    }

    internal static TimeSpan RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        return header?.Delta ?? (header?.Date is { } date ? date - DateTimeOffset.UtcNow : TimeSpan.Zero);
    }
}

public enum IndexerPriority
{
    Interactive,
    Background,
}
