using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.State.Db;

namespace Jellyfin.Plugin.PhantomLibrary.State;

/// <summary>One durable admission row per upstream, shared by every plugin process.</summary>
public sealed class IndexerHealthStore
{
    internal const string TableSql = @"CREATE TABLE IF NOT EXISTS indexer_health (indexer_key TEXT PRIMARY KEY,
        next_at BIGINT NOT NULL DEFAULT 0,
        backoff_until BIGINT NOT NULL DEFAULT 0,
        interactive_until BIGINT NOT NULL DEFAULT 0,
        lease_until BIGINT NOT NULL DEFAULT 0,
        owner TEXT NOT NULL DEFAULT '',
        failures INTEGER NOT NULL DEFAULT 0,
        requests BIGINT NOT NULL DEFAULT 0,
        errors BIGINT NOT NULL DEFAULT 0,
        throttles BIGINT NOT NULL DEFAULT 0,
        last_latency_ms BIGINT NOT NULL DEFAULT 0);";

    private readonly Func<CancellationToken, Task<DbConnection>> _open;
    private readonly bool _postgres;

    public IndexerHealthStore(PhantomDb db) : this(db.OpenIndexerHealthConnectionAsync, db.Backend == PhantomDbBackend.Postgres) { }

    internal IndexerHealthStore(Func<CancellationToken, Task<DbConnection>> open, bool postgres)
    {
        _open = open;
        _postgres = postgres;
    }

    // The authoritative clock is the database, not either colour's wall clock.
    private string Now => _postgres
        ? "CAST(EXTRACT(EPOCH FROM clock_timestamp()) * 1000 AS BIGINT)"
        : "CAST((julianday('now') - 2440587.5) * 86400000 AS INTEGER)";

    internal async Task<string> TryAcquireAsync(string key, string owner, bool interactive, int intervalMs, CancellationToken ct)
    {
        await using var conn = await _open(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.AddWithValue("key", key);
        cmd.AddWithValue("owner", owner);
        cmd.AddWithValue("interval", intervalMs);
        cmd.CommandText = "INSERT INTO indexer_health (indexer_key) VALUES (@key) ON CONFLICT (indexer_key) DO NOTHING";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        if (interactive)
        {
            cmd.CommandText = $"UPDATE indexer_health SET interactive_until={Now}+2000 WHERE indexer_key=@key";
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Atomic compare-and-set: no read/modify/write race and no table scan.
        // An abandoned HTTP request holds at most 35s (HTTP is bounded at 30s).
        cmd.CommandText = $@"UPDATE indexer_health SET owner=@owner, lease_until={Now}+35000,
            next_at={Now}+@interval, requests=requests+1
            WHERE indexer_key=@key AND backoff_until<={Now} AND next_at<={Now}
              AND lease_until<={Now} {(interactive ? "" : $"AND interactive_until<={Now}")}
            RETURNING owner";
        if (await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null)
        {
            return "admitted";
        }

        cmd.CommandText = $@"SELECT CASE WHEN backoff_until>{Now} THEN 'backoff'
            WHEN lease_until>{Now} THEN 'busy'
            WHEN interactive_until>{Now} THEN 'priority' ELSE 'budget' END
            FROM indexer_health WHERE indexer_key=@key";
        return (string)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    internal async Task CompleteAsync(string key, string owner, string status, long latencyMs, long retryAfterMs, CancellationToken ct, int intervalMs = 1000)
    {
        await using var conn = await _open(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.AddWithValue("key", key);
        cmd.AddWithValue("owner", owner);
        cmd.AddWithValue("latency", latencyMs);
        cmd.AddWithValue("retry", Math.Max(0, retryAfterMs));
        cmd.AddWithValue("interval", intervalMs);
        // A stale lease holder may never release/reset its successor. Cancellation
        // does not refund a request already sent; nor does it invent an upstream error.
        // Cool down from completion too: admission and the actual HTTP send may
        // be separated by scheduling/transport delay, especially across processes.
        var failed = status is not ("ok" or "cancelled" or "preempted");
        var delay = _postgres ? "LEAST(300000, 1000 * POWER(2, LEAST(failures, 9)))::BIGINT"
            : "MIN(300000, 1000 * (1 << MIN(failures, 9)))";
        var backoff = $"{Now} + CASE WHEN @retry>{delay} THEN @retry ELSE {delay} END";
        cmd.CommandText = $@"UPDATE indexer_health SET owner='', lease_until=0, last_latency_ms=@latency,
            next_at=CASE WHEN next_at>{Now}+@interval THEN next_at ELSE {Now}+@interval END,
            failures={(failed ? "CASE WHEN failures<20 THEN failures+1 ELSE 20 END" : status == "ok" ? "0" : "failures")},
            errors=errors+{(failed ? 1 : 0)}, throttles=throttles+{(status == "429" ? 1 : 0)}
            {(failed ? $", backoff_until=CASE WHEN backoff_until>{backoff} THEN backoff_until ELSE {backoff} END" : "")}
            WHERE indexer_key=@key AND owner=@owner";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal async Task<bool> ShouldYieldAsync(string key, string owner, CancellationToken ct)
    {
        await using var conn = await _open(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.AddWithValue("key", key);
        cmd.AddWithValue("owner", owner);
        cmd.CommandText = $"SELECT 1 FROM indexer_health WHERE indexer_key=@key AND owner=@owner AND interactive_until>{Now}";
        return await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    internal async Task DisableUntilAsync(string key, DateTimeOffset until, CancellationToken ct)
    {
        await using var conn = await _open(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.AddWithValue("key", key);
        cmd.AddWithValue("until", until.ToUnixTimeMilliseconds());
        cmd.CommandText = @"INSERT INTO indexer_health (indexer_key, backoff_until) VALUES (@key, @until)
            ON CONFLICT (indexer_key) DO UPDATE SET backoff_until=CASE
            WHEN indexer_health.backoff_until>@until THEN indexer_health.backoff_until ELSE @until END";
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
