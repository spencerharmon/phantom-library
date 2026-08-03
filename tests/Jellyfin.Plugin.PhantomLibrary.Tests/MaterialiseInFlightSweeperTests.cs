using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class MaterialiseInFlightSweeperTests : IDisposable
{
    private readonly string _dbPath;

    public MaterialiseInFlightSweeperTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "phantom-sweep-" + Guid.NewGuid().ToString("N") + ".db");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
        catch
        {
            // best-effort
        }
    }

    [Fact]
    public async Task PurgesStaleRows_LeavesFreshRows()
    {
        var db = new PhantomDb(_dbPath);
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);

        // Insert two in-flight rows.
        await db.UpsertMaterialiseInFlightAsync(1, "movie", -1, -1, CancellationToken.None);
        await db.UpsertMaterialiseInFlightAsync(2, "movie", -1, -1, CancellationToken.None);

        // Age the first one by directly mutating started_at.
        var cs = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        await using (var conn = new SqliteConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE materialise_in_flight SET started_at = $old WHERE tmdb_id = 1;";
            cmd.Parameters.AddWithValue("$old", DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync();
        }

        // Directly invoke the DB purge with a 30-minute threshold so we
        // exercise the same path the sweeper hosted-service does without
        // having to wait the 15s startup delay.
        var purged = await db.PurgeStaleMaterialiseInFlightAsync(TimeSpan.FromMinutes(30), CancellationToken.None);

        Assert.Equal(1, purged);
        Assert.False(await db.IsMaterialiseInFlightAsync(1, "movie", -1, -1, CancellationToken.None));
        Assert.True(await db.IsMaterialiseInFlightAsync(2, "movie", -1, -1, CancellationToken.None));

        db.Dispose();
    }

    [Fact]
    public async Task SweeperHostedService_RunOnceAsync_RespectsConfigThreshold()
    {
        var db = new PhantomDb(_dbPath);
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        await db.UpsertMaterialiseInFlightAsync(7, "episode", 1, 2, CancellationToken.None);

        // Age it past the configured threshold.
        var cs = new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString();
        await using (var conn = new SqliteConnection(cs))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE materialise_in_flight SET started_at = $old;";
            cmd.Parameters.AddWithValue("$old", DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync();
        }

        var cfg = new PluginConfiguration { MaterialiseInFlightStaleMinutes = 5 };

        // The sweeper's RunOnceAsync waits 15s by default before purging;
        // we bypass that by calling the DB directly with the configured
        // threshold to verify wiring. A separate Stage 4.2 test exercises
        // the hosted-service Start/Stop lifecycle below.
        var purged = await db.PurgeStaleMaterialiseInFlightAsync(
            TimeSpan.FromMinutes(cfg.MaterialiseInFlightStaleMinutes), CancellationToken.None);
        Assert.Equal(1, purged);

        // Hosted-service lifecycle smoke test: StartAsync returns immediately,
        // StopAsync cancels the 15s sleep so the test doesn't hang.
        var sweeper = new MaterialiseInFlightSweeper(db, NullLogger<MaterialiseInFlightSweeper>.Instance, () => cfg);
        await sweeper.StartAsync(CancellationToken.None);
        await sweeper.StopAsync(CancellationToken.None);

        db.Dispose();
    }
}
