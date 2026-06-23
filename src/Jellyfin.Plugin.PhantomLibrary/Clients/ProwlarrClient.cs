using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Clients;

/// <summary>Prowlarr indexer client (<c>GET /api/v1/search</c>).</summary>
public sealed class ProwlarrClient : IIndexerClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly ILogger<ProwlarrClient> _logger;
    private readonly Func<(string baseUrl, string apiKey)> _configProvider;

    public ProwlarrClient(HttpClient http, ILogger<ProwlarrClient> logger)
        : this(http, logger, () => (Plugin.Instance?.Configuration.ProwlarrBaseUrl ?? string.Empty,
                                    Plugin.Instance?.Configuration.ProwlarrApiKey ?? string.Empty))
    {
    }

    // Test-only ctor (not picked by DI — marked internal so ActivatorUtilities ignores it).
    internal ProwlarrClient(HttpClient http, ILogger<ProwlarrClient> logger, Func<(string, string)> configProvider)
    {
        _http = http;
        _logger = logger;
        _configProvider = configProvider;
    }

    public string Name => "Prowlarr";

    public bool IsEnabled
    {
        get
        {
            var (b, k) = _configProvider();
            return !string.IsNullOrWhiteSpace(b) && !string.IsNullOrWhiteSpace(k);
        }
    }

    public async Task<IReadOnlyList<IndexerCandidate>> SearchAsync(IndexerQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var (baseUrl, apiKey) = _configProvider();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return Array.Empty<IndexerCandidate>();
        }

        var isEpisode = string.Equals(query.Type, "episode", StringComparison.OrdinalIgnoreCase);
        var cat = isEpisode ? "5000" : "2000";
        var seriesImdb = query.SeriesImdb ?? (isEpisode ? query.Imdb : null);

        var queryStr = isEpisode
            ? BuildTextQuery(query)
            : !string.IsNullOrWhiteSpace(query.Imdb)
                ? query.Imdb!
                : BuildTextQuery(query);

        if (string.IsNullOrWhiteSpace(queryStr))
        {
            return Array.Empty<IndexerCandidate>();
        }

        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/api/v1/search?query={1}&type=search&categories={2}",
            baseUrl.TrimEnd('/'),
            Uri.EscapeDataString(queryStr),
            cat);

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Api-Key", apiKey);

        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Prowlarr request failed (network)");
            throw new IndexerTransientException("Prowlarr transport failure", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Prowlarr request timed out");
            throw new IndexerTransientException("Prowlarr request timed out", ex);
        }

        try
        {
            var status = (int)resp.StatusCode;
            if (status == 401 || status == 403)
            {
                throw new IndexerAuthException($"Prowlarr authentication failed ({status})");
            }

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Prowlarr returned {Status}; treating indexer result as transient", status);
                throw new IndexerTransientException($"Prowlarr returned HTTP {status}");
            }

            List<ProwlarrItemDto>? items;
            try
            {
                var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                items = await JsonSerializer.DeserializeAsync<List<ProwlarrItemDto>>(body, JsonOpts, ct).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Prowlarr returned malformed JSON");
                throw new IndexerTransientException("Prowlarr returned malformed JSON", ex);
            }

            if (items is null)
            {
                throw new IndexerTransientException("Prowlarr returned malformed JSON: expected array");
            }

            var results = new List<IndexerCandidate>(items.Count);
            foreach (var it in items)
            {
                var candidate = await MapItemAsync(it, ct).ConfigureAwait(false);
                if (candidate is not null)
                {
                    results.Add(candidate);
                }
            }

            return results;
        }
        finally
        {
            resp.Dispose();
        }
    }

    private async Task<IndexerCandidate?> MapItemAsync(ProwlarrItemDto it, CancellationToken ct)
    {
        // Prefer magnetUrl. If absent, accept magnet downloadUrl. Some
        // Prowlarr indexers (notably LimeTorrents) expose a Prowlarr
        // /download URL that 301-redirects to the real magnet; resolve
        // that header without following the non-HTTP magnet redirect.
        var magnet = it.MagnetUrl;
        if (string.IsNullOrWhiteSpace(magnet))
        {
            if (!string.IsNullOrWhiteSpace(it.DownloadUrl) && it.DownloadUrl!.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                magnet = it.DownloadUrl;
            }
            else if (!string.IsNullOrWhiteSpace(it.DownloadUrl))
            {
                magnet = await TryResolveMagnetRedirectAsync(it.DownloadUrl!, ct).ConfigureAwait(false);
            }
        }

        if (string.IsNullOrWhiteSpace(magnet))
        {
            return null;
        }

        var hash = MagnetUtils.ExtractInfoHash(magnet);
        if (string.IsNullOrWhiteSpace(hash))
        {
            _logger.LogDebug("Prowlarr candidate {Title} skipped: no info-hash in magnet", it.Title);
            return null;
        }

        return new IndexerCandidate
        {
            Title = it.Title ?? string.Empty,
            Magnet = magnet!,
            InfoHash = hash!,
            Size = it.Size ?? 0,
            Seeders = it.Seeders ?? 0,
            Leechers = it.Leechers ?? 0,
            Source = it.Indexer,
            IndexerName = Name,
        };
    }

    private async Task<string?> TryResolveMagnetRedirectAsync(string downloadUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Prowlarr download URL {Url} could not be resolved to magnet", downloadUrl);
            return null;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Prowlarr download URL {Url} timed out resolving magnet", downloadUrl);
            return null;
        }

        using (resp)
        {
            var location = resp.Headers.Location?.ToString();
            if (!string.IsNullOrWhiteSpace(location)
                && location.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                return location;
            }
        }

        return null;
    }

    private static string BuildTextQuery(IndexerQuery q)
    {
        if (string.IsNullOrWhiteSpace(q.Title))
        {
            return string.Empty;
        }

        var s = q.Title!;
        if (string.Equals(q.Type, "episode", StringComparison.OrdinalIgnoreCase)
            && q.Season is int se && q.Episode is int ep)
        {
            s = string.Format(CultureInfo.InvariantCulture, "{0} S{1:00}E{2:00}", s, se, ep);
        }
        else if (q.Year is int y)
        {
            s = string.Format(CultureInfo.InvariantCulture, "{0} {1}", s, y);
        }

        return s;
    }

    private sealed class ProwlarrItemDto
    {
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("size")] public long? Size { get; set; }
        [JsonPropertyName("seeders")] public int? Seeders { get; set; }
        [JsonPropertyName("leechers")] public int? Leechers { get; set; }
        [JsonPropertyName("magnetUrl")] public string? MagnetUrl { get; set; }
        [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; set; }
        [JsonPropertyName("indexer")] public string? Indexer { get; set; }
    }
}
