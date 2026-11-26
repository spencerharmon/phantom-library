using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Diagnostics;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.Scheduled;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// p13-degenerate-query-guard (ROI P13): no indexer request — Prowlarr,
/// per-indexer Torznab via Prowlarr, Torrentio — may carry an empty or
/// punctuation/whitespace-only title term (production: 43 searches / 6 h with
/// <c>Term: [. 2026]</c>). Drives the REAL <see cref="MagnetSelector"/>,
/// <see cref="MagnetCacheBuilder"/>, <see cref="AvailabilityProbeWorker"/>,
/// <see cref="ProwlarrClient"/>, <see cref="TorrentioClient"/>, and
/// <see cref="TmdbQueryTitleRepairSource"/> (over a real <see cref="TmdbClient"/>)
/// against a RECORDING fake HTTP backend, asserting on the actual outbound
/// request URLs. Movie AND episode parity; interactive (ProbeAsync — the
/// materialise / details path) AND background (availability sweep reconcile,
/// magnet-cache builder).
/// </summary>
public sealed class DegenerateQueryGuardTests : IDisposable
{
    private const string ProwlarrBase = "http://prowlarr.test:9696";
    private const string TorrentioBase = "http://torrentio.test";
    private const string TmdbBase = "http://tmdb.test/3";

    private readonly string _dbPath =
        Path.Combine(Path.GetTempPath(), "phantom-p13-degenerate-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch (IOException)
        {
            // best-effort temp cleanup
        }
    }

    // ------------------------------------------------------------------
    // Guard semantics
    // ------------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData(" . ")]
    [InlineData("?!")]
    [InlineData("-–—")]
    [InlineData("\t\n\u00A0")]
    [InlineData("\u200B")]
    [InlineData("\u200B.\u200B")]
    public void Guard_DegenerateTitles_NormaliseToNull(string? title)
    {
        Assert.Null(QueryTitleGuard.Normalize(title));
        Assert.True(QueryTitleGuard.IsDegenerate(title));
    }

    [Theory]
    [InlineData("Heat", "Heat")]
    [InlineData("  Spirited   Away ", "Spirited Away")]
    [InlineData("9", "9")]
    [InlineData("1917", "1917")]
    [InlineData("M*A*S*H", "M*A*S*H")]
    [InlineData("千と千尋の神隠し", "千と千尋の神隠し")]
    [InlineData("Amélie", "Amélie")]
    public void Guard_UsableTitles_AreKeptVerbatimModuloWhitespace(string title, string expected)
    {
        Assert.Equal(expected, QueryTitleGuard.Normalize(title));
    }

    // ------------------------------------------------------------------
    // Recoverable: repaired from the TMDB original title (movie + episode)
    // ------------------------------------------------------------------

    [Fact]
    public async Task Movie_DotTitle_RepairedFromOriginalTitle_NoDegenerateTermSent()
    {
        using var db = await NewDbAsync();
        await SeedMetadataAsync(db, 5001, "movie", ".", "Original Movie Title", 2026);
        var http = new RecordingIndexerBackend();
        var selector = BuildSelector(db, http, prowlarr: true, torrentio: true);

        var before = PhantomMetrics.DegenerateQueryCount("movie", "repaired_original_title");
        var probe = await selector.ProbeAsync(5001, "tt5001", "movie", null, null, ".", 2026, CancellationToken.None);

        AssertNoDegenerateTerm(http);
        Assert.Equal(new[] { "tt5001", "Original Movie Title 2026" }, http.ProwlarrTerms());
        Assert.Contains(http.Requests, u => u.Contains("/stream/movie/tt5001.json", StringComparison.Ordinal));
        Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
        Assert.Equal(before + 1, PhantomMetrics.DegenerateQueryCount("movie", "repaired_original_title"));
    }

    [Fact]
    public async Task Episode_PunctuationTitle_RepairedFromOriginalTitle_NoDegenerateTermSent()
    {
        using var db = await NewDbAsync();
        await SeedMetadataAsync(db, 5002, "series", "?!", "Original Series Name", 2019);
        var http = new RecordingIndexerBackend();
        var selector = BuildSelector(db, http, prowlarr: true, torrentio: true);

        var probe = await selector.ProbeAsync(5002, "tt5002", "episode", 1, 2, "?!", 2019, CancellationToken.None);

        AssertNoDegenerateTerm(http);
        Assert.Equal(new[] { "Original Series Name S01E02" }, http.ProwlarrTerms());
        Assert.Contains(http.Requests, u => u.Contains("/stream/series/tt5002:1:2.json", StringComparison.Ordinal));
        Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
    }

    // ------------------------------------------------------------------
    // Recoverable: repaired from TMDB alternative / translated titles
    // ------------------------------------------------------------------

    [Fact]
    public async Task Movie_EmptyTitle_NoOriginal_RepairedFromTmdbAlternativeTitle()
    {
        using var db = await NewDbAsync();
        await SeedMetadataAsync(db, 5003, "movie", string.Empty, null, 2021);
        var http = new RecordingIndexerBackend
        {
            TmdbMovieVariantsJson = "{\"alternative_titles\":{\"titles\":["
                + "{\"iso_3166_1\":\"FR\",\"title\":\"...\"},"
                + "{\"iso_3166_1\":\"US\",\"title\":\"Alt US Title\"}]},"
                + "\"translations\":{\"translations\":[]}}",
        };
        var selector = BuildSelector(db, http, prowlarr: true, torrentio: false);

        var before = PhantomMetrics.DegenerateQueryCount("movie", "repaired_alternate_title");
        var probe = await selector.ProbeAsync(5003, null, "movie", null, null, string.Empty, 2021, CancellationToken.None);

        AssertNoDegenerateTerm(http);
        Assert.Equal(new[] { "Alt US Title 2021" }, http.ProwlarrTerms());
        Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
        Assert.Equal(before + 1, PhantomMetrics.DegenerateQueryCount("movie", "repaired_alternate_title"));
    }

    [Fact]
    public async Task Episode_WhitespaceTitle_NoOriginal_RepairedFromTmdbTranslation()
    {
        using var db = await NewDbAsync();
        await SeedMetadataAsync(db, 5004, "series", "   ", null, 2018);
        var http = new RecordingIndexerBackend
        {
            TmdbSeriesVariantsJson = "{\"alternative_titles\":{\"results\":[]},"
                + "\"translations\":{\"translations\":["
                + "{\"iso_3166_1\":\"US\",\"iso_639_1\":\"en\",\"data\":{\"name\":\"Translated Show\"}}]}}",
        };
        var selector = BuildSelector(db, http, prowlarr: true, torrentio: false);

        var probe = await selector.ProbeAsync(5004, null, "episode", 3, 4, "   ", 2018, CancellationToken.None);

        AssertNoDegenerateTerm(http);
        Assert.Equal(new[] { "Translated Show S03E04" }, http.ProwlarrTerms());
        Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
    }

    // ------------------------------------------------------------------
    // Unrepairable title but IMDB id present: IMDB-id-only query
    // ------------------------------------------------------------------

    [Fact]
    public async Task Movie_Unrepairable_WithImdb_QueriesImdbOnly_NoTextTerm()
    {
        using var db = await NewDbAsync();
        await SeedMetadataAsync(db, 5005, "movie", ".", null, 2026);
        var http = new RecordingIndexerBackend();
        var selector = BuildSelector(db, http, prowlarr: true, torrentio: true);

        var before = PhantomMetrics.DegenerateQueryCount("movie", "imdb_only");
        var probe = await selector.ProbeAsync(5005, "tt5005", "movie", null, null, ".", 2026, CancellationToken.None);

        AssertNoDegenerateTerm(http);
        Assert.Equal(new[] { "tt5005" }, http.ProwlarrTerms());
        Assert.Contains(http.Requests, u => u.Contains("/stream/movie/tt5005.json", StringComparison.Ordinal));
        Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
        Assert.Equal(before + 1, PhantomMetrics.DegenerateQueryCount("movie", "imdb_only"));
    }

    [Fact]
    public async Task Episode_Unrepairable_WithImdb_ProwlarrSkipped_TorrentioImdbOnly()
    {
        using var db = await NewDbAsync();
        await SeedMetadataAsync(db, 5006, "series", ".", null, 2020);
        var http = new RecordingIndexerBackend();
        var selector = BuildSelector(db, http, prowlarr: true, torrentio: true);

        var probe = await selector.ProbeAsync(5006, "tt5006", "episode", 1, 1, ".", 2020, CancellationToken.None);

        // Prowlarr episode search is text-only, so it must not be called at all.
        Assert.Empty(http.ProwlarrTerms());
        Assert.DoesNotContain(http.Requests, u => u.StartsWith(ProwlarrBase, StringComparison.Ordinal));
        Assert.Contains(http.Requests, u => u.Contains("/stream/series/tt5006:1:1.json", StringComparison.Ordinal));
        Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
    }

    // ------------------------------------------------------------------
    // Unrecoverable: distinct degenerate_query_skipped outcome, ZERO requests
    // ------------------------------------------------------------------

    [Fact]
    public async Task Movie_Unrecoverable_SkippedWithZeroIndexerRequests()
    {
        using var db = await NewDbAsync();
        await SeedMetadataAsync(db, 5007, "movie", ".", null, 2026);
        var http = new RecordingIndexerBackend();
        var selector = BuildSelector(db, http, prowlarr: true, torrentio: true);

        var before = PhantomMetrics.DegenerateQueryCount("movie", MagnetSelector.DegenerateQuerySkippedKind);
        var probe = await selector.ProbeAsync(5007, null, "movie", null, null, ".", 2026, CancellationToken.None);

        Assert.Equal(MagnetProbeOutcome.DegenerateQuerySkipped, probe.Outcome);
        Assert.Equal("degenerate_query_skipped", probe.ErrorKind);
        Assert.Empty(http.IndexerRequests());
        Assert.Equal(before + 1, PhantomMetrics.DegenerateQueryCount("movie", MagnetSelector.DegenerateQuerySkippedKind));
    }

    [Fact]
    public async Task Episode_Unrecoverable_SkippedWithZeroIndexerRequests()
    {
        using var db = await NewDbAsync();
        await SeedMetadataAsync(db, 5008, "series", " - ", null, 2020);
        var http = new RecordingIndexerBackend();
        var selector = BuildSelector(db, http, prowlarr: true, torrentio: true);

        var before = PhantomMetrics.DegenerateQueryCount("episode", MagnetSelector.DegenerateQuerySkippedKind);
        var probe = await selector.ProbeAsync(5008, null, "episode", 2, 5, " - ", 2020, CancellationToken.None);

        Assert.Equal(MagnetProbeOutcome.DegenerateQuerySkipped, probe.Outcome);
        Assert.Empty(http.IndexerRequests());
        Assert.Equal(before + 1, PhantomMetrics.DegenerateQueryCount("episode", MagnetSelector.DegenerateQuerySkippedKind));
    }

    [Fact]
    public async Task Episode_Unrepairable_WithImdb_ProwlarrOnly_SkippedWithZeroRequests()
    {
        // IMDB id present, but the only enabled indexer (Prowlarr) cannot serve
        // an IMDB-only EPISODE query: skip, never send "S01E01" alone.
        using var db = await NewDbAsync();
        await SeedMetadataAsync(db, 5009, "series", ".", null, 2020);
        var http = new RecordingIndexerBackend();
        var selector = BuildSelector(db, http, prowlarr: true, torrentio: false);

        var probe = await selector.ProbeAsync(5009, "tt5009", "episode", 1, 1, ".", 2020, CancellationToken.None);

        Assert.Equal(MagnetProbeOutcome.DegenerateQuerySkipped, probe.Outcome);
        Assert.Empty(http.IndexerRequests());
    }

    // ------------------------------------------------------------------
    // Background: magnet-cache builder (production DI wiring)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("movie", -1, -1)]
    [InlineData("episode", 1, 3)]
    public async Task CacheBuilder_Unrecoverable_RecordsDegenerateSkip_ZeroIndexerRequests(string type, int season, int episode)
    {
        using var db = await NewDbAsync();
        var tmdbId = type == "movie" ? 5101 : 5102;
        await SeedMetadataAsync(db, tmdbId, type == "movie" ? "movie" : "series", ".", null, 2026);
        var http = new RecordingIndexerBackend();
        var builder = BuildProductionCacheBuilder(db, http);

        await db.EnqueueMagnetCacheJobAsync(tmdbId, type, season, episode, "test", 0, CancellationToken.None);
        var result = await builder.ProcessNextAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("degenerate_query_skipped", result!.Error);
        Assert.Equal(0, result.CandidateCount);
        Assert.Equal("degenerate_query_skipped", await ReadMagnetJobErrorAsync(tmdbId, type, season, episode));
        Assert.Empty(http.IndexerRequests());
    }

    [Theory]
    [InlineData("movie", -1, -1, "Cache Original 2026")]
    [InlineData("episode", 1, 3, "Cache Original S01E03")]
    public async Task CacheBuilder_Recoverable_BuildsFromRepairedTerm(string type, int season, int episode, string expectedTerm)
    {
        using var db = await NewDbAsync();
        var tmdbId = type == "movie" ? 5103 : 5104;
        await SeedMetadataAsync(db, tmdbId, type == "movie" ? "movie" : "series", ".", "Cache Original", 2026);
        var http = new RecordingIndexerBackend();
        var builder = BuildProductionCacheBuilder(db, http);

        await db.EnqueueMagnetCacheJobAsync(tmdbId, type, season, episode, "test", 0, CancellationToken.None);
        var result = await builder.ProcessNextAsync(CancellationToken.None);

        Assert.NotNull(result);
        Assert.Null(result!.Error);
        Assert.True(result.CandidateCount > 0);
        AssertNoDegenerateTerm(http);
        Assert.Equal(new[] { expectedTerm }, http.ProwlarrTerms());
    }

    // ------------------------------------------------------------------
    // Background: availability sweep reconcile (the "Term: [. 2026]" path)
    // ------------------------------------------------------------------

    [Fact]
    public async Task AvailabilitySweep_DotTitleNoImdb_ReconcileSendsNoDegenerateProwlarrTerm()
    {
        // A no-IMDB movie whose stored title is "." (the production shape):
        // the sweep pre-filter routes it to the FULL-indexer reconcile probe,
        // which previously sent Prowlarr "query=. 2026".
        using var db = await NewDbAsync();
        await db.UpsertCatalogueHitsAsync(
            new[] { new TmdbMetadataRow(5201, "movie", ".", 2020, null, null, null, null, null, null, null, DateTimeOffset.UtcNow) },
            sourceMask: 1,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var cfg = Config();
        cfg.AvailabilityProbeEnabled = true;
        cfg.AvailabilityLeaseMinutes = 1;
        cfg.AvailabilityNoIndexerRetryHours = 24;
        var http = new RecordingIndexerBackend();
        var handlerClient = new HttpClient(http);
        var tmdb = new TmdbClient(handlerClient, new StubKeyProvider("k", TmdbBase));
        var selector = BuildSelector(db, http, prowlarr: true, torrentio: true, cfg: cfg);
        var worker = new AvailabilityProbeWorker(
            db,
            selector,
            new TmdbExternalIdResolver(db, tmdb, NullLogger<TmdbExternalIdResolver>.Instance),
            tmdb,
            new ChannelStateProvider(db),
            NullLogger<AvailabilityProbeWorker>.Instance,
            () => cfg);

        var didWork = await InvokeProbeOneAsync(worker, cfg);

        Assert.True(didWork);
        Assert.Empty(http.IndexerRequests());
        var status = await ReadAvailabilityStatusAsync(5201);
        Assert.Equal("unknown", status);
    }

    // ------------------------------------------------------------------
    // Client-level backstop
    // ------------------------------------------------------------------

    [Fact]
    public async Task ProwlarrClient_DegenerateEpisodeTitle_AbstainsWithoutRequest()
    {
        var http = new RecordingIndexerBackend();
        var client = new ProwlarrClient(new HttpClient(http), NullLogger<ProwlarrClient>.Instance, () => (ProwlarrBase, "K"));

        await Assert.ThrowsAsync<IndexerNotApplicableException>(() => client.SearchAsync(
            new IndexerQuery { Type = "episode", Title = ".", Season = 1, Episode = 1, SeriesImdb = "tt1" },
            CancellationToken.None));
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task ProwlarrClient_DegenerateMovieTitle_SendsImdbTermOnly()
    {
        var http = new RecordingIndexerBackend();
        var client = new ProwlarrClient(new HttpClient(http), NullLogger<ProwlarrClient>.Instance, () => (ProwlarrBase, "K"));

        _ = await client.SearchAsync(
            new IndexerQuery { Type = "movie", Title = ".", Year = 2026, Imdb = "tt42" },
            CancellationToken.None);

        Assert.Equal(new[] { "tt42" }, http.ProwlarrTerms());
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static void AssertNoDegenerateTerm(RecordingIndexerBackend http)
    {
        foreach (var term in http.ProwlarrTerms())
        {
            Assert.False(QueryTitleGuard.IsDegenerate(term), $"degenerate Prowlarr term sent: [{term}]");

            // A year/episode-tag-only term ("2026", "S01E02", ". 2026") means
            // the TITLE part was degenerate even if the whole has a digit.
            var titlePart = System.Text.RegularExpressions.Regex.Replace(term, @"\s*(\d{4}|S\d{2}E\d{2})$", string.Empty);
            Assert.False(
                QueryTitleGuard.IsDegenerate(titlePart) && !term.StartsWith("tt", StringComparison.Ordinal),
                $"Prowlarr term with degenerate title part sent: [{term}]");
        }
    }

    private static PluginConfiguration Config() => new()
    {
        MinSeeders = 1,
        MinSizeGb1080p = 1,
        MinSizeGb4K = 1,
        MagnetCacheTtlHours = 24,
        MagnetCacheBuildLeaseMinutes = 10,
        IndexerProbeTimeoutSeconds = 30,
    };

    private MagnetSelector BuildSelector(
        PhantomDb db,
        RecordingIndexerBackend http,
        bool prowlarr,
        bool torrentio,
        PluginConfiguration? cfg = null)
    {
        cfg ??= Config();
        var indexers = new List<IIndexerClient>();
        if (prowlarr)
        {
            indexers.Add(new ProwlarrClient(new HttpClient(http), NullLogger<ProwlarrClient>.Instance, () => (ProwlarrBase, "K")));
        }

        if (torrentio)
        {
            indexers.Add(new TorrentioClient(new HttpClient(http), NullLogger<TorrentioClient>.Instance, () => TorrentioBase));
        }

        var tmdb = new TmdbClient(new HttpClient(http), new StubKeyProvider("k", TmdbBase));
        var finalCfg = cfg;
        return new MagnetSelector(
            indexers,
            new QualityScorer(NullLogger<QualityScorer>.Instance),
            NullLogger<MagnetSelector>.Instance,
            () => finalCfg,
            new TmdbQueryTitleRepairSource(db, tmdb));
    }

    private MagnetCacheBuilder BuildProductionCacheBuilder(PhantomDb db, RecordingIndexerBackend http)
    {
        var cfg = Config();
        var selector = BuildSelector(db, http, prowlarr: true, torrentio: false, cfg: cfg);
        var tmdb = new TmdbClient(new HttpClient(http), new StubKeyProvider("k", TmdbBase));
        var externalIds = new TmdbExternalIdResolver(db, tmdb, NullLogger<TmdbExternalIdResolver>.Instance);

        // The public (DI) ctor wires the PRODUCTION TMDB meta resolver; swap
        // only the config provider (Plugin.Instance is absent under test).
        var builder = new MagnetCacheBuilder(db, selector, externalIds, NullLogger<MagnetCacheBuilder>.Instance);
        var field = typeof(MagnetCacheBuilder).GetField("_configProvider", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(nameof(MagnetCacheBuilder), "_configProvider");
        field.SetValue(builder, (Func<PluginConfiguration>)(() => cfg));
        return builder;
    }

    private async Task<PhantomDb> NewDbAsync()
    {
        var db = new PhantomDb(_dbPath);
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        return db;
    }

    // Raw insert so empty / whitespace titles (which UpsertTmdbMetadataAsync's
    // argument guard refuses) can be represented exactly as a bad TMDB row.
    private async Task SeedMetadataAsync(PhantomDb db, int tmdbId, string type, string title, string? originalTitle, int year)
    {
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT OR REPLACE INTO tmdb_metadata
                (tmdb_id, type, title, year, original_title, fetched_at)
            VALUES ($tmdb, $type, $title, $year, $orig, $now);";
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$year", year);
        cmd.Parameters.AddWithValue("$orig", (object?)originalTitle ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<string?> ReadMagnetJobErrorAsync(int tmdbId, string type, int season, int episode)
    {
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT last_error FROM magnet_cache_jobs WHERE tmdb_id=$t AND type=$ty AND season=$s AND episode=$e;";
        cmd.Parameters.AddWithValue("$t", tmdbId);
        cmd.Parameters.AddWithValue("$ty", type);
        cmd.Parameters.AddWithValue("$s", season);
        cmd.Parameters.AddWithValue("$e", episode);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? null : (string)v;
    }

    private async Task<string?> ReadAvailabilityStatusAsync(int tmdbId)
    {
        await using var conn = new SqliteConnection("Data Source=" + _dbPath);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT status FROM availability_items WHERE tmdb_id=$t AND type='movie';";
        cmd.Parameters.AddWithValue("$t", tmdbId);
        var v = await cmd.ExecuteScalarAsync();
        return v is null or DBNull ? null : (string)v;
    }

    private static async Task<bool> InvokeProbeOneAsync(AvailabilityProbeWorker worker, PluginConfiguration cfg)
    {
        var method = typeof(AvailabilityProbeWorker).GetMethod(
            "ProbeOneAvailabilityAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(PluginConfiguration), typeof(CancellationToken) },
            modifiers: null)
            ?? throw new MissingMethodException(nameof(AvailabilityProbeWorker), "ProbeOneAvailabilityAsync");
        var task = (Task<bool>)(method.Invoke(worker, new object[] { cfg, CancellationToken.None })
            ?? throw new InvalidOperationException("ProbeOneAvailabilityAsync returned null"));
        return await task;
    }

    /// <summary>
    /// Recording fake HTTP backend for Prowlarr (discovery + per-indexer Torznab),
    /// Torrentio (<c>/stream/...</c>) and TMDB (<c>/3/...</c>). Every outbound
    /// request URL is recorded; each indexer answers with one high-seeder
    /// 1080p candidate so a successful probe is observable.
    /// </summary>
    private sealed class RecordingIndexerBackend : HttpMessageHandler
    {
        private readonly object _gate = new();

        public List<string> Requests { get; } = new();

        public string TmdbMovieVariantsJson { get; init; } = "{\"alternative_titles\":{\"titles\":[]},\"translations\":{\"translations\":[]}}";

        public string TmdbSeriesVariantsJson { get; init; } = "{\"alternative_titles\":{\"results\":[]},\"translations\":{\"translations\":[]}}";

        public IReadOnlyList<string> IndexerRequests()
        {
            lock (_gate)
            {
                return Requests
                    .Where(u => u.StartsWith(ProwlarrBase, StringComparison.Ordinal) || u.StartsWith(TorrentioBase, StringComparison.Ordinal))
                    .ToList();
            }
        }

        public IReadOnlyList<string> ProwlarrTerms()
        {
            lock (_gate)
            {
                return Requests
                    .Where(u => u.StartsWith(ProwlarrBase + "/1/api?", StringComparison.Ordinal))
                    .Select(u => System.Web.HttpUtility.ParseQueryString(new Uri(u).Query)["q"] ?? string.Empty)
                    .ToList();
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            lock (_gate)
            {
                Requests.Add(url);
            }

            string body;
            if (url == ProwlarrBase + "/api/v1/indexer")
            {
                body = "[{\"id\":1,\"name\":\"torznab-x\",\"enable\":true,\"protocol\":\"torrent\"}]";
            }
            else if (url.StartsWith(ProwlarrBase + "/1/api?", StringComparison.Ordinal))
            {
                var hash = "P" + Math.Abs(url.GetHashCode(StringComparison.Ordinal)).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
                body = ParallelIndexerSearchTests.FakeProwlarr.Torznab("torznab-x", new[]
                {
                    new ParallelIndexerSearchTests.TorznabItem("Release 1080p WEB", hash, 200, 5000000000),
                });
            }
            else if (url.StartsWith(TorrentioBase, StringComparison.Ordinal))
            {
                body = "{\"streams\":[{\"name\":\"Torrentio\\n1080p\",\"title\":\"Release 1080p\\n👤 150 💾 6.0 GB\",\"infoHash\":\"T0RRENT10HASH\"}]}";
            }
            else if (url.StartsWith(TmdbBase, StringComparison.Ordinal))
            {
                var path = new Uri(url).AbsolutePath;
                if (path.EndsWith("/external_ids", StringComparison.Ordinal))
                {
                    body = "{\"imdb_id\":null}";
                }
                else if (path.StartsWith("/3/movie/", StringComparison.Ordinal))
                {
                    body = TmdbMovieVariantsJson;
                }
                else if (path.StartsWith("/3/tv/", StringComparison.Ordinal))
                {
                    body = TmdbSeriesVariantsJson;
                }
                else
                {
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
                }
            }
            else
            {
                throw new InvalidOperationException("unexpected outbound request: " + url);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
