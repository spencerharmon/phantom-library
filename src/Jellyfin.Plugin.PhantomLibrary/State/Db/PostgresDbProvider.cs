using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Jellyfin.Plugin.PhantomLibrary.State.Db;

/// <summary>
/// PostgreSQL backend for <see cref="PhantomDb"/> (config-gated opt-in via
/// <see cref="PhantomDb.CreatePostgres(string)"/>; SQLite stays the compiled-in
/// default). Consumes the DSN/Secret config surface added by
/// <c>p4-chart-postgres-wiring</c> (the <c>PHANTOM_POSTGRES_*</c> env vars), wired
/// through <see cref="PhantomDbOptions"/>.
/// </summary>
public sealed class PostgresDbProvider : IPhantomDbProvider
{
    private const string SchemaMetaTable = "phantom_schema_meta";

    /// <summary>
    /// Primary-key column list per table, needed to translate SQLite's
    /// <c>INSERT OR REPLACE</c> / <c>INSERT OR IGNORE</c> (which has no Postgres
    /// equivalent) into <c>INSERT ... ON CONFLICT (pk) DO UPDATE/NOTHING</c>. Kept
    /// in exact sync with the <c>PRIMARY KEY</c> clauses in
    /// <see cref="PhantomDb.SchemaV10Sql"/> — a table whose PK changes must update
    /// its entry here too, or the Postgres backend's upsert silently targets the
    /// wrong conflict columns.
    /// </summary>
    private static readonly Dictionary<string, string[]> PrimaryKeysByTable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["magnet_cache"] = new[] { "tmdb_id", "imdb_id", "type", "season", "episode", "preset" },
        ["magnet_failure_cache"] = new[] { "tmdb_id", "imdb_id", "type", "season", "episode", "preset", "magnet" },
        ["unavailable_marker"] = new[] { "tmdb_id", "imdb_id", "type", "season", "episode" },
        ["tmdb_cache"] = new[] { "endpoint", "params_hash", "language" },
        ["catalogue_items"] = new[] { "tmdb_id", "type" },
        ["availability_items"] = new[] { "tmdb_id", "type", "season", "episode" },
        ["series_expansion_state"] = new[] { "series_tmdb_id" },
        ["tmdb_episode_cache"] = new[] { "series_tmdb_id", "season", "episode" },
        ["materialised_state"] = new[] { "tmdb_id", "type", "season", "episode" },
        ["tmdb_metadata"] = new[] { "tmdb_id", "type" },
        ["gostream_path_tmdb"] = new[] { "path" },
    };

    private static readonly Regex UpsertPattern = new(
        @"INSERT\s+OR\s+(REPLACE|IGNORE)\s+INTO\s+(?<table>\w+)\s*\((?<cols>[^)]+)\)\s*VALUES\s*\((?<vals>[^;]+?)\)\s*;?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // SQLite's "a IS b" / "a IS NOT b" are general NULL-safe (in)equality
    // operators; Postgres restricts IS to NULL/TRUE/FALSE/UNKNOWN and needs the
    // standard-SQL "IS [NOT] DISTINCT FROM" operators for the same NULL-safe
    // semantics against a bound parameter (see PurgeStaleMaterialiseInFlightAsync's
    // "owner IS @self" / "owner IS NOT @self").
    private static readonly Regex IsParamPattern = new(
        @"\bIS(\s+NOT)?\s+(@\w+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="PostgresDbProvider"/> class.
    /// </summary>
    /// <param name="connectionString">An Npgsql connection string (built by <see cref="PhantomDb.CreatePostgres"/>).</param>
    public PostgresDbProvider(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <inheritdoc/>
    public PhantomDbBackend Backend => PhantomDbBackend.Postgres;

    /// <inheritdoc/>
    public DbConnection CreateConnection() => new NpgsqlConnection(_connectionString);

    /// <inheritdoc/>
    public Task PrepareConnectionAsync(DbConnection conn, CancellationToken ct) => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task<int> ReadSchemaVersionAsync(DbConnection conn, CancellationToken ct)
    {
        // CREATE TABLE IF NOT EXISTS is not atomic against a CONCURRENT session
        // also racing to create the same table for the first time: Postgres can
        // raise a duplicate-key error against its own system catalog
        // (pg_type/pg_class) even though the DDL itself says "IF NOT EXISTS".
        // This is a documented Postgres behaviour (two sessions both seeing the
        // table absent, both proceeding to CREATE). Retry once past that race —
        // by the second attempt the table is guaranteed to exist under any
        // interleaving of two concurrent creators.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await using var ensure = conn.CreateCommand();
                ensure.CommandText = $"CREATE TABLE IF NOT EXISTS {SchemaMetaTable} (version INTEGER NOT NULL);";
                await ensure.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                break;
            }
            catch (PostgresException ex) when (ex.SqlState == "23505" && attempt == 0)
            {
                // Lost the create race to a concurrent sibling replica; the table
                // now exists (created by whichever session won). Fall through and
                // read it normally.
            }
        }

        await using var v = conn.CreateCommand();
        v.CommandText = $"SELECT version FROM {SchemaMetaTable} LIMIT 1;";
        var result = await v.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is null or DBNull ? 0 : Convert.ToInt32(result, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public async Task WriteSchemaVersionAsync(DbConnection conn, DbTransaction tx, int version, CancellationToken ct)
    {
        await using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            del.CommandText = $"DELETE FROM {SchemaMetaTable};";
            await del.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using var ins = conn.CreateCommand();
        ins.Transaction = tx;
        ins.CommandText = $"INSERT INTO {SchemaMetaTable} (version) VALUES (@version);";
        ins.Parameters.Add(new NpgsqlParameter("version", version));
        await ins.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public string TranslateUpsertSql(string sqliteUpsertSql)
    {
        ArgumentNullException.ThrowIfNull(sqliteUpsertSql);
        var m = UpsertPattern.Match(sqliteUpsertSql.Trim());
        if (!m.Success)
        {
            throw new InvalidOperationException(
                $"PostgresDbProvider.TranslateUpsertSql could not parse upsert statement: {sqliteUpsertSql}");
        }

        var isIgnore = string.Equals(m.Groups[1].Value, "IGNORE", StringComparison.OrdinalIgnoreCase);
        var table = m.Groups["table"].Value;
        var cols = m.Groups["cols"].Value.Split(',').Select(c => c.Trim()).ToArray();
        var vals = m.Groups["vals"].Value.Trim();

        if (!PrimaryKeysByTable.TryGetValue(table, out var pk))
        {
            throw new InvalidOperationException(
                $"PostgresDbProvider.TranslateUpsertSql has no registered primary key for table '{table}'. "
                + "Add it to PrimaryKeysByTable, kept in sync with PhantomDb.SchemaV10Sql.");
        }

        var conflictCols = string.Join(", ", pk);
        var colList = string.Join(", ", cols);

        if (isIgnore)
        {
            return $"INSERT INTO {table} ({colList}) VALUES ({vals}) ON CONFLICT ({conflictCols}) DO NOTHING;";
        }

        var updateCols = cols.Where(c => !pk.Contains(c, StringComparer.OrdinalIgnoreCase)).ToArray();
        var setClause = string.Join(", ", updateCols.Select(c => $"{c} = EXCLUDED.{c}"));
        return $"INSERT INTO {table} ({colList}) VALUES ({vals}) ON CONFLICT ({conflictCols}) DO UPDATE SET {setClause};";
    }

    /// <inheritdoc/>
    public string TranslateSql(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);
        return IsParamPattern.Replace(sql, m =>
        {
            var isNot = m.Groups[1].Success;
            var param = m.Groups[2].Value;
            return isNot ? $"IS DISTINCT FROM {param}" : $"IS NOT DISTINCT FROM {param}";
        });
    }

    /// <inheritdoc/>
    public void ClearPool()
    {
        using var conn = new NpgsqlConnection(_connectionString);
        NpgsqlConnection.ClearPool(conn);
    }
}
