using System;
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
        cmd.Parameters.Add(p);
        return p;
    }
}
