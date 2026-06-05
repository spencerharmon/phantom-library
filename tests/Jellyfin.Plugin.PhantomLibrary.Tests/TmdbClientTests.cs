using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class TmdbClientTests
{
    private const string SearchMoviesPayload = """
    {
      "page": 1,
      "results": [
        {
          "id": 603,
          "title": "The Matrix",
          "original_title": "The Matrix",
          "overview": "A computer hacker learns...",
          "poster_path": "/p96dm7sCMn4VYAStA6siNz30G1r.jpg",
          "backdrop_path": "/fNG7i7RqMErkcqhohV2a6cV1Ehy.jpg",
          "release_date": "1999-03-30",
          "vote_average": 8.2,
          "vote_count": 24000
        },
        {
          "id": 605,
          "title": "The Matrix Revolutions",
          "release_date": "2003-11-05",
          "vote_average": 6.7,
          "vote_count": 8000
        }
      ],
      "total_pages": 1,
      "total_results": 2
    }
    """;

    private const string MovieDetailsPayload = """
    {
      "id": 603,
      "title": "The Matrix",
      "original_title": "The Matrix",
      "overview": "Hacker fights machines.",
      "poster_path": "/x.jpg",
      "release_date": "1999-03-30",
      "vote_average": 8.2,
      "vote_count": 24000,
      "runtime": 136,
      "genres": [{"id":28,"name":"Action"},{"id":878,"name":"Science Fiction"}],
      "status": "Released",
      "tagline": "Welcome to the Real World.",
      "imdb_id": "tt0133093",
      "budget": 63000000,
      "revenue": 463517383,
      "external_ids": { "imdb_id": "tt0133093" }
    }
    """;

    private static TmdbClient Build(QueuedHandler handler, string apiKey = "test-key")
    {
        var http = new HttpClient(handler) { BaseAddress = null };
        return new TmdbClient(http, new StubKeyProvider(apiKey));
    }

    [Fact]
    public async Task SearchMovies_ParsesResults()
    {
        var handler = new QueuedHandler().Enqueue(HttpStatusCode.OK, SearchMoviesPayload);
        var client = Build(handler);

        var hits = await client.SearchMoviesAsync("matrix", null, "en-US", CancellationToken.None);

        Assert.Equal(2, hits.Count);
        Assert.Equal(603, hits[0].Id);
        Assert.Equal("The Matrix", hits[0].Title);
        Assert.Equal("1999-03-30", hits[0].ReleaseDate);
        Assert.Equal(8.2, hits[0].VoteAverage);
        Assert.Single(handler.Requests);
        var url = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("search/movie", url, StringComparison.Ordinal);
        Assert.Contains("api_key=test-key", url, StringComparison.Ordinal);
        Assert.Contains("query=matrix", url, StringComparison.Ordinal);
        Assert.Contains("language=en-US", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMovie_ParsesDetailsAndExternalIds()
    {
        var handler = new QueuedHandler().Enqueue(HttpStatusCode.OK, MovieDetailsPayload);
        var client = Build(handler);

        var movie = await client.GetMovieAsync(603, "en-US", CancellationToken.None);

        Assert.NotNull(movie);
        Assert.Equal(603, movie!.Id);
        Assert.Equal("tt0133093", movie.ImdbId);
        Assert.Equal(136, movie.Runtime);
        Assert.Contains("Action", movie.Genres);
        Assert.Contains("Science Fiction", movie.Genres);
        Assert.Equal("Released", movie.Status);
        Assert.Equal("Welcome to the Real World.", movie.Tagline);
        var url = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("append_to_response=external_ids", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingApiKey_Throws()
    {
        var handler = new QueuedHandler();
        var client = Build(handler, apiKey: "");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.SearchMoviesAsync("matrix", null, null, CancellationToken.None));
        Assert.Contains("TMDB API key", ex.Message, StringComparison.Ordinal);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task RateLimit_RetriesOnce()
    {
        var handler = new QueuedHandler()
            .Enqueue(HttpStatusCode.TooManyRequests, null, msg =>
            {
                msg.Headers.TryAddWithoutValidation("Retry-After", "1");
            })
            .Enqueue(HttpStatusCode.OK, SearchMoviesPayload);
        var client = Build(handler);

        var hits = await client.SearchMoviesAsync("matrix", null, null, CancellationToken.None);

        Assert.Equal(2, hits.Count);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task GetMovie_404_ReturnsNull()
    {
        var handler = new QueuedHandler().Enqueue(HttpStatusCode.NotFound, """{"status_message":"not found"}""");
        var client = Build(handler);

        var movie = await client.GetMovieAsync(99999999, null, CancellationToken.None);

        Assert.Null(movie);
    }

    [Fact]
    public async Task UnexpectedError_ThrowsTmdbApiException()
    {
        var handler = new QueuedHandler().Enqueue(HttpStatusCode.InternalServerError, """{"err":"boom"}""");
        var client = Build(handler);

        await Assert.ThrowsAsync<TmdbApiException>(
            () => client.SearchMoviesAsync("x", null, null, CancellationToken.None));
    }

    private const string TrendingMoviesPayload = """
    {
      "page": 1,
      "results": [
        {
          "id": 603,
          "title": "The Matrix",
          "original_title": "The Matrix",
          "overview": "Hacker vs machines.",
          "poster_path": "/p.jpg",
          "backdrop_path": "/b.jpg",
          "release_date": "1999-03-30",
          "vote_average": 8.2,
          "vote_count": 24000,
          "genre_ids": [28, 878]
        },
        {
          "id": 11,
          "title": "Star Wars",
          "release_date": "1977-05-25",
          "vote_average": 8.6,
          "vote_count": 18000,
          "genre_ids": [12, 28, 878]
        }
      ],
      "total_pages": 1,
      "total_results": 2
    }
    """;

    private const string SimilarMoviesPayload = """
    {
      "page": 1,
      "results": [
        {
          "id": 605,
          "title": "The Matrix Revolutions",
          "overview": "Sequel.",
          "release_date": "2003-11-05",
          "vote_average": 6.7,
          "vote_count": 8000,
          "genre_ids": [28]
        }
      ],
      "total_pages": 1, "total_results": 1
    }
    """;

    [Fact]
    public async Task GetTrendingMovies_ParsesResults_AndUsesWeekWindow()
    {
        var handler = new QueuedHandler().Enqueue(HttpStatusCode.OK, TrendingMoviesPayload);
        var client = Build(handler);

        var hits = await client.GetTrendingMoviesAsync("week", "en-US", CancellationToken.None);

        Assert.Equal(2, hits.Count);
        Assert.Equal(603, hits[0].Id);
        Assert.Equal("The Matrix", hits[0].Title);
        Assert.NotNull(hits[0].GenreIds);
        Assert.Contains(28, hits[0].GenreIds!);
        var url = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("/trending/movie/week", url, StringComparison.Ordinal);
        Assert.Contains("page=1", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetTrendingMovies_NormalisesUnknownWindowToWeek()
    {
        var handler = new QueuedHandler().Enqueue(HttpStatusCode.OK, TrendingMoviesPayload);
        var client = Build(handler);

        await client.GetTrendingMoviesAsync("garbage", null, CancellationToken.None);

        var url = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("/trending/movie/week", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSimilarMovies_ParsesResults_AndUsesCorrectEndpoint()
    {
        var handler = new QueuedHandler().Enqueue(HttpStatusCode.OK, SimilarMoviesPayload);
        var client = Build(handler);

        var hits = await client.GetSimilarMoviesAsync(603, "en-US", CancellationToken.None);

        Assert.Single(hits);
        Assert.Equal(605, hits[0].Id);
        Assert.Equal("The Matrix Revolutions", hits[0].Title);
        var url = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("/movie/603/similar", url, StringComparison.Ordinal);
    }

    private const string SeasonPayload = """
    {
      "id": 12345,
      "season_number": 1,
      "episodes": [
        { "id": 100, "episode_number": 1, "season_number": 1, "name": "Pilot", "overview": "o1", "air_date": "2020-01-01", "runtime": 42, "vote_average": 7.5 },
        { "id": 101, "episode_number": 2, "season_number": 1, "name": "E2", "air_date": "2020-01-08", "runtime": 44 }
      ]
    }
    """;

    [Fact]
    public async Task GetSeason_ParsesEpisodes()
    {
        var handler = new QueuedHandler().Enqueue(HttpStatusCode.OK, SeasonPayload);
        var client = Build(handler);

        var season = await client.GetSeasonAsync(99, 1, "en-US", CancellationToken.None);

        Assert.NotNull(season);
        Assert.Equal(99, season!.SeriesTmdbId);
        Assert.Equal(1, season.SeasonNumber);
        Assert.Equal(2, season.Episodes.Count);
        Assert.Equal("Pilot", season.Episodes[0].Name);
        Assert.Equal(1, season.Episodes[0].EpisodeNumber);
        Assert.Equal(42, season.Episodes[0].Runtime);
        var url = handler.Requests[0].RequestUri!.ToString();
        Assert.Contains("/tv/99/season/1", url, StringComparison.Ordinal);
    }

    private const string MovieWithCollectionPayload = """
    {
      "id": 671,
      "title": "Harry Potter and the Philosopher's Stone",
      "release_date": "2001-11-16",
      "belongs_to_collection": { "id": 1241, "name": "Harry Potter Collection" }
    }
    """;

    private const string CollectionPayload = """
    {
      "id": 1241,
      "name": "Harry Potter Collection",
      "parts": [
        { "id": 671, "title": "Philosopher's Stone", "release_date": "2001-11-16" },
        { "id": 672, "title": "Chamber of Secrets", "release_date": "2002-11-15" },
        { "id": 673, "title": "Prisoner of Azkaban", "release_date": "2004-05-31" }
      ]
    }
    """;

    private const string SequelMoviePayload = """
    {
      "id": 672,
      "title": "Harry Potter and the Chamber of Secrets",
      "release_date": "2002-11-15",
      "runtime": 161,
      "status": "Released",
      "genres": [],
      "vote_average": 7.7,
      "vote_count": 1000,
      "external_ids": { "imdb_id": "tt0295297" }
    }
    """;

    [Fact]
    public async Task GetMovieCollectionSequel_ReturnsImmediateNextEntry()
    {
        var handler = new QueuedHandler()
            .Enqueue(HttpStatusCode.OK, MovieWithCollectionPayload)
            .Enqueue(HttpStatusCode.OK, CollectionPayload)
            .Enqueue(HttpStatusCode.OK, SequelMoviePayload);
        var client = Build(handler);

        var sequel = await client.GetMovieCollectionSequelAsync(671, null, CancellationToken.None);

        Assert.NotNull(sequel);
        Assert.Equal(672, sequel!.Id);
        Assert.Equal("tt0295297", sequel.ImdbId);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("append_to_response=external_ids%2Cbelongs_to_collection", handler.Requests[0].RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains("/collection/1241", handler.Requests[1].RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Contains("/movie/672", handler.Requests[2].RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMovieCollectionSequel_NoCollection_ReturnsNull()
    {
        const string noCollection = """
        { "id": 999, "title": "Standalone", "release_date": "2000-01-01" }
        """;
        var handler = new QueuedHandler().Enqueue(HttpStatusCode.OK, noCollection);
        var client = Build(handler);

        var sequel = await client.GetMovieCollectionSequelAsync(999, null, CancellationToken.None);
        Assert.Null(sequel);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetMovieCollectionSequel_LastInCollection_ReturnsNull()
    {
        const string lastEntry = """
        {
          "id": 673,
          "title": "Prisoner of Azkaban",
          "release_date": "2004-05-31",
          "belongs_to_collection": { "id": 1241, "name": "HP" }
        }
        """;
        var handler = new QueuedHandler()
            .Enqueue(HttpStatusCode.OK, lastEntry)
            .Enqueue(HttpStatusCode.OK, CollectionPayload);
        var client = Build(handler);

        var sequel = await client.GetMovieCollectionSequelAsync(673, null, CancellationToken.None);
        Assert.Null(sequel);
    }
}
