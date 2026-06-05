using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Clients;

/// <summary>
/// HTTP implementation of <see cref="IGostreamClient"/>. Resolves the gostream
/// base URL per-call from the live plugin configuration so operator edits in
/// the dashboard take effect without a restart.
/// </summary>
public sealed class GostreamClient : IGostreamClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ILogger<GostreamClient> _logger;
    private readonly Func<string> _baseUrlProvider;
    private Lazy<Task<bool>>? _vaultModeProbe;
    private readonly object _vaultProbeLock = new();

    public GostreamClient(HttpClient http, ILogger<GostreamClient> logger)
        : this(http, logger, () => Plugin.Instance?.Configuration.GostreamBaseUrl ?? string.Empty)
    {
    }

    // Test-friendly ctor: lets tests inject a base URL provider without a live Plugin singleton.
    public GostreamClient(HttpClient http, ILogger<GostreamClient> logger, Func<string> baseUrlProvider)
    {
        _http = http;
        _logger = logger;
        _baseUrlProvider = baseUrlProvider;
    }

    public async Task<GostreamAddResult> AddAsync(GostreamAddRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var url = BuildUrl("/api/library/add");

        HttpResponseMessage? response = null;
        try
        {
            response = await PostWithOneRetryAsync(url, request, ct).ConfigureAwait(false);

            var status = (int)response.StatusCode;
            if (status == 200 || status == 409)
            {
                var body = await response.Content.ReadFromJsonAsync<AddResponseDto>(JsonOpts, ct).ConfigureAwait(false)
                    ?? throw new GostreamServerException(status, "gostream /api/library/add returned empty body");
                ValidateAddResponse(body);
                return new GostreamAddResult
                {
                    StubPath = body.StubPath!,
                    FusePath = body.FusePath!,
                    Hash = body.Hash!,
                    Size = body.Size,
                    AlreadyExisted = status == 409,
                };
            }

            var errText = await SafeReadErrorAsync(response, ct).ConfigureAwait(false);
            switch (status)
            {
                case 504:
                    throw new GostreamTimeoutException($"gostream timeout: {errText}");
                case 422:
                    throw new GostreamNoValidFilesException($"gostream no_valid_files: {errText}");
                case 400:
                case 405:
                case 415:
                    throw new GostreamBadRequestException($"gostream {status}: {errText}");
                default:
                    throw new GostreamServerException(status, $"gostream {status}: {errText}");
            }
        }
        finally
        {
            response?.Dispose();
        }
    }

    public async Task RemoveAsync(string stubPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stubPath))
        {
            throw new ArgumentException("stubPath required", nameof(stubPath));
        }

        var url = BuildUrl("/api/library/remove");
        using var content = JsonContent.Create(new { stub_path = stubPath });
        using var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status == 204 || status == 200)
        {
            return;
        }

        if (status == 404)
        {
            _logger.LogDebug("gostream /api/library/remove 404 for {Stub} (already gone)", stubPath);
            return;
        }

        var err = await SafeReadErrorAsync(response, ct).ConfigureAwait(false);
        if (status >= 500)
        {
            throw new GostreamServerException(status, $"gostream remove {status}: {err}");
        }

        throw new GostreamBadRequestException($"gostream remove {status}: {err}");
    }

    public async Task<bool> ProbeAsync(CancellationToken ct)
    {
        // Try OPTIONS on the add endpoint. The gostream handler responds 405 to
        // anything but POST, which we treat as "endpoint present". Any HTTP
        // response counts as reachable; only transport errors mean "absent".
        var url = BuildUrl("/api/library/add");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Options, url);
            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            return true;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "gostream probe failed");
            return false;
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "gostream probe timed out");
            return false;
        }
    }

    public async Task PrestageAsync(string stubPath, int priority, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stubPath))
        {
            throw new ArgumentException("stubPath required", nameof(stubPath));
        }

        var url = BuildUrl("/api/library/prestage");
        using var content = JsonContent.Create(new { stub_path = stubPath, priority });
        using var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status >= 200 && status < 300)
        {
            return;
        }

        var err = await SafeReadErrorAsync(response, ct).ConfigureAwait(false);
        if (status >= 500)
        {
            throw new GostreamServerException(status, $"gostream prestage {status}: {err}");
        }

        throw new GostreamBadRequestException($"gostream prestage {status}: {err}");
    }

    public async Task UnprestageAsync(string stubPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stubPath))
        {
            throw new ArgumentException("stubPath required", nameof(stubPath));
        }

        var url = BuildUrl("/api/library/unprestage");
        using var content = JsonContent.Create(new { stub_path = stubPath });
        using var response = await _http.PostAsync(url, content, ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status >= 200 && status < 300)
        {
            return;
        }

        if (status == 404)
        {
            _logger.LogDebug("gostream /api/library/unprestage 404 for {Stub} (already gone)", stubPath);
            return;
        }

        var err = await SafeReadErrorAsync(response, ct).ConfigureAwait(false);
        if (status >= 500)
        {
            throw new GostreamServerException(status, $"gostream unprestage {status}: {err}");
        }

        throw new GostreamBadRequestException($"gostream unprestage {status}: {err}");
    }

    public Task<bool> IsVaultModePresentAsync(CancellationToken ct)
    {
        // Probe /api/library/prestage/status?stub_path=__probe__ once per
        // process and cache the answer. Vault Mode-present servers respond
        // 404 with a JSON body ("no such stub"); absent servers respond
        // 404 with non-JSON, 405, or fail at the transport layer.
        Lazy<Task<bool>> lazy;
        lock (_vaultProbeLock)
        {
            lazy = _vaultModeProbe ??= new Lazy<Task<bool>>(() => ProbeVaultModeAsync(CancellationToken.None));
        }

        return lazy.Value.WaitAsync(ct);
    }

    private async Task<bool> ProbeVaultModeAsync(CancellationToken ct)
    {
        try
        {
            var url = BuildUrl("/api/library/prestage/status?stub_path=__probe__");
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            var status = (int)resp.StatusCode;
            if (status == 404)
            {
                // 404 with JSON body => Vault Mode handler present, no record.
                // 404 with non-JSON => handler absent / generic Not Found page.
                var contentType = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
                if (contentType.Contains("json", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                try
                {
                    var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(body))
                    {
                        using var doc = JsonDocument.Parse(body);
                        return true;
                    }
                }
                catch (JsonException)
                {
                    return false;
                }

                return false;
            }

            if (status == 405)
            {
                return false;
            }

            return status >= 200 && status < 300;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "vault-mode probe transport failure");
            return false;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogDebug(ex, "vault-mode probe timed out");
            return false;
        }
        catch (InvalidOperationException ex)
        {
            // base URL not configured
            _logger.LogDebug(ex, "vault-mode probe config missing");
            return false;
        }
    }

    // ---- internals ----

    private async Task<HttpResponseMessage> PostWithOneRetryAsync(string url, GostreamAddRequest request, CancellationToken ct)
    {
        HttpResponseMessage response = await PostJsonAsync(url, request, ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status >= 500 && status != 504)
        {
            response.Dispose();
            _logger.LogWarning("gostream returned {Status}; retrying once after 1s", status);
            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            response = await PostJsonAsync(url, request, ct).ConfigureAwait(false);
        }

        return response;
    }

    private Task<HttpResponseMessage> PostJsonAsync(string url, GostreamAddRequest request, CancellationToken ct)
    {
        var content = JsonContent.Create(request, options: JsonOpts);
        return _http.PostAsync(url, content, ct);
    }

    private static async Task<string> SafeReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return string.Empty;
            }

            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var err))
                {
                    return err.GetString() ?? body;
                }
            }
            catch (JsonException)
            {
            }

            return body;
        }
        catch (HttpRequestException)
        {
            return string.Empty;
        }
    }

    private static void ValidateAddResponse(AddResponseDto body)
    {
        if (string.IsNullOrWhiteSpace(body.StubPath)
            || string.IsNullOrWhiteSpace(body.FusePath)
            || string.IsNullOrWhiteSpace(body.Hash)
            || body.Size <= 0)
        {
            throw new GostreamServerException(
                "gostream /api/library/add returned incomplete response (missing required field)");
        }
    }

    private string BuildUrl(string path)
    {
        var baseUrl = _baseUrlProvider();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidOperationException(
                "Gostream base URL is not configured (Dashboard > Plugins > Phantom Library).");
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}{1}",
            baseUrl.TrimEnd('/'),
            path);
    }

    private sealed class AddResponseDto
    {
        [JsonPropertyName("stub_path")] public string? StubPath { get; init; }
        [JsonPropertyName("fuse_path")] public string? FusePath { get; init; }
        [JsonPropertyName("hash")] public string? Hash { get; init; }
        [JsonPropertyName("size")] public long Size { get; init; }
    }
}
