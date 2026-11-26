using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.State.Db;
using Npgsql;

namespace Jellyfin.Plugin.PhantomLibrary.State;

public sealed partial class PhantomDb
{
    // A cursor includes the index's due key, not just a series id. Filtering a
    // tmdb-only cursor after a due range scans the entire due prefix on wrap.
    internal sealed record AvailabilityCursor(long Due, int Tmdb, int Season, int Episode);
    private readonly ConcurrentDictionary<int, AvailabilityCursor> _episodeClaimCursors = new();
    internal const int MaximumAvailabilityClaimBatch = 32;

    internal static string AvailabilityClaimSql(bool afterCursor) => $@"
WITH due AS MATERIALIZED (
    SELECT tmdb_id,type,season,episode,claim_due
    FROM availability_items
    WHERE type=@type AND claim_bucket=@bucket AND claim_due < @now
      {(afterCursor ? "AND (claim_due,tmdb_id,season,episode) > (@cursorDue,@cursorTmdb,@cursorSeason,@cursorEpisode)" : string.Empty)}
    ORDER BY claim_due,tmdb_id,season,episode
    LIMIT @batch FOR UPDATE SKIP LOCKED
)
UPDATE availability_items a
SET lease_owner=@owner, lease_until=@until,
    probe_generation=a.probe_generation+1, attempt_count=a.attempt_count+1
FROM due d
WHERE a.tmdb_id=d.tmdb_id AND a.type=d.type AND a.season=d.season AND a.episode=d.episode
RETURNING a.tmdb_id,a.type,a.season,a.episode,a.status,a.checked_at,a.next_check_at,
    a.candidate_magnet,a.candidate_info_hash,{ResolvedSizeSql("a.candidate_size", "a.candidate_size_bytes", "a.candidate_")},a.candidate_seeders,
    a.candidate_indexer,a.candidate_source,a.probe_generation,a.lease_owner,a.attempt_count,a.negative_streak,d.claim_due;";

    /// <summary>
    /// Bounded atomic leases, with server row locks rather than the process write
    /// semaphore. PostgreSQL always uses exact type/bucket predicates. SQLite
    /// retains its single-writer transaction path. Leases start together; callers
    /// must not queue a serial network backlog behind a batch's lease deadline.
    /// </summary>
    public async Task<IReadOnlyList<AvailabilityItemRow>> ClaimDueAvailabilityBatchAsync(
        string owner, TimeSpan leaseDuration, DateTimeOffset now, string policyHash,
        int batchSize, CancellationToken ct, string? preferredType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyHash);
        if (batchSize is < 1 or > MaximumAvailabilityClaimBatch) throw new ArgumentOutOfRangeException(nameof(batchSize));
        ArgumentOutOfRangeException.ThrowIfLessThan(leaseDuration, TimeSpan.FromSeconds(1));
        if (preferredType is not (null or "movie" or "episode")) throw new ArgumentException("Expected movie or episode", nameof(preferredType));
        var result = new List<AvailabilityItemRow>(batchSize);
        if (Backend != PhantomDbBackend.Postgres)
        {
            for (var i = 0; i < batchSize; i++)
            {
                var row = await ClaimDueAvailabilitySqliteAsync(owner, leaseDuration, now, policyHash, preferredType, ct).ConfigureAwait(false);
                if (row is null) break;
                result.Add(row);
            }
            return result;
        }

        await using var connection = (NpgsqlConnection)await OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        var types = preferredType is null ? new[] { "episode", "movie" } : new[] { preferredType };
        for (var bucket = 0; bucket < AvailabilitySchedule.BucketCount && result.Count < batchSize; bucket++)
        {
            foreach (var type in types)
            {
                if (result.Count == batchSize) break;
                AvailabilityCursor? cursor = null;
                if (type == "episode") _episodeClaimCursors.TryGetValue(bucket, out cursor);
                var claimed = await ClaimAvailabilitySliceAsync(connection, owner, leaseDuration, now, type, bucket, batchSize - result.Count, cursor, ct).ConfigureAwait(false);
                if (claimed.Count == 0 && cursor is not null)
                    claimed = await ClaimAvailabilitySliceAsync(connection, owner, leaseDuration, now, type, bucket, batchSize - result.Count, null, ct).ConfigureAwait(false);
                result.AddRange(claimed);
            }
        }
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return result;
    }

    internal static NpgsqlCommand CreateAvailabilityClaimCommand(NpgsqlConnection connection, string owner, TimeSpan leaseDuration,
        DateTimeOffset now, string type, int bucket, int batchSize, AvailabilityCursor? cursor)
    {
        var cmd = new NpgsqlCommand(AvailabilityClaimSql(cursor is not null), connection);
        cmd.Parameters.AddWithValue("owner", owner);
        cmd.Parameters.AddWithValue("until", now.Add(leaseDuration).ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("now", now.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("type", type);
        cmd.Parameters.AddWithValue("bucket", (long)bucket);
        cmd.Parameters.AddWithValue("batch", batchSize);
        if (cursor is not null)
        {
            cmd.Parameters.AddWithValue("cursorDue", cursor.Due);
            cmd.Parameters.AddWithValue("cursorTmdb", cursor.Tmdb);
            cmd.Parameters.AddWithValue("cursorSeason", cursor.Season);
            cmd.Parameters.AddWithValue("cursorEpisode", cursor.Episode);
        }
        return cmd;
    }

    private async Task<List<AvailabilityItemRow>> ClaimAvailabilitySliceAsync(NpgsqlConnection connection, string owner,
        TimeSpan leaseDuration, DateTimeOffset now, string type, int bucket, int batchSize, AvailabilityCursor? cursor, CancellationToken ct)
    {
        await using var command = CreateAvailabilityClaimCommand(connection, owner, leaseDuration, now, type, bucket, batchSize, cursor);
        var result = new List<AvailabilityItemRow>();
        AvailabilityCursor? last = null;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = ReadAvailability(reader);
            result.Add(row);
            var key = new AvailabilityCursor(reader.GetInt64(17), row.TmdbId, row.Season, row.Episode);
            // UPDATE RETURNING is not ordered, even though the locked slice is.
            if (last is null || (key.Due, key.Tmdb, key.Season, key.Episode).CompareTo((last.Due, last.Tmdb, last.Season, last.Episode)) > 0) last = key;
        }
        if (type == "episode" && last is not null) _episodeClaimCursors[bucket] = last;
        return result;
    }

    /// <summary>Release only this generation, including cancellation before a probe completes.</summary>
    public async Task ReleaseAvailabilityLeaseAsync(AvailabilityItemRow lease, CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct).ConfigureAwait(false);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = @"UPDATE availability_items SET lease_owner=NULL,lease_until=NULL
WHERE tmdb_id=@tmdb AND type=@type AND season=@season AND episode=@episode
AND lease_owner=@owner AND probe_generation=@generation";
        cmd.AddWithValue("tmdb", lease.TmdbId);
        cmd.AddWithValue("type", lease.Type);
        cmd.AddWithValue("season", lease.Season);
        cmd.AddWithValue("episode", lease.Episode);
        cmd.AddWithValue("owner", lease.LeaseOwner!);
        cmd.AddWithValue("generation", lease.ProbeGeneration);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One resumable, globally rate-limited keyset slice. Policy churn does not
    /// restart an in-progress sweep; the latest policy gets a final full sweep.
    /// The cursor and changed rows commit together. Unlike claims this may wait
    /// for a row lock; cancellation rolls back BOTH work and cursor.
    /// </summary>
    public async Task<int> RescheduleAvailabilityPolicyAsync(string policy, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(policy);
        if (Backend != PhantomDbBackend.Postgres) return 0;
        await using var connection = (NpgsqlConnection)await OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        async Task<object?> Sql(string sql, params (string Key, object Value)[] parameters)
        {
            await using var command = new NpgsqlCommand(sql, connection, tx);
            foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value);
            return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        }
        await Sql("INSERT INTO availability_reschedule(id,policy,sweep_policy) VALUES (1,@policy,@policy) ON CONFLICT(id) DO NOTHING", ("policy", policy)).ConfigureAwait(false);
        string previous, sweep, type;
        int tmdb, season, episode, scanning;
        long next;
        await using (var command = new NpgsqlCommand("SELECT policy,sweep_policy,cursor_tmdb,cursor_type,cursor_season,cursor_episode,next_slice,scanning FROM availability_reschedule WHERE id=1 FOR UPDATE SKIP LOCKED", connection, tx))
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(ct).ConfigureAwait(false)) return 0;
            previous = reader.GetString(0); sweep = reader.GetString(1);
            tmdb = reader.GetInt32(2); type = reader.GetString(3); season = reader.GetInt32(4); episode = reader.GetInt32(5);
            next = reader.GetInt64(6); scanning = reader.GetInt32(7);
        }
        if (previous != policy)
        {
            if (scanning == 0) { tmdb = -1; type = ""; season = -1; episode = -1; sweep = policy; }
            scanning = 1;
        }
        // A new policy must not bypass the global slice rate limit.
        if (next > now.ToUnixTimeSeconds()) return 0;
        if (scanning == 0) { await tx.CommitAsync(ct).ConfigureAwait(false); return 0; }
        var changed = 0;
        var selected = 0;
        await using (var command = new NpgsqlCommand(@"WITH slice AS MATERIALIZED (
SELECT tmdb_id,type,season,episode FROM availability_items
WHERE (tmdb_id,type,season,episode) > (@tmdb,@type,@season,@episode)
ORDER BY tmdb_id,type,season,episode LIMIT 256 FOR UPDATE
), changed AS (
UPDATE availability_items a SET
next_check_at=CASE WHEN probe_policy_hash IS DISTINCT FROM @policy THEN LEAST(next_check_at,@now) ELSE next_check_at END,
claim_bucket=claim_bucket FROM slice s
WHERE a.tmdb_id=s.tmdb_id AND a.type=s.type AND a.season=s.season AND a.episode=s.episode
AND (claim_bucket=-1 OR (probe_policy_hash IS DISTINCT FROM @policy AND next_check_at>@now)) RETURNING 1
)
SELECT tmdb_id,type,season,episode,(SELECT count(*) FROM slice),(SELECT count(*) FROM changed)
FROM slice ORDER BY tmdb_id DESC,type DESC,season DESC,episode DESC LIMIT 1", connection, tx))
        {
            command.Parameters.AddWithValue("tmdb", tmdb); command.Parameters.AddWithValue("type", type);
            command.Parameters.AddWithValue("season", season); command.Parameters.AddWithValue("episode", episode);
            command.Parameters.AddWithValue("policy", policy); command.Parameters.AddWithValue("now", now.ToUnixTimeSeconds());
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                tmdb = reader.GetInt32(0); type = reader.GetString(1); season = reader.GetInt32(2); episode = reader.GetInt32(3);
                selected = checked((int)reader.GetInt64(4)); changed = checked((int)reader.GetInt64(5));
            }
        }
        if (selected < 256)
        {
            scanning = sweep == policy ? 0 : 1;
            tmdb = -1; type = ""; season = -1; episode = -1; sweep = policy;
        }
        await Sql(@"UPDATE availability_reschedule SET policy=@policy,sweep_policy=@sweep,cursor_tmdb=@tmdb,cursor_type=@type,
cursor_season=@season,cursor_episode=@episode,scanning=@scanning,next_slice=@next WHERE id=1",
            ("policy", policy), ("sweep", sweep), ("tmdb", tmdb), ("type", type), ("season", season), ("episode", episode),
            ("scanning", scanning), ("next", now.ToUnixTimeSeconds() + 1)).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
        return changed;
    }
}
