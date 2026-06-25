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
    private readonly Func<string> _tokenProvider;
    private Lazy<Task<bool>>? _vaultModeProbe;
    private readonly object _vaultProbeLock = new();

    public GostreamClient(HttpClient http, ILogger<GostreamClient> logger)
        : this(
            http,
            logger,
            () => Plugin.Instance?.Configuration.GostreamBaseUrl ?? string.Empty,
            () => Plugin.Instance?.Configuration.GostreamApiToken ?? string.Empty)
    {
    }

    // Test-friendly ctor: internal so ActivatorUtilities ignores it during DI resolution.
    internal GostreamClient(HttpClient http, ILogger<GostreamClient> logger, Func<string> baseUrlProvider)
        : this(http, logger, baseUrlProvider, () => string.Empty)
    {
    }

    internal GostreamClient(HttpClient http, ILogger<GostreamClient> logger, Func<string> baseUrlProvider, Func<string> tokenProvider)
    {
        _http = http;
        _logger = logger;
        _baseUrlProvider = baseUrlProvider;
        _tokenProvider = tokenProvider;
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

    public async Task<GostreamValidateResult> ValidateAsync(GostreamValidateRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var url = BuildUrl("/api/library/validate");

        using var response = await PostJsonAsync(url, request, ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status == 200)
        {
            var body = await response.Content.ReadFromJsonAsync<ValidateResponseDto>(JsonOpts, ct).ConfigureAwait(false)
                ?? throw new GostreamServerException(status, "gostream /api/library/validate returned empty body");
            return ToValidateResult(body);
        }

        var errText = await SafeReadErrorAsync(response, ct).ConfigureAwait(false);
        switch (status)
        {
            case 400:
            case 401:
            case 403:
            case 404:
            case 405:
            case 415:
                throw new GostreamBadRequestException($"gostream validate {status}: {errText}");
            case 504:
                return new GostreamValidateResult
                {
                    Status = "transient",
                    Reason = "metadata_timeout",
                    ValidationSessionId = request.ValidationSessionId,
                };
            default:
                if (status >= 500)
                {
                    return new GostreamValidateResult
                    {
                        Status = "transient",
                        Reason = "gostream_server_error",
                        ValidationSessionId = request.ValidationSessionId,
                    };
                }

                throw new GostreamServerException(status, $"gostream validate {status}: {errText}");
        }
    }

    public async Task ReleaseValidationAsync(GostreamValidationReleaseRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Hash))
        {
            return;
        }

        var url = BuildUrl("/api/library/validate/release");
        using var response = await PostJsonAsync(url, request, ct).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        if (status >= 200 && status < 300)
        {
            return;
        }

        var err = await SafeReadErrorAsync(response, ct).ConfigureAwait(false);
        if (status >= 500)
        {
            throw new GostreamServerException(status, $"gostream validation release {status}: {err}");
        }

        throw new GostreamBadRequestException($"gostream validation release {status}: {err}");
    }

    public async Task RemoveAsync(string stubPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stubPath))
        {
            throw new ArgumentException("stubPath required", nameof(stubPath));
        }

        var url = BuildUrl("/api/library/remove");
        using var response = await PostJsonAsync(url, new { stub_path = stubPath }, ct).ConfigureAwait(false);
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

    private async Task<HttpResponseMessage> PostJsonAsync(string url, object request, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(request, options: JsonOpts),
        };
        AddLibraryAuthHeader(message);
        return await _http.SendAsync(message, ct).ConfigureAwait(false);
    }

    private void AddLibraryAuthHeader(HttpRequestMessage message)
    {
        var token = _tokenProvider();
        if (!string.IsNullOrWhiteSpace(token))
        {
            message.Headers.TryAddWithoutValidation("X-Gostream-Token", token);
        }
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

    private static GostreamValidateResult ToValidateResult(ValidateResponseDto body)
    {
        if (string.IsNullOrWhiteSpace(body.Status))
        {
            throw new GostreamServerException("gostream /api/library/validate returned incomplete response (missing status)");
        }

        return new GostreamValidateResult
        {
            Status = body.Status!,
            Reason = body.Reason,
            Hash = body.Hash,
            SelectedFile = body.SelectedFile is null
                ? null
                : new GostreamSelectedFile
                {
                    Id = body.SelectedFile.Id,
                    Path = body.SelectedFile.Path,
                    Size = body.SelectedFile.Size,
                },
            AudioTracks = body.AudioTracks ?? Array.Empty<GostreamAudioTrack>(),
            SelectedAudioIndex = body.SelectedAudioIndex,
            SelectedAudioLanguage = body.SelectedAudioLanguage,
            ValidationSessionId = body.ValidationSessionId,
            ValidationLeaseExpiresAt = body.ValidationLeaseExpiresAt,
        };
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

    private sealed class ValidateResponseDto
    {
        [JsonPropertyName("status")] public string? Status { get; init; }
        [JsonPropertyName("reason")] public string? Reason { get; init; }
        [JsonPropertyName("hash")] public string? Hash { get; init; }
        [JsonPropertyName("selected_file")] public SelectedFileDto? SelectedFile { get; init; }
        [JsonPropertyName("audio_tracks")] public GostreamAudioTrack[]? AudioTracks { get; init; }
        [JsonPropertyName("selected_audio_index")] public int? SelectedAudioIndex { get; init; }
        [JsonPropertyName("selected_audio_language")] public string? SelectedAudioLanguage { get; init; }
        [JsonPropertyName("validation_session_id")] public string? ValidationSessionId { get; init; }
        [JsonPropertyName("validation_lease_expires_at")] public DateTimeOffset? ValidationLeaseExpiresAt { get; init; }
    }

    private sealed class SelectedFileDto
    {
        [JsonPropertyName("id")] public int? Id { get; init; }
        [JsonPropertyName("path")] public string? Path { get; init; }
        [JsonPropertyName("size")] public long? Size { get; init; }
    }
}
