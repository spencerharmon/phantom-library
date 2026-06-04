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

    public ProwlarrClient(HttpClient http, ILogger<ProwlarrClient> logger, Func<(string, string)> configProvider)
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

        var cat = string.Equals(query.Type, "movie", StringComparison.OrdinalIgnoreCase) ? "2000" : "5000";
        var queryStr = !string.IsNullOrWhiteSpace(query.Imdb)
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
            return Array.Empty<IndexerCandidate>();
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Prowlarr request timed out");
            return Array.Empty<IndexerCandidate>();
        }

        try
        {
            var status = (int)resp.StatusCode;
            if (status == 401 || status == 403)
            {
                throw new IndexerAuthException($"Prowlarr authentication failed ({status})");
            }

            if (status >= 500)
            {
                _logger.LogWarning("Prowlarr returned {Status}; returning empty result", status);
                return Array.Empty<IndexerCandidate>();
            }

            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Prowlarr returned {Status}; returning empty result", status);
                return Array.Empty<IndexerCandidate>();
            }

            var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var items = await JsonSerializer.DeserializeAsync<List<ProwlarrItemDto>>(body, JsonOpts, ct).ConfigureAwait(false)
                ?? new List<ProwlarrItemDto>();
            var results = new List<IndexerCandidate>(items.Count);
            foreach (var it in items)
            {
                var candidate = MapItem(it);
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

    private IndexerCandidate? MapItem(ProwlarrItemDto it)
    {
        // Prefer magnetUrl. If absent, accept downloadUrl iff it is a magnet:
        // URI. http(s) .torrent links are skipped — we don't fetch .torrent
        // files in v0.1. (Follow-up: support .torrent → magnet conversion.)
        var magnet = it.MagnetUrl;
        if (string.IsNullOrWhiteSpace(magnet))
        {
            if (!string.IsNullOrWhiteSpace(it.DownloadUrl) && it.DownloadUrl!.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                magnet = it.DownloadUrl;
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
