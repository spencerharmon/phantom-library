using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Library;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public sealed class FavouriteRecommendationIngestorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PhantomDb _db;

    public FavouriteRecommendationIngestorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "phantom-favrec-tests-" + Guid.NewGuid().ToString("N") + ".db");
        _db = new PhantomDb(_dbPath);
        // Force schema creation, mirroring the other DB-backed test fixtures.
        // The disabled-ingest no-op path writes nothing, so without this the
        // catalogue_items table would not exist when ListCatalogueAsync reads it.
        // Microsoft.Data.Sqlite's async API runs synchronously, so bridging the
        // one-time init here does not deadlock.
        _db.SetMetaAsync("__init__", "1", CancellationToken.None).GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        _db.Dispose();
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

    private FavouriteRecommendationIngestor NewIngestor(StubTmdbClient tmdb, PluginConfiguration? config = null)
    {
        var cached = new CachedTmdbReader(tmdb, _db, NullLogger<CachedTmdbReader>.Instance);
        return new FavouriteRecommendationIngestor(
            cached,
            _db,
            NullLogger<FavouriteRecommendationIngestor>.Instance,
            () => config);
    }

    private static TmdbSearchHit Hit(int id, string? title = null, string? date = "2024-01-01")
        => new(
            Id: id,
            Title: title ?? ("Title " + id),
            OriginalTitle: title ?? ("Title " + id),
            Overview: "overview " + id,
            PosterPath: "/poster" + id + ".jpg",
            BackdropPath: "/backdrop" + id + ".jpg",
            ReleaseDate: date,
            VoteAverage: 7.0,
            VoteCount: 50);

    private static TmdbSearchHit TitlelessHit(int id)
        => new(id, null, null, null, null, null, null, null, null);

    private async Task<IReadOnlyList<int>> ListCatalogueAsync(string type)
    {
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT tmdb_id FROM catalogue_items WHERE type=$type ORDER BY tmdb_id;";
        cmd.Parameters.AddWithValue("$type", type);
        var list = new List<int>();
        await using var r = await cmd.ExecuteReaderAsync(CancellationToken.None);
        while (await r.ReadAsync(CancellationToken.None))
        {
            list.Add(r.GetInt32(0));
        }

        return list;
    }

    private async Task<int> ScalarCountAsync(string sql)
    {
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbPath }.ToString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync(CancellationToken.None);
        return Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Ingest_Movie_WritesCatalogueMetadataAndAvailability()
    {
        var tmdb = new StubTmdbClient();
        tmdb.SimilarMovies[42] = new[] { Hit(501), Hit(502) };
        tmdb.MovieRecommendations[42] = new[] { Hit(502), Hit(503) }; // 502 overlaps → deduped
        var sut = NewIngestor(tmdb);

        var result = await sut.IngestForFavouriteAsync(42, "movie", CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.Equal(4, result.CandidatesConsidered);
        Assert.Equal(3, result.Inserted);
        Assert.Equal(3, result.AvailabilityInserted);
        Assert.Equal(0, result.SeriesExpansionInserted);

        var movies = await ListCatalogueAsync("movie");
        Assert.Equal(new[] { 501, 502, 503 }, movies);
        Assert.DoesNotContain(42, movies); // seed itself is never recommended back
        Assert.Equal(3, await ScalarCountAsync("SELECT COUNT(*) FROM availability_items WHERE type='movie';"));
    }

    [Fact]
    public async Task Ingest_Series_WritesCatalogueAndSeriesExpansion()
    {
        var tmdb = new StubTmdbClient();
        tmdb.SimilarSeries[200] = new[] { Hit(601) };
        tmdb.SeriesRecommendations[200] = new[] { Hit(602) };
        var sut = NewIngestor(tmdb);

        var result = await sut.IngestForFavouriteAsync(200, "series", CancellationToken.None);

        Assert.True(result.Enabled);
        Assert.Equal(2, result.CandidatesConsidered);
        Assert.Equal(2, result.Inserted);
        Assert.Equal(2, result.SeriesExpansionInserted);
        Assert.Equal(0, result.AvailabilityInserted);

        var series = await ListCatalogueAsync("series");
        Assert.Equal(new[] { 601, 602 }, series);
        Assert.Equal(2, await ScalarCountAsync("SELECT COUNT(*) FROM series_expansion_state;"));
        Assert.Single(tmdb.SimilarSeriesCalls);
        Assert.Single(tmdb.SeriesRecommendationCalls);
    }

    [Fact]
    public async Task Ingest_DropsSeedId()
    {
        var tmdb = new StubTmdbClient();
        tmdb.SimilarMovies[42] = new[] { Hit(42), Hit(43) };
        var sut = NewIngestor(tmdb);

        var result = await sut.IngestForFavouriteAsync(42, "movie", CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(new[] { 43 }, await ListCatalogueAsync("movie"));
    }

    [Fact]
    public async Task Ingest_DropsTitlelessHits()
    {
        var tmdb = new StubTmdbClient();
        tmdb.SimilarMovies[42] = new[] { TitlelessHit(701), Hit(702) };
        var sut = NewIngestor(tmdb);

        var result = await sut.IngestForFavouriteAsync(42, "movie", CancellationToken.None);

        Assert.Equal(1, result.Inserted);
        Assert.Equal(new[] { 702 }, await ListCatalogueAsync("movie"));
    }

    [Fact]
    public async Task Ingest_RespectsMaxPerFavourite()
    {
        var tmdb = new StubTmdbClient();
        tmdb.SimilarMovies[42] = new[] { Hit(501), Hit(502), Hit(503), Hit(504) };
        var sut = NewIngestor(tmdb, new PluginConfiguration { FavouriteRecommendationsMaxPerFavourite = 2 });

        var result = await sut.IngestForFavouriteAsync(42, "movie", CancellationToken.None);

        Assert.Equal(4, result.CandidatesConsidered);
        Assert.Equal(2, result.Inserted);
        Assert.Equal(2, (await ListCatalogueAsync("movie")).Count);
    }

    [Fact]
    public async Task Ingest_Disabled_IsNoOpAndSkipsTmdb()
    {
        var tmdb = new StubTmdbClient();
        tmdb.SimilarMovies[42] = new[] { Hit(501) };
        var sut = NewIngestor(tmdb, new PluginConfiguration { FavouriteRecommendationsEnabled = false });

        var result = await sut.IngestForFavouriteAsync(42, "movie", CancellationToken.None);

        Assert.False(result.Enabled);
        Assert.Equal(0, result.Inserted);
        Assert.Empty(await ListCatalogueAsync("movie"));
        Assert.Empty(tmdb.SimilarMovieCalls);
        Assert.Empty(tmdb.MovieRecommendationCalls);
    }

    [Fact]
    public async Task Ingest_InvalidType_Throws()
    {
        var sut = NewIngestor(new StubTmdbClient());
        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.IngestForFavouriteAsync(42, "person", CancellationToken.None));
    }

    [Fact]
    public async Task Ingest_NonPositiveTmdb_Throws()
    {
        var sut = NewIngestor(new StubTmdbClient());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => sut.IngestForFavouriteAsync(0, "movie", CancellationToken.None));
    }
}
