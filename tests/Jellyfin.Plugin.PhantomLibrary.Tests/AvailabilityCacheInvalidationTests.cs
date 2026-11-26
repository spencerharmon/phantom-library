using System.Reflection;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.Scheduled;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public sealed class AvailabilityCacheInvalidationTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"phantom-cache-invalidation-{Guid.NewGuid():N}.db");
    private const int TmdbId = 99000001;
    private static readonly MagnetCandidate Candidate = new(
        "magnet:?xt=urn:btih:" + new string('a', 40), new string('a', 40), 5000000000L, 40, "Torrentio");

    public static IEnumerable<object[]> Cases()
    {
        foreach (var type in new[] { "movie", "episode" })
        {
            // Cache state is independent of availability status. Recovery must
            // invalidate, but ordinary available refreshes must not churn caches.
            yield return new object[] { type, "available", "missing", "available", true };
            yield return new object[] { type, "available", "expired", "available", true };
            yield return new object[] { type, "available", "fresh", "available", false };
            yield return new object[] { type, "available", "never-assessed", "available", false };
            yield return new object[] { type, "available", "missing", "reconciled", true };
            yield return new object[] { type, "available", "missing", "write-failure", false };
            foreach (var status in new[] { "unknown", "unavailable", "available" })
            {
                if (status != "available")
                {
                    yield return new object[] { type, status, "missing", "available", true };
                }
                yield return new object[] { type, status, "missing", "unavailable", status == "available" };
                yield return new object[] { type, status, "missing", "transient", false };
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Probe_InvalidatesOnlyAffectedChannelAfterVisibilityChange(
        string type, string status, string cache, string outcome, bool invalidates)
    {
        using var db = new PhantomDb(_path);
        var ct = CancellationToken.None;
        var now = DateTimeOffset.UtcNow;
        var season = type == "movie" ? -1 : 1;
        var episode = season;
        var metadataType = type == "movie" ? "movie" : "series";
        await db.UpsertCatalogueHitsAsync(new[]
        {
            new TmdbMetadataRow(TmdbId, metadataType, "Cache Recovery", 2020,
                null, null, null, null, null, null, null, now),
        }, 1, now, ct);
        await db.SetImdbIdAsync(TmdbId, metadataType, "tt99000001", ct);
        await using var conn = new SqliteConnection("Data Source=" + _path);
        await conn.OpenAsync(ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO availability_items
                (tmdb_id,type,season,episode,status,next_check_at,candidate_magnet)
                VALUES (@id,@type,@s,@s,@status,0,@magnet)
                ON CONFLICT(tmdb_id,type,season,episode) DO UPDATE SET
                    status=excluded.status,next_check_at=0,candidate_magnet=excluded.candidate_magnet;";
            cmd.Parameters.AddWithValue("@id", TmdbId);
            cmd.Parameters.AddWithValue("@type", type);
            cmd.Parameters.AddWithValue("@s", season);
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@magnet", cache == "never-assessed" ? DBNull.Value : Candidate.Magnet);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        if (cache is "fresh" or "expired")
        {
            await db.UpsertSourceCandidatesAsync(TmdbId, type, season, episode, "test",
                new[] { Candidate }, "prior_probe", TimeSpan.FromHours(1), ct);
            if (cache == "expired")
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "UPDATE source_candidates SET expires_at=0;";
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        if (outcome == "write-failure")
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = @"CREATE TRIGGER reject_candidate BEFORE INSERT ON source_candidates
                BEGIN SELECT RAISE(ABORT, 'test candidate persistence failure'); END;";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var cfg = new PluginConfiguration { SourcePickerPreset = "test", EpisodeReleaseDelayHours = 0 };
        var state = new ChannelStateProvider(db);
        var changedKind = type == "movie" ? ChannelStateProvider.KindMovies : ChannelStateProvider.KindShows;
        var otherKind = type == "movie" ? ChannelStateProvider.KindShows : ChannelStateProvider.KindMovies;
        var before = state.DataVersion(changedKind);
        var otherBefore = state.DataVersion(otherKind);
        var tmdb = Mock.Of<ITmdbClient>();
        var indexer = new Mock<IIndexerClient>();
        indexer.SetupGet(x => x.IsEnabled).Returns(true);
        indexer.SetupGet(x => x.IsAvailabilityOracle).Returns(true);
        var selector = new MagnetSelector(new[] { indexer.Object }, new QualityScorer(NullLogger<QualityScorer>.Instance),
            NullLogger<MagnetSelector>.Instance, () => cfg);
        var probes = 0;
        var reconciles = 0;
        using var worker = new AvailabilityProbeWorker(db, selector,
            new TmdbExternalIdResolver(db, tmdb, NullLogger<TmdbExternalIdResolver>.Instance), tmdb,
            state, NullLogger<AvailabilityProbeWorker>.Instance, () => cfg,
            probe: (_, _, _, _, _, _, _, _) =>
            {
                probes++;
                Assert.Equal(before, state.DataVersion(changedKind));
                return Task.FromResult(outcome switch
                {
                    "transient" => MagnetProbeResult.Transient("timeout", "test timeout"),
                    "unavailable" => MagnetProbeResult.DefinitiveUnavailable(),
                    "reconciled" => MagnetProbeResult.NoCapableIndexer("test abstention"),
                    _ => MagnetProbeResult.Available(new[] { Candidate }),
                });
            },
            reconcileProbe: (_, _, _, _, _, _, _, _) =>
            {
                reconciles++;
                return Task.FromResult(MagnetProbeResult.Available(new[] { Candidate }));
            });
        var probeMethod = typeof(AvailabilityProbeWorker).GetMethod("ProbeOneAvailabilityAsync",
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[] { typeof(PluginConfiguration), typeof(CancellationToken) }, null)!;
        Assert.True(await (Task<bool>)probeMethod.Invoke(worker, new object[] { cfg, ct })!);
        Assert.Equal(1, probes);
        Assert.Equal(outcome == "reconciled" ? 1 : 0, reconciles);
        Assert.Equal(invalidates, before != state.DataVersion(changedKind));
        Assert.Equal(otherBefore, state.DataVersion(otherKind));

        var expectedStatus = outcome switch
        {
            "available" or "reconciled" => "available",
            "unavailable" => "unavailable",
            _ => status,
        };
        await using var read = conn.CreateCommand();
        read.CommandText = "SELECT status FROM availability_items WHERE type=@type;";
        read.Parameters.AddWithValue("@type", type);
        Assert.Equal(expectedStatus, await read.ExecuteScalarAsync(ct));
        if (outcome is "available" or "reconciled")
        {
            var stored = Assert.Single(await db.ListSourceCandidatesAsync(TmdbId, type, season, episode, "test", false, ct));
            Assert.Equal(Candidate.Magnet, stored.Magnet);
            Assert.Equal("availability_probe", stored.Source);
        }
        if (outcome == "write-failure")
        {
            Assert.Empty(await db.ListSourceCandidatesAsync(TmdbId, type, season, episode, "test", false, ct));
        }
    }

    public void Dispose()
    {
        File.Delete(_path);
    }
}
