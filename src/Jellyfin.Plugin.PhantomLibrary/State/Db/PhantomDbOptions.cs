using System;
using Npgsql;

namespace Jellyfin.Plugin.PhantomLibrary.State.Db;

/// <summary>
/// Resolves the <c>PHANTOM_POSTGRES_*</c> environment-variable config surface
/// added by <c>p4-chart-postgres-wiring</c> (the plugin's own logical DB — a
/// separate Postgres database on the same server as <c>Jellyfin.Pgsql</c>'s
/// <c>POSTGRES_*</c> vars, which this plugin does not read). SQLite remains the
/// default: Postgres activates only when <c>PHANTOM_POSTGRES_HOST</c> is set.
/// </summary>
public static class PhantomDbOptions
{
    /// <summary>
    /// Builds an Npgsql connection string from the <c>PHANTOM_POSTGRES_*</c>
    /// environment variables, or returns <see langword="null"/> if
    /// <c>PHANTOM_POSTGRES_HOST</c> is unset (i.e. Postgres is not enabled —
    /// callers should fall back to the SQLite default).
    /// </summary>
    /// <returns>An Npgsql connection string, or <see langword="null"/>.</returns>
    public static string? TryBuildPostgresConnectionString()
    {
        var host = Environment.GetEnvironmentVariable("PHANTOM_POSTGRES_HOST");
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        var csb = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Database = Environment.GetEnvironmentVariable("PHANTOM_POSTGRES_DB") ?? "phantom",
            Username = Environment.GetEnvironmentVariable("PHANTOM_POSTGRES_USER") ?? "phantom",
            Password = Environment.GetEnvironmentVariable("PHANTOM_POSTGRES_PASSWORD"),
        };

        var portRaw = Environment.GetEnvironmentVariable("PHANTOM_POSTGRES_PORT");
        if (!string.IsNullOrWhiteSpace(portRaw) && int.TryParse(portRaw, out var port))
        {
            csb.Port = port;
        }

        return csb.ToString();
    }
}
