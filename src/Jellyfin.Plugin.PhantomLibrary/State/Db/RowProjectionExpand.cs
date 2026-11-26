using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Jellyfin.Plugin.PhantomLibrary.State.Db;

/// <summary>
/// Transactional prefix component of ConcurrentIndexExpand, NOT an executor.
/// Uses its dedicated connection, schema/name locks, transaction and receipt.
/// Installation has no intermediate state: missing or changed owned objects are
/// refused, never CREATE OR REPLACE'd or silently adopted.
/// </summary>
internal static class RowProjectionExpand
{
    internal const string MetadataSql = @"
CREATE TABLE IF NOT EXISTS phantom_expand_row_projections (
    projection_name TEXT PRIMARY KEY, migration_name TEXT NOT NULL,
    definition TEXT NOT NULL, catalogue TEXT NOT NULL
)";

    internal static async Task RequireBatchAsync(NpgsqlConnection connection, string migration, IReadOnlyList<AdditiveRowProjection> projections, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand("SELECT projection_name FROM phantom_expand_row_projections WHERE migration_name=@migration", connection);
        cmd.Parameters.AddWithValue("migration", migration);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!projections.Any(p => p.Name == reader.GetString(0)))
            {
                throw new InvalidOperationException("An already published projection cannot be omitted from its expand");
            }
        }
    }

    internal static async Task InstallOrVerifyAsync(NpgsqlConnection connection, string schema, string migration, AdditiveRowProjection projection, CancellationToken ct)
    {
        var table = ExpandIndex.Quote(schema) + "." + ExpandIndex.Quote(projection.Table);
        string? definition = null;
        string? catalogue = null;
        await using (var command = new NpgsqlCommand("SELECT migration_name, definition, catalogue FROM phantom_expand_row_projections WHERE projection_name=@name", connection))
        {
            command.Parameters.AddWithValue("name", projection.Name);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            if (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (reader.GetString(0) != migration)
                {
                    throw Conflict(projection, "another migration owns this name");
                }

                definition = reader.GetString(1);
                catalogue = reader.GetString(2);
            }
        }

        if (definition is not null)
        {
            if (definition != projection.Definition)
            {
                throw Conflict(projection, "published/future definition differs");
            }

            await VerifyAsync(connection, schema, projection, catalogue!, ct).ConfigureAwait(false);
            return;
        }

        var oid = await LockTargetAsync(connection, schema, projection, ct).ConfigureAwait(false);
        foreach (var source in projection.Sources)
        {
            var type = (long?)await ScalarAsync(connection, @"SELECT atttypid::bigint FROM pg_catalog.pg_attribute
WHERE attrelid=@oid::oid AND attname=@column AND attnum>0 AND NOT attisdropped AND attgenerated=''",
                ct, ("oid", oid), ("column", source.Name)).ConfigureAwait(false);
            // Built-in OIDs only: no domains, user-defined casts/operators, collations
            // or executable generated sources. Explicit casts bind numeric operators.
            if (source.Type == RowExpression.Kind.Number ? type is not (20 or 21 or 23) : type != 25)
            {
                throw Conflict(projection, "source is missing or not the declared built-in primitive: " + source.Name);
            }
        }

        foreach (var column in projection.Columns)
        {
            var exists = await ScalarAsync(connection, "SELECT 1 FROM pg_catalog.pg_attribute WHERE attrelid=@oid::oid AND attname=@column AND NOT attisdropped", ct,
                ("oid", oid), ("column", column.Name)).ConfigureAwait(false);
            if (exists is not null)
            {
                throw Conflict(projection, "output already exists (legacy/external columns cannot be adopted): " + column.Name);
            }
        }

        var collision = await ScalarAsync(connection, @"SELECT 1 FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
WHERE n.nspname=@schema AND p.proname=@function
UNION ALL SELECT 1 FROM pg_catalog.pg_trigger WHERE tgrelid=@oid::oid AND tgname=@trigger", ct,
            ("schema", schema), ("function", projection.FunctionName), ("oid", oid), ("trigger", projection.TriggerName)).ConfigureAwait(false);
        if (collision is not null)
        {
            throw Conflict(projection, "function or trigger name is externally owned");
        }

        foreach (var column in projection.Columns)
        {
            await ScalarAsync(connection, $"ALTER TABLE {table} ADD COLUMN {ExpandIndex.Quote(column.Name)} pg_catalog.int8 DEFAULT {column.Default.ToString(CultureInfo.InvariantCulture)}", ct).ConfigureAwait(false);
        }

        var function = ExpandIndex.Quote(schema) + "." + ExpandIndex.Quote(projection.FunctionName);
        // Only the closed compiler emits this body. Raw SQL classification is NOT
        // broadened to accept CREATE FUNCTION, DO, triggers or executable defaults.
        await ScalarAsync(connection, $"CREATE FUNCTION {function}() RETURNS pg_catalog.trigger LANGUAGE plpgsql IMMUTABLE SECURITY INVOKER SET search_path TO pg_catalog AS '{projection.Body.Replace("'", "''", StringComparison.Ordinal)}'", ct).ConfigureAwait(false);
        // Also watch output assignments: a new writer cannot supply stale projections.
        // Unrelated updates need not recompute defaults during a bounded backfill.
        var watched = projection.Sources.Select(s => s.Name).Concat(projection.Columns.Select(c => c.Name)).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal);
        await ScalarAsync(connection, $"CREATE TRIGGER {ExpandIndex.Quote(projection.TriggerName)} BEFORE INSERT OR UPDATE OF {string.Join(", ", watched.Select(ExpandIndex.Quote))} ON {table} FOR EACH ROW EXECUTE FUNCTION {function}()", ct).ConfigureAwait(false);
        var installed = await CatalogueAsync(connection, schema, projection, ct).ConfigureAwait(false);
        await ScalarAsync(connection, "INSERT INTO phantom_expand_row_projections (projection_name,migration_name,definition,catalogue) VALUES (@name,@migration,@definition,@catalogue)", ct,
            ("name", projection.Name), ("migration", migration), ("definition", projection.Definition), ("catalogue", installed)).ConfigureAwait(false);
    }

    internal static async Task VerifyAsync(NpgsqlConnection connection, string schema, AdditiveRowProjection projection, CancellationToken ct)
    {
        var catalogue = (string?)await ScalarAsync(connection, "SELECT catalogue FROM phantom_expand_row_projections WHERE projection_name=@name", ct, ("name", projection.Name)).ConfigureAwait(false)
            ?? throw Conflict(projection, "ownership record disappeared");
        await VerifyAsync(connection, schema, projection, catalogue, ct).ConfigureAwait(false);
    }

    private static async Task VerifyAsync(NpgsqlConnection connection, string schema, AdditiveRowProjection projection, string expected, CancellationToken ct)
    {
        await LockTargetAsync(connection, schema, projection, ct).ConfigureAwait(false);
        var current = await CatalogueAsync(connection, schema, projection, ct).ConfigureAwait(false);
        if (expected != current)
        {
            throw Conflict(projection, "catalogue drift, missing object or external replacement; refusing overwrite");
        }
    }

    // Caller must hold a transaction through installation/verification and receipt.
    // ONLY avoids recursively locking foreign descendants even for a refused target.
    // ACCESS EXCLUSIVE blocks both incoming and outgoing inheritance DDL, as well as
    // ordinary catalogue drift; re-read after acquiring it (never trust a pre-lock OID).
    private static async Task<object> LockTargetAsync(NpgsqlConnection connection, string schema, AdditiveRowProjection projection, CancellationToken ct)
    {
        var table = ExpandIndex.Quote(schema) + "." + ExpandIndex.Quote(projection.Table);
        const string targetSql = @"SELECT c.oid::bigint FROM pg_catalog.pg_class c
JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace
WHERE n.nspname=@schema AND c.relname=@table AND c.relkind='r' AND NOT c.relispartition";
        if (await ScalarAsync(connection, targetSql, ct, ("schema", schema), ("table", projection.Table)).ConfigureAwait(false) is null)
        {
            throw Conflict(projection, "target must be an ordinary, nonpartition table in the current schema");
        }

        await ScalarAsync(connection, $"LOCK TABLE ONLY {table} IN ACCESS EXCLUSIVE MODE", ct).ConfigureAwait(false);
        var oid = await ScalarAsync(connection, targetSql + @"
AND NOT EXISTS (SELECT 1 FROM pg_catalog.pg_inherits i WHERE i.inhrelid=c.oid OR i.inhparent=c.oid)",
            ct, ("schema", schema), ("table", projection.Table)).ConfigureAwait(false);
        return oid ?? throw Conflict(projection, "target must be an ordinary, nonpartition table without incoming or outgoing inheritance");
    }

    private static async Task<string> CatalogueAsync(NpgsqlConnection connection, string schema, AdditiveRowProjection projection, CancellationToken ct) =>
        (string)(await ScalarAsync(connection, @"SELECT jsonb_build_object(
 'table', (SELECT jsonb_build_array(c.oid,c.relnamespace,c.relowner,c.relkind,c.relispartition) FROM pg_catalog.pg_class c WHERE c.oid=t.oid),
 'columns', (SELECT jsonb_agg(jsonb_build_array(a.attnum,a.attname,a.atttypid,a.atttypmod,a.attnotnull,a.attcollation,a.attidentity,a.attgenerated,
                    pg_catalog.pg_get_expr(d.adbin,d.adrelid,false)) ORDER BY a.attname)
             FROM pg_catalog.pg_attribute a LEFT JOIN pg_catalog.pg_attrdef d ON d.adrelid=a.attrelid AND d.adnum=a.attnum
             WHERE a.attrelid=t.oid AND NOT a.attisdropped AND a.attname=ANY(@columns)),
 'function', (SELECT jsonb_agg(to_jsonb(p) ORDER BY p.oid) FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname=@schema AND p.proname=@function),
 'trigger', (SELECT jsonb_agg(to_jsonb(g) ORDER BY g.oid) FROM pg_catalog.pg_trigger g WHERE g.tgrelid=t.oid AND g.tgname=@trigger)
)::text FROM (SELECT pg_catalog.to_regclass(@table) AS oid) t", ct,
            ("schema", schema), ("table", ExpandIndex.Quote(schema) + "." + ExpandIndex.Quote(projection.Table)),
            ("columns", projection.Sources.Select(s => s.Name).Concat(projection.Columns.Select(c => c.Name)).Distinct(StringComparer.Ordinal).ToArray()),
            ("function", projection.FunctionName), ("trigger", projection.TriggerName)).ConfigureAwait(false))!;

    private static InvalidOperationException Conflict(AdditiveRowProjection projection, string reason) => new($"Projection {projection.Name}: {reason}");

    private static Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, CancellationToken ct, params (string Name, object Value)[] parameters) =>
        ConcurrentIndexExpand.ScalarAsync(connection, sql, ct, parameters);
}
