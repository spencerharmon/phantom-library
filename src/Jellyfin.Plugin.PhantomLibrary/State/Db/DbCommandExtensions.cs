using System;
using System.Data;
using System.Data.Common;

namespace Jellyfin.Plugin.PhantomLibrary.State.Db;

/// <summary>
/// Provider-agnostic parameter-binding helper. <see cref="Microsoft.Data.Sqlite.SqliteParameterCollection"/>
/// and Npgsql's parameter collection both ship their own <c>AddWithValue</c>, but
/// neither is declared on the shared <see cref="DbParameterCollection"/> base type,
/// so code written against the generic <see cref="DbCommand"/> abstraction (as
/// <see cref="PhantomDb"/> is, to run unchanged against either backend) cannot call
/// either directly. This extension fills that gap using only members
/// <see cref="DbCommand"/> itself declares (<see cref="DbCommand.CreateParameter"/>),
/// so it works identically for a <c>SqliteCommand</c> or an <c>NpgsqlCommand</c>.
/// </summary>
public static class DbCommandExtensions
{
    /// <summary>
    /// Adds a named parameter with the given value (or <see cref="DBNull.Value"/> for
    /// a <see langword="null"/> value) to <paramref name="cmd"/>.
    /// </summary>
    /// <param name="cmd">The command to add the parameter to.</param>
    /// <param name="name">The parameter name (matching the placeholder in <see cref="DbCommand.CommandText"/>).</param>
    /// <param name="value">The value to bind.</param>
    /// <returns>The created parameter.</returns>
    public static DbParameter AddWithValue(this DbCommand cmd, string name, object? value)
    {
        ArgumentNullException.ThrowIfNull(cmd);
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        // Set DbType from the CLR value type. Without it Npgsql sends the parameter as `unknown`,
        // which Postgres rejects with 42P08 "could not determine data type of parameter" wherever it
        // cannot infer the type from context (SQLite tolerates untyped params; Postgres does not).
        // DbType is provider-agnostic — Microsoft.Data.Sqlite honors it too. A null (DBNull) value has
        // no CLR type to infer; those rely on the surrounding column/expression context.
        switch (value)
        {
            case null: break;
            case int: p.DbType = DbType.Int32; break;
            case long: p.DbType = DbType.Int64; break;
            case short: p.DbType = DbType.Int16; break;
            case byte: p.DbType = DbType.Byte; break;
            case bool: p.DbType = DbType.Boolean; break;
            case string: p.DbType = DbType.String; break;
            case double: p.DbType = DbType.Double; break;
            case float: p.DbType = DbType.Single; break;
            case decimal: p.DbType = DbType.Decimal; break;
            case Guid: p.DbType = DbType.Guid; break;
            case DateTimeOffset: p.DbType = DbType.DateTimeOffset; break;
            case DateTime: p.DbType = DbType.DateTime; break;
            case byte[]: p.DbType = DbType.Binary; break;
            default: break;
        }
        cmd.Parameters.Add(p);
        return p;
    }
}
