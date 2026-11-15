using System.Diagnostics.Metrics;
using System.Reflection;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Diagnostics;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.Scheduled;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Npgsql;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

[CollectionDefinition("P0 metrics", DisableParallelization = true)]
public sealed class P0MetricsCollection;

[Collection("P0 metrics")]
public sealed class P0ObservabilityTests
{
    [Fact]
    public async Task P0Observability_PostgresCommittedOutcomesBacklogAndDisabledWorker()
    {
        var dsn = Environment.GetEnvironmentVariable("PHANTOM_TEST_POSTGRES_DSN");
        Assert.False(string.IsNullOrWhiteSpace(dsn), "Real disposable PostgreSQL is required, never skip this regression");
        var schema = "p0_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(dsn);
        await admin.OpenAsync();
        await using var sql = admin.CreateCommand();
        sql.CommandText = $"CREATE SCHEMA {schema}";
        await sql.ExecuteNonQueryAsync();
        try
        {
            var scoped = new NpgsqlConnectionStringBuilder(dsn) { SearchPath = schema }.ToString();
            using var db = PhantomDb.CreatePostgres(scoped);
            await db.SetMetaAsync("init", "1", default);
            var records = new List<(string Name, long Value, Dictionary<string, object?> Tags)>();
            using var listener = new MeterListener();
            listener.InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == PhantomFlowMetrics.MeterName && instrument.Name.StartsWith("phantom_availability_", StringComparison.Ordinal))
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                records.Add((instrument.Name, value, tags.ToArray().ToDictionary(p => p.Key, p => p.Value))));
            listener.Start();
            var cfg = new PluginConfiguration { AvailabilityProbeEnabled = false, AvailabilityMaxBatchSize = 2, AvailabilityYieldToUserSeconds = 0 };
            var policy = (string)typeof(AvailabilityProbeWorker).GetMethod("ComputePolicyHash", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { cfg })!;
            var now = DateTimeOffset.UtcNow;
            sql.CommandText = $@"INSERT INTO {schema}.availability_items
                (tmdb_id,type,season,episode,status,next_check_at,priority,probe_policy_hash,last_error_kind,lease_until)
                SELECT i, t, CASE WHEN t='movie' THEN -1 ELSE 1 END, CASE WHEN t='movie' THEN -1 ELSE i END,
                'unknown', CASE WHEN i=1 THEN @past ELSE @future END,
                CASE WHEN t='movie' THEN 10 ELSE 0 END, @policy,
                CASE WHEN i=2 THEN 'unreleased' ELSE NULL END,
                CASE WHEN i=3 THEN @future ELSE NULL END
                FROM generate_series(1,4) i CROSS JOIN (VALUES ('movie'),('episode')) media(t)";
            sql.Parameters.AddWithValue("past", now.AddMinutes(-1).ToUnixTimeSeconds());
            sql.Parameters.AddWithValue("future", now.AddHours(1).ToUnixTimeSeconds());
            sql.Parameters.AddWithValue("policy", policy);
            await sql.ExecuteNonQueryAsync();
            var tmdb = new Mock<ITmdbClient>().Object;
            var selector = new MagnetSelector(Array.Empty<IIndexerClient>(), new QualityScorer(NullLogger<QualityScorer>.Instance),
                NullLogger<MagnetSelector>.Instance, () => cfg);
            using var worker = new AvailabilityProbeWorker(db, selector,
                new TmdbExternalIdResolver(db, tmdb, NullLogger<TmdbExternalIdResolver>.Instance), tmdb,
                new ChannelStateProvider(db), NullLogger<AvailabilityProbeWorker>.Instance, () => cfg);
            var tick = typeof(AvailabilityProbeWorker).GetMethod("TickAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await (Task)tick.Invoke(worker, new object[] { CancellationToken.None })!;
            listener.RecordObservableInstruments();
            var backlog = records.Where(r => r.Name == "phantom_availability_backlog").ToArray();
            Assert.Equal(8, backlog.Length);
            Assert.All(backlog, r => Assert.Equal(1, r.Value));
            Assert.DoesNotContain(records, r => r.Name == "phantom_availability_claims_total" && r.Value > 0);
            var stamp = records.Single(r => r.Name == "phantom_availability_backlog_observed_at").Value;
            records.Clear();

            foreach (var type in new[] { "movie", "episode" })
            {
                var lease = await db.ClaimDueAvailabilityAsync("test-worker", TimeSpan.FromMinutes(1), now, policy, default, type);
                Assert.NotNull(lease);
                Assert.Equal(type == "movie" ? "priority" : "background", lease.ClaimLane);
                Assert.Null(await db.ClaimDueAvailabilityAsync("other-worker", TimeSpan.FromMinutes(1), now, policy, default, type));
                Assert.False(await db.CompleteAvailabilityProbeAsync(lease with { LeaseOwner = "wrong-owner" }, "unavailable", now,
                    now.AddHours(1), policy, null, null, null, default));
                Assert.True(await db.CompleteAvailabilityProbeAsync(lease, "unavailable", now, now.AddHours(1), policy, null, null, null, default));
                Assert.False(await db.CompleteAvailabilityProbeAsync(lease, "unavailable", now, now.AddHours(1), policy, null, null, null, default));
            }

            Assert.Equal(2, records.Where(r => r.Name == "phantom_availability_claims_total").Sum(r => r.Value));
            Assert.Equal(2, records.Where(r => r.Name == "phantom_availability_completions_total").Sum(r => r.Value));
            Assert.All(records, r => Assert.Equal(2, r.Tags.Count));
            records.Clear();
            await db.RefreshAvailabilityBacklogAsync(policy, now.AddSeconds(10), default);
            listener.RecordObservableInstruments();
            Assert.Equal(stamp, records.Single(r => r.Name == "phantom_availability_backlog_observed_at").Value);
            Assert.Equal(2, records.Where(r => r.Name == "phantom_availability_backlog" && (string?)r.Tags["state"] == "eligible").Sum(r => r.Value));
            records.Clear();
            await db.RefreshAvailabilityBacklogAsync(policy, now.AddSeconds(301), default);
            listener.RecordObservableInstruments();
            Assert.Equal(0, records.Where(r => r.Name == "phantom_availability_backlog" && (string?)r.Tags["state"] == "eligible").Sum(r => r.Value));

            // The real enabled worker claims and commits transient outcomes for missing metadata.
            sql.Parameters.Clear();
            sql.CommandText = $"UPDATE {schema}.availability_items SET next_check_at=0, lease_until=NULL, probe_policy_hash=@policy";
            sql.Parameters.AddWithValue("policy", policy);
            await sql.ExecuteNonQueryAsync();
            cfg.AvailabilityProbeEnabled = true;
            records.Clear();
            await (Task)tick.Invoke(worker, new object[] { CancellationToken.None })!;
            Assert.Contains(records, r => r.Name == "phantom_availability_claims_total" && (string?)r.Tags["media_type"] == "movie");
            Assert.Contains(records, r => r.Name == "phantom_availability_claims_total" && (string?)r.Tags["media_type"] == "episode");
            Assert.Equal(2, records.Where(r => r.Name == "phantom_availability_completions_total").Sum(r => r.Value));
        }
        finally
        {
            sql.Parameters.Clear();
            sql.CommandText = $"DROP SCHEMA {schema} CASCADE";
            await sql.ExecuteNonQueryAsync();
        }
    }
}
