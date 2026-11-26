using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.PhantomLibrary.Swarm;

/// <summary>
/// HTTP client for the swarm-health DHT measurement service (ROI Priority 0.5,
/// <c>tools/swarm-health/swarm_health_service.py</c>): <c>POST /measure</c>
/// returns the number of distinct DHT peers per infohash. Infohashes the
/// service could not measure in time are absent from the result (never 0).
/// </summary>
public class SwarmHealthClient
{
    private readonly HttpClient _http;

    public SwarmHealthClient(HttpClient http)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
    }

    /// <summary>Measures <paramref name="infoHashes"/>; returns infohash (case-insensitive keys) → distinct DHT peers.</summary>
    public virtual async Task<IReadOnlyDictionary<string, int>> MeasureAsync(string baseUrl, IReadOnlyCollection<string> infoHashes, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentNullException.ThrowIfNull(infoHashes);
        if (infoHashes.Count == 0)
        {
            return new Dictionary<string, int>();
        }

        var uri = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "measure");
        // Buffered (Content-Length) body: PostAsJsonAsync would stream it chunked.
        using var content = JsonContent.Create(new MeasureRequest { InfoHashes = infoHashes.ToArray() });
        await content.LoadIntoBufferAsync().ConfigureAwait(false);
        using var resp = await _http.PostAsync(uri, content, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadFromJsonAsync<MeasureResponse>(cancellationToken: ct).ConfigureAwait(false);
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in body?.Results ?? Array.Empty<MeasureResult>())
        {
            if (!string.IsNullOrEmpty(r.InfoHash) && r.DhtPeers >= 0)
            {
                map[r.InfoHash] = r.DhtPeers;
            }
        }

        return map;
    }

    private sealed class MeasureRequest
    {
        [JsonPropertyName("info_hashes")]
        public string[] InfoHashes { get; set; } = Array.Empty<string>();
    }

    private sealed class MeasureResponse
    {
        [JsonPropertyName("results")]
        public MeasureResult[]? Results { get; set; }
    }

    private sealed class MeasureResult
    {
        [JsonPropertyName("info_hash")]
        public string InfoHash { get; set; } = string.Empty;

        [JsonPropertyName("dht_peers")]
        public int DhtPeers { get; set; }
    }
}
