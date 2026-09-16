using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
            var needRedirect = new List<ProwlarrItemDto>();
            foreach (var it in items)
            {
                // First pass: NO network. infoHash / guid-magnet / literal magnet
                // cover the magnet-based indexers (~75% of real results) and can
                // never stall the search budget.
                var candidate = MapItemNoNetwork(it);
                if (candidate is not null)
                {
                    results.Add(candidate);
                }
                else if (!string.IsNullOrWhiteSpace(it.MagnetUrl) || !string.IsNullOrWhiteSpace(it.DownloadUrl))
                {
                    needRedirect.Add(it);
                }
            }

            // Second pass: BOUNDED, CONCURRENT, short-capped redirect resolution
            // for the results that carried no info-hash/magnet (e.g. LimeTorrents /
            // TorrentDownload, which expose only a Prowlarr /download proxy). This
            // was previously done inline per-result with the full 30s search
            // budget, so a single slow .torrent indexer stalled the whole search
            // (and, via the drain worker, the whole queue). Cap the count and run
            // them together, each with its own short timeout, so this phase costs
            // ~RedirectResolveTimeoutSeconds worst-case regardless of result count.
            if (needRedirect.Count > 0)
            {
                var toResolve = needRedirect
                    .OrderByDescending(r => r.Seeders ?? 0)
                    .Take(MaxRedirectResolves)
                    .Select(it => MapItemViaRedirectAsync(it, ct));
                foreach (var resolved in await Task.WhenAll(toResolve).ConfigureAwait(false))
                {
                    if (resolved is not null)
                    {
                        results.Add(resolved);
                    }
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

    // Cap on how many info-hash-less results per search get the network
    // redirect-resolution fallback, ordered by seeders. Bounds the worst-case
    // cost of a search when a .torrent-only indexer returns many hits.
    private const int MaxRedirectResolves = 25;

    // Per-attempt timeout for one redirect resolution. A Prowlarr /download
    // proxy that 301s to a magnet answers in ~20ms; a .torrent passthrough that
    // hangs is abandoned at this cap instead of consuming the search budget.
    private const int RedirectResolveTimeoutSeconds = 5;

    // No-network mapping: use the info-hash (and ready magnet: in `guid`) that
    // Prowlarr returns on every torrent result, or an explicit magnet: URI in
    // magnetUrl/downloadUrl. Returns null when only a proxy /download URL is
    // present (that result is handed to the bounded redirect pass instead).
    // NEVER touches magnetUrl/downloadUrl as if they were magnets: for most
    // indexers they are Prowlarr's PROXY /download URL, not a magnet: URI.
    private IndexerCandidate? MapItemNoNetwork(ProwlarrItemDto it)
    {
        string? magnet = null;

        if (!string.IsNullOrWhiteSpace(it.Guid)
            && it.Guid!.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            magnet = it.Guid;
        }

        if (magnet is null && !string.IsNullOrWhiteSpace(it.InfoHash))
        {
            magnet = MagnetUtils.BuildMagnet(it.InfoHash!.Trim(), it.Title);
        }

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

        return magnet is null ? null : BuildCandidate(it, magnet);
    }

    // Network fallback: resolve a Prowlarr /download proxy (or other http
    // download URL) whose 3xx Location is a magnet:, without following the
    // non-HTTP magnet hop. Bounded by RedirectResolveTimeoutSeconds per attempt.
    private async Task<IndexerCandidate?> MapItemViaRedirectAsync(ProwlarrItemDto it, CancellationToken ct)
    {
        foreach (var url in new[] { it.MagnetUrl, it.DownloadUrl })
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var magnet = await TryResolveMagnetRedirectAsync(url!, ct).ConfigureAwait(false);
            if (magnet is not null)
            {
                return BuildCandidate(it, magnet);
            }
        }

        return null;
    }

    private IndexerCandidate? BuildCandidate(ProwlarrItemDto it, string magnet)
    {
        var hash = MagnetUtils.ExtractInfoHash(magnet);
        if (string.IsNullOrWhiteSpace(hash))
        {
            _logger.LogDebug("Prowlarr candidate {Title} skipped: no info-hash in magnet", it.Title);
            return null;
        }

        return new IndexerCandidate
        {
            Title = it.Title ?? string.Empty,
            Magnet = magnet,
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

        // Hard per-attempt cap so one hung .torrent passthrough cannot consume
        // the caller's whole indexer budget. A real /download proxy 301s in ~20ms.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(RedirectResolveTimeoutSeconds));

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Prowlarr download URL {Url} could not be resolved to magnet", downloadUrl);
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug("Prowlarr download URL {Url} redirect resolve timed out after {Seconds}s", downloadUrl, RedirectResolveTimeoutSeconds);
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
