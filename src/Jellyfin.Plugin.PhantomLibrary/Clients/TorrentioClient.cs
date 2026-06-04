using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Clients;

/// <summary>
/// Torrentio public stream-aggregator client. Requires an IMDB id; callers
/// must resolve TMDB→IMDB upstream when only TMDB is available.
/// </summary>
public sealed class TorrentioClient : IIndexerClient
{
    // Captures "👤 NNN" — the seeder count Torrentio embeds in the stream title line.
    private static readonly Regex SeederRegex = new(@"👤\s*(\d+)", RegexOptions.Compiled);

    // Captures "💾 NN.NN GB" or "💾 NNN MB".
    private static readonly Regex SizeRegex = new(@"💾\s*([\d.]+)\s*(GB|MB|KB|TB)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly ILogger<TorrentioClient> _logger;
    private readonly Func<string> _baseUrlProvider;

    public TorrentioClient(HttpClient http, ILogger<TorrentioClient> logger)
        : this(http, logger, () => Plugin.Instance?.Configuration.TorrentioBaseUrl ?? string.Empty)
    {
    }

    public TorrentioClient(HttpClient http, ILogger<TorrentioClient> logger, Func<string> baseUrlProvider)
    {
        _http = http;
        _logger = logger;
        _baseUrlProvider = baseUrlProvider;
    }

    public string Name => "Torrentio";

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_baseUrlProvider());

    public async Task<IReadOnlyList<IndexerCandidate>> SearchAsync(IndexerQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        var baseUrl = _baseUrlProvider();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return Array.Empty<IndexerCandidate>();
        }

        if (string.IsNullOrWhiteSpace(query.Imdb))
        {
            _logger.LogWarning("Torrentio requires an IMDB id; query for type={Type} title={Title} skipped", query.Type, query.Title);
            return Array.Empty<IndexerCandidate>();
        }

        string url;
        if (string.Equals(query.Type, "movie", StringComparison.OrdinalIgnoreCase))
        {
            url = string.Format(CultureInfo.InvariantCulture, "{0}/stream/movie/{1}.json", baseUrl.TrimEnd('/'), query.Imdb);
        }
        else if (string.Equals(query.Type, "episode", StringComparison.OrdinalIgnoreCase))
        {
            if (query.Season is null || query.Episode is null)
            {
                _logger.LogWarning("Torrentio episode query missing season/episode");
                return Array.Empty<IndexerCandidate>();
            }

            url = string.Format(CultureInfo.InvariantCulture, "{0}/stream/series/{1}:{2}:{3}.json",
                baseUrl.TrimEnd('/'), query.Imdb, query.Season.Value, query.Episode.Value);
        }
        else
        {
            return Array.Empty<IndexerCandidate>();
        }

        HttpResponseMessage resp;
        try
        {
            resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Torrentio request failed");
            return Array.Empty<IndexerCandidate>();
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Torrentio request timed out");
            return Array.Empty<IndexerCandidate>();
        }

        try
        {
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Torrentio returned {Status}; returning empty result", (int)resp.StatusCode);
                return Array.Empty<IndexerCandidate>();
            }

            var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var dto = await JsonSerializer.DeserializeAsync<TorrentioResponseDto>(stream, JsonOpts, ct).ConfigureAwait(false);
            if (dto?.Streams is null)
            {
                return Array.Empty<IndexerCandidate>();
            }

            var results = new List<IndexerCandidate>(dto.Streams.Count);
            foreach (var s in dto.Streams)
            {
                if (string.IsNullOrWhiteSpace(s.InfoHash))
                {
                    continue;
                }

                var title = s.Title ?? string.Empty;
                var seeders = ParseSeeders(title);
                var size = ParseSize(title);
                var displayName = ExtractFirstLine(title) ?? s.Name ?? s.InfoHash!;
                var magnet = MagnetUtils.BuildMagnet(s.InfoHash!, displayName);
                results.Add(new IndexerCandidate
                {
                    Title = displayName,
                    Magnet = magnet,
                    InfoHash = s.InfoHash!,
                    Size = size,
                    Seeders = seeders,
                    Leechers = 0,
                    Source = s.Name,
                    IndexerName = Name,
                });
            }

            return results;
        }
        finally
        {
            resp.Dispose();
        }
    }

    public static int ParseSeeders(string title)
    {
        var m = SeederRegex.Match(title);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    public static long ParseSize(string title)
    {
        var m = SizeRegex.Match(title);
        if (!m.Success)
        {
            return 0;
        }

        if (!double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
        {
            return 0;
        }

        var unit = m.Groups[2].Value.ToUpperInvariant();
        double mult = unit switch
        {
            "KB" => 1024d,
            "MB" => 1024d * 1024,
            "GB" => 1024d * 1024 * 1024,
            "TB" => 1024d * 1024 * 1024 * 1024,
            _ => 0,
        };
        return (long)(v * mult);
    }

    private static string? ExtractFirstLine(string s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }

        var i = s.IndexOf('\n');
        return i < 0 ? s : s[..i];
    }

    private sealed class TorrentioResponseDto
    {
        [JsonPropertyName("streams")] public List<TorrentioStreamDto>? Streams { get; set; }
    }

    private sealed class TorrentioStreamDto
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("title")] public string? Title { get; set; }
        [JsonPropertyName("infoHash")] public string? InfoHash { get; set; }
        [JsonPropertyName("fileIdx")] public int? FileIdx { get; set; }
    }
}
