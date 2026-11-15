using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Jellyfin.Plugin.PhantomLibrary.State.Db;

/// <summary>
/// PostgreSQL-only, versioned feature expand on top of schema v22. This narrow
/// routine owns trigger DDL and nontransactional concurrent indexes; it deliberately
/// does not relax SchemaExpandMigrator's general additive statement classifier.
/// </summary>
internal static class AvailabilitySchedulerSchema
{
    internal const string MigrationName = "availability_indexed_scheduler_v1";
    private const long ExpandLock = 741608123512L;

    internal static readonly (string Name, string Columns)[] Indexes =
    {
        ("idx_availability_scheduler_due_v1", "(scheduler_due_at, tmdb_id, type, season, episode) WHERE NOT scheduler_ready"),
        ("idx_availability_scheduler_ready_v1", "(priority DESC, scheduler_bucket, next_check_at, tmdb_id, type, season, episode) WHERE scheduler_ready"),
        ("idx_availability_scheduler_type_v1", "(type, priority DESC, scheduler_bucket, next_check_at, tmdb_id, season, episode) WHERE scheduler_ready"),
        ("idx_availability_scheduler_episode_v1", "(priority DESC, tmdb_id, scheduler_bucket, season, episode) WHERE scheduler_ready AND type='episode'"),
    };

    // Constant defaults are metadata-only on supported PostgreSQL versions (11+).
    // Existing rows enter the due queue unhydrated, not an UPDATE/table rewrite.
    internal const string ExpandSql = """
        ALTER TABLE availability_items ADD COLUMN IF NOT EXISTS scheduler_ready BOOLEAN NOT NULL DEFAULT FALSE;
        ALTER TABLE availability_items ADD COLUMN IF NOT EXISTS scheduler_bucket SMALLINT NOT NULL DEFAULT 0;
        ALTER TABLE availability_items ADD COLUMN IF NOT EXISTS scheduler_due_at BIGINT NOT NULL DEFAULT '-9223372036854775808';
        CREATE TABLE IF NOT EXISTS availability_scheduler_state (
            id INTEGER PRIMARY KEY CHECK (id=1),
            policy_hash TEXT,
            pending_policy_hash TEXT,
            episode_cursor BIGINT NOT NULL DEFAULT '-2147483649',
            cursor_tmdb BIGINT NOT NULL DEFAULT '-2147483649',
            cursor_type TEXT NOT NULL DEFAULT '',
            cursor_season INTEGER NOT NULL DEFAULT -1,
            cursor_episode INTEGER NOT NULL DEFAULT -1,
            sweep_complete BOOLEAN NOT NULL DEFAULT FALSE,
            next_sweep_at BIGINT NOT NULL DEFAULT 0
        );
        INSERT INTO availability_scheduler_state(id) VALUES (1) ON CONFLICT DO NOTHING;
        CREATE OR REPLACE FUNCTION phantom_availability_schedule_v1() RETURNS trigger
        LANGUAGE plpgsql AS $schedule$
        DECLARE
            active_policy TEXT;
            schedule_now BIGINT;
        BEGIN
            SELECT policy_hash INTO active_policy FROM availability_scheduler_state WHERE id=1;
            schedule_now := COALESCE(NULLIF(current_setting('phantom.availability_now', true), '')::BIGINT,
                                     floor(extract(epoch FROM statement_timestamp()))::BIGINT);
            NEW.scheduler_bucket := CASE WHEN NEW.checked_at IS NULL THEN 0
                                         WHEN NEW.status='available' THEN 1
                                         WHEN NEW.status='unavailable' THEN 2 ELSE 3 END;
            NEW.scheduler_due_at := NEW.next_check_at;
            IF NEW.probe_policy_hash IS NULL OR
               (active_policy IS NOT NULL AND NEW.probe_policy_hash <> active_policy) THEN
                NEW.scheduler_due_at := '-9223372036854775808'::BIGINT;
            END IF;
            IF NEW.lease_until IS NOT NULL THEN
                -- Legacy claims require lease_until < now, not <= now.
                NEW.scheduler_due_at := greatest(NEW.scheduler_due_at, NEW.lease_until::BIGINT + 1);
            END IF;
            NEW.scheduler_ready := NEW.scheduler_due_at <= schedule_now;
            RETURN NEW;
        END;
        $schedule$;
        CREATE OR REPLACE TRIGGER phantom_availability_schedule_v1
            BEFORE INSERT OR UPDATE ON availability_items
            FOR EACH ROW EXECUTE FUNCTION phantom_availability_schedule_v1();
        """;

    /// <summary>
    /// Test-accessible production entry point. Requires an open connection without
    /// a transaction. Does not modify the base schema version (including newer DBs).
    /// </summary>
    internal static async Task EnsureAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        // A session lock spans the transactional expand AND concurrent index builds.
        // Always release it, including ambiguous cancellation during lock acquisition.
        try
        {
            await using (var gate = new NpgsqlCommand("SELECT pg_advisory_lock(@key);", connection))
            {
                gate.Parameters.AddWithValue("key", ExpandLock);
                await gate.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await using (var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false))
            {
                await using var command = new NpgsqlCommand("""
                    CREATE TABLE IF NOT EXISTS phantom_expand_migrations (name TEXT PRIMARY KEY, applied_at BIGINT NOT NULL);
                    """, connection, tx);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                command.CommandText = "SELECT 1 FROM phantom_expand_migrations WHERE name=@name;";
                command.Parameters.AddWithValue("name", MigrationName);
                if (await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is null)
                {
                    command.Parameters.Clear();
                    command.CommandText = ExpandSql;
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }

            foreach (var (name, columns) in Indexes)
            {
                await using var command = new NpgsqlCommand("""
                    SELECT i.indisvalid FROM pg_index i
                    WHERE i.indexrelid=to_regclass(@name) AND i.indrelid='availability_items'::regclass;
                    """, connection);
                command.Parameters.AddWithValue("name", name);
                var valid = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
                command.Parameters.Clear();
                command.CommandTimeout = 0;
                if (valid is false)
                {
                    // An interrupted concurrent build leaves an invalid index that
                    // IF NOT EXISTS alone would silently keep forever.
                    command.CommandText = $"DROP INDEX CONCURRENTLY {name};";
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                if (valid is not true)
                {
                    command.CommandText = $"CREATE INDEX CONCURRENTLY IF NOT EXISTS {name} ON availability_items {columns};";
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            await using var record = new NpgsqlCommand("""
                INSERT INTO phantom_expand_migrations(name, applied_at) VALUES (@name, @now)
                ON CONFLICT(name) DO NOTHING;
                """, connection);
            record.Parameters.AddWithValue("name", MigrationName);
            record.Parameters.AddWithValue("now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await record.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseExpandLockAsync(connection).ConfigureAwait(false);
        }
    }

    private static async Task ReleaseExpandLockAsync(NpgsqlConnection connection)
    {
            try
            {
                await using var release = new NpgsqlCommand("SELECT pg_advisory_unlock(@key);", connection);
                release.CommandTimeout = 5;
                release.Parameters.AddWithValue("key", ExpandLock);
                await release.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Never return a possibly locked session to a pool configured with
                // No Reset On Close. Clearing this pool discards this connector.
                NpgsqlConnection.ClearPool(connection);
                await connection.CloseAsync().ConfigureAwait(false);
                throw;
            }
    }
}
