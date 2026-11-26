using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Api;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Playback;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Dto;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// A REAL HTTP gostream stand-in on a loopback port implementing the
/// gostream:phantom-player-stats-endpoint contract
/// (<c>GET /api/library/stats?hash=</c>, <c>X-Gostream-Token</c> auth, 404
/// <c>torrent_not_found</c> for unloaded hashes). The plugin's production
/// <see cref="GostreamClient"/> talks to it over real sockets.
/// </summary>
internal sealed class FakeGostreamStatsServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public FakeGostreamStatsServer(string token)
    {
        Token = token;
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        BaseUrl = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseUrl + "/");
        _listener.Start();
        _loop = Task.Run(LoopAsync);
    }

    public string BaseUrl { get; }

    public string Token { get; }

    /// <summary>hash → raw JSON body served with 200.</summary>
    public ConcurrentDictionary<string, string> Loaded { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, every stats request answers this status + body.</summary>
    public (int Status, string Body)? ForcedResponse { get; set; }

    public ConcurrentQueue<(string PathAndQuery, string? Token)> Requests { get; } = new();

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }

            var req = ctx.Request;
            var token = req.Headers["X-Gostream-Token"];
            Requests.Enqueue((req.Url!.PathAndQuery, token));
            int status;
            string body;
            if (req.Url.AbsolutePath != "/api/library/stats")
            {
                (status, body) = (404, "404 page not found");
            }
            else if (!string.Equals(token, Token, StringComparison.Ordinal))
            {
                (status, body) = (401, "{\"error\":\"missing_or_invalid_token\"}");
            }
            else if (ForcedResponse is { } forced)
            {
                (status, body) = forced;
            }
            else if (req.HttpMethod != "GET")
            {
                (status, body) = (405, "{\"error\":\"method_not_allowed\"}");
            }
            else
            {
                var hash = req.QueryString["hash"] ?? string.Empty;
                (status, body) = Loaded.TryGetValue(hash, out var json)
                    ? (200, json)
                    : (404, "{\"error\":\"torrent_not_found\"}");
            }

            var bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.StatusCode = status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.Headers["Cache-Control"] = "no-store";
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            ctx.Response.Close();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        try
        {
            _loop.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
        }

        _cts.Dispose();
    }
}

/// <summary>
/// p14-player-stats-overlay: real .NET controller tests — the production
/// <see cref="PhantomLibraryPlayerStatsController"/> + <see cref="PlayerStatsService"/>
/// + <see cref="GostreamClient"/> + a real SQLite <see cref="PhantomDb"/>, against
/// <see cref="FakeGostreamStatsServer"/> over real HTTP. Movie/episode parity.
/// </summary>
public sealed class PlayerStatsOverlayTests : IDisposable
{
    private const string Token = "gs-secret-token-p14-DO-NOT-LEAK";
    private const string Preset = "gostream-default";
    private const int MovieTmdb = 99000001;
    private const int SeriesTmdb = 99100001;
    private const string MovieHash = "1111111111111111111111111111111111111111";
    private const string EpisodeHash = "2222222222222222222222222222222222222222";
    private const string MovieRelease = "Phantom.Rig.Alpha.2024.1080p.WEB-DL.x264-RIG";
    private const string EpisodeRelease = "Phantom.Rig.Delta.S01E01.1080p.WEB-DL.x264-RIG";
    private const string MovieMagnet = "magnet:?xt=urn:btih:" + MovieHash + "&dn=Phantom+Rig+Alpha";
    private const string EpisodeMagnet = "magnet:?xt=urn:btih:" + EpisodeHash + "&dn=Phantom+Rig+Delta+S01E01";

    private static readonly Guid MovieItemId = Guid.Parse("0d3c1e0e-0000-4000-8000-000000000001");
    private static readonly Guid EpisodeItemId = Guid.Parse("0d3c1e0e-0000-4000-8000-000000000002");
    private static readonly Guid PhantomOnlyItemId = Guid.Parse("0d3c1e0e-0000-4000-8000-000000000003");
    private static readonly Guid LibraryItemId = Guid.Parse("0d3c1e0e-0000-4000-8000-000000000004");
    private static readonly Guid ExternalItemId = Guid.Parse("0d3c1e0e-0000-4000-8000-000000000005");
    private static readonly Guid UserId = Guid.Parse("8eb11ac1-9939-4621-896c-31d5cba4951c");
    private static readonly Guid OtherUserId = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private const string DeviceId = "phantom-test-device";

    private readonly string _dbPath;
    private readonly FakeGostreamStatsServer _gostream;
    private readonly List<SessionInfo> _sessions = new();
    private readonly Mock<ISessionManager> _sessionManager = new(MockBehavior.Loose);

    public PlayerStatsOverlayTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "phantom-p14-stats-" + Guid.NewGuid().ToString("N") + ".db");
        _gostream = new FakeGostreamStatsServer(Token);
        _sessionManager.SetupGet(m => m.Sessions).Returns(() => _sessions);

        var rec = new Mock<IRecordingsManager>(MockBehavior.Loose);
        rec.Setup(r => r.GetActiveRecordingInfo(It.IsAny<string>())).Returns((ActiveRecordingInfo?)null);
        Video.RecordingsManager = rec.Object;
    }

    public void Dispose()
    {
        _gostream.Dispose();
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch (IOException)
        {
        }
    }

    // ---------------------------------------------------------------- fixtures

    private static string StatsJson(string hash, string state = "streaming") => $$"""
        {"hash":"{{hash}}","peers":17,"seeders":9,"download_rate_bytes_per_sec":2621440,
         "bytes_completed":734003200,"size":2147483648,"read_offset":52428800,
         "buffered_ahead_bytes":33554432,"buffered_ahead_seconds":null,"stream_state":"{{state}}",
         "last_read_at":"2026-11-26T08:00:00Z","torrent_sampled_at":"2026-11-26T08:00:01Z",
         "stream_sampled_at":"2026-11-26T08:00:00Z"}
        """;

    private async Task<PhantomDb> NewSeededDbAsync()
    {
        var db = new PhantomDb(_dbPath);
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        var now = DateTimeOffset.UtcNow;

        // Movie: materialised + winning magnet cached + indexer release title.
        await db.InsertMaterialisedStateAsync(MovieTmdb, "movie", -1, -1, "/stubs/alpha.mkv", "/fuse/alpha.mkv", CancellationToken.None);
        await db.PutCachedMagnetAsync(
            new MagnetCacheKey(MovieTmdb, "tt99000001", "movie", null, null, Preset),
            new MagnetCacheEntry
            {
                Magnet = MovieMagnet,
                InfoHash = MovieHash.ToUpperInvariant(),
                Size = 2147483648L,
                Seeders = 120,
                Indexer = "rig-indexer",
                CachedAt = now,
                Ttl = TimeSpan.FromSeconds(1), // expired TTL must NOT hide the playing source
                Source = "user",
            },
            CancellationToken.None);
        await SeedCandidateAsync(MovieTmdb, "movie", -1, -1, MovieMagnet, MovieHash, MovieRelease);

        // Episode S01E01: same shape, real season/episode numbers.
        await db.InsertMaterialisedStateAsync(SeriesTmdb, "episode", 1, 1, "/stubs/delta-s01e01.mkv", "/fuse/delta-s01e01.mkv", CancellationToken.None);
        await db.PutCachedMagnetAsync(
            new MagnetCacheKey(SeriesTmdb, "tt99100001", "episode", 1, 1, Preset),
            new MagnetCacheEntry
            {
                Magnet = EpisodeMagnet,
                InfoHash = EpisodeHash,
                Size = 734003200L,
                Seeders = 45,
                Indexer = "rig-tv-indexer",
                CachedAt = now,
                Ttl = TimeSpan.FromDays(1),
                Source = "user",
            },
            CancellationToken.None);
        await SeedCandidateAsync(SeriesTmdb, "episode", 1, 1, EpisodeMagnet, EpisodeHash, EpisodeRelease);
        return db;
    }

    private async Task SeedCandidateAsync(int tmdb, string type, int season, int episode, string magnet, string hash, string title)
    {
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO source_candidates
            (tmdb_id,type,season,episode,preset,magnet,info_hash,indexer,title,seeders,size,rank,source,fetched_at,expires_at)
            VALUES ($tmdb,$type,$season,$episode,$preset,$magnet,$hash,'rig-indexer',$title,120,0,1,'test',$now,$exp);";
        cmd.Parameters.AddWithValue("$tmdb", tmdb);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$season", season);
        cmd.Parameters.AddWithValue("$episode", episode);
        cmd.Parameters.AddWithValue("$preset", Preset);
        cmd.Parameters.AddWithValue("$magnet", magnet);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$exp", DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();
    }

    private static Mock<ILibraryManager> Library()
    {
        var lib = new Mock<ILibraryManager>(MockBehavior.Loose);
        lib.Setup(l => l.GetItemById(MovieItemId)).Returns(new Movie
        {
            Id = MovieItemId, Name = "Phantom Rig Alpha", ExternalId = "movie_" + MovieTmdb, ChannelId = ChannelIds.Movies,
        });
        lib.Setup(l => l.GetItemById(EpisodeItemId)).Returns(new Episode
        {
            Id = EpisodeItemId, Name = "Phantom Rig Delta Episode 1", ExternalId = $"episode_{SeriesTmdb}_s1e1", ChannelId = ChannelIds.Shows,
        });
        lib.Setup(l => l.GetItemById(PhantomOnlyItemId)).Returns(new Movie
        {
            Id = PhantomOnlyItemId, Name = "Phantom Rig Charlie", ExternalId = "movie_99000003", ChannelId = ChannelIds.Movies, Tags = new[] { "phantom" },
        });
        lib.Setup(l => l.GetItemById(LibraryItemId)).Returns(new Movie { Id = LibraryItemId, Name = "Regular library movie" });
        lib.Setup(l => l.GetItemById(ExternalItemId)).Returns(new Movie
        {
            Id = ExternalItemId, Name = "External", ExternalId = "movie_99000004", ChannelId = ChannelIds.Movies, Tags = new[] { "external" },
        });
        return lib;
    }

    private GostreamClient Client(string token = Token, string? baseUrl = null)
        => new(
            new HttpClient { Timeout = TimeSpan.FromSeconds(10) },
            NullLogger<GostreamClient>.Instance,
            () => baseUrl ?? _gostream.BaseUrl,
            () => token);

    private PhantomLibraryPlayerStatsController Controller(
        PhantomDb db,
        Guid? userId = null,
        string? deviceId = DeviceId,
        bool admin = false,
        IGostreamClient? gostream = null)
    {
        var service = new PlayerStatsService(
            Library().Object,
            db,
            gostream ?? Client(),
            NullLogger<PlayerStatsService>.Instance,
            () => new PluginConfiguration { SourcePickerPreset = Preset });
        var ctrl = new PhantomLibraryPlayerStatsController(service, _sessionManager.Object);
        var claims = new List<Claim>
        {
            new("Jellyfin-UserId", (userId ?? UserId).ToString("N")),
            new(ClaimTypes.Role, admin ? "Administrator" : "User"),
        };
        if (deviceId is not null)
        {
            claims.Add(new Claim("Jellyfin-DeviceId", deviceId));
        }

        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) },
        };
        return ctrl;
    }

    private SessionInfo Playing(Guid itemId, Guid? userId = null, string deviceId = DeviceId, bool paused = false)
    {
        var s = new SessionInfo(_sessionManager.Object, NullLogger.Instance)
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId ?? UserId,
            DeviceId = deviceId,
            Client = "Jellyfin Web",
            LastActivityDate = DateTime.UtcNow,
            NowPlayingItem = new BaseItemDto { Id = itemId },
        };
        s.PlayState.MediaSourceId = itemId.ToString("N");
        s.PlayState.IsPaused = paused;
        s.PlayState.PositionTicks = 123_000_000;
        _sessions.Add(s);
        return s;
    }

    private static PlayerStatsResponse Body(ActionResult<PlayerStatsResponse> result, int expectedStatus = 200)
    {
        var obj = Assert.IsAssignableFrom<ObjectResult>(result.Result);
        Assert.Equal(expectedStatus, obj.StatusCode ?? 200);
        return Assert.IsType<PlayerStatsResponse>(obj.Value);
    }

    /// <summary>Serialises with Jellyfin's own API defaults (camelCase + WhenWritingNull).</summary>
    private static string HostJson(PlayerStatsResponse r)
        => JsonSerializer.Serialize(r, Jellyfin.Extensions.Json.JsonDefaults.CamelCaseOptions);

    // ---------------------------------------------------------------- movie / episode parity

    [Fact]
    public async Task Current_PlayingMaterialisedMovie_ReturnsSourceAndLiveGostreamStats()
    {
        using var db = await NewSeededDbAsync();
        _gostream.Loaded[MovieHash] = StatsJson(MovieHash);
        Playing(MovieItemId, paused: true);

        var res = Body(await Controller(db).Current(CancellationToken.None));

        Assert.Equal(PlayerStatsStatus.Ok, res.Status);
        Assert.True(res.Playing);
        Assert.True(res.IsPhantom);
        Assert.Equal("movie", res.Type);
        Assert.Equal(MovieTmdb, res.TmdbId);
        Assert.Null(res.Season);
        Assert.Null(res.Episode);
        Assert.Equal(MovieItemId.ToString("N"), res.ItemId);
        Assert.True(res.IsPaused);
        Assert.Equal(123_000_000, res.PositionTicks);
        Assert.NotNull(res.Source);
        Assert.Equal(MovieRelease, res.Source!.Release);
        Assert.Equal("candidate", res.Source.ReleaseSource);
        Assert.Equal(2147483648L, res.Source.SizeBytes);
        Assert.Equal("rig-indexer", res.Source.Indexer);
        Assert.Equal(MovieHash, res.Source.InfoHash); // uppercase cached hash normalised to gostream's lowercase form
        Assert.Equal(120, res.Source.SeedersAtSelection);
        Assert.NotNull(res.Stream);
        Assert.Equal("streaming", res.Stream!.State);
        Assert.Equal(17, res.Stream.Peers);
        Assert.Equal(9, res.Stream.Seeders);
        Assert.Equal(2621440d, res.Stream.DownloadRateBytesPerSec);
        Assert.Equal(734003200L, res.Stream.BytesCompleted);
        Assert.Equal(2147483648L, res.Stream.TorrentSize);
        Assert.Equal(52428800L, res.Stream.ReadOffset);
        Assert.Equal(33554432L, res.Stream.BufferedAheadBytes);
        Assert.Null(res.Stream.BufferedAheadSeconds); // gostream null stays null, never a fabricated 0
        Assert.Equal(DateTimeOffset.Parse("2026-11-26T08:00:00Z", System.Globalization.CultureInfo.InvariantCulture), res.Stream.LastReadAt);
        Assert.InRange(res.PollIntervalMs, 2000, 5000);

        // The plugin asked gostream for exactly this hash, with the server-side token.
        var call = Assert.Single(_gostream.Requests);
        Assert.Equal("/api/library/stats?hash=" + MovieHash, call.PathAndQuery);
        Assert.Equal(Token, call.Token);
    }

    [Fact]
    public async Task Current_PlayingMaterialisedEpisode_ReturnsSameShapeAsMovie()
    {
        using var db = await NewSeededDbAsync();
        _gostream.Loaded[EpisodeHash] = StatsJson(EpisodeHash, "stalled");
        Playing(EpisodeItemId);

        var res = Body(await Controller(db).Current(CancellationToken.None));

        Assert.Equal(PlayerStatsStatus.Ok, res.Status);
        Assert.True(res.IsPhantom);
        Assert.Equal("episode", res.Type);
        Assert.Equal(SeriesTmdb, res.TmdbId);
        Assert.Equal(1, res.Season);
        Assert.Equal(1, res.Episode);
        Assert.Equal(EpisodeRelease, res.Source!.Release);
        Assert.Equal(734003200L, res.Source.SizeBytes);
        Assert.Equal("rig-tv-indexer", res.Source.Indexer);
        Assert.Equal(EpisodeHash, res.Source.InfoHash);
        Assert.Equal("stalled", res.Stream!.State);
        Assert.Equal(17, res.Stream.Peers);
        Assert.Equal("/api/library/stats?hash=" + EpisodeHash, Assert.Single(_gostream.Requests).PathAndQuery);
    }

    [Fact]
    public async Task SerialisedPayload_NeverLeaksTokenOrGostreamUrl_AndKeepsExplicitNulls()
    {
        using var db = await NewSeededDbAsync();
        _gostream.Loaded[MovieHash] = StatsJson(MovieHash);
        Playing(MovieItemId);

        var json = HostJson(Body(await Controller(db).Current(CancellationToken.None)));

        Assert.DoesNotContain(Token, json, StringComparison.Ordinal);
        Assert.DoesNotContain(_gostream.BaseUrl, json, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/stubs/", json, StringComparison.Ordinal);
        Assert.DoesNotContain("/fuse/", json, StringComparison.Ordinal);
        Assert.DoesNotContain("token", json, StringComparison.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        var stream = doc.RootElement.GetProperty("stream");
        Assert.Equal(JsonValueKind.Null, stream.GetProperty("bufferedAheadSeconds").ValueKind); // host WhenWritingNull must not drop it
        Assert.Equal(17, stream.GetProperty("peers").GetInt32());
        Assert.Equal(MovieRelease, doc.RootElement.GetProperty("source").GetProperty("release").GetString());
        Assert.Equal("ok", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("season").ValueKind);
    }

    [Fact]
    public async Task ExpiredMagnetCacheTtl_StillReportsThePlayingSource()
    {
        using var db = await NewSeededDbAsync();
        await Task.Delay(1100); // the movie's cache row has a 1 s TTL
        _gostream.Loaded[MovieHash] = StatsJson(MovieHash);
        Playing(MovieItemId);

        var res = Body(await Controller(db).Current(CancellationToken.None));

        Assert.Equal(PlayerStatsStatus.Ok, res.Status);
        Assert.Equal(MovieHash, res.Source!.InfoHash);
    }

    [Fact]
    public async Task ReleaseFallsBackToMagnetDisplayName_WhenCandidateRowIsGone()
    {
        using var db = await NewSeededDbAsync();
        await using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString()))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM source_candidates;";
            await cmd.ExecuteNonQueryAsync();
        }

        _gostream.Loaded[EpisodeHash] = StatsJson(EpisodeHash);
        Playing(EpisodeItemId);

        var res = Body(await Controller(db).Current(CancellationToken.None));

        Assert.Equal("Phantom Rig Delta S01E01", res.Source!.Release);
        Assert.Equal("magnet_dn", res.Source.ReleaseSource);
    }

    // ---------------------------------------------------------------- gostream outcomes

    [Fact]
    public async Task GostreamTorrentNotFound_ReportsNotLoaded_KeepsSourceMetadata()
    {
        using var db = await NewSeededDbAsync();
        Playing(MovieItemId); // hash not loaded in the fake engine → 404

        var res = Body(await Controller(db).Current(CancellationToken.None));

        Assert.Equal(PlayerStatsStatus.TorrentNotLoaded, res.Status);
        Assert.Null(res.Stream);
        Assert.Equal(MovieRelease, res.Source!.Release);
    }

    [Fact]
    public async Task GostreamRejectsWrongToken_ReportsUnavailable_ProvingTheTokenIsActuallySent()
    {
        using var db = await NewSeededDbAsync();
        _gostream.Loaded[MovieHash] = StatsJson(MovieHash);
        Playing(MovieItemId);

        var res = Body(await Controller(db, gostream: Client(token: "wrong")).Current(CancellationToken.None));

        Assert.Equal(PlayerStatsStatus.GostreamUnavailable, res.Status);
        Assert.Null(res.Stream);
        Assert.Equal("wrong", Assert.Single(_gostream.Requests).Token);
    }

    [Fact]
    public async Task GostreamServerError_ReportsUnavailable_WithoutEchoingUpstreamText()
    {
        using var db = await NewSeededDbAsync();
        _gostream.ForcedResponse = (500, "{\"error\":\"internal gostream-engine.svc.cluster.local exploded\"}");
        Playing(MovieItemId);

        var res = Body(await Controller(db).Current(CancellationToken.None));

        Assert.Equal(PlayerStatsStatus.GostreamUnavailable, res.Status);
        Assert.DoesNotContain("cluster.local", HostJson(res), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GostreamUnreachable_ReportsUnavailable()
    {
        using var db = await NewSeededDbAsync();
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var deadPort = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Playing(EpisodeItemId);

        var res = Body(await Controller(db, gostream: Client(baseUrl: $"http://127.0.0.1:{deadPort}")).Current(CancellationToken.None));

        Assert.Equal(PlayerStatsStatus.GostreamUnavailable, res.Status);
        Assert.Equal(EpisodeRelease, res.Source!.Release);
    }

    // ---------------------------------------------------------------- non-ok item states

    [Fact]
    public async Task PhantomNotMaterialised_ReportsNotMaterialised_WithoutCallingGostream()
    {
        using var db = await NewSeededDbAsync();
        Playing(PhantomOnlyItemId);

        var res = Body(await Controller(db).Current(CancellationToken.None));

        Assert.Equal(PlayerStatsStatus.NotMaterialised, res.Status);
        Assert.True(res.IsPhantom);
        Assert.Null(res.Source);
        Assert.Empty(_gostream.Requests);
    }

    [Fact]
    public async Task PhantomMaterialising_ReportsMaterialising()
    {
        using var db = await NewSeededDbAsync();
        await db.UpsertMaterialiseInFlightAsync(99000003, "movie", -1, -1, CancellationToken.None);
        Playing(PhantomOnlyItemId);

        var res = Body(await Controller(db).Current(CancellationToken.None));

        Assert.Equal(PlayerStatsStatus.Materialising, res.Status);
        Assert.Empty(_gostream.Requests);
    }

    [Theory]
    [InlineData("library")]
    [InlineData("external")]
    public async Task NonPhantomItem_ReportsNotPhantom_WithoutCallingGostream(string which)
    {
        using var db = await NewSeededDbAsync();
        Playing(which == "library" ? LibraryItemId : ExternalItemId);

        var res = Body(await Controller(db).Current(CancellationToken.None));

        Assert.Equal(PlayerStatsStatus.NotPhantom, res.Status);
        Assert.True(res.Playing);
        Assert.False(res.IsPhantom);
        Assert.Null(res.Source);
        Assert.Empty(_gostream.Requests);
    }

    [Fact]
    public async Task MaterialisedButNoCachedMagnet_ReportsNoSource()
    {
        using var db = await NewSeededDbAsync();
        await db.InsertMaterialisedStateAsync(99000003, "movie", -1, -1, "/stubs/c.mkv", "/fuse/c.mkv", CancellationToken.None);
        Playing(PhantomOnlyItemId);

        var res = Body(await Controller(db).Current(CancellationToken.None));

        Assert.Equal(PlayerStatsStatus.NoSource, res.Status);
        Assert.NotNull(res.MaterialisedAt);
        Assert.Empty(_gostream.Requests);
    }

    // ---------------------------------------------------------------- session scoping / auth

    [Fact]
    public async Task Current_NoPlayingSession_ReturnsNotPlaying_AndNeverCallsGostream()
    {
        using var db = await NewSeededDbAsync();

        var res = Body(await Controller(db).Current(CancellationToken.None));

        Assert.Equal(PlayerStatsStatus.NotPlaying, res.Status);
        Assert.False(res.Playing);
        Assert.Empty(_gostream.Requests);
    }

    [Fact]
    public async Task Current_OnlyAnotherUsersOrDevicesSessionIsPlaying_ReturnsNotPlaying()
    {
        using var db = await NewSeededDbAsync();
        _gostream.Loaded[MovieHash] = StatsJson(MovieHash);
        Playing(MovieItemId, userId: OtherUserId);
        Playing(EpisodeItemId, deviceId: "some-other-device");

        var res = Body(await Controller(db).Current(CancellationToken.None));

        Assert.Equal(PlayerStatsStatus.NotPlaying, res.Status);
        Assert.Empty(_gostream.Requests);
    }

    [Fact]
    public async Task Current_PicksTheCallingDevicesSession()
    {
        using var db = await NewSeededDbAsync();
        _gostream.Loaded[MovieHash] = StatsJson(MovieHash);
        _gostream.Loaded[EpisodeHash] = StatsJson(EpisodeHash);
        Playing(MovieItemId, deviceId: "tv-in-the-lounge");
        Playing(EpisodeItemId);

        var res = Body(await Controller(db).Current(CancellationToken.None));

        Assert.Equal("episode", res.Type);
        Assert.Equal(EpisodeHash, res.Source!.InfoHash);
    }

    [Fact]
    public async Task ForItem_NonAdminNotPlayingIt_IsForbidden_AndNeverCallsGostream()
    {
        using var db = await NewSeededDbAsync();
        _gostream.Loaded[MovieHash] = StatsJson(MovieHash);
        Playing(EpisodeItemId); // playing something else

        var res = Body(await Controller(db).ForItem(MovieItemId, null, null, CancellationToken.None), 403);

        Assert.Equal(PlayerStatsStatus.NotPlaying, res.Status);
        Assert.Null(res.Source);
        Assert.Empty(_gostream.Requests);
    }

    [Fact]
    public async Task ForItem_NonAdminPlayingIt_GetsStats_EchoingPlaySession()
    {
        using var db = await NewSeededDbAsync();
        _gostream.Loaded[EpisodeHash] = StatsJson(EpisodeHash);
        Playing(EpisodeItemId);

        var res = Body(await Controller(db).ForItem(EpisodeItemId, "play-session-42", null, CancellationToken.None));

        Assert.Equal(PlayerStatsStatus.Ok, res.Status);
        Assert.True(res.Playing);
        Assert.Equal("play-session-42", res.PlaySessionId);
        Assert.Equal(EpisodeItemId.ToString("N"), res.MediaSourceId);
    }

    [Fact]
    public async Task ForItem_MediaSourceMismatch_IsForbiddenForNonAdmin()
    {
        using var db = await NewSeededDbAsync();
        Playing(MovieItemId);

        var res = Body(await Controller(db).ForItem(MovieItemId, null, "not-the-playing-source", CancellationToken.None), 403);

        Assert.False(res.Playing);
        Assert.Empty(_gostream.Requests);
    }

    [Fact]
    public async Task ForItem_AdminWithoutSession_GetsStats_PlayingFalse()
    {
        using var db = await NewSeededDbAsync();
        _gostream.Loaded[MovieHash] = StatsJson(MovieHash);

        var res = Body(await Controller(db, admin: true).ForItem(MovieItemId, null, null, CancellationToken.None));

        Assert.Equal(PlayerStatsStatus.Ok, res.Status);
        Assert.False(res.Playing);
        Assert.Equal(17, res.Stream!.Peers);
    }

    [Fact]
    public async Task ForItem_UnknownItem_IsNotFound()
    {
        using var db = await NewSeededDbAsync();

        var res = Body(await Controller(db, admin: true).ForItem(Guid.NewGuid(), null, null, CancellationToken.None), 404);

        Assert.Equal(PlayerStatsStatus.ItemNotFound, res.Status);
    }

    [Fact]
    public void Controller_DataEndpointsRequireAuth_ScriptIsAnonymous()
    {
        var t = typeof(PhantomLibraryPlayerStatsController);
        Assert.NotNull(t.GetMethod(nameof(PhantomLibraryPlayerStatsController.Current))!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false).SingleOrDefault());
        Assert.NotNull(t.GetMethod(nameof(PhantomLibraryPlayerStatsController.ForItem))!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), false).SingleOrDefault());
        Assert.NotNull(t.GetMethod(nameof(PhantomLibraryPlayerStatsController.PlayerStatsScript))!
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute), false).SingleOrDefault());
    }

    [Fact]
    public void OverlayScript_IsEmbedded_AndServedAsJavascript()
    {
        var ctrl = new PhantomLibraryPlayerStatsController(
            new PlayerStatsService(Library().Object, new PhantomDb(_dbPath), Client(), NullLogger<PlayerStatsService>.Instance, () => new PluginConfiguration()),
            _sessionManager.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

        var file = Assert.IsType<FileStreamResult>(ctrl.PlayerStatsScript());
        Assert.Equal("text/javascript", file.ContentType);
        using var reader = new StreamReader(file.FileStream);
        var js = reader.ReadToEnd();
        Assert.Contains("Plugins/PhantomLibrary/PlayerStats/Current", js, StringComparison.Ordinal);
        Assert.Contains("__phantomPlayerStatsLoaded", js, StringComparison.Ordinal);
    }

    [Fact]
    public void BadgesScript_BootstrapsTheOverlay()
    {
        var name = "Jellyfin.Plugin.PhantomLibrary.Configuration.phantomBadges.js";
        using var stream = typeof(Plugin).Assembly.GetManifestResourceStream(name);
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);
        Assert.Contains("/Plugins/PhantomLibrary/playerstats.js", reader.ReadToEnd(), StringComparison.Ordinal);
    }

    // ---------------------------------------------------------------- hash normalisation

    [Theory]
    [InlineData("ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD", "abcdefabcdefabcdefabcdefabcdefabcdefabcd")]
    [InlineData("CI3CTFIZUUF2TPPN22GJP3Y5JY6D4SFI", "1236299519a50ba9bdedd68c97ef1d4e3c3e48a8")]
    [InlineData("not-a-hash", null)]
    [InlineData("deadbeef", null)]
    [InlineData("", null)]
    public void NormaliseInfoHash_AcceptsHexAndBase32Only(string raw, string? expected)
        => Assert.Equal(expected, PlayerStatsService.NormaliseInfoHash(raw));

    [Fact]
    public async Task NonHexCachedHash_FallsBackToMagnetBtih()
    {
        using var db = await NewSeededDbAsync();
        await db.PutCachedMagnetAsync(
            new MagnetCacheKey(MovieTmdb, "tt99000001", "movie", null, null, Preset),
            new MagnetCacheEntry
            {
                Magnet = MovieMagnet, InfoHash = "deadbeef", Size = 10, Seeders = 1, Indexer = "x",
                CachedAt = DateTimeOffset.UtcNow.AddMinutes(1), Ttl = TimeSpan.FromDays(1), Source = "user",
            },
            CancellationToken.None);
        _gostream.Loaded[MovieHash] = StatsJson(MovieHash);
        Playing(MovieItemId);

        var res = Body(await Controller(db).Current(CancellationToken.None));

        Assert.Equal(PlayerStatsStatus.Ok, res.Status);
        Assert.Equal(MovieHash, res.Source!.InfoHash);
    }
}
