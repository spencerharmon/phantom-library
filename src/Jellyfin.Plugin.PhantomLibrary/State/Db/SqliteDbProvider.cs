using System;
using System.Data.Common;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.PhantomLibrary.State.Db;

/// <summary>
/// The compiled-in default SQLite backend for <see cref="PhantomDb"/> — one file
/// per color, unchanged from pre-provider-abstraction behaviour.
/// </summary>
public sealed class SqliteDbProvider : IPhantomDbProvider
{
    private readonly string _connectionString;

    /// <summary>
    /// Initializes a new instance of the <see cref="SqliteDbProvider"/> class.
    /// </summary>
    /// <param name="dbPath">Path to the SQLite database file.</param>
    public SqliteDbProvider(string dbPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var b = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        };
        _connectionString = b.ToString();
    }

    /// <inheritdoc/>
    public PhantomDbBackend Backend => PhantomDbBackend.Sqlite;

    /// <inheritdoc/>
    public DbConnection CreateConnection() => new SqliteConnection(_connectionString);

    /// <inheritdoc/>
    public async Task PrepareConnectionAsync(DbConnection conn, CancellationToken ct)
    {
        await using (var pragma = conn.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=WAL;";
            await pragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        // Cross-process writers (N Jellyfin replicas sharing one phantom.db, per the
        // multiwriter audit) hit SQLITE_BUSY immediately without this — see
        // p4-phantomdb-multiwriter-safety-fixes. Postgres has no pragma equivalent
        // (MVCC + normal lock-wait semantics apply instead), so this is SQLite-only.
        await using (var busyPragma = conn.CreateCommand())
        {
            busyPragma.CommandText = "PRAGMA busy_timeout=5000;";
            await busyPragma.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public async Task<int> ReadSchemaVersionAsync(DbConnection conn, CancellationToken ct)
    {
        await using var v = conn.CreateCommand();
        v.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await v.ExecuteScalarAsync(ct).ConfigureAwait(false) ?? 0, CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public async Task WriteSchemaVersionAsync(DbConnection conn, DbTransaction tx, int version, CancellationToken ct)
    {
        await using var sv = conn.CreateCommand();
        sv.Transaction = tx;
        sv.CommandText = $"PRAGMA user_version = {version};";
        await sv.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public string TranslateUpsertSql(string sqliteUpsertSql) => sqliteUpsertSql;

    /// <inheritdoc/>
    public string TranslateSql(string sql) => sql;

    /// <inheritdoc/>
    public void ClearPool()
    {
        // Clear ONLY this database's connection pool, never the process-global
        // pool. Microsoft.Data.Sqlite pools are keyed on the exact connection
        // string, so ClearPool(conn) releases just the pooled sqlite3 handles for
        // THIS instance's DataSource, letting the (unique, per-instance) file be
        // deleted afterwards.
        //
        // The previous SqliteConnection.ClearAllPools() was process-global:
        // disposing one PhantomDb tore down the pooled connections of EVERY other
        // live PhantomDb. Under xUnit's default per-class parallelism this raced
        // concurrent test classes — one class's teardown disposed the sqlite3
        // handle another class was mid-query on, surfacing as
        // "System.ObjectDisposedException: Cannot access a disposed object.
        // Object name: 'SQLitePCL.sqlite3'." Scoping the clear to this connection
        // string removes the cross-instance race entirely.
        using var conn = new SqliteConnection(_connectionString);
        SqliteConnection.ClearPool(conn);
    }
}
