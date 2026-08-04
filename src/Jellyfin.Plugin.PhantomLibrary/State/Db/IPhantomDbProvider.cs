using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.PhantomLibrary.State.Db;

/// <summary>
/// Which physical database engine a <see cref="PhantomDb"/> instance is backed by.
/// </summary>
public enum PhantomDbBackend
{
    /// <summary>Per-color SQLite file (the compiled-in default).</summary>
    Sqlite,

    /// <summary>Shared PostgreSQL logical database (opt-in, see <c>p4-chart-postgres-wiring</c>).</summary>
    Postgres,
}

/// <summary>
/// Backend-specific glue <see cref="PhantomDb"/> needs so its SQL (written once,
/// parameterised with <c>@name</c> placeholders that both Microsoft.Data.Sqlite and
/// Npgsql accept) can run unchanged against either engine. The two engines differ in
/// exactly three ways PhantomDb's SQL cannot itself paper over:
/// <list type="bullet">
/// <item>connection creation/pooling,</item>
/// <item>schema-version bookkeeping (SQLite has a native <c>PRAGMA user_version</c>
/// slot; Postgres has no equivalent, so a one-row <c>phantom_schema_meta</c> table
/// stands in for it), and</item>
/// <item>SQLite's non-standard <c>INSERT OR REPLACE</c> / <c>INSERT OR IGNORE</c>
/// upsert syntax, which has no Postgres equivalent and must become
/// <c>INSERT ... ON CONFLICT (...) DO UPDATE/NOTHING</c>.</item>
/// </list>
/// Every other statement in <see cref="PhantomDb"/> (plain SELECT/UPDATE/DELETE/INSERT,
/// and the schema DDL itself) is deliberately written using only column types and
/// syntax both engines share, so it needs no per-backend translation at all.
/// </summary>
public interface IPhantomDbProvider
{
    /// <summary>Gets which engine this provider talks to.</summary>
    PhantomDbBackend Backend { get; }

    /// <summary>Creates a new (unopened) connection.</summary>
    DbConnection CreateConnection();

    /// <summary>
    /// Runs any engine-specific per-connection setup (SQLite's WAL journal mode and
    /// cross-process <c>busy_timeout</c>). A no-op for Postgres, which has no
    /// equivalent pragmas and instead relies on normal MVCC/lock-wait behaviour.
    /// </summary>
    Task PrepareConnectionAsync(DbConnection conn, CancellationToken ct);

    /// <summary>
    /// Reads the current schema version (0 for a fresh/never-initialised database),
    /// creating whatever bookkeeping object holds it if absent.
    /// </summary>
    Task<int> ReadSchemaVersionAsync(DbConnection conn, CancellationToken ct);

    /// <summary>Durably records the schema version within the given transaction.</summary>
    Task WriteSchemaVersionAsync(DbConnection conn, DbTransaction tx, int version, CancellationToken ct);

    /// <summary>
    /// Rewrites a SQLite-flavoured <c>INSERT OR REPLACE</c> / <c>INSERT OR IGNORE</c>
    /// statement into the target engine's native upsert syntax. A no-op for SQLite
    /// (the input syntax already is SQLite's own).
    /// </summary>
    string TranslateUpsertSql(string sqliteUpsertSql);

    /// <summary>
    /// Rewrites SQLite's NULL-safe <c>IS @param</c> / <c>IS NOT @param</c> equality
    /// operators (legal in SQLite, where <c>IS</c> is a general NULL-safe equality
    /// operator) into the target engine's equivalent. A no-op for SQLite. Postgres
    /// restricts <c>IS</c> to <c>NULL</c>/<c>TRUE</c>/<c>FALSE</c>/<c>UNKNOWN</c> and
    /// needs the standard-SQL <c>IS [NOT] DISTINCT FROM</c> NULL-safe operators instead.
    /// </summary>
    string TranslateSql(string sql);

    /// <summary>
    /// Releases pooled connections for THIS instance's connection string only —
    /// never a process-global pool clear (see <see cref="PhantomDb.Dispose"/>).
    /// </summary>
    void ClearPool();
}
