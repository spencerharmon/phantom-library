using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.PhantomLibrary.Search;

/// <summary>
/// TMDB-backed <see cref="IRemoteSearchProvider{MovieInfo}"/>. Surfaces TMDB
/// hits inside Jellyfin's native search/identify UI on every client.
/// </summary>
public sealed class TmdbMovieRemoteSearchProvider : IRemoteSearchProvider<MovieInfo>
{
    private const int YearDistanceToleranceYears = 2;
    private const string TmdbProviderId = "Tmdb";

    private readonly ITmdbClient _tmdb;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Initializes a new instance of the <see cref="TmdbMovieRemoteSearchProvider"/> class.</summary>
    public TmdbMovieRemoteSearchProvider(ITmdbClient tmdbClient, IHttpClientFactory httpClientFactory)
    {
        _tmdb = tmdbClient ?? throw new ArgumentNullException(nameof(tmdbClient));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    /// <inheritdoc/>
    public string Name => "Phantom Library (TMDB)";

    /// <inheritdoc/>
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo searchInfo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(searchInfo);

        var language = string.IsNullOrWhiteSpace(searchInfo.MetadataLanguage) ? "en-US" : searchInfo.MetadataLanguage;
        var config = await _tmdb.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);

        if (searchInfo.ProviderIds is not null
            && searchInfo.ProviderIds.TryGetValue(TmdbProviderId, out var tmdbIdRaw)
            && int.TryParse(tmdbIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId))
        {
            var movie = await _tmdb.GetMovieAsync(tmdbId, language, cancellationToken).ConfigureAwait(false);
            if (movie is null)
            {
                return Array.Empty<RemoteSearchResult>();
            }

            return new[] { MapDetailsToResult(movie, config) };
        }

        if (string.IsNullOrWhiteSpace(searchInfo.Name))
        {
            return Array.Empty<RemoteSearchResult>();
        }

        var hits = await _tmdb.SearchMoviesAsync(searchInfo.Name, searchInfo.Year, language, cancellationToken)
            .ConfigureAwait(false);

        var results = new List<RemoteSearchResult>(hits.Count);
        foreach (var hit in hits)
        {
            if (string.IsNullOrWhiteSpace(hit.Title))
            {
                continue;
            }

            var year = ParseYear(hit.ReleaseDate);
            if (searchInfo.Year is { } wantedYear && year is { } gotYear
                && Math.Abs(gotYear - wantedYear) > YearDistanceToleranceYears)
            {
                continue;
            }

            results.Add(MapHitToResult(hit, year, config));
        }

        return results;
    }

    /// <inheritdoc/>
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        return client.GetAsync(new Uri(url), cancellationToken);
    }

    private RemoteSearchResult MapHitToResult(TmdbSearchHit hit, int? year, TmdbConfiguration config)
    {
        var result = new RemoteSearchResult
        {
            Name = hit.Title,
            Overview = hit.Overview,
            ProductionYear = year,
            PremiereDate = ParseDate(hit.ReleaseDate),
            SearchProviderName = Name,
        };
        result.ProviderIds[TmdbProviderId] = hit.Id.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(hit.PosterPath))
        {
            result.ImageUrl = config.BuildPosterUrl(hit.PosterPath, "w500");
        }

        return result;
    }

    private RemoteSearchResult MapDetailsToResult(TmdbMovieDetails details, TmdbConfiguration config)
    {
        var year = ParseYear(details.ReleaseDate);
        var result = new RemoteSearchResult
        {
            Name = details.Title,
            Overview = details.Overview,
            ProductionYear = year,
            PremiereDate = ParseDate(details.ReleaseDate),
            SearchProviderName = Name,
        };
        result.ProviderIds[TmdbProviderId] = details.Id.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(details.ImdbId))
        {
            result.ProviderIds["Imdb"] = details.ImdbId!;
        }

        if (!string.IsNullOrWhiteSpace(details.PosterPath))
        {
            result.ImageUrl = config.BuildPosterUrl(details.PosterPath, "w500");
        }

        return result;
    }

    internal static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4)
        {
            return null;
        }

        return int.TryParse(date.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : null;
    }

    internal static DateTime? ParseDate(string? date)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            return null;
        }

        return DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt
            : null;
    }
}
