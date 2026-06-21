using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
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

public sealed class AvailabilityProbeWorkerTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "phantom-availability-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }

    [Fact]
    public async Task TransientAllFail_DoesNotWriteUnavailableOrMarker()
    {
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 99000001);
        await db.SetImdbIdAsync(99000001, "movie", "tt99000001", CancellationToken.None);
        var cfg = Config();
        var worker = BuildWorker(db, cfg, new TransientIndexer("timeout"));

        var didWork = await InvokeProbeOneAsync(worker, cfg);

        Assert.True(didWork);
        var (status, error) = await ReadAvailabilityAsync(99000001);
        Assert.Equal("unknown", status);
        Assert.Equal("indexer_partial_or_total_failure", error);
        Assert.Null(await db.IsMarkedUnavailableAsync(new UnavailableKey(99000001, "tt99000001", "movie", null, null), CancellationToken.None));
        Assert.Equal(0, await CountRowsAsync("magnet_failure_cache"));
    }

    [Fact]
    public async Task DefinitiveEmpty_WritesUnavailable()
    {
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 99000002);
        await db.SetImdbIdAsync(99000002, "movie", "tt99000002", CancellationToken.None);
        var cfg = Config();
        var worker = BuildWorker(db, cfg, new EmptyIndexer());

        var didWork = await InvokeProbeOneAsync(worker, cfg);

        Assert.True(didWork);
        var (status, _) = await ReadAvailabilityAsync(99000002);
        Assert.Equal("unavailable", status);
    }

    private async Task<PhantomDb> NewDbAsync()
    {
        var db = new PhantomDb(_dbPath);
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        return db;
    }

    private static async Task SeedMovieAsync(PhantomDb db, int tmdbId)
    {
        await db.UpsertCatalogueHitsAsync(new[]
        {
            new TmdbMetadataRow(
                tmdbId,
                "movie",
                "Availability Test Movie",
                2020,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                DateTimeOffset.UtcNow),
        }, sourceMask: 1, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    private static PluginConfiguration Config() => new()
    {
        AvailabilityProbeEnabled = true,
        AvailabilityLeaseMinutes = 1,
        AvailabilityTransientRetryMinutes = 5,
        AvailabilityAvailableTtlDays = 7,
        AvailabilityUnavailableTtlDays = 7,
        MagnetCacheTtlHours = 24,
        SourcePickerPreset = "test",
        MinSeeders = 1,
        MinSizeGb1080p = 1,
        MinSizeGb4K = 1,
    };

    private static AvailabilityProbeWorker BuildWorker(PhantomDb db, PluginConfiguration cfg, IIndexerClient indexer)
    {
        var tmdb = new Mock<ITmdbClient>(MockBehavior.Loose);
        var externalIds = new TmdbExternalIdResolver(db, tmdb.Object, NullLogger<TmdbExternalIdResolver>.Instance);
        var scorer = new QualityScorer(NullLogger<QualityScorer>.Instance);
        var selector = new MagnetSelector(new[] { indexer }, scorer, NullLogger<MagnetSelector>.Instance, () => cfg);
        return new AvailabilityProbeWorker(
            db,
            selector,
            externalIds,
            tmdb.Object,
            new ChannelStateProvider(db),
            NullLogger<AvailabilityProbeWorker>.Instance,
            () => cfg);
    }

    private static async Task<bool> InvokeProbeOneAsync(AvailabilityProbeWorker worker, PluginConfiguration cfg)
    {
        var method = typeof(AvailabilityProbeWorker).GetMethod("ProbeOneAvailabilityAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(AvailabilityProbeWorker), "ProbeOneAvailabilityAsync");
        var task = (Task<bool>)(method.Invoke(worker, new object[] { cfg, CancellationToken.None })
            ?? throw new InvalidOperationException("ProbeOneAvailabilityAsync returned null"));
        return await task;
    }

    private async Task<int> CountRowsAsync(string table)
    {
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM " + table + ";";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<(string Status, string? ErrorKind)> ReadAvailabilityAsync(int tmdbId)
    {
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status,last_error_kind FROM availability_items WHERE tmdb_id=$tmdb AND type='movie' AND season=-1 AND episode=-1;";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        await using var r = await cmd.ExecuteReaderAsync();
        Assert.True(await r.ReadAsync());
        return (r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1));
    }

    private sealed class TransientIndexer(string message) : IIndexerClient
    {
        public string Name => "transient";
        public bool IsEnabled => true;
        public Task<IReadOnlyList<IndexerCandidate>> SearchAsync(IndexerQuery query, CancellationToken ct)
            => throw new IndexerTransientException(message);
    }

    private sealed class EmptyIndexer : IIndexerClient
    {
        public string Name => "empty";
        public bool IsEnabled => true;
        public Task<IReadOnlyList<IndexerCandidate>> SearchAsync(IndexerQuery query, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<IndexerCandidate>>(Array.Empty<IndexerCandidate>());
    }
}
