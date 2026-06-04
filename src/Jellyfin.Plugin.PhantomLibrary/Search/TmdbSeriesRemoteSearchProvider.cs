using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.PhantomLibrary.Search;

/// <summary>TMDB-backed <see cref="IRemoteSearchProvider{SeriesInfo}"/>.</summary>
public sealed class TmdbSeriesRemoteSearchProvider : IRemoteSearchProvider<SeriesInfo>
{
    private const int YearDistanceToleranceYears = 2;
    private const string TmdbProviderId = "Tmdb";

    private readonly ITmdbClient _tmdb;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Initializes a new instance of the <see cref="TmdbSeriesRemoteSearchProvider"/> class.</summary>
    public TmdbSeriesRemoteSearchProvider(ITmdbClient tmdbClient, IHttpClientFactory httpClientFactory)
    {
        _tmdb = tmdbClient ?? throw new ArgumentNullException(nameof(tmdbClient));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    /// <inheritdoc/>
    public string Name => "Phantom Library (TMDB)";

    /// <inheritdoc/>
    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(SeriesInfo searchInfo, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(searchInfo);

        var language = string.IsNullOrWhiteSpace(searchInfo.MetadataLanguage) ? "en-US" : searchInfo.MetadataLanguage;
        var config = await _tmdb.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);

        if (searchInfo.ProviderIds is not null
            && searchInfo.ProviderIds.TryGetValue(TmdbProviderId, out var tmdbIdRaw)
            && int.TryParse(tmdbIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId))
        {
            var series = await _tmdb.GetSeriesAsync(tmdbId, language, cancellationToken).ConfigureAwait(false);
            if (series is null)
            {
                return Array.Empty<RemoteSearchResult>();
            }

            return new[] { MapDetailsToResult(series, config) };
        }

        if (string.IsNullOrWhiteSpace(searchInfo.Name))
        {
            return Array.Empty<RemoteSearchResult>();
        }

        var hits = await _tmdb.SearchSeriesAsync(searchInfo.Name, searchInfo.Year, language, cancellationToken)
            .ConfigureAwait(false);

        var results = new List<RemoteSearchResult>(hits.Count);
        foreach (var hit in hits)
        {
            if (string.IsNullOrWhiteSpace(hit.Title))
            {
                continue;
            }

            var year = TmdbMovieRemoteSearchProvider.ParseYear(hit.ReleaseDate);
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
            PremiereDate = TmdbMovieRemoteSearchProvider.ParseDate(hit.ReleaseDate),
            SearchProviderName = Name,
        };
        result.ProviderIds[TmdbProviderId] = hit.Id.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(hit.PosterPath))
        {
            result.ImageUrl = config.BuildPosterUrl(hit.PosterPath, "w500");
        }

        return result;
    }

    private RemoteSearchResult MapDetailsToResult(TmdbSeriesDetails details, TmdbConfiguration config)
    {
        var year = TmdbMovieRemoteSearchProvider.ParseYear(details.FirstAirDate);
        var result = new RemoteSearchResult
        {
            Name = details.Name,
            Overview = details.Overview,
            ProductionYear = year,
            PremiereDate = TmdbMovieRemoteSearchProvider.ParseDate(details.FirstAirDate),
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
}
