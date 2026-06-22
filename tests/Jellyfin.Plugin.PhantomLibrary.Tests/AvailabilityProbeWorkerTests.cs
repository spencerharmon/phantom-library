using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
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
    public async Task StartAsync_DisabledConfigurationStillArmsTimerForLaterEnable()
    {
        using var db = await NewDbAsync();
        var cfg = Config();
        cfg.AvailabilityProbeEnabled = false;
        var worker = BuildWorker(db, cfg, new EmptyIndexer());

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var timerField = typeof(AvailabilityProbeWorker).GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingFieldException(nameof(AvailabilityProbeWorker), "_timer");
            Assert.NotNull(timerField.GetValue(worker));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [Fact]
    public async Task Tick_PrefersSeriesExpansionSoMovieBacklogDoesNotStarveTv()
    {
        using var db = await NewDbAsync();
        await SeedMovieAsync(db, 99000010);
        await SeedSeriesAsync(db, 99000100);
        await db.SetImdbIdAsync(99000010, "movie", "tt99000010", CancellationToken.None);
        var cfg = Config();
        cfg.AvailabilityMaxBatchSize = 1;
        var tmdb = new Mock<ITmdbClient>(MockBehavior.Loose);
        tmdb.Setup(t => t.GetSeriesAsync(99000100, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TmdbSeriesDetails(
                99000100,
                "TV Parity Series",
                "TV Parity Series",
                string.Empty,
                string.Empty,
                "2020-01-01",
                "2020-01-01",
                0,
                0,
                Array.Empty<string>(),
                string.Empty,
                1,
                1,
                Array.Empty<string>(),
                "tt99000100"));
        tmdb.Setup(t => t.GetSeasonAsync(99000100, 1, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TmdbSeasonDetails
            {
                SeriesTmdbId = 99000100,
                SeasonNumber = 1,
                Episodes = new List<TmdbEpisodeSummary>
                {
                    new() { Id = 990001001, SeasonNumber = 1, EpisodeNumber = 1, Name = "Pilot", AirDate = "2020-01-01" },
                },
            });
        var worker = BuildWorker(db, cfg, new EmptyIndexer(), tmdb.Object);

        await InvokeTickAsync(worker);

        Assert.Equal(1, await CountEpisodeAvailabilityRowsAsync());
        Assert.Equal(0, await ReadAttemptCountAsync(99000010, "movie", -1, -1));
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

    private static Task SeedMovieAsync(PhantomDb db, int tmdbId)
        => SeedCatalogueAsync(db, tmdbId, "movie", "Availability Test Movie");

    private static Task SeedSeriesAsync(PhantomDb db, int tmdbId)
        => SeedCatalogueAsync(db, tmdbId, "series", "TV Parity Series");

    private static async Task SeedCatalogueAsync(PhantomDb db, int tmdbId, string type, string title)
    {
        await db.UpsertCatalogueHitsAsync(new[]
        {
            new TmdbMetadataRow(
                tmdbId,
                type,
                title,
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
        return BuildWorker(db, cfg, indexer, tmdb.Object);
    }

    private static AvailabilityProbeWorker BuildWorker(PhantomDb db, PluginConfiguration cfg, IIndexerClient indexer, ITmdbClient tmdb)
    {
        var externalIds = new TmdbExternalIdResolver(db, tmdb, NullLogger<TmdbExternalIdResolver>.Instance);
        var scorer = new QualityScorer(NullLogger<QualityScorer>.Instance);
        var selector = new MagnetSelector(new[] { indexer }, scorer, NullLogger<MagnetSelector>.Instance, () => cfg);
        return new AvailabilityProbeWorker(
            db,
            selector,
            externalIds,
            tmdb,
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

    private static async Task InvokeTickAsync(AvailabilityProbeWorker worker)
    {
        var method = typeof(AvailabilityProbeWorker).GetMethod("TickAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(AvailabilityProbeWorker), "TickAsync");
        var task = (Task)(method.Invoke(worker, new object[] { CancellationToken.None })
            ?? throw new InvalidOperationException("TickAsync returned null"));
        await task;
    }

    private async Task<int> CountRowsAsync(string table)
    {
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM " + table + ";";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<int> CountEpisodeAvailabilityRowsAsync()
    {
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM availability_items WHERE type='episode';";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task<int> ReadAttemptCountAsync(int tmdbId, string type, int season, int episode)
    {
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT attempt_count FROM availability_items WHERE tmdb_id=$tmdb AND type=$type AND season=$season AND episode=$episode;";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$season", season);
        cmd.Parameters.AddWithValue("$episode", episode);
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
