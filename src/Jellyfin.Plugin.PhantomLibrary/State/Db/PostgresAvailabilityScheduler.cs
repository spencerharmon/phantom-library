using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Jellyfin.Plugin.PhantomLibrary.State.Db;

internal static class PostgresAvailabilityScheduler
{
    internal const int MaxBatchSize = 32;
    internal const int MaintenanceBatchSize = 256;
    internal const int SweepIntervalSeconds = 60;

    internal static async Task<IReadOnlyList<AvailabilityItemRow>> ClaimAsync(
        NpgsqlConnection connection, string owner, TimeSpan leaseDuration, DateTimeOffset now,
        string policyHash, int batchSize, CancellationToken ct, string? preferredType = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(policyHash);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, MaxBatchSize);
        ArgumentOutOfRangeException.ThrowIfLessThan(leaseDuration, TimeSpan.FromSeconds(1));
        await using var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false);
        // Transaction-local clock also makes trigger behavior deterministic in tests.
        await using (var clock = new NpgsqlCommand("SELECT set_config('phantom.availability_now', @now, true);", connection, tx))
        {
            clock.Parameters.AddWithValue("now", now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            await clock.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var maintenance = CreateSweepCommand(connection, tx, policyHash, now))
        {
            await maintenance.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var promotion = CreatePromotionCommand(connection, tx, now))
        {
            await promotion.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (preferredType is not null)
        {
            // Both LIMIT probes follow the ready/type indexes; ties retain alternation.
            await using var priority = new NpgsqlCommand("""
                SELECT type FROM availability_items WHERE scheduler_ready
                  AND priority > COALESCE((
                      SELECT priority FROM availability_items WHERE scheduler_ready AND type=@type
                      ORDER BY priority DESC LIMIT 1), '-2147483649'::BIGINT)
                ORDER BY priority DESC,scheduler_bucket,next_check_at,tmdb_id,type,season,episode
                LIMIT 1;
                """, connection, tx);
            priority.Parameters.AddWithValue("type", preferredType);
            if (await priority.ExecuteScalarAsync(ct).ConfigureAwait(false) is string urgentType)
            {
                preferredType = urgentType;
            }
        }

        var rows = new List<AvailabilityItemRow>(batchSize);
        await using (var claim = CreateClaimCommand(connection, tx, owner, leaseDuration, now, batchSize, preferredType))
        await using (var reader = await claim.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                rows.Add(PhantomDb.ReadAvailability(reader));
            }
        }

        await tx.CommitAsync(ct).ConfigureAwait(false);
        return rows;
    }

    // These builders are the production commands, not simplified EXPLAIN facsimiles.
    internal static NpgsqlCommand CreatePromotionCommand(NpgsqlConnection connection, NpgsqlTransaction tx, DateTimeOffset now)
    {
        var command = new NpgsqlCommand("""
            WITH due AS MATERIALIZED (
                SELECT tmdb_id,type,season,episode FROM availability_items
                WHERE NOT scheduler_ready AND scheduler_due_at <= @now
                ORDER BY scheduler_due_at,tmdb_id,type,season,episode
                LIMIT 256 FOR UPDATE SKIP LOCKED
            )
            UPDATE availability_items a SET scheduler_ready=TRUE FROM due d
            WHERE (a.tmdb_id,a.type,a.season,a.episode)=(d.tmdb_id,d.type,d.season,d.episode);
            """, connection, tx);
        command.Parameters.AddWithValue("now", now.ToUnixTimeSeconds());
        return command;
    }

    internal static NpgsqlCommand CreateSweepCommand(NpgsqlConnection connection, NpgsqlTransaction tx, string policyHash, DateTimeOffset now)
    {
        // The singleton lock serializes short scheduler transactions, not probes.
        // Never skip a page's locked rows: advancing past them would lose invalidation.
        // Hash requests cannot reset an unfinished sweep, and completed sweeps stay
        // parked until a different policy is requested after the persisted cooldown.
        var command = new NpgsqlCommand("""
            WITH locked AS MATERIALIZED (
                SELECT * FROM availability_scheduler_state WHERE id=1 FOR UPDATE
            ), state AS (
                UPDATE availability_scheduler_state s SET
                    pending_policy_hash=@policy,
                    policy_hash=CASE WHEN l.policy_hash IS NULL OR (l.sweep_complete AND l.next_sweep_at<=@now)
                                     THEN @policy ELSE l.policy_hash END,
                    cursor_tmdb=CASE WHEN l.sweep_complete AND l.next_sweep_at<=@now AND l.policy_hash IS DISTINCT FROM @policy
                                     THEN -2147483649 ELSE l.cursor_tmdb END,
                    cursor_type=CASE WHEN l.sweep_complete AND l.next_sweep_at<=@now AND l.policy_hash IS DISTINCT FROM @policy
                                     THEN '' ELSE l.cursor_type END,
                    cursor_season=CASE WHEN l.sweep_complete AND l.next_sweep_at<=@now AND l.policy_hash IS DISTINCT FROM @policy
                                     THEN -1 ELSE l.cursor_season END,
                    cursor_episode=CASE WHEN l.sweep_complete AND l.next_sweep_at<=@now AND l.policy_hash IS DISTINCT FROM @policy
                                     THEN -1 ELSE l.cursor_episode END,
                    sweep_complete=CASE WHEN l.policy_hash IS NULL OR (l.sweep_complete AND l.next_sweep_at<=@now AND l.policy_hash IS DISTINCT FROM @policy)
                                     THEN FALSE ELSE l.sweep_complete END
                FROM locked l WHERE s.id=l.id RETURNING s.*
            ) SELECT id FROM state;
            WITH page AS MATERIALIZED (
                SELECT a.tmdb_id,a.type,a.season,a.episode FROM availability_items a
                WHERE (a.tmdb_id,a.type,a.season,a.episode) >
                    (SELECT cursor_tmdb,cursor_type,cursor_season,cursor_episode FROM availability_scheduler_state WHERE id=1 AND NOT sweep_complete)
                ORDER BY a.tmdb_id,a.type,a.season,a.episode LIMIT 256
            ), touched AS (
                UPDATE availability_items a SET scheduler_ready=a.scheduler_ready
                FROM page p WHERE (a.tmdb_id,a.type,a.season,a.episode)=(p.tmdb_id,p.type,p.season,p.episode)
                RETURNING a.tmdb_id
            )
            UPDATE availability_scheduler_state SET
                cursor_tmdb=COALESCE((SELECT tmdb_id FROM page ORDER BY tmdb_id DESC,type DESC,season DESC,episode DESC LIMIT 1),cursor_tmdb),
                cursor_type=COALESCE((SELECT type FROM page ORDER BY tmdb_id DESC,type DESC,season DESC,episode DESC LIMIT 1),cursor_type),
                cursor_season=COALESCE((SELECT season FROM page ORDER BY tmdb_id DESC,type DESC,season DESC,episode DESC LIMIT 1),cursor_season),
                cursor_episode=COALESCE((SELECT episode FROM page ORDER BY tmdb_id DESC,type DESC,season DESC,episode DESC LIMIT 1),cursor_episode),
                sweep_complete=(SELECT count(*)<256 FROM page),
                next_sweep_at=CASE WHEN (SELECT count(*)<256 FROM page) THEN @now+60 ELSE next_sweep_at END
            WHERE id=1 AND NOT sweep_complete;
            """, connection, tx);
        command.Parameters.AddWithValue("policy", policyHash);
        command.Parameters.AddWithValue("now", now.ToUnixTimeSeconds());
        return command;
    }

    internal static NpgsqlCommand CreateClaimCommand(
        NpgsqlConnection connection, NpgsqlTransaction tx, string owner, TimeSpan leaseDuration,
        DateTimeOffset now, int batchSize, string? preferredType = null)
    {
        var episode = preferredType == "episode";
        var selection = episode ? """
            WITH RECURSIVE top_priority AS MATERIALIZED (
                SELECT priority FROM availability_items WHERE scheduler_ready AND type='episode'
                ORDER BY priority DESC LIMIT 1
            ), picked AS (
                SELECT a.*, 1 AS ordinal, a.tmdb_id AS first_tmdb
                FROM (SELECT episode_cursor FROM availability_scheduler_state WHERE id=1) s
                CROSS JOIN LATERAL (
                    SELECT * FROM (SELECT a.* FROM availability_items a
                     WHERE scheduler_ready AND type='episode' AND priority=(SELECT priority FROM top_priority)
                       AND tmdb_id>s.episode_cursor
                     ORDER BY tmdb_id,scheduler_bucket,season,episode LIMIT 1 FOR UPDATE SKIP LOCKED) forward
                    UNION ALL
                    SELECT * FROM (SELECT a.* FROM availability_items a
                     WHERE scheduler_ready AND type='episode' AND priority=(SELECT priority FROM top_priority)
                       AND tmdb_id<=s.episode_cursor
                     ORDER BY tmdb_id,scheduler_bucket,season,episode LIMIT 1 FOR UPDATE SKIP LOCKED) wrap
                    LIMIT 1
                ) a
                UNION ALL
                SELECT a.*, p.ordinal+1, p.first_tmdb FROM picked p
                CROSS JOIN LATERAL (
                    SELECT * FROM (SELECT a.* FROM availability_items a
                     WHERE scheduler_ready AND type='episode' AND priority=(SELECT priority FROM top_priority)
                       AND tmdb_id>p.tmdb_id
                       AND tmdb_id<CASE WHEN p.tmdb_id<p.first_tmdb THEN p.first_tmdb::BIGINT ELSE 2147483648 END
                     ORDER BY tmdb_id,scheduler_bucket,season,episode LIMIT 1 FOR UPDATE SKIP LOCKED) forward
                    UNION ALL
                    SELECT * FROM (SELECT a.* FROM availability_items a
                     WHERE scheduler_ready AND type='episode' AND priority=(SELECT priority FROM top_priority)
                       AND p.tmdb_id>=p.first_tmdb AND tmdb_id<p.first_tmdb
                     ORDER BY tmdb_id,scheduler_bucket,season,episode LIMIT 1 FOR UPDATE SKIP LOCKED) wrap
                    LIMIT 1
                ) a WHERE p.ordinal<@batch
            ), cursor_update AS (
                UPDATE availability_scheduler_state SET episode_cursor=(SELECT tmdb_id FROM picked ORDER BY ordinal DESC LIMIT 1)
                WHERE id=1 AND EXISTS(SELECT 1 FROM picked)
            )
            """ : $"""
            WITH picked AS MATERIALIZED (
                SELECT a.* FROM availability_items a WHERE scheduler_ready
                {(preferredType is null ? string.Empty : "AND type=@type")}
                ORDER BY priority DESC,scheduler_bucket,next_check_at,tmdb_id,type,season,episode
                LIMIT @batch FOR UPDATE SKIP LOCKED
            )
            """;
        var command = new NpgsqlCommand(selection + """
            , claimed AS (
                UPDATE availability_items a SET lease_owner=@owner,lease_until=@until,
                    probe_generation=a.probe_generation+1,attempt_count=a.attempt_count+1,
                    next_check_at=least(a.next_check_at,@now),
                    probe_policy_hash=(SELECT policy_hash FROM availability_scheduler_state WHERE id=1)
                FROM picked p WHERE (a.tmdb_id,a.type,a.season,a.episode)=(p.tmdb_id,p.type,p.season,p.episode)
                RETURNING a.*
            )
            SELECT c.tmdb_id,c.type,c.season,c.episode,c.status,c.checked_at,c.next_check_at,
                c.candidate_magnet,c.candidate_info_hash,c.candidate_size,c.candidate_seeders,
                c.candidate_indexer,c.candidate_source,c.probe_generation,c.lease_owner,c.attempt_count,c.negative_streak
            FROM claimed c JOIN picked p USING(tmdb_id,type,season,episode)
            """ + (episode ? " ORDER BY p.ordinal;" : " ORDER BY p.priority DESC,p.scheduler_bucket,p.next_check_at,p.tmdb_id,p.type,p.season,p.episode;"), connection, tx);
        command.Parameters.AddWithValue("owner", owner);
        command.Parameters.AddWithValue("now", now.ToUnixTimeSeconds());
        command.Parameters.AddWithValue("until", now.Add(leaseDuration).ToUnixTimeSeconds());
        command.Parameters.AddWithValue("batch", batchSize);
        if (preferredType is not null && !episode)
        {
            command.Parameters.AddWithValue("type", preferredType);
        }

        return command;
    }
}
