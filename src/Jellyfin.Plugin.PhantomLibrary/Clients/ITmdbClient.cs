using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;

namespace Jellyfin.Plugin.PhantomLibrary.Clients;

/// <summary>Thin TMDB v3 REST client surface used by the plugin.</summary>
public interface ITmdbClient
{
    /// <summary>Searches TMDB movies by free-text query.</summary>
    Task<IReadOnlyList<TmdbSearchHit>> SearchMoviesAsync(string query, int? year, string? languageCode, CancellationToken cancellationToken);

    /// <summary>Searches TMDB series by free-text query.</summary>
    Task<IReadOnlyList<TmdbSearchHit>> SearchSeriesAsync(string query, int? firstAirYear, string? languageCode, CancellationToken cancellationToken);

    /// <summary>Fetches a single movie's full details (returns null on 404).</summary>
    Task<TmdbMovieDetails?> GetMovieAsync(int tmdbId, string? languageCode, CancellationToken cancellationToken);

    /// <summary>Fetches a single series' full details (returns null on 404).</summary>
    Task<TmdbSeriesDetails?> GetSeriesAsync(int tmdbId, string? languageCode, CancellationToken cancellationToken);

    /// <summary>Fetches a movie's image bundle (posters, backdrops, logos).</summary>
    Task<TmdbImages?> GetMovieImagesAsync(int tmdbId, string? languageCode, CancellationToken cancellationToken);

    /// <summary>Fetches a series' image bundle.</summary>
    Task<TmdbImages?> GetSeriesImagesAsync(int tmdbId, string? languageCode, CancellationToken cancellationToken);

    /// <summary>Fetches the TMDB /configuration endpoint (image CDN base URL and size buckets); process-cached for 12h.</summary>
    Task<TmdbConfiguration> GetConfigurationAsync(CancellationToken cancellationToken);

    /// <summary>Returns the IMDB id for a movie via /movie/{id}/external_ids, or null if absent.</summary>
    Task<string?> GetImdbIdForMovieAsync(int tmdbId, CancellationToken cancellationToken);

    /// <summary>Returns the IMDB id for a series via /tv/{id}/external_ids, or null if absent.</summary>
    Task<string?> GetImdbIdForSeriesAsync(int tmdbId, CancellationToken cancellationToken);

    /// <summary>Trending movies. <paramref name="window"/> is "day" or "week".</summary>
    Task<IReadOnlyList<TmdbSearchHit>> GetTrendingMoviesAsync(string window, string? languageCode, CancellationToken cancellationToken);

    /// <summary>
    /// Discover movies, popularity-sorted. <paramref name="page"/> is 1-indexed (TMDB convention).
    /// Returns up to 20 hits per page; empty page → empty array.
    /// </summary>
    Task<IReadOnlyList<TmdbSearchHit>> DiscoverMoviesAsync(int page, string? languageCode, CancellationToken cancellationToken);

    /// <summary>
    /// Discover TV series, popularity-sorted. <paramref name="page"/> is 1-indexed (TMDB convention).
    /// Returns up to 20 hits per page; empty page → empty array.
    /// </summary>
    Task<IReadOnlyList<TmdbSearchHit>> DiscoverSeriesAsync(int page, string? languageCode, CancellationToken cancellationToken);

    /// <summary>Trending series. <paramref name="window"/> is "day" or "week".</summary>
    Task<IReadOnlyList<TmdbSearchHit>> GetTrendingSeriesAsync(string window, string? languageCode, CancellationToken cancellationToken);

    /// <summary>Movies similar to <paramref name="tmdbId"/>.</summary>
    Task<IReadOnlyList<TmdbSearchHit>> GetSimilarMoviesAsync(int tmdbId, string? languageCode, CancellationToken cancellationToken);

    /// <summary>Series similar to <paramref name="tmdbId"/>.</summary>
    Task<IReadOnlyList<TmdbSearchHit>> GetSimilarSeriesAsync(int tmdbId, string? languageCode, CancellationToken cancellationToken);

    /// <summary>Movies recommended off the back of <paramref name="tmdbId"/>.</summary>
    Task<IReadOnlyList<TmdbSearchHit>> GetMovieRecommendationsAsync(int tmdbId, string? languageCode, CancellationToken cancellationToken);

    /// <summary>Series recommended off the back of <paramref name="tmdbId"/>.</summary>
    Task<IReadOnlyList<TmdbSearchHit>> GetSeriesRecommendationsAsync(int tmdbId, string? languageCode, CancellationToken cancellationToken);

    /// <summary>Fetches a TV season payload (episode summaries).</summary>
    Task<TmdbSeasonDetails?> GetSeasonAsync(int seriesTmdbId, int seasonNumber, string? languageCode, CancellationToken cancellationToken);

    /// <summary>Fetches single-episode details including external_ids.</summary>
    Task<TmdbEpisodeDetails?> GetEpisodeAsync(int seriesTmdbId, int seasonNumber, int episodeNumber, string? languageCode, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the immediate next entry in the same TMDB collection as
    /// <paramref name="movieTmdbId"/>, ordered by release_date. Returns
    /// null if the movie has no collection or is the last entry.
    /// </summary>
    Task<TmdbMovieDetails?> GetMovieCollectionSequelAsync(int movieTmdbId, string? languageCode, CancellationToken cancellationToken);
}
