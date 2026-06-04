using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;

namespace Jellyfin.Plugin.PhantomLibrary.Clients;

/// <summary>
/// TMDB v3 client implemented over <see cref="HttpClient"/> obtained via
/// <see cref="IHttpClientFactory"/>. API key is read per request from
/// <see cref="ITmdbApiKeyProvider"/> so operator updates take effect
/// without restart. The /configuration endpoint is cached in-process for
/// 12 hours.
/// </summary>
public sealed class TmdbClient : ITmdbClient
{
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const int MaxRetryAfterSeconds = 30;
    private static readonly TimeSpan ConfigurationCacheTtl = TimeSpan.FromHours(12);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
    };

    private readonly HttpClient _http;
    private readonly ITmdbApiKeyProvider _keyProvider;
    private readonly SemaphoreSlim _configLock = new(1, 1);
    private TmdbConfiguration? _cachedConfiguration;
    private DateTimeOffset _cachedConfigurationExpiry;

    /// <summary>Initializes a new instance of the <see cref="TmdbClient"/> class.</summary>
    public TmdbClient(HttpClient httpClient, ITmdbApiKeyProvider keyProvider)
    {
        _http = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _keyProvider = keyProvider ?? throw new ArgumentNullException(nameof(keyProvider));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TmdbSearchHit>> SearchMoviesAsync(string query, int? year, string? languageCode, CancellationToken cancellationToken)
    {
        var qs = new List<KeyValuePair<string, string?>>
        {
            new("query", query),
            new("include_adult", "false"),
        };
        if (year is not null)
        {
            qs.Add(new("year", year.Value.ToString(CultureInfo.InvariantCulture)));
        }

        var path = BuildPath("/search/movie", languageCode, qs);
        var resp = await GetJsonAsync<TmdbSearchResponse<TmdbMovieSearchHitDto>>(path, cancellationToken).ConfigureAwait(false);
        if (resp?.Results is null)
        {
            return Array.Empty<TmdbSearchHit>();
        }

        return resp.Results.Select(r => new TmdbSearchHit(
            r.Id, r.Title, r.OriginalTitle, r.Overview, r.PosterPath, r.BackdropPath,
            r.ReleaseDate, r.VoteAverage, r.VoteCount)).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TmdbSearchHit>> SearchSeriesAsync(string query, int? firstAirYear, string? languageCode, CancellationToken cancellationToken)
    {
        var qs = new List<KeyValuePair<string, string?>>
        {
            new("query", query),
            new("include_adult", "false"),
        };
        if (firstAirYear is not null)
        {
            qs.Add(new("first_air_date_year", firstAirYear.Value.ToString(CultureInfo.InvariantCulture)));
        }

        var path = BuildPath("/search/tv", languageCode, qs);
        var resp = await GetJsonAsync<TmdbSearchResponse<TmdbSeriesSearchHitDto>>(path, cancellationToken).ConfigureAwait(false);
        if (resp?.Results is null)
        {
            return Array.Empty<TmdbSearchHit>();
        }

        return resp.Results.Select(r => new TmdbSearchHit(
            r.Id, r.Name, r.OriginalName, r.Overview, r.PosterPath, r.BackdropPath,
            r.FirstAirDate, r.VoteAverage, r.VoteCount)).ToList();
    }

    /// <inheritdoc/>
    public async Task<TmdbMovieDetails?> GetMovieAsync(int tmdbId, string? languageCode, CancellationToken cancellationToken)
    {
        var path = BuildPath(
            $"/movie/{tmdbId.ToString(CultureInfo.InvariantCulture)}",
            languageCode,
            new[] { new KeyValuePair<string, string?>("append_to_response", "external_ids") });
        var dto = await GetJsonAsync<TmdbMovieDetailsDto>(path, cancellationToken, allow404: true).ConfigureAwait(false);
        if (dto is null)
        {
            return null;
        }

        var imdb = !string.IsNullOrWhiteSpace(dto.ImdbId) ? dto.ImdbId : dto.ExternalIds?.ImdbId;
        return new TmdbMovieDetails(
            dto.Id,
            dto.Title,
            dto.OriginalTitle,
            dto.Overview,
            dto.PosterPath,
            dto.BackdropPath,
            dto.ReleaseDate,
            dto.VoteAverage,
            dto.VoteCount,
            dto.Runtime ?? 0,
            (dto.Genres ?? Array.Empty<TmdbGenreDto>()).Select(g => g.Name).ToArray(),
            dto.Status ?? string.Empty,
            dto.Tagline,
            string.IsNullOrWhiteSpace(imdb) ? null : imdb,
            dto.Budget,
            dto.Revenue);
    }

    /// <inheritdoc/>
    public async Task<TmdbSeriesDetails?> GetSeriesAsync(int tmdbId, string? languageCode, CancellationToken cancellationToken)
    {
        var path = BuildPath(
            $"/tv/{tmdbId.ToString(CultureInfo.InvariantCulture)}",
            languageCode,
            new[] { new KeyValuePair<string, string?>("append_to_response", "external_ids") });
        var dto = await GetJsonAsync<TmdbSeriesDetailsDto>(path, cancellationToken, allow404: true).ConfigureAwait(false);
        if (dto is null)
        {
            return null;
        }

        var imdb = dto.ExternalIds?.ImdbId;
        return new TmdbSeriesDetails(
            dto.Id,
            dto.Name ?? string.Empty,
            dto.OriginalName,
            dto.Overview,
            dto.PosterPath,
            dto.BackdropPath,
            dto.FirstAirDate,
            dto.VoteAverage,
            dto.VoteCount,
            (dto.Genres ?? Array.Empty<TmdbGenreDto>()).Select(g => g.Name).ToArray(),
            dto.Status ?? string.Empty,
            dto.NumberOfSeasons ?? 0,
            dto.NumberOfEpisodes ?? 0,
            dto.OriginCountry ?? Array.Empty<string>(),
            string.IsNullOrWhiteSpace(imdb) ? null : imdb);
    }

    /// <inheritdoc/>
    public Task<TmdbImages?> GetMovieImagesAsync(int tmdbId, string? languageCode, CancellationToken cancellationToken)
        => GetImagesAsync($"/movie/{tmdbId.ToString(CultureInfo.InvariantCulture)}/images", languageCode, cancellationToken);

    /// <inheritdoc/>
    public Task<TmdbImages?> GetSeriesImagesAsync(int tmdbId, string? languageCode, CancellationToken cancellationToken)
        => GetImagesAsync($"/tv/{tmdbId.ToString(CultureInfo.InvariantCulture)}/images", languageCode, cancellationToken);

    private async Task<TmdbImages?> GetImagesAsync(string endpoint, string? languageCode, CancellationToken cancellationToken)
    {
        // include_image_language ensures we get language-tagged + null (universal) entries
        var extras = new List<KeyValuePair<string, string?>>();
        if (!string.IsNullOrWhiteSpace(languageCode))
        {
            var primary = languageCode.Split('-')[0];
            extras.Add(new("include_image_language", $"{primary},null,en"));
        }

        var path = BuildPath(endpoint, languageCode, extras);
        var dto = await GetJsonAsync<TmdbImagesDto>(path, cancellationToken, allow404: true).ConfigureAwait(false);
        if (dto is null)
        {
            return null;
        }

        return new TmdbImages(
            (dto.Posters ?? Array.Empty<TmdbImageDto>()).Select(MapImage).ToArray(),
            (dto.Backdrops ?? Array.Empty<TmdbImageDto>()).Select(MapImage).ToArray(),
            (dto.Logos ?? Array.Empty<TmdbImageDto>()).Select(MapImage).ToArray());
    }

    private static TmdbImage MapImage(TmdbImageDto d) => new(
        d.FilePath ?? string.Empty,
        d.Width ?? 0,
        d.Height ?? 0,
        d.VoteAverage ?? 0,
        d.VoteCount ?? 0,
        d.Iso6391);

    /// <inheritdoc/>
    public async Task<TmdbConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        if (_cachedConfiguration is not null && DateTimeOffset.UtcNow < _cachedConfigurationExpiry)
        {
            return _cachedConfiguration;
        }

        await _configLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cachedConfiguration is not null && DateTimeOffset.UtcNow < _cachedConfigurationExpiry)
            {
                return _cachedConfiguration;
            }

            var path = BuildPath("/configuration", null, null);
            var dto = await GetJsonAsync<TmdbConfigurationDto>(path, cancellationToken).ConfigureAwait(false)
                ?? throw new TmdbApiException("TMDB /configuration returned empty body");
            var images = dto.Images ?? throw new TmdbApiException("TMDB /configuration missing 'images' object");
            var cfg = new TmdbConfiguration(
                images.SecureBaseUrl ?? throw new TmdbApiException("TMDB /configuration missing secure_base_url"),
                images.PosterSizes ?? Array.Empty<string>(),
                images.BackdropSizes ?? Array.Empty<string>(),
                images.LogoSizes ?? Array.Empty<string>());
            _cachedConfiguration = cfg;
            _cachedConfigurationExpiry = DateTimeOffset.UtcNow.Add(ConfigurationCacheTtl);
            return cfg;
        }
        finally
        {
            _configLock.Release();
        }
    }

    /// <inheritdoc/>
    public Task<string?> GetImdbIdForMovieAsync(int tmdbId, CancellationToken cancellationToken)
        => GetImdbIdAsync($"/movie/{tmdbId.ToString(CultureInfo.InvariantCulture)}/external_ids", cancellationToken);

    /// <inheritdoc/>
    public Task<string?> GetImdbIdForSeriesAsync(int tmdbId, CancellationToken cancellationToken)
        => GetImdbIdAsync($"/tv/{tmdbId.ToString(CultureInfo.InvariantCulture)}/external_ids", cancellationToken);

    private async Task<string?> GetImdbIdAsync(string endpoint, CancellationToken cancellationToken)
    {
        var path = BuildPath(endpoint, null, null);
        var dto = await GetJsonAsync<TmdbExternalIdsResponseDto>(path, cancellationToken, allow404: true).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(dto?.ImdbId) ? null : dto!.ImdbId;
    }

    // ---------- HTTP plumbing ----------

    private string BuildPath(string endpoint, string? languageCode, IEnumerable<KeyValuePair<string, string?>>? extra)
    {
        var key = _keyProvider.GetApiKey();
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                "TMDB API key not configured — set it in Dashboard > Plugins > Phantom Library.");
        }

        var sb = new System.Text.StringBuilder();
        sb.Append(BaseUrl).Append(endpoint);
        sb.Append("?api_key=").Append(Uri.EscapeDataString(key));
        if (!string.IsNullOrWhiteSpace(languageCode))
        {
            sb.Append("&language=").Append(Uri.EscapeDataString(languageCode));
        }

        if (extra is not null)
        {
            foreach (var kv in extra)
            {
                if (kv.Value is null)
                {
                    continue;
                }

                sb.Append('&').Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value));
            }
        }

        return sb.ToString();
    }

    private async Task<T?> GetJsonAsync<T>(string url, CancellationToken cancellationToken, bool allow404 = false)
        where T : class
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (resp.StatusCode == HttpStatusCode.TooManyRequests && attempt == 0)
            {
                var delay = TimeSpan.FromSeconds(1);
                if (resp.Headers.RetryAfter?.Delta is { } d)
                {
                    delay = d;
                }
                else if (resp.Headers.TryGetValues("Retry-After", out var vals))
                {
                    var first = vals.FirstOrDefault();
                    if (int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var secs))
                    {
                        delay = TimeSpan.FromSeconds(secs);
                    }
                }

                if (delay > TimeSpan.FromSeconds(MaxRetryAfterSeconds))
                {
                    delay = TimeSpan.FromSeconds(MaxRetryAfterSeconds);
                }

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (allow404 && resp.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!resp.IsSuccessStatusCode)
            {
                string? body = null;
                try
                {
                    body = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException)
                {
                    // body read failure — surface the status alone
                }

                throw new TmdbApiException((int)resp.StatusCode, RedactKey(url), body);
            }

            return await resp.Content.ReadFromJsonAsync<T>(JsonOpts, cancellationToken).ConfigureAwait(false);
        }

        // Loop only exits via return; this is unreachable but keeps the compiler happy.
        throw new TmdbApiException("TMDB rate-limit retry exhausted unexpectedly");
    }

    private static string RedactKey(string url)
    {
        var i = url.IndexOf("api_key=", StringComparison.Ordinal);
        if (i < 0)
        {
            return url;
        }

        var amp = url.IndexOf('&', i);
        return amp < 0
            ? url[..i] + "api_key=***"
            : url[..i] + "api_key=***" + url[amp..];
    }
}
