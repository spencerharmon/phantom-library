using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using E = Jellyfin.Plugin.PhantomLibrary.State.Db.RowExpression;

namespace Jellyfin.Plugin.PhantomLibrary.State.Db;

/// <summary>
/// PostgreSQL availability expand. No legacy row is rewritten during DDL. Rows
/// with bucket -1 are admitted by the bounded scheduling sweep, never claimed
/// using a guessed due time (an old writer may still own their lease).
/// </summary>
internal static class AvailabilitySchedule
{
    internal const string Name = "availability_schedule_v1";
    internal const int BucketCount = 6;
    internal static readonly AdditiveRowProjection Projection = new(Name, 1, "availability_items",
        new RowProjectionColumn("claim_bucket", -1,
            E.Choose(E.GreaterThan(E.NumberColumn("priority"), E.Number(99)), E.Number(0),
            E.Choose(E.GreaterThan(E.NumberColumn("priority"), E.Number(0)), E.Number(1),
            E.Choose(E.IsNull(E.NumberColumn("checked_at")), E.Number(2),
            E.Choose(E.Equal(E.TextColumn("status"), E.Text("available")), E.Number(3),
            E.Choose(E.Equal(E.TextColumn("status"), E.Text("unavailable")), E.Number(4), E.Number(5))))))),
        new RowProjectionColumn("claim_due", 0,
            E.Greatest(E.Coalesce(E.NumberColumn("next_check_at"), E.Number(0)),
                E.Coalesce(E.NumberColumn("lease_until"), E.Number(0)))));

    internal static readonly string[] Statements =
    {
        "CREATE TABLE IF NOT EXISTS availability_reschedule (id INTEGER PRIMARY KEY, policy TEXT NOT NULL, sweep_policy TEXT NOT NULL, cursor_tmdb INTEGER NOT NULL DEFAULT -1, cursor_type TEXT NOT NULL DEFAULT '', cursor_season INTEGER NOT NULL DEFAULT -1, cursor_episode INTEGER NOT NULL DEFAULT -1, next_slice BIGINT NOT NULL DEFAULT 0, scanning INTEGER NOT NULL DEFAULT 1);",
        "CREATE INDEX CONCURRENTLY IF NOT EXISTS idx_availability_claim_v1 ON availability_items (type, claim_bucket, claim_due, tmdb_id, season, episode);",
    };

    internal static Task<bool> ExpandAsync(NpgsqlConnection connection, CancellationToken ct) =>
        SchemaExpandMigrator.ApplyAsync(connection, Name, Statements, new[] { Projection }, ct);
}
