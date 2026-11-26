using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Clients;

/// <summary>
/// Prowlarr indexer client — per-indexer fan-out (p13-parallel-indexer-search,
/// ROI Priority 13).
///
/// The enabled upstream indexers are DISCOVERED at runtime from
/// <c>GET /api/v1/indexer</c> (cached in <see cref="ProwlarrIndexerDirectory"/> for
/// <c>ProwlarrIndexerDiscoveryTtlMinutes</c>; never hard-coded ids/names) and each
/// one is queried through Prowlarr's per-indexer Torznab endpoint
/// <c>GET /{id}/api?t=search&amp;q=...&amp;cat=...</c>. <c>MagnetSelector</c> drives
/// the per-indexer calls concurrently under its search budget, so one slow
/// upstream indexer no longer sets the latency of the whole search (the old
/// single aggregate <c>/api/v1/search</c> call did).
/// </summary>
public sealed class ProwlarrClient : IFanOutIndexerClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly ILogger<ProwlarrClient> _logger;
    private readonly Func<(string baseUrl, string apiKey)> _configProvider;
    private readonly ProwlarrIndexerDirectory _directory;

    public ProwlarrClient(HttpClient http, ILogger<ProwlarrClient> logger, ProwlarrIndexerDirectory directory)
        : this(
            http,
            logger,
            () => (Plugin.Instance?.Configuration.ProwlarrBaseUrl ?? string.Empty,
                   Plugin.Instance?.Configuration.ProwlarrApiKey ?? string.Empty),
            directory)
    {
    }

    // Test-only ctor (not picked by DI — marked internal so ActivatorUtilities ignores it).
    internal ProwlarrClient(
        HttpClient http,
        ILogger<ProwlarrClient> logger,
        Func<(string, string)> configProvider,
        ProwlarrIndexerDirectory? directory = null)
    {
        _http = http;
        _logger = logger;
        _configProvider = configProvider;
        _directory = directory ?? new ProwlarrIndexerDirectory();
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

    /// <summary>
    /// Prowlarr movie searches include an IMDB-id term (<c>query=tt…</c>) so a
    /// movie can be served from the IMDB id alone; episode searches are text
    /// only (<c>"Title SxxEyy"</c>) and cannot.
    /// </summary>
    public bool SupportsImdbOnlyQuery(string type)
        => string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stand-alone full search (no budget): discovers the enabled indexers and
    /// queries every one concurrently, unioning the results. <c>MagnetSelector</c>
    /// does NOT use this path — it calls <see cref="DiscoverIndexersAsync"/> /
    /// <see cref="SearchIndexerAsync"/> itself so it can apply the budget. Throws
    /// transient only when EVERY indexer failed.
    /// </summary>
    public async Task<IReadOnlyList<IndexerCandidate>> SearchAsync(IndexerQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (!IsEnabled)
        {
            return Array.Empty<IndexerCandidate>();
        }

        // Retain the client-level degenerate-query backstop even for callers
        // outside MagnetSelector: abstain before issuing discovery traffic.
        if (BuildSearchQueries(query, string.Equals(query.Type, "episode", StringComparison.OrdinalIgnoreCase)).Count == 0)
        {
            throw new IndexerNotApplicableException("Prowlarr has no non-degenerate query term for this item");
        }

        var indexers = await DiscoverIndexersAsync(query, ct).ConfigureAwait(false);
        var tasks = indexers.Select(async ix =>
        {
            try
            {
                return ((IReadOnlyList<IndexerCandidate>?)await SearchIndexerAsync(ix, query, ct).ConfigureAwait(false), (Exception?)null);
            }
            catch (IndexerTransientException ex)
            {
                return (null, ex);
            }
        }).ToList();
        var outcomes = await Task.WhenAll(tasks).ConfigureAwait(false);

        var results = new List<IndexerCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Exception? transient = null;
        var ok = 0;
        foreach (var (hits, error) in outcomes)
        {
            if (hits is null)
            {
                transient ??= error;
                continue;
            }

            ok++;
            foreach (var hit in hits)
            {
                if (seen.Add(hit.InfoHash))
                {
                    results.Add(hit);
                }
            }
        }

        if (ok == 0 && transient is not null)
        {
            throw new IndexerTransientException("every Prowlarr indexer failed", transient);
        }

        return results;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<DiscoveredIndexer>> DiscoverIndexersAsync(IndexerQuery query, CancellationToken ct)
    {
        var (baseUrl, apiKey) = _configProvider();
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            return Task.FromResult<IReadOnlyList<DiscoveredIndexer>>(Array.Empty<DiscoveredIndexer>());
        }

        return _directory.GetAsync(baseUrl, apiKey, FetchIndexersAsync, _logger, ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IndexerCandidate>> SearchIndexerAsync(DiscoveredIndexer indexer, IndexerQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(indexer);
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
            // No IMDB term (episodes never send one) and a missing/degenerate
            // title: there is nothing honest to search for. Abstain instead of
            // sending an empty/punctuation-only term (p13-degenerate-query-guard)
            // or reporting a definitive empty result.
            throw new IndexerNotApplicableException("Prowlarr has no non-degenerate query term for this item");
        }

        var results = new List<IndexerCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IndexerTransientException? transient = null;
        OperationCanceledException? cancellation = null;
        var successfulResponses = 0;

        // The movie IMDB-id and title+year queries run concurrently too, so one
        // indexer's latency is max(queries), not their sum. Results are unioned in
        // query order (deterministic), independent of completion order.
        var attempts = queries
            .Select(queryStr => SearchTorznabAsync(baseUrl, apiKey, indexer, queryStr, cat, ct))
            .ToList();
        try
        {
            await Task.WhenAll(attempts).ConfigureAwait(false);
        }
        catch (Exception) when (attempts.All(a => a.IsCompleted))
        {
            // Classified per attempt below.
        }

        foreach (var attempt in attempts)
        {
            IReadOnlyList<IndexerCandidate> hits;
            try
            {
                hits = await attempt.ConfigureAwait(false);
                successfulResponses++;
            }
            catch (OperationCanceledException ex)
            {
                // Do not let a timed-out sibling erase successful attempts.
                // Preserve cancellation (including direct caller cancellation),
                // carrying hits to the collector that owns the timeout token.
                cancellation ??= ex;
                hits = ex is IndexerPartialCancellationException partial
                    ? partial.Candidates
                    : Array.Empty<IndexerCandidate>();
            }
            catch (IndexerTransientException ex)
            {
                transient ??= ex;
                continue;
            }

            foreach (var hit in hits)
            {
                if (seen.Add(hit.InfoHash))
                {
                    results.Add(hit);
                }
            }
        }

        if (cancellation is not null)
        {
            throw new IndexerPartialCancellationException(results.ToArray(), cancellation);
        }

        if (successfulResponses == 0 && transient is not null)
        {
            throw transient;
        }

        return results;
    }

    private async Task<IReadOnlyList<DiscoveredIndexer>> FetchIndexersAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        var url = baseUrl.TrimEnd('/') + "/api/v1/indexer";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Api-Key", apiKey);
        using var resp = await SendAsync(req, "indexer discovery", ct).ConfigureAwait(false);

        List<ProwlarrIndexerDto>? items;
        try
        {
            var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            items = await JsonSerializer.DeserializeAsync<List<ProwlarrIndexerDto>>(body, JsonOpts, ct).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            throw new IndexerTransientException("Prowlarr indexer list was malformed JSON", ex);
        }

        if (items is null)
        {
            throw new IndexerTransientException("Prowlarr indexer list was malformed JSON: expected array");
        }

        // Only enabled TORRENT indexers can yield a magnet; usenet indexers are skipped.
        return items
            .Where(i => i.Enable && i.Id > 0
                && !string.Equals(i.Protocol, "usenet", StringComparison.OrdinalIgnoreCase))
            .Select(i => new DiscoveredIndexer(
                i.Id,
                string.IsNullOrWhiteSpace(i.Name) ? i.Id.ToString(CultureInfo.InvariantCulture) : i.Name!,
                i.Protocol ?? "torrent"))
            .ToList();
    }

    private async Task<IReadOnlyList<IndexerCandidate>> SearchTorznabAsync(
        string baseUrl,
        string apiKey,
        DiscoveredIndexer indexer,
        string queryStr,
        string cat,
        CancellationToken ct)
    {
        var url = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/{1}/api?t=search&q={2}&cat={3}&extended=1&apikey={4}",
            baseUrl.TrimEnd('/'),
            indexer.Id,
            Uri.EscapeDataString(queryStr),
            cat,
            Uri.EscapeDataString(apiKey));

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add("X-Api-Key", apiKey);
        using var resp = await SendAsync(req, indexer.Name, ct).ConfigureAwait(false);

        XDocument doc;
        try
        {
            var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            doc = await XDocument.LoadAsync(body, LoadOptions.None, ct).ConfigureAwait(false);
        }
        catch (XmlException ex)
        {
            throw new IndexerTransientException($"Prowlarr indexer {indexer.Name} returned malformed Torznab XML", ex);
        }

        if (doc.Root is { } root && root.Name.LocalName == "error")
        {
            var code = (string?)root.Attribute("code") ?? "?";
            var description = (string?)root.Attribute("description") ?? string.Empty;
            if (code is "100" or "101" or "102")
            {
                throw new IndexerAuthException($"Prowlarr indexer {indexer.Name} rejected credentials (torznab {code})");
            }

            throw new IndexerTransientException($"Prowlarr indexer {indexer.Name} torznab error {code}: {description}");
        }

        var results = new List<IndexerCandidate>();
        OperationCanceledException? cancellation = null;
        foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "item"))
        {
            try
            {
                var candidate = await MapTorznabItemAsync(item, indexer, ct).ConfigureAwait(false);
                if (candidate is not null)
                {
                    results.Add(candidate);
                }
            }
            catch (OperationCanceledException ex)
            {
                // The XML is already here. Keep directly usable magnets on
                // either side of a stalled redirect; further network I/O still
                // observes ct. Do not turn cancellation into a successful empty.
                cancellation ??= ex;
            }
        }

        if (cancellation is not null)
        {
            throw new IndexerPartialCancellationException(results.ToArray(), cancellation);
        }

        return results;
    }

    /// <summary>Sends a request, mapping transport/HTTP failures onto the indexer exception contract.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, string what, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Prowlarr {What} request failed (network)", what);
            throw new IndexerTransientException($"Prowlarr {what} transport failure", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Prowlarr {What} request timed out", what);
            throw new IndexerTransientException($"Prowlarr {what} request timed out", ex);
        }

        var status = (int)resp.StatusCode;
        if (status == 401 || status == 403)
        {
            resp.Dispose();
            throw new IndexerAuthException($"Prowlarr {what} authentication failed ({status})");
        }

        if (!resp.IsSuccessStatusCode)
        {
            resp.Dispose();
            _logger.LogWarning("Prowlarr {What} returned {Status}; treating as transient", what, status);
            throw new IndexerTransientException($"Prowlarr {what} returned HTTP {status}");
        }

        return resp;
    }

    private async Task<IndexerCandidate?> MapTorznabItemAsync(XElement item, DiscoveredIndexer indexer, CancellationToken ct)
    {
        string? Child(string localName) => item.Elements().FirstOrDefault(e => e.Name.LocalName == localName)?.Value;

        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in item.Elements().Where(e => e.Name.LocalName == "attr"))
        {
            var name = (string?)a.Attribute("name");
            var value = (string?)a.Attribute("value");
            if (!string.IsNullOrWhiteSpace(name) && value is not null)
            {
                attrs.TryAdd(name, value);
            }
        }

        var enclosureUrl = (string?)item.Elements().FirstOrDefault(e => e.Name.LocalName == "enclosure")?.Attribute("url");
        var link = Child("link");

        // Prefer an explicit magnet (torznab:attr magneturl), then any magnet
        // link/enclosure, then an infohash attr, and only as a last resort an
        // http(s) download URL that redirects to a magnet (LimeTorrents-style).
        string? magnet = null;
        foreach (var c in new[] { attrs.GetValueOrDefault("magneturl"), link, enclosureUrl })
        {
            if (!string.IsNullOrWhiteSpace(c) && c.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            {
                magnet = c;
                break;
            }
        }

        var title = Child("title") ?? string.Empty;
        if (magnet is null && attrs.TryGetValue("infohash", out var ih) && !string.IsNullOrWhiteSpace(ih))
        {
            magnet = MagnetUtils.BuildMagnet(ih.Trim(), title);
        }

        if (magnet is null)
        {
            var download = !string.IsNullOrWhiteSpace(link) ? link : enclosureUrl;
            if (!string.IsNullOrWhiteSpace(download))
            {
                magnet = await TryResolveMagnetRedirectAsync(download!, ct).ConfigureAwait(false);
            }
        }

        var hash = MagnetUtils.ExtractInfoHash(magnet);
        if (magnet is null || string.IsNullOrWhiteSpace(hash))
        {
            _logger.LogDebug("Prowlarr indexer {Indexer} item {Title} skipped: no magnet/info-hash", indexer.Name, title);
            return null;
        }

        static long ParseLong(string? s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

        var size = ParseLong(Child("size"));
        if (size <= 0)
        {
            size = ParseLong(attrs.GetValueOrDefault("size"));
        }

        if (size <= 0 && enclosureUrl is not null)
        {
            size = ParseLong((string?)item.Elements().First(e => e.Name.LocalName == "enclosure").Attribute("length"));
        }

        var seeders = (int)Math.Clamp(ParseLong(attrs.GetValueOrDefault("seeders")), 0, int.MaxValue);
        var peers = ParseLong(attrs.GetValueOrDefault("peers"));
        var leechers = attrs.TryGetValue("leechers", out var l)
            ? (int)Math.Clamp(ParseLong(l), 0, int.MaxValue)
            : (int)Math.Clamp(peers - seeders, 0, int.MaxValue);

        return new IndexerCandidate
        {
            Title = title,
            Magnet = magnet,
            InfoHash = hash!,
            Size = size,
            Seeders = seeders,
            Leechers = leechers,
            Source = indexer.Name,
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

        ct.ThrowIfCancellationRequested();
        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        HttpResponseMessage resp;
        try
        {
            resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Prowlarr download URL could not be resolved to magnet");
            return null;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Prowlarr download URL timed out resolving magnet");
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

    private static string BuildTextQuery(IndexerQuery q)
    {
        // Degenerate-query guard (p13): never build "<punctuation> <year>"
        // (the production "Term: [. 2026]") — a title with no letter/digit
        // yields NO text term at all.
        var title = Sources.QueryTitleGuard.Normalize(q.Title);
        if (title is null)
        {
            return string.Empty;
        }

        var s = title;
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

    private sealed class ProwlarrIndexerDto
    {
        [JsonPropertyName("id")] public int Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("enable")] public bool Enable { get; set; }
        [JsonPropertyName("protocol")] public string? Protocol { get; set; }
    }
}

/// <summary>
/// p13-parallel-indexer-search: process-wide (DI singleton) cache of the enabled
/// Prowlarr indexer list, keyed by base URL + API key and refreshed after
/// <c>ProwlarrIndexerDiscoveryTtlMinutes</c>. <see cref="ProwlarrClient"/> is a
/// transient typed HttpClient, so the cache must live here to survive across
/// searches. Refreshes are single-flighted; a failed refresh falls back to the
/// last good list (stale-while-error) so one Prowlarr API hiccup does not blank
/// every search.
/// </summary>
public sealed class ProwlarrIndexerDirectory : IDisposable
{
    private readonly Func<TimeSpan> _ttl;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Entry? _entry;

    public ProwlarrIndexerDirectory()
        : this(
            () => TimeSpan.FromMinutes(Plugin.Instance?.Configuration.ProwlarrIndexerDiscoveryTtlMinutes ?? 15),
            () => DateTimeOffset.UtcNow)
    {
    }

    internal ProwlarrIndexerDirectory(Func<TimeSpan> ttl, Func<DateTimeOffset> clock)
    {
        _ttl = ttl ?? throw new ArgumentNullException(nameof(ttl));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    /// <summary>Number of times the list was (re)fetched from Prowlarr — observability/tests.</summary>
    public int FetchCount { get; private set; }

    internal async Task<IReadOnlyList<DiscoveredIndexer>> GetAsync(
        string baseUrl,
        string apiKey,
        Func<string, string, CancellationToken, Task<IReadOnlyList<DiscoveredIndexer>>> fetch,
        ILogger logger,
        CancellationToken ct)
    {
        var key = baseUrl.TrimEnd('/') + "\n" + apiKey;
        var current = _entry;
        if (current is not null && current.Key == key && _clock() < current.FetchedAt + _ttl())
        {
            return current.Indexers;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            current = _entry;
            if (current is not null && current.Key == key && _clock() < current.FetchedAt + _ttl())
            {
                return current.Indexers;
            }

            try
            {
                FetchCount++;
                var fresh = await fetch(baseUrl, apiKey, ct).ConfigureAwait(false);
                _entry = new Entry(key, fresh, _clock());
                return fresh;
            }
            catch (IndexerTransientException ex) when (current is not null && current.Key == key)
            {
                logger.LogWarning(ex, "Prowlarr indexer discovery failed; reusing the last discovered list ({Count} indexers)", current.Indexers.Count);
                return current.Indexers;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private sealed record Entry(string Key, IReadOnlyList<DiscoveredIndexer> Indexers, DateTimeOffset FetchedAt);
}
