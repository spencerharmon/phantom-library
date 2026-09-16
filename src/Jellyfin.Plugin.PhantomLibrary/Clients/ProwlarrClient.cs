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
        var queries = BuildSearchQueries(query, isEpisode);
        if (queries.Count == 0)
        {
            return Array.Empty<IndexerCandidate>();
        }

        var results = new List<IndexerCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IndexerTransientException? transient = null;
        var successfulResponses = 0;

        // Run the (at most two: imdb + title) query variants CONCURRENTLY rather
        // than sequentially. Each variant is a full fan-out across every enabled
        // Prowlarr indexer; serializing them made a movie search cost the SUM of
        // two fan-outs, which blew the MagnetSelector per-indexer budget
        // (IndexerProbeTimeoutSeconds) whenever a public indexer was slow. Firing
        // them together makes the wall cost the MAX of the two instead.
        var settled = await Task.WhenAll(
            queries.Select(q => RunQuerySafeAsync(baseUrl, apiKey, q, cat, ct)))
            .ConfigureAwait(false);

        foreach (var outcome in settled)
        {
            if (outcome.Auth is not null)
            {
                throw outcome.Auth;
            }

            if (outcome.Transient is not null)
            {
                transient ??= outcome.Transient;
                continue;
            }

            successfulResponses++;
            foreach (var hit in outcome.Hits)
            {
                if (string.IsNullOrWhiteSpace(hit.InfoHash) || seen.Add(hit.InfoHash))
                {
                    results.Add(hit);
                }
            }
        }

        if (successfulResponses == 0 && transient is not null)
        {
            throw transient;
        }

        return results;
    }

    private readonly record struct QueryOutcome(
        IReadOnlyList<IndexerCandidate> Hits,
        IndexerTransientException? Transient,
        IndexerAuthException? Auth);

    // Runs one query variant, capturing (never propagating) the transient/auth
    // outcome so sibling variants running concurrently are never cancelled by
    // one variant's failure. Auth is surfaced back to the caller to rethrow.
    private async Task<QueryOutcome> RunQuerySafeAsync(
        string baseUrl,
        string apiKey,
        string queryStr,
        string cat,
        CancellationToken ct)
    {
        try
        {
            var hits = await SearchSingleAsync(baseUrl, apiKey, queryStr, cat, ct).ConfigureAwait(false);
            return new QueryOutcome(hits, null, null);
        }
        catch (IndexerAuthException ex)
        {
            return new QueryOutcome(Array.Empty<IndexerCandidate>(), null, ex);
        }
        catch (IndexerTransientException ex)
        {
            return new QueryOutcome(Array.Empty<IndexerCandidate>(), ex, null);
        }
    }

    private async Task<IReadOnlyList<IndexerCandidate>> SearchSingleAsync(
        string baseUrl,
        string apiKey,
        string queryStr,
        string cat,
        CancellationToken ct)
    {
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

    private static List<string> BuildSearchQueries(IndexerQuery query, bool isEpisode)
    {
        var queries = new List<string>();
        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (!queries.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                queries.Add(value);
            }
        }

        if (isEpisode)
        {
            Add(BuildTextQuery(query));
            return queries;
        }

        Add(query.Imdb);
        Add(BuildTextQuery(query));
        return queries;
    }

    private async Task<IndexerCandidate?> MapItemAsync(ProwlarrItemDto it, CancellationToken ct)
    {
        // Prowlarr returns the torrent's info-hash (and frequently a ready
        // magnet: URI in `guid`) on EVERY torrent result. Use those FIRST: they
        // need no network round-trip and, crucially, they avoid `magnetUrl` /
        // `downloadUrl`, which for most indexers is Prowlarr's PROXY download
        // indirection URL (http://<prowlarr>/<indexerId>/download?apikey=...),
        // NOT a magnet: URI. Feeding that proxy URL to ExtractInfoHash yields
        // nothing and silently drops the candidate — the bug that made Prowlarr
        // return "built 0 candidates" for search results it had actually found.
        string? magnet = null;

        // 1. A `guid` that is already a magnet: URI is the richest source
        //    (carries dn + the indexer's own tracker list).
        if (!string.IsNullOrWhiteSpace(it.Guid)
            && it.Guid!.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            magnet = it.Guid;
        }

        // 2. Synthesize a magnet straight from the info-hash Prowlarr already
        //    handed us (default tracker list attached by BuildMagnet).
        if (magnet is null && !string.IsNullOrWhiteSpace(it.InfoHash))
        {
            magnet = MagnetUtils.BuildMagnet(it.InfoHash!.Trim(), it.Title);
        }

        // 3. Legacy fallbacks for indexers that expose an explicit magnet: URI
        //    directly in magnetUrl/downloadUrl.
        if (magnet is null)
        {
            foreach (var url in new[] { it.MagnetUrl, it.DownloadUrl })
            {
                if (!string.IsNullOrWhiteSpace(url)
                    && url!.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
                {
                    magnet = url;
                    break;
                }
            }
        }

        // 4. Last resort: a Prowlarr /download proxy that 3xx-redirects to a
        //    magnet: (read from the Location header without following the
        //    non-HTTP magnet hop). Only reached when 1-3 all fail, so it never
        //    adds a network round-trip on the common path.
        if (magnet is null)
        {
            foreach (var url in new[] { it.MagnetUrl, it.DownloadUrl })
            {
                if (string.IsNullOrWhiteSpace(url))
                {
                    continue;
                }

                magnet = await TryResolveMagnetRedirectAsync(url!, ct).ConfigureAwait(false);
                if (magnet is not null)
                {
                    break;
                }
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
        [JsonPropertyName("infoHash")] public string? InfoHash { get; set; }
        [JsonPropertyName("guid")] public string? Guid { get; set; }
        [JsonPropertyName("indexer")] public string? Indexer { get; set; }
    }
}
