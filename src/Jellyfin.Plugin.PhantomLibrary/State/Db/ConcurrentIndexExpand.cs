using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Jellyfin.Plugin.PhantomLibrary.State.Db;

/// <summary>
/// Shared index expand executor. The session locks live on a dedicated, UNPOOLED
/// physical connection: disposal closes the socket even if cancellation races lock
/// acquisition, a command fails, or the unlock cannot reach the server. No caller
/// connection (or pooled session) ever owns one of these session locks.
/// </summary>
internal static class ConcurrentIndexExpand
{
    private sealed record State(long Oid, long Table, string Definition, bool Valid);
    private sealed record Prepared(ExpandIndex Index, State Expected);

    // Compare server-parsed definitions, not SQL spelling or IF NOT EXISTS notices.
    // attnums may differ on LIKE tables after dropped columns, so compare named
    // key definitions instead of indkey. Opclasses/collations/options remain OIDs.
    private const string Signature = @"jsonb_build_object(
        'method', am.amname, 'unique', i.indisunique, 'nulls_not_distinct', i.indnullsnotdistinct,
        'keys', i.indnkeyatts, 'attributes', i.indnatts,
        'columns', ARRAY(SELECT pg_catalog.pg_get_indexdef(i.indexrelid, n, false)
                         FROM generate_series(1, i.indnatts) n),
        'options', i.indoption::text, 'classes', i.indclass::text, 'collations', i.indcollation::text,
        'expression', pg_catalog.pg_get_expr(i.indexprs, i.indrelid, false),
        'predicate', pg_catalog.pg_get_expr(i.indpred, i.indrelid, false))::text";

    internal static async Task<bool> ApplyAsync(NpgsqlConnection caller, string name, IReadOnlyList<string> statements, CancellationToken ct)
    {
        var schema = (string?)await ScalarAsync(caller, "SELECT pg_catalog.current_schema()", ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Expand requires an existing current schema");
        var indexes = statements.Select(s => ExpandIndex.Parse(ExpandIndex.Clean(s ?? string.Empty))).Where(i => i is not null).Cast<ExpandIndex>().ToArray();
        if (indexes.Any(i => i.Schema is not null && i.Schema != schema) || indexes.Select(i => i.Name).Distinct(StringComparer.Ordinal).Count() != indexes.Length)
        {
            throw new NonAdditiveSchemaChangeException(string.Join("\n", statements), "indexes must have distinct names and belong to the current schema (explicit table schema is recommended)");
        }

        // CloneWith preserves credentials even if Persist Security Info is false.
        var settings = new NpgsqlConnectionStringBuilder(caller.ConnectionString)
        {
            Pooling = false,
            Multiplexing = false,
            Enlist = false,
            SearchPath = ExpandIndex.Quote(schema) + ",pg_catalog",
            ApplicationName = "phantom-expand:" + name,
        };
        await using var connection = caller.CloneWith(settings.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // Schema-wide serialization also protects first-use metadata and different
        // migration names contending for the same index. The second key interlocks
        // with the original transactional/contract helpers' per-name xact lock.
        await LockAsync(connection, SchemaExpandMigrator.StableHash("phantom-expand-schema:" + schema), ct).ConfigureAwait(false);
        await LockAsync(connection, SchemaExpandMigrator.StableHash(name), ct).ConfigureAwait(false);

        // No finally-unlock using the caller's cancelled token: disposing this
        // nonpooled connection releases *all* locks, including ambiguous acquisitions.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ScalarAsync(connection, @"
CREATE TABLE IF NOT EXISTS phantom_expand_migrations (name TEXT PRIMARY KEY, applied_at BIGINT NOT NULL);
CREATE TABLE IF NOT EXISTS phantom_expand_index_builds (
    index_name TEXT PRIMARY KEY, migration_name TEXT NOT NULL,
    table_oid BIGINT NOT NULL, definition TEXT NOT NULL, index_oid BIGINT
);", ct).ConfigureAwait(false);
                break;
            }
            catch (PostgresException ex) when (ex.SqlState == "23505" && attempt < 3)
            {
                // A first-use IsAppliedAsync diagnostics reader can create the
                // receipts table without taking our schema lock. Retry only that
                // metadata bootstrap, never a failed unique index build.
            }
        }

        var receipt = await ScalarAsync(connection, "SELECT applied_at FROM phantom_expand_migrations WHERE name=@name", ct, ("name", name)).ConfigureAwait(false);
        var already = receipt is not null;
        // A receipt is an observation, not a substitute for catalogue validation.
        // Remove it before any work that can fail; only verified completion restores it.
        await ScalarAsync(connection, "DELETE FROM phantom_expand_migrations WHERE name=@name", ct, ("name", name)).ConfigureAwait(false);

        var prepared = new List<Prepared>();
        var changed = !already;
        await using (var tx = await connection.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            foreach (var raw in statements)
            {
                var sql = ExpandIndex.Clean(raw ?? string.Empty);
                if (sql.Length == 0)
                {
                    continue;
                }

                var index = ExpandIndex.Parse(sql);
                if (index is null)
                {
                    if (!already)
                    {
                        await ScalarAsync(connection, sql, ct).ConfigureAwait(false);
                    }

                    continue;
                }

                var table = ExpandIndex.Quote(schema) + "." + ExpandIndex.Quote(index.Table);
                var tableOid = (long?)await ScalarAsync(connection,
                    "SELECT c.oid::bigint FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname=@schema AND c.relname=@table AND c.relkind='r'",
                    ct, ("schema", schema), ("table", index.Table)).ConfigureAwait(false) ?? 0;
                if (tableOid == 0)
                {
                    throw new InvalidOperationException($"Expand requires an ordinary table: {table}");
                }

                // PostgreSQL itself normalizes the requested definition on an empty
                // temporary LIKE table. This never builds a second index on real rows.
                var probe = "expand_probe_" + Guid.NewGuid().ToString("N");
                await ScalarAsync(connection, $"CREATE TEMP TABLE {probe} (LIKE {table}) ON COMMIT DROP", ct).ConfigureAwait(false);
                await ScalarAsync(connection, index.Sql("pg_temp." + probe, probe + "_idx", false), ct).ConfigureAwait(false);
                var expected = (await ReadAsync(connection, null, probe + "_idx", ct).ConfigureAwait(false))!
                    with { Table = tableOid };
                var current = await ReadAsync(connection, schema, index.Name, ct).ConfigureAwait(false);
                await ReserveAsync(connection, name, index, expected, current, ct).ConfigureAwait(false);
                prepared.Add(new Prepared(index, expected));
                if (!index.Concurrent)
                {
                    changed |= await BuildAsync(connection, schema, index, expected, ct).ConfigureAwait(false);
                }
            }

            // For mixed batches this commits ONLY the additive prefix + reservations,
            // never a completion receipt. A retry replays the idempotent prefix.
            if (indexes.All(i => !i.Concurrent))
            {
                await RecordAsync(connection, name, receipt, ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        if (indexes.Any(i => i.Concurrent))
        {
            foreach (var item in prepared.Where(p => p.Index.Concurrent))
            {
                changed |= await BuildAsync(connection, schema, item.Index, item.Expected, ct).ConfigureAwait(false);
            }

            // Verify EVERY index again, including the transactional prefix, before
            // recording success. Reservations and receipts never bypass this check.
            foreach (var item in prepared)
            {
                var current = await ReadAsync(connection, schema, item.Index.Name, ct).ConfigureAwait(false);
                RequireDefinition(item.Index, item.Expected, current);
                if (current?.Valid != true)
                {
                    throw new InvalidOperationException($"Index {item.Index.Name} did not become valid");
                }
            }

            await RecordAsync(connection, name, receipt, ct).ConfigureAwait(false);
        }

        return changed;
    }

    private static async Task ReserveAsync(NpgsqlConnection connection, string name, ExpandIndex index, State expected, State? current, CancellationToken ct)
    {
        RequireDefinition(index, expected, current);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT migration_name, table_oid, definition, index_oid FROM phantom_expand_index_builds WHERE index_name=@index";
        command.Parameters.AddWithValue("index", index.Name);
        bool owned;
        bool reserved;
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            reserved = await reader.ReadAsync(ct).ConfigureAwait(false);
            owned = reserved && reader.GetString(0) == name && reader.GetInt64(1) == expected.Table && reader.GetString(2) == expected.Definition;
            if (owned && current is { Valid: false } && !reader.IsDBNull(3) && reader.GetInt64(3) != current.Oid)
            {
                // A previously observed index was replaced outside this migrator.
                // The old receipt/reservation does not confer ownership of that OID.
                owned = false;
            }
        }

        if ((reserved && !owned) || (current is { Valid: false } && !owned))
        {
            throw new InvalidOperationException($"Index {index.Name} is not an interrupted build owned by migration {name}; refusing to drop or claim it");
        }

        if (!reserved && current is null)
        {
            // Durable absent-name reservation BEFORE CREATE INDEX CONCURRENTLY.
            // After a process crash (even before CREATE returned an OID), only this
            // migration and this exact table+definition may recover its invalid build.
            await ScalarAsync(connection, "INSERT INTO phantom_expand_index_builds (index_name,migration_name,table_oid,definition) VALUES (@index,@name,@table,@definition)", ct,
                ("index", index.Name), ("name", name), ("table", expected.Table), ("definition", expected.Definition)).ConfigureAwait(false);
        }

        if (owned && current is null)
        {
            await ScalarAsync(connection, "UPDATE phantom_expand_index_builds SET index_oid=NULL WHERE index_name=@index", ct, ("index", index.Name)).ConfigureAwait(false);
        }
    }

    private static async Task<bool> BuildAsync(NpgsqlConnection connection, string schema, ExpandIndex index, State expected, CancellationToken ct)
    {
        var current = await ReadAsync(connection, schema, index.Name, ct).ConfigureAwait(false);
        RequireDefinition(index, expected, current);
        if (current is { Valid: true })
        {
            await RememberOidAsync(connection, index, current.Oid, ct).ConfigureAwait(false);
            return false;
        }

        if (current is not null)
        {
            // ReserveAsync proved ownership under the same session lock. No CASCADE,
            // no valid-index drop, no user-provided DROP, and no parallel private DDL.
            await ScalarAsync(connection, "UPDATE phantom_expand_index_builds SET index_oid=NULL WHERE index_name=@index", ct, ("index", index.Name)).ConfigureAwait(false);
            await ScalarAsync(connection, $"DROP INDEX {(index.Concurrent ? "CONCURRENTLY " : string.Empty)}{ExpandIndex.Quote(schema)}.{ExpandIndex.Quote(index.Name)}", ct).ConfigureAwait(false);
        }

        await ScalarAsync(connection, index.Sql(ExpandIndex.Quote(schema) + "." + ExpandIndex.Quote(index.Table), index.Name, index.Concurrent), ct).ConfigureAwait(false);
        var built = await ReadAsync(connection, schema, index.Name, ct).ConfigureAwait(false);
        RequireDefinition(index, expected, built);
        if (built?.Valid != true)
        {
            throw new InvalidOperationException($"Index {index.Name} did not become valid");
        }

        await RememberOidAsync(connection, index, built.Oid, ct).ConfigureAwait(false);
        return true;
    }

    private static Task<object?> RememberOidAsync(NpgsqlConnection connection, ExpandIndex index, long oid, CancellationToken ct) =>
        ScalarAsync(connection, "UPDATE phantom_expand_index_builds SET index_oid=@oid WHERE index_name=@index", ct, ("oid", oid), ("index", index.Name));

    private static void RequireDefinition(ExpandIndex index, State expected, State? current)
    {
        if (current is not null && (current.Table != expected.Table || current.Definition != expected.Definition))
        {
            throw new InvalidOperationException($"Conflicting catalogue definition for index {index.Name}; refusing to drop it");
        }
    }

    private static async Task<State?> ReadAsync(NpgsqlConnection connection, string? schema, string name, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $@"SELECT COALESCE(i.indrelid::bigint,0), {Signature},
            COALESCE(i.indisvalid AND i.indisready AND i.indislive AND NOT i.indisprimary AND NOT i.indisexclusion, false), c.oid::bigint
            FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace ns ON ns.oid=c.relnamespace
            LEFT JOIN pg_catalog.pg_index i ON i.indexrelid=c.oid
            LEFT JOIN pg_catalog.pg_am am ON am.oid=c.relam
            WHERE c.relname=@name AND {(schema is null ? "ns.oid=pg_catalog.pg_my_temp_schema()" : "ns.nspname=@schema")}";
        command.Parameters.AddWithValue("name", name);
        if (schema is not null)
        {
            command.Parameters.AddWithValue("schema", schema);
        }

        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false) ? new State(reader.GetInt64(3), reader.GetInt64(0), reader.GetString(1), reader.GetBoolean(2)) : null;
    }

    private static async Task LockAsync(NpgsqlConnection connection, long key, CancellationToken ct)
    {
        // A blocking SELECT pg_advisory_lock holds a statement snapshot. CIC can
        // then wait for that snapshot while its owner waits for our lock (40P01).
        // End each unsuccessful SELECT before waiting, so waiters retain no snapshot.
        while (!(bool)(await ScalarAsync(connection, "SELECT pg_catalog.pg_try_advisory_lock(@key)", ct, ("key", key)).ConfigureAwait(false))!)
        {
            await Task.Delay(25, ct).ConfigureAwait(false);
        }
    }

    private static Task<object?> RecordAsync(NpgsqlConnection connection, string name, object? receipt, CancellationToken ct) =>
        ScalarAsync(connection, "INSERT INTO phantom_expand_migrations (name,applied_at) VALUES (@name,@at)", ct,
            ("name", name), ("at", receipt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()));

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 0; // Builds/waits may be long; the supplied token bounds them.
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
    }
}
