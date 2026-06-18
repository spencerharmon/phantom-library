using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Library;
using Jellyfin.Plugin.PhantomLibrary.Scheduled;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class DiscoveryRefreshTaskTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PhantomDb _db;
    private readonly ChannelStateProvider _state;

    public DiscoveryRefreshTaskTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "phantom-disc-tests-" + Guid.NewGuid().ToString("N") + ".db");
        _db = new PhantomDb(_dbPath);
        _state = new ChannelStateProvider(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        try
        {
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
        }
        catch
        {
            // best-effort
        }
    }

    private DiscoveryRefreshTask NewTask(StubTmdbClient tmdb, PluginConfiguration? config = null)
    {
        var cached = new CachedTmdbReader(tmdb, _db, NullLogger<CachedTmdbReader>.Instance);
        return new DiscoveryRefreshTask(
            cached,
            tmdb,
            _db,
            _state,
            NullLogger<DiscoveryRefreshTask>.Instance,
            () => config);
    }

    private async Task SetDiscoveryLastRefreshedAsync(int tmdbId, string type, DateTimeOffset lastRefreshed)
    {
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"UPDATE discovery_cache
            SET last_refreshed=$lastRefreshed
            WHERE tmdb_id=$tmdb AND type=$type;";
        cmd.Parameters.AddWithValue("$lastRefreshed", lastRefreshed.ToUnixTimeSeconds());
        cmd.Parameters.AddWithValue("$tmdb", tmdbId);
        cmd.Parameters.AddWithValue("$type", type);
        await cmd.ExecuteNonQueryAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Execute_PopulatesDiscoveryCache_FromTrending()
    {
        var tmdb = new StubTmdbClient
        {
            TrendingMovies = new[] { Hit(101, "Movie 101"), Hit(102, "Movie 102") },
            TrendingSeries = new[] { Hit(201, "Series 201") },
        };
        var task = NewTask(tmdb);

        await task.ExecuteAsync(new Progress<double>(_ => { }), CancellationToken.None);

        var movies = await _db.ListDiscoveryCacheAsync("movie", CancellationToken.None);
        var series = await _db.ListDiscoveryCacheAsync("series", CancellationToken.None);

        Assert.Equal(2, movies.Count);
        Assert.Contains(movies, r => r.TmdbId == 101);
        Assert.Contains(movies, r => r.TmdbId == 102);
        Assert.Single(series);
        Assert.Equal(201, series[0].TmdbId);
    }

    [Fact]
    public async Task Execute_WarmsTmdbMetadata_ForEveryDiscoveredTmdb()
    {
        // Regression for plan §3.1 IMPORTANT 4: every discovered (tmdb, type)
        // must end up in tmdb_metadata so the channel can render without an
        // upstream TMDB hit on the browse hot path.
        var tmdb = new StubTmdbClient
        {
            TrendingMovies = new[] { Hit(101, "Movie 101"), Hit(102, "Movie 102") },
            TrendingSeries = new[] { Hit(201, "Series 201") },
        };
        var task = NewTask(tmdb);

        await task.ExecuteAsync(new Progress<double>(_ => { }), CancellationToken.None);

        var m101 = await _db.GetTmdbMetadataAsync(101, "movie", CancellationToken.None);
        var m102 = await _db.GetTmdbMetadataAsync(102, "movie", CancellationToken.None);
        var s201 = await _db.GetTmdbMetadataAsync(201, "series", CancellationToken.None);

        Assert.NotNull(m101);
        Assert.Equal("Movie 101", m101!.Title);
        Assert.Equal(1999, m101.Year);
        Assert.NotNull(m101.PosterUrl);
        Assert.StartsWith("https://image.tmdb.org/t/p/w500/", m101.PosterUrl, StringComparison.Ordinal);

        Assert.NotNull(m102);
        Assert.Equal("Movie 102", m102!.Title);

        Assert.NotNull(s201);
        Assert.Equal("Series 201", s201!.Title);
        Assert.Equal(2010, s201.Year);
    }

    [Fact]
    public async Task Execute_PopulatesDiscoveryCache_FromDiscoverWalkRespectingConfiguredSplitCap()
    {
        var tmdb = new StubTmdbClient();
        tmdb.DiscoverMoviePages[1] = new[] { Hit(301, "Movie 301"), Hit(302, "Movie 302") };
        tmdb.DiscoverMoviePages[2] = new[] { Hit(303, "Movie 303"), Hit(304, "Movie 304") };
        tmdb.DiscoverSeriesPages[1] = new[] { Hit(401, "Series 401"), Hit(402, "Series 402"), Hit(403, "Series 403") };

        var task = NewTask(tmdb, new PluginConfiguration { SuggestionsCatalogueMaxItems = 5 });

        await task.ExecuteAsync(new Progress<double>(_ => { }), CancellationToken.None);

        var movies = await _db.ListDiscoveryCacheAsync("movie", CancellationToken.None);
        var series = await _db.ListDiscoveryCacheAsync("series", CancellationToken.None);

        Assert.Equal(new[] { 301, 302, 303 }, movies.Select(r => r.TmdbId).OrderBy(id => id));
        Assert.Equal(new[] { 401, 402 }, series.Select(r => r.TmdbId).OrderBy(id => id));
        Assert.DoesNotContain(movies, r => r.TmdbId == 304);
        Assert.DoesNotContain(series, r => r.TmdbId == 403);
        Assert.Equal(new[] { 1, 2 }, tmdb.DiscoverMoviePageCalls);
        Assert.Equal(new[] { 1 }, tmdb.DiscoverSeriesPageCalls);
    }

    [Fact]
    public async Task Execute_WarmsTmdbMetadata_ForDiscoverRows()
    {
        var tmdb = new StubTmdbClient();
        tmdb.DiscoverMoviePages[1] = new[] { Hit(301, "Movie 301") };
        tmdb.DiscoverSeriesPages[1] = new[] { Hit(401, "Series 401") };

        var task = NewTask(tmdb, new PluginConfiguration { SuggestionsCatalogueMaxItems = 2 });

        await task.ExecuteAsync(new Progress<double>(_ => { }), CancellationToken.None);

        var movie = await _db.GetTmdbMetadataAsync(301, "movie", CancellationToken.None);
        var series = await _db.GetTmdbMetadataAsync(401, "series", CancellationToken.None);

        Assert.NotNull(movie);
        Assert.Equal("Movie 301", movie!.Title);
        Assert.NotNull(series);
        Assert.Equal("Series 401", series!.Title);
    }

    [Fact]
    public async Task Execute_DiscoverWalkStopsAtEmptyPage()
    {
        var tmdb = new StubTmdbClient();
        tmdb.DiscoverMoviePages[1] = new[] { Hit(301, "Movie 301") };
        tmdb.DiscoverMoviePages[2] = Array.Empty<TmdbSearchHit>();
        tmdb.DiscoverSeriesPages[1] = Array.Empty<TmdbSearchHit>();

        var task = NewTask(tmdb, new PluginConfiguration { SuggestionsCatalogueMaxItems = 10 });

        await task.ExecuteAsync(new Progress<double>(_ => { }), CancellationToken.None);

        var movies = await _db.ListDiscoveryCacheAsync("movie", CancellationToken.None);
        var series = await _db.ListDiscoveryCacheAsync("series", CancellationToken.None);

        Assert.Single(movies);
        Assert.Equal(301, movies[0].TmdbId);
        Assert.Empty(series);
        Assert.Equal(new[] { 1, 2 }, tmdb.DiscoverMoviePageCalls);
        Assert.Equal(new[] { 1 }, tmdb.DiscoverSeriesPageCalls);
    }

    [Fact]
    public async Task Execute_DiscoverCancellationPropagates()
    {
        var tmdb = new StubTmdbClient();
        tmdb.DiscoverMoviePagesCancel.Add(1);
        var task = NewTask(tmdb, new PluginConfiguration { SuggestionsCatalogueMaxItems = 2 });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            task.ExecuteAsync(new Progress<double>(_ => { }), CancellationToken.None));
    }

    [Fact]
    public async Task Execute_DiscoverWalkDeduplicatesTrendingRowsBeforeMetadataWarm()
    {
        var tmdb = new StubTmdbClient
        {
            TrendingMovies = new[] { Hit(101, "Movie 101") },
        };
        tmdb.DiscoverMoviePages[1] = new[] { Hit(101, "Movie 101"), Hit(102, "Movie 102") };

        var task = NewTask(tmdb, new PluginConfiguration { SuggestionsCatalogueMaxItems = 3 });

        await task.ExecuteAsync(new Progress<double>(_ => { }), CancellationToken.None);

        var movies = await _db.ListDiscoveryCacheAsync("movie", CancellationToken.None);
        Assert.Equal(new[] { 101, 102 }, movies.Select(r => r.TmdbId).OrderBy(id => id));
        Assert.Empty(tmdb.MovieDetailCalls);
        var meta101 = await _db.GetTmdbMetadataAsync(101, "movie", CancellationToken.None);
        var meta102 = await _db.GetTmdbMetadataAsync(102, "movie", CancellationToken.None);
        Assert.NotNull(meta101);
        Assert.NotNull(meta102);
    }

    [Fact]
    public async Task Execute_DiscoverWalkStopsAtTmdbPageLimit()
    {
        var tmdb = new StubTmdbClient { GeneratedDiscoverMoviePages = 600 };
        var task = NewTask(tmdb, new PluginConfiguration { SuggestionsCatalogueMaxItems = 2000 });

        await task.ExecuteAsync(new Progress<double>(_ => { }), CancellationToken.None);

        Assert.Equal(500, tmdb.DiscoverMoviePageCalls.Count);
        Assert.Equal(500, tmdb.DiscoverMoviePageCalls.Max());
        Assert.DoesNotContain(501, tmdb.DiscoverMoviePageCalls);
    }

    [Fact]
    public async Task Execute_TtlEviction_RemovesStaleKeepsFresh()
    {
        // Seed one stale row + one fresh row in discovery_cache; no
        // materialised_state, so neither is protected by materialise.
        await _db.UpsertDiscoveryCacheAsync(900, "movie", CancellationToken.None);
        await SetDiscoveryLastRefreshedAsync(900, "movie", DateTimeOffset.UtcNow.AddDays(-2));
        await _db.UpsertDiscoveryCacheAsync(901, "movie", CancellationToken.None);

        var tmdb = new StubTmdbClient();
        var task = NewTask(tmdb, new PluginConfiguration
        {
            DiscoveryCacheTtlDays = 1,
            SuggestionsCatalogueMaxItems = 0,
        });

        await task.ExecuteAsync(new Progress<double>(_ => { }), CancellationToken.None);

        var remaining = await _db.ListDiscoveryCacheAsync("movie", CancellationToken.None);
        Assert.Contains(remaining, r => r.TmdbId == 901);
        Assert.DoesNotContain(remaining, r => r.TmdbId == 900);
    }

    [Fact]
    public async Task Execute_TtlEviction_ProtectsMaterialisedDiscoveryRows()
    {
        // Seed two stale discovery rows. The matching materialised_state row
        // protects tmdb=500; unprotected tmdb=501 should be pruned by the task.
        await _db.UpsertDiscoveryCacheAsync(500, "movie", CancellationToken.None);
        await SetDiscoveryLastRefreshedAsync(500, "movie", DateTimeOffset.UtcNow.AddDays(-2));
        await _db.InsertMaterialisedStateAsync(500, "movie", -1, -1, "/stub", "/fuse", CancellationToken.None);
        await _db.UpsertDiscoveryCacheAsync(501, "movie", CancellationToken.None);
        await SetDiscoveryLastRefreshedAsync(501, "movie", DateTimeOffset.UtcNow.AddDays(-2));

        var tmdb = new StubTmdbClient();
        var task = NewTask(tmdb, new PluginConfiguration
        {
            DiscoveryCacheTtlDays = 1,
            SuggestionsCatalogueMaxItems = 0,
        });
        await task.ExecuteAsync(new Progress<double>(_ => { }), CancellationToken.None);

        var rows = await _db.ListDiscoveryCacheAsync("movie", CancellationToken.None);
        Assert.Contains(rows, r => r.TmdbId == 500);
        Assert.DoesNotContain(rows, r => r.TmdbId == 501);
    }

    [Fact]
    public async Task Execute_BumpsBothChannelDataVersions()
    {
        var moviesBefore = _state.DataVersion(ChannelStateProvider.KindMovies);
        var showsBefore = _state.DataVersion(ChannelStateProvider.KindShows);

        var tmdb = new StubTmdbClient
        {
            TrendingMovies = new[] { Hit(1, "X") },
            TrendingSeries = new[] { Hit(2, "Y") },
        };
        var task = NewTask(tmdb);

        await Task.Delay(5); // ensure a different unix-ms tick
        await task.ExecuteAsync(new Progress<double>(_ => { }), CancellationToken.None);

        var moviesAfter = _state.DataVersion(ChannelStateProvider.KindMovies);
        var showsAfter = _state.DataVersion(ChannelStateProvider.KindShows);

        Assert.NotEqual(moviesBefore, moviesAfter);
        Assert.NotEqual(showsBefore, showsAfter);
    }

    [Fact]
    public async Task Execute_DetailsFetchFailure_DoesNotMatterForDiscoveryHitMetadata()
    {
        var tmdb = new StubTmdbClient
        {
            TrendingMovies = new[] { Hit(1, "OK") },
            // Discovery refresh now warms tmdb_metadata from the TMDB hit
            // itself, so per-id details failures must not leave cold rows.
            DetailsThrowFor = new HashSet<int> { 1 },
        };
        var task = NewTask(tmdb);

        await task.ExecuteAsync(new Progress<double>(_ => { }), CancellationToken.None);

        var rows = await _db.ListDiscoveryCacheAsync("movie", CancellationToken.None);
        Assert.Single(rows);
        var meta = await _db.GetTmdbMetadataAsync(1, "movie", CancellationToken.None);
        Assert.NotNull(meta);
        Assert.Equal("OK", meta!.Title);
    }

    private static TmdbSearchHit Hit(int id, string title)
    {
        return new TmdbSearchHit(
            Id: id,
            Title: title,
            OriginalTitle: title,
            Overview: "ov-" + id,
            PosterPath: "/poster" + id + ".jpg",
            BackdropPath: "/backdrop" + id + ".jpg",
            ReleaseDate: id >= 200 ? "2010-01-01" : "1999-01-01",
            VoteAverage: 7.2,
            VoteCount: 100);
    }
}

internal sealed class StubTmdbClient : ITmdbClient
{
    public IReadOnlyList<TmdbSearchHit> TrendingMovies { get; set; } = Array.Empty<TmdbSearchHit>();
    public IReadOnlyList<TmdbSearchHit> TrendingSeries { get; set; } = Array.Empty<TmdbSearchHit>();
    public Dictionary<int, IReadOnlyList<TmdbSearchHit>> DiscoverMoviePages { get; } = new();
    public Dictionary<int, IReadOnlyList<TmdbSearchHit>> DiscoverSeriesPages { get; } = new();
    public int GeneratedDiscoverMoviePages { get; set; }
    public List<int> DiscoverMoviePageCalls { get; } = new();
    public List<int> DiscoverSeriesPageCalls { get; } = new();
    public HashSet<int> DiscoverMoviePagesCancel { get; } = new();
    public List<int> MovieDetailCalls { get; } = new();
    public List<int> SeriesDetailCalls { get; } = new();
    public HashSet<int> DetailsThrowFor { get; set; } = new();

    public Task<IReadOnlyList<TmdbSearchHit>> GetTrendingMoviesAsync(string window, string? languageCode, CancellationToken ct)
        => Task.FromResult(TrendingMovies);

    public Task<IReadOnlyList<TmdbSearchHit>> GetTrendingSeriesAsync(string window, string? languageCode, CancellationToken ct)
        => Task.FromResult(TrendingSeries);

    public Task<TmdbMovieDetails?> GetMovieAsync(int tmdbId, string? languageCode, CancellationToken ct)
    {
        MovieDetailCalls.Add(tmdbId);
        if (DetailsThrowFor.Contains(tmdbId))
        {
            throw new InvalidOperationException("simulated failure for tmdb=" + tmdbId);
        }

        var hit = FindHit(AllMovieHits(), tmdbId);
        if (hit is null)
        {
            return Task.FromResult<TmdbMovieDetails?>(null);
        }

        return Task.FromResult<TmdbMovieDetails?>(new TmdbMovieDetails(
            Id: hit.Id,
            Title: hit.Title,
            OriginalTitle: hit.OriginalTitle,
            Overview: hit.Overview,
            PosterPath: hit.PosterPath,
            BackdropPath: hit.BackdropPath,
            ReleaseDate: hit.ReleaseDate,
            VoteAverage: hit.VoteAverage,
            VoteCount: hit.VoteCount,
            Runtime: 120,
            Genres: new[] { "Drama" },
            Status: "Released",
            Tagline: null,
            ImdbId: null,
            Budget: null,
            Revenue: null));
    }

    public Task<TmdbSeriesDetails?> GetSeriesAsync(int tmdbId, string? languageCode, CancellationToken ct)
    {
        SeriesDetailCalls.Add(tmdbId);
        if (DetailsThrowFor.Contains(tmdbId))
        {
            throw new InvalidOperationException("simulated failure for tmdb=" + tmdbId);
        }

        var hit = FindHit(AllSeriesHits(), tmdbId);
        if (hit is null)
        {
            return Task.FromResult<TmdbSeriesDetails?>(null);
        }

        return Task.FromResult<TmdbSeriesDetails?>(new TmdbSeriesDetails(
            Id: hit.Id,
            Name: hit.Title ?? "Series",
            OriginalName: hit.OriginalTitle,
            Overview: hit.Overview,
            PosterPath: hit.PosterPath,
            BackdropPath: hit.BackdropPath,
            FirstAirDate: hit.ReleaseDate,
            VoteAverage: hit.VoteAverage,
            VoteCount: hit.VoteCount,
            Genres: new[] { "Drama" },
            Status: "Returning Series",
            NumberOfSeasons: 3,
            NumberOfEpisodes: 24,
            OriginCountry: new[] { "US" },
            ImdbId: null));
    }

    private IReadOnlyList<TmdbSearchHit> AllMovieHits()
        => TrendingMovies.Concat(DiscoverMoviePages.Values.SelectMany(page => page)).ToArray();

    private IReadOnlyList<TmdbSearchHit> AllSeriesHits()
        => TrendingSeries.Concat(DiscoverSeriesPages.Values.SelectMany(page => page)).ToArray();

    private static TmdbSearchHit? FindHit(IReadOnlyList<TmdbSearchHit> source, int id)
    {
        foreach (var h in source)
        {
            if (h.Id == id)
            {
                return h;
            }
        }

        return null;
    }

    // --- All other ITmdbClient members: not exercised by these tests. ---
    public Task<IReadOnlyList<TmdbSearchHit>> SearchMoviesAsync(string query, int? year, string? languageCode, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TmdbSearchHit>>(Array.Empty<TmdbSearchHit>());

    public Task<IReadOnlyList<TmdbSearchHit>> SearchSeriesAsync(string query, int? firstAirYear, string? languageCode, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TmdbSearchHit>>(Array.Empty<TmdbSearchHit>());

    public Task<TmdbImages?> GetMovieImagesAsync(int tmdbId, string? languageCode, CancellationToken ct)
        => Task.FromResult<TmdbImages?>(null);

    public Task<TmdbImages?> GetSeriesImagesAsync(int tmdbId, string? languageCode, CancellationToken ct)
        => Task.FromResult<TmdbImages?>(null);

    public Task<TmdbConfiguration> GetConfigurationAsync(CancellationToken ct)
        => Task.FromResult(new TmdbConfiguration("https://image.tmdb.org/t/p/", new[] { "w500" }, new[] { "w500" }, new[] { "w500" }));

    public Task<string?> GetImdbIdForMovieAsync(int tmdbId, CancellationToken ct) => Task.FromResult<string?>(null);
    public Task<string?> GetImdbIdForSeriesAsync(int tmdbId, CancellationToken ct) => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<TmdbSearchHit>> DiscoverMoviesAsync(int page, string? languageCode, CancellationToken ct)
    {
        DiscoverMoviePageCalls.Add(page);
        if (DiscoverMoviePagesCancel.Contains(page))
        {
            throw new OperationCanceledException(ct);
        }

        if (DiscoverMoviePages.TryGetValue(page, out var hits))
        {
            return Task.FromResult(hits);
        }

        if (page <= GeneratedDiscoverMoviePages)
        {
            return Task.FromResult<IReadOnlyList<TmdbSearchHit>>(new[]
            {
                new TmdbSearchHit(
                    Id: 300000 + page,
                    Title: "Generated Movie " + page,
                    OriginalTitle: "Generated Movie " + page,
                    Overview: "generated",
                    PosterPath: "/generated" + page + ".jpg",
                    BackdropPath: null,
                    ReleaseDate: "2024-01-01",
                    VoteAverage: 6.5,
                    VoteCount: 10),
            });
        }

        return Task.FromResult<IReadOnlyList<TmdbSearchHit>>(Array.Empty<TmdbSearchHit>());
    }

    public Task<IReadOnlyList<TmdbSearchHit>> DiscoverSeriesAsync(int page, string? languageCode, CancellationToken ct)
    {
        DiscoverSeriesPageCalls.Add(page);
        return Task.FromResult(DiscoverSeriesPages.GetValueOrDefault(page, Array.Empty<TmdbSearchHit>()));
    }

    public Task<IReadOnlyList<TmdbSearchHit>> GetSimilarMoviesAsync(int tmdbId, string? languageCode, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TmdbSearchHit>>(Array.Empty<TmdbSearchHit>());

    public Task<IReadOnlyList<TmdbSearchHit>> GetSimilarSeriesAsync(int tmdbId, string? languageCode, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TmdbSearchHit>>(Array.Empty<TmdbSearchHit>());

    public Task<IReadOnlyList<TmdbSearchHit>> GetMovieRecommendationsAsync(int tmdbId, string? languageCode, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TmdbSearchHit>>(Array.Empty<TmdbSearchHit>());

    public Task<IReadOnlyList<TmdbSearchHit>> GetSeriesRecommendationsAsync(int tmdbId, string? languageCode, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<TmdbSearchHit>>(Array.Empty<TmdbSearchHit>());

    public Task<TmdbSeasonDetails?> GetSeasonAsync(int seriesTmdbId, int seasonNumber, string? languageCode, CancellationToken ct)
        => Task.FromResult<TmdbSeasonDetails?>(null);

    public Task<TmdbEpisodeDetails?> GetEpisodeAsync(int seriesTmdbId, int seasonNumber, int episodeNumber, string? languageCode, CancellationToken ct)
        => Task.FromResult<TmdbEpisodeDetails?>(null);

    public Task<TmdbMovieDetails?> GetMovieCollectionSequelAsync(int movieTmdbId, string? languageCode, CancellationToken ct)
        => Task.FromResult<TmdbMovieDetails?>(null);
}
