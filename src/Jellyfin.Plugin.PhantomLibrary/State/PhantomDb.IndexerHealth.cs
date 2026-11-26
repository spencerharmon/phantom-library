using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.State.Db;

namespace Jellyfin.Plugin.PhantomLibrary.State;

public sealed partial class PhantomDb
{
    // Shared by the fresh schema and the v24 -> v25 expand. No catalogue access.
    internal const string IndexerHealthTableSql = """
        CREATE TABLE IF NOT EXISTS indexer_health (indexer_key TEXT NOT NULL PRIMARY KEY,
            next_at BIGINT NOT NULL DEFAULT 0,
            backoff_until BIGINT NOT NULL DEFAULT 0,
            interactive_until BIGINT NOT NULL DEFAULT 0,
            lease_until BIGINT NOT NULL DEFAULT 0,
            owner TEXT NOT NULL DEFAULT '',
            failures INTEGER NOT NULL DEFAULT 0,
            requests BIGINT NOT NULL DEFAULT 0,
            errors BIGINT NOT NULL DEFAULT 0,
            rate_limits BIGINT NOT NULL DEFAULT 0,
            skipped BIGINT NOT NULL DEFAULT 0,
            latency_ms BIGINT NOT NULL DEFAULT 0,
            last_status TEXT NOT NULL DEFAULT '');
        """;

    // Use the DATABASE clock: two colours need not have synchronized host clocks.
    private string IndexerNowSql => Backend == PhantomDbBackend.Postgres
        ? "CAST(EXTRACT(EPOCH FROM clock_timestamp()) * 1000 AS BIGINT)"
        : "CAST((julianday('now') - 2440587.5) * 86400000 AS BIGINT)";

    internal async Task RegisterIndexerAsync(string key, bool interactive, int reservationMs, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            INSERT INTO indexer_health (indexer_key, interactive_until) VALUES (@key, @until)
            ON CONFLICT (indexer_key) DO UPDATE SET interactive_until =
                CASE WHEN @interactive = 1 THEN {IndexerNowSql} + @reservation ELSE indexer_health.interactive_until END;
            """;
        // The initial value also uses the DB clock, not a caller timestamp.
        cmd.CommandText = cmd.CommandText.Replace("@until", interactive ? $"{IndexerNowSql} + @reservation" : "0", StringComparison.Ordinal);
        cmd.AddWithValue("@key", key);
        cmd.AddWithValue("@interactive", interactive ? 1 : 0);
        cmd.AddWithValue("@reservation", reservationMs);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal async Task<string> TryAcquireIndexerAsync(string key, string owner, bool interactive, int intervalMs, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        // Atomic conditional update: rechecked under the per-key row lock in PG.
        // One in-flight request also makes recovery a single shared half-open probe.
        cmd.CommandText = $"""
            UPDATE indexer_health SET owner = @owner, lease_until = {IndexerNowSql} + 60000,
                next_at = {IndexerNowSql} + @interval, requests = requests + 1
            WHERE indexer_key = @key AND backoff_until <= {IndexerNowSql}
                AND next_at <= {IndexerNowSql} AND lease_until <= {IndexerNowSql}
                AND (@interactive = 1 OR interactive_until <= {IndexerNowSql});
            """;
        cmd.AddWithValue("@owner", owner);
        cmd.AddWithValue("@interval", intervalMs);
        cmd.AddWithValue("@key", key);
        cmd.AddWithValue("@interactive", interactive ? 1 : 0);
        if (await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1)
        {
            return "admitted";
        }

        cmd.CommandText = $"""
            SELECT CASE WHEN backoff_until > {IndexerNowSql} THEN 'skipped_backoff'
                WHEN @interactive = 0 AND interactive_until > {IndexerNowSql} THEN 'skipped_preempted'
                ELSE 'skipped_budget' END FROM indexer_health WHERE indexer_key = @key;
            """;
        return (string)(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false))!;
    }

    internal async Task CompleteIndexerAsync(string key, string owner, string status, long latencyMs, long retryMs, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        // Capped exponential failure delay (5,10,20,40,80,160,300 seconds).
        // Explicit upstream Retry-After is a floor and is NOT shortened by the cap.
        var delay = "CASE failures WHEN 0 THEN 5000 WHEN 1 THEN 10000 WHEN 2 THEN 20000 WHEN 3 THEN 40000 WHEN 4 THEN 80000 WHEN 5 THEN 160000 ELSE 300000 END";
        var until = $"{IndexerNowSql} + CASE WHEN @retry > ({delay}) THEN @retry ELSE ({delay}) END";
        cmd.CommandText = $"""
            UPDATE indexer_health SET owner = '', lease_until = 0, last_status = @status, latency_ms = @latency,
                failures = CASE WHEN @failed = 1 THEN CASE WHEN failures < 30 THEN failures + 1 ELSE 30 END
                    WHEN @status = 'ok' THEN 0 ELSE failures END,
                errors = errors + @failed, rate_limits = rate_limits + @limited,
                backoff_until = CASE WHEN @failed = 1 AND backoff_until < ({until}) THEN ({until}) ELSE backoff_until END
            WHERE indexer_key = @key AND owner = @owner;
            """;
        cmd.AddWithValue("@status", status);
        cmd.AddWithValue("@latency", latencyMs);
        cmd.AddWithValue("@failed", status is "error" or "429" or "timeout" or "auth" ? 1 : 0);
        cmd.AddWithValue("@limited", status == "429" ? 1 : 0);
        cmd.AddWithValue("@retry", retryMs);
        cmd.AddWithValue("@key", key);
        cmd.AddWithValue("@owner", owner);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal async Task DisableIndexerUntilAsync(string key, DateTimeOffset until, CancellationToken ct)
    {
        await RegisterIndexerAsync(key, false, 0, ct).ConfigureAwait(false);
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE indexer_health SET backoff_until = CASE WHEN backoff_until < @until THEN @until ELSE backoff_until END
            WHERE indexer_key = @key;
            """;
        cmd.AddWithValue("@key", key);
        cmd.AddWithValue("@until", until.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal async Task SkipIndexerAsync(string key, string status, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE indexer_health SET skipped = skipped + 1, last_status = @status WHERE indexer_key = @key;";
        cmd.AddWithValue("@key", key);
        cmd.AddWithValue("@status", status);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
