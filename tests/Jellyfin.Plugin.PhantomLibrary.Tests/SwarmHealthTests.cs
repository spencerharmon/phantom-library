using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Scheduled;
using Jellyfin.Plugin.PhantomLibrary.State;
using Jellyfin.Plugin.PhantomLibrary.Swarm;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>ROI Priority 0.5 (dht-swarm-health): storage, measurement worker, scoring, shelf, client.</summary>
public sealed class SwarmHealthTests : IDisposable
{
    private const string ServiceUrl = "http://swarm-health.test:8095";
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), "phantom-swarm-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        try
        {
            File.Delete(_dbPath);
        }
        catch
        {
            // best-effort
        }
    }

    [Theory]
    [InlineData(null, 0.5f)]
    [InlineData(0, 0f)]
    [InlineData(7, 7f)]
    [InlineData(250, 100f)]
    public void Score_EncodesLiveThenUnmeasuredThenDeadForDescendingSort(int? peers, float expected)
        => Assert.Equal(expected, SwarmHealthScore.ToCriticRating(peers));

    [Fact]
    public void Score_DescendingOrder_IsLiveBySizeThenUnmeasuredThenDead()
    {
        var ordered = new int?[] { 0, null, 3, 80 }
            .OrderByDescending(p => SwarmHealthScore.ToCriticRating(p))
            .ToArray();
        Assert.Equal(new int?[] { 80, 3, null, 0 }, ordered);
    }

    [Fact]
    public void HealthiestRow_RanksLiveSwarmsBySize_SkipsDeadAndUnmeasured()
    {
        var items = new List<ChannelItemInfo>
        {
            Movie(1), Movie(2), Movie(3), Movie(4), Series(5),
        };
        var peers = new Dictionary<int, int> { [1] = 4, [2] = 0, [3] = 40, [5] = 9 };

        var row = SwarmHealthScore.HealthiestRow(items, peers, rowSize: 10);

        Assert.Equal(new[] { "movie_3", "series_5", "movie_1" }, row.Select(i => i.Id).ToArray());
        Assert.Single(SwarmHealthScore.HealthiestRow(items, peers, rowSize: 1));
    }

    [Fact]
    public async Task Db_DueItems_AreAvailableCandidatesWithoutFreshMeasurement_MoviesFirst()
    {
        using var db = await NewDbAsync();
        await InsertAvailabilityAsync(101, "movie", -1, -1, "available", Hash('a'));
        await InsertAvailabilityAsync(102, "movie", -1, -1, "unavailable", Hash('b'));
        await InsertAvailabilityAsync(103, "movie", -1, -1, "available", null);
        await InsertAvailabilityAsync(201, "episode", 1, 2, "available", Hash('C')); // mixed case stored
        await InsertAvailabilityAsync(104, "movie", -1, -1, "available", Hash('d'));
        var now = DateTimeOffset.UtcNow;
        await db.UpsertSwarmHealthAsync(new[] { new SwarmHealthMeasurement(Hash('d'), 104, "movie", -1, -1, 5, now) }, default);

        var due = await db.GetSwarmHealthDueItemsAsync(10, now.AddHours(-1), default);

        Assert.Equal(new[] { (101, "movie"), (201, "episode") }, due.Select(d => (d.TmdbId, d.Type)).ToArray());
        Assert.Equal(Hash('c'), due[1].InfoHash);

        // A measurement older than the staleness bound is due again.
        var later = await db.GetSwarmHealthDueItemsAsync(10, now.AddHours(1), default);
        Assert.Contains(later, d => d.TmdbId == 104);
    }

    [Fact]
    public async Task Db_PeersByTmdb_TakesBestFreshMeasurement_AggregatesEpisodesToSeries()
    {
        using var db = await NewDbAsync();
        var now = DateTimeOffset.UtcNow;
        await db.UpsertSwarmHealthAsync(
            new[]
            {
                new SwarmHealthMeasurement(Hash('1'), 7, "episode", 1, 1, 3, now),
                new SwarmHealthMeasurement(Hash('2'), 7, "episode", 1, 2, 12, now),
                new SwarmHealthMeasurement(Hash('3'), 8, "episode", 1, 1, 50, now.AddDays(-30)),
                new SwarmHealthMeasurement(Hash('4'), 9, "movie", -1, -1, 6, now),
            },
            default);
        await db.UpsertSwarmHealthAsync(new[] { new SwarmHealthMeasurement(Hash('4').ToUpperInvariant(), 9, "movie", -1, -1, 2, now) }, default);

        var series = await db.GetSwarmPeersByTmdbAsync("episode", now.AddDays(-7), default);
        var movies = await db.GetSwarmPeersByTmdbAsync("movie", now.AddDays(-7), default);

        Assert.Equal(12, series[7]);
        Assert.False(series.ContainsKey(8)); // stale measurement does not rank
        Assert.Equal(2, movies[9]);           // upsert is keyed case-insensitively and overwrites
    }

    [Fact]
    public async Task Worker_MeasuresDueCandidatesOncePerInfohash_AndStoresOnlyAnsweredOnes()
    {
        using var db = await NewDbAsync();
        await InsertAvailabilityAsync(301, "movie", -1, -1, "available", Hash('e'));
        await InsertAvailabilityAsync(400, "episode", 1, 1, "available", Hash('f')); // season pack:
        await InsertAvailabilityAsync(400, "episode", 1, 2, "available", Hash('f')); // same infohash
        await InsertAvailabilityAsync(302, "movie", -1, -1, "available", Hash('9'));
        var client = new FakeClient(new Dictionary<string, int> { [Hash('e')] = 14, [Hash('f')] = 0 });
        var worker = NewWorker(db, client, new Mock<ILibraryManager>(MockBehavior.Strict).Object);

        var stored = await worker.MeasureOnceAsync(Config(), default);

        Assert.Equal(2, stored);
        Assert.Single(client.Requests);
        Assert.Equal(3, client.Requests[0].Count); // e, f (deduped), 9
        var movies = await db.GetSwarmPeersByTmdbAsync("movie", DateTimeOffset.UtcNow.AddDays(-1), default);
        var series = await db.GetSwarmPeersByTmdbAsync("episode", DateTimeOffset.UtcNow.AddDays(-1), default);
        Assert.Equal(14, movies[301]);
        Assert.False(movies.ContainsKey(302)); // unanswered stays unmeasured (due again next tick)
        Assert.Equal(0, series[400]);
        Assert.Contains(await db.GetSwarmHealthDueItemsAsync(10, DateTimeOffset.UtcNow.AddHours(-1), default), d => d.TmdbId == 302);
    }

    [Fact]
    public async Task Worker_ServiceFailure_StoresNothing()
    {
        using var db = await NewDbAsync();
        await InsertAvailabilityAsync(501, "movie", -1, -1, "available", Hash('a'));
        var worker = NewWorker(db, new FakeClient(null), new Mock<ILibraryManager>(MockBehavior.Strict).Object);

        Assert.Equal(0, await worker.MeasureOnceAsync(Config(), default));
        Assert.Empty(await db.GetSwarmPeersByTmdbAsync("movie", DateTimeOffset.UtcNow.AddDays(-1), default));
    }

    [Fact]
    public async Task Worker_Sync_WritesPeersOntoPhantomBaseItems_SkipsUnchanged()
    {
        using var db = await NewDbAsync();
        var now = DateTimeOffset.UtcNow;
        await db.UpsertSwarmHealthAsync(
            new[]
            {
                new SwarmHealthMeasurement(Hash('a'), 11, "movie", -1, -1, 37, now),
                new SwarmHealthMeasurement(Hash('b'), 12, "movie", -1, -1, 0, now),
            },
            default);
        var live = new Movie { Id = Guid.NewGuid(), ExternalId = "movie_11" };
        var dead = new Movie { Id = Guid.NewGuid(), ExternalId = "movie_12", CriticRating = 5f };
        var unmeasured = new Movie { Id = Guid.NewGuid(), ExternalId = "movie_13" };
        var already = new Movie { Id = Guid.NewGuid(), ExternalId = "movie_14", CriticRating = SwarmHealthScore.UnmeasuredScore };
        var orphan = new Movie { Id = Guid.NewGuid(), ExternalId = "orphan_0123456789abcdef" };
        var lib = new Mock<ILibraryManager>(MockBehavior.Loose);
        lib.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ChannelIds.Contains(ChannelIds.Movies))))
            .Returns(new List<BaseItem> { live, dead, unmeasured, already, orphan });
        lib.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.ChannelIds.Contains(ChannelIds.Shows))))
            .Returns(new List<BaseItem>());
        var updated = new List<BaseItem>();
        lib.Setup(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Callback<BaseItem, BaseItem, ItemUpdateType, CancellationToken>((i, _, _, _) => updated.Add(i))
            .Returns(Task.CompletedTask);
        var worker = NewWorker(db, new FakeClient(new Dictionary<string, int>()), lib.Object);

        var written = await worker.SyncBaseItemsAsync(Config(), default);

        Assert.Equal(4, written);
        Assert.Equal(37f, live.CriticRating);
        Assert.Equal(0f, dead.CriticRating);
        Assert.Equal(SwarmHealthScore.UnmeasuredScore, unmeasured.CriticRating);
        Assert.Equal(SwarmHealthScore.UnmeasuredScore, orphan.CriticRating); // never left NULL (sorts first)
        Assert.DoesNotContain(already, updated);
    }

    [Fact]
    public async Task Rows_AppendHealthiestSwarmsShelf_OnlyWhenEnabledAndLive()
    {
        using var db = await NewDbAsync();
        await db.UpsertSwarmHealthAsync(new[] { new SwarmHealthMeasurement(Hash('a'), 21, "movie", -1, -1, 9, DateTimeOffset.UtcNow) }, default);
        var items = new List<ChannelItemInfo> { Movie(20), Movie(21) };
        var baseRows = Array.Empty<CuratedRow>();

        var enabled = await SwarmHealthRows.AppendHealthiestAsync(baseRows, items, db, "movie", Config(), default);
        var disabledCfg = Config();
        disabledCfg.SwarmHealthServiceUrl = string.Empty;
        var disabled = await SwarmHealthRows.AppendHealthiestAsync(baseRows, items, db, "movie", disabledCfg, default);

        var row = Assert.Single(enabled);
        Assert.Equal(CuratedRows.KeyHealthiestSwarms, row.Key);
        Assert.Equal("movie_21", Assert.Single(row.Items).Id);
        Assert.Empty(disabled);
    }

    [Fact]
    public async Task Client_PostsInfohashes_ParsesResults()
    {
        string? body = null;
        var handler = new LambdaHandler(async req =>
        {
            Assert.Equal("http://swarm-health.test:8095/measure", req.RequestUri!.ToString());
            body = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"results\":[{\"info_hash\":\"" + Hash('a') + "\",\"dht_peers\":8,\"measured_at\":1}],\"dht_nodes\":200}",
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var client = new SwarmHealthClient(new HttpClient(handler));

        var result = await client.MeasureAsync(ServiceUrl + "/", new[] { Hash('a'), Hash('b') }, default);

        Assert.Contains("\"info_hashes\":[\"" + Hash('a') + "\",\"" + Hash('b') + "\"]", body, StringComparison.Ordinal);
        Assert.Equal(8, result[Hash('a').ToUpperInvariant()]);
        Assert.False(result.ContainsKey(Hash('b')));
    }

    private static string Hash(char c) => new string(char.ToLowerInvariant(c), 40).Replace(char.ToLowerInvariant(c), c);

    private static ChannelItemInfo Movie(int tmdb) => new() { Id = ChannelItemId.ForMovie(tmdb).Encode(), Name = "m" + tmdb };

    private static ChannelItemInfo Series(int tmdb) => new() { Id = ChannelItemId.ForSeries(tmdb).Encode(), Name = "s" + tmdb };

    private static PluginConfiguration Config() => new()
    {
        SwarmHealthServiceUrl = ServiceUrl,
        SwarmHealthBatchSize = 50,
        SwarmHealthRecheckHours = 24,
        SwarmHealthRankWindowDays = 7,
        CuratedRowSize = 10,
    };

    private static SwarmHealthWorker NewWorker(PhantomDb db, SwarmHealthClient client, ILibraryManager lib)
        => new(db, client, lib, NullLogger<SwarmHealthWorker>.Instance, Config);

    private async Task<PhantomDb> NewDbAsync()
    {
        var db = new PhantomDb(_dbPath);
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        return db;
    }

    private async Task InsertAvailabilityAsync(int tmdbId, string type, int season, int episode, string status, string? infoHash)
    {
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO availability_items
                (tmdb_id, type, season, episode, status, next_check_at, priority, candidate_info_hash)
            VALUES ($tmdb,$type,$season,$episode,$status,$next,0,$hash);";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$season", season);
        cmd.Parameters.AddWithValue("$episode", episode);
        cmd.Parameters.AddWithValue("$status", status);
        cmd.Parameters.AddWithValue("$next", DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$hash", (object?)infoHash ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    private sealed class FakeClient : SwarmHealthClient
    {
        private readonly Dictionary<string, int>? _answers;

        public FakeClient(Dictionary<string, int>? answers)
            : base(new HttpClient())
        {
            _answers = answers;
        }

        public List<IReadOnlyCollection<string>> Requests { get; } = new();

        public override Task<IReadOnlyDictionary<string, int>> MeasureAsync(string baseUrl, IReadOnlyCollection<string> infoHashes, CancellationToken ct)
        {
            Requests.Add(infoHashes);
            if (_answers is null)
            {
                throw new HttpRequestException("service down");
            }

            IReadOnlyDictionary<string, int> result = infoHashes
                .Where(h => _answers.ContainsKey(h))
                .ToDictionary(h => h, h => _answers[h], StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(result);
        }
    }

    private sealed class LambdaHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _fn;

        public LambdaHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> fn) => _fn = fn;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _fn(request);
    }
}
