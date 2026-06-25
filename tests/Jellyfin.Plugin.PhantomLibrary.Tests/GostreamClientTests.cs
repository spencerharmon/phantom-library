using System.Net;
using System.Net.Http;
using System.Threading;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class GostreamClientTests
{
    private static GostreamClient MakeClient(QueuedHandler h, string token = "")
    {
        var http = new HttpClient(h) { BaseAddress = null };
        return new GostreamClient(http, NullLogger<GostreamClient>.Instance, () => "http://gs.test:9080", () => token);
    }

    private const string OkBody = "{\"stub_path\":\"/r/x.mkv\",\"fuse_path\":\"/f/x.mkv\",\"hash\":\"abc\",\"size\":123}";

    [Fact]
    public async Task Add_Returns_Parsed_Result_On_200()
    {
        var h = new QueuedHandler().Enqueue(HttpStatusCode.OK, OkBody);
        var c = MakeClient(h);
        var r = await c.AddAsync(new GostreamAddRequest { Type = "movie", Title = "X", Magnet = "magnet:?xt=urn:btih:abc" }, CancellationToken.None);
        Assert.Equal("/f/x.mkv", r.FusePath);
        Assert.Equal("abc", r.Hash);
        Assert.Equal(123, r.Size);
        Assert.False(r.AlreadyExisted);
    }

    [Fact]
    public async Task Add_Sets_AlreadyExisted_On_409()
    {
        var h = new QueuedHandler().Enqueue(HttpStatusCode.Conflict, OkBody);
        var c = MakeClient(h);
        var r = await c.AddAsync(new GostreamAddRequest { Type = "movie", Title = "X", Magnet = "magnet:?xt=urn:btih:abc" }, CancellationToken.None);
        Assert.True(r.AlreadyExisted);
        Assert.Equal("/f/x.mkv", r.FusePath);
    }

    [Fact]
    public async Task Add_Sends_Configured_Gostream_Token()
    {
        var h = new QueuedHandler().Enqueue(HttpStatusCode.OK, OkBody);
        var c = MakeClient(h, "secret-token");

        await c.AddAsync(new GostreamAddRequest { Type = "movie", Title = "X", Magnet = "magnet:?xt=urn:btih:abc" }, CancellationToken.None);

        Assert.Equal("secret-token", h.Requests[0].Headers.GetValues("X-Gostream-Token").Single());
    }

    [Fact]
    public async Task Remove_Sends_Configured_Gostream_Token()
    {
        var h = new QueuedHandler().Enqueue(HttpStatusCode.NoContent);
        var c = MakeClient(h, "secret-token");

        await c.RemoveAsync("/r/x.mkv", CancellationToken.None);

        Assert.Equal("secret-token", h.Requests[0].Headers.GetValues("X-Gostream-Token").Single());
    }

    [Fact]
    public async Task Validate_Returns_Parsed_Result_And_Sends_Token()
    {
        var body = """
            {
              "status":"valid",
              "hash":"abc",
              "selected_file":{"id":12,"path":"Avatar S02E01.mkv","size":456},
              "audio_tracks":[{"stream_index":1,"language":"pol"},{"stream_index":2,"language":"eng","title":"English"}],
              "selected_audio_index":2,
              "selected_audio_language":"eng",
              "validation_session_id":"session-1",
              "validation_lease_expires_at":"2026-06-24T01:23:45Z"
            }
            """;
        var h = new QueuedHandler().Enqueue(HttpStatusCode.OK, body);
        var c = MakeClient(h, "secret-token");

        var result = await c.ValidateAsync(new GostreamValidateRequest
        {
            Type = "episode",
            Title = "Avatar",
            Season = 2,
            Episode = 1,
            Magnet = "magnet:?xt=urn:btih:abc",
            PreferredAudioLanguage = "eng",
            RequiredAudioLanguages = new[] { "eng" },
            ValidationSessionId = "session-1",
        }, CancellationToken.None);

        Assert.Equal("valid", result.Status);
        Assert.Equal("abc", result.Hash);
        Assert.Equal(12, result.SelectedFile?.Id);
        Assert.Equal("Avatar S02E01.mkv", result.SelectedFile?.Path);
        Assert.Equal(456, result.SelectedFile?.Size);
        Assert.Equal(2, result.SelectedAudioIndex);
        Assert.Equal("eng", result.SelectedAudioLanguage);
        Assert.Equal(2, result.AudioTracks.Count);
        Assert.Equal("secret-token", h.Requests[0].Headers.GetValues("X-Gostream-Token").Single());
        Assert.EndsWith("/api/library/validate", h.Requests[0].RequestUri!.AbsolutePath, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_5xx_Returns_Transient_Result()
    {
        var h = new QueuedHandler().Enqueue(HttpStatusCode.BadGateway, "{\"error\":\"upstream\"}");
        var c = MakeClient(h);

        var result = await c.ValidateAsync(new GostreamValidateRequest
        {
            Type = "movie",
            Title = "X",
            Magnet = "magnet:?xt=urn:btih:abc",
            ValidationSessionId = "session-1",
        }, CancellationToken.None);

        Assert.Equal("transient", result.Status);
        Assert.Equal("gostream_server_error", result.Reason);
        Assert.Equal("session-1", result.ValidationSessionId);
    }

    [Fact]
    public async Task ReleaseValidation_Sends_Configured_Gostream_Token()
    {
        var h = new QueuedHandler().Enqueue(HttpStatusCode.OK, "{\"status\":\"released\"}");
        var c = MakeClient(h, "secret-token");

        await c.ReleaseValidationAsync(new GostreamValidationReleaseRequest
        {
            ValidationSessionId = "session-1",
            Hash = "abc",
        }, CancellationToken.None);

        Assert.Equal("secret-token", h.Requests[0].Headers.GetValues("X-Gostream-Token").Single());
        Assert.EndsWith("/api/library/validate/release", h.Requests[0].RequestUri!.AbsolutePath, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReleaseValidation_Skips_When_Hash_Missing()
    {
        var h = new QueuedHandler();
        var c = MakeClient(h, "secret-token");

        await c.ReleaseValidationAsync(new GostreamValidationReleaseRequest
        {
            ValidationSessionId = "session-1",
            Hash = null,
        }, CancellationToken.None);

        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task Add_504_Throws_Timeout()
    {
        var h = new QueuedHandler().Enqueue(HttpStatusCode.GatewayTimeout, "{\"error\":\"metadata_timeout\"}");
        var c = MakeClient(h);
        await Assert.ThrowsAsync<GostreamTimeoutException>(() =>
            c.AddAsync(new GostreamAddRequest { Type = "movie", Title = "X", Magnet = "m" }, CancellationToken.None));
    }

    [Fact]
    public async Task Add_422_Throws_NoValidFiles()
    {
        var h = new QueuedHandler().Enqueue(HttpStatusCode.UnprocessableEntity, "{\"error\":\"no_valid_files\"}");
        var c = MakeClient(h);
        await Assert.ThrowsAsync<GostreamNoValidFilesException>(() =>
            c.AddAsync(new GostreamAddRequest { Type = "movie", Title = "X", Magnet = "m" }, CancellationToken.None));
    }

    [Fact]
    public async Task Add_5xx_Retries_Once_Then_Throws()
    {
        var h = new QueuedHandler()
            .Enqueue(HttpStatusCode.BadGateway, "{\"error\":\"upstream\"}")
            .Enqueue(HttpStatusCode.BadGateway, "{\"error\":\"upstream\"}");
        var c = MakeClient(h);
        await Assert.ThrowsAsync<GostreamServerException>(() =>
            c.AddAsync(new GostreamAddRequest { Type = "movie", Title = "X", Magnet = "m" }, CancellationToken.None));
        Assert.Equal(2, h.Requests.Count);
    }

    [Fact]
    public async Task Add_5xx_Then_200_Succeeds_After_Retry()
    {
        var h = new QueuedHandler()
            .Enqueue(HttpStatusCode.InternalServerError, "{\"error\":\"oops\"}")
            .Enqueue(HttpStatusCode.OK, OkBody);
        var c = MakeClient(h);
        var r = await c.AddAsync(new GostreamAddRequest { Type = "movie", Title = "X", Magnet = "m" }, CancellationToken.None);
        Assert.Equal("/f/x.mkv", r.FusePath);
    }

    [Fact]
    public async Task Remove_204_Succeeds()
    {
        var h = new QueuedHandler().Enqueue(HttpStatusCode.NoContent);
        var c = MakeClient(h);
        await c.RemoveAsync("/r/x.mkv", CancellationToken.None);
    }

    [Fact]
    public async Task Remove_404_Swallowed()
    {
        var h = new QueuedHandler().Enqueue(HttpStatusCode.NotFound, "{\"error\":\"not_found\"}");
        var c = MakeClient(h);
        await c.RemoveAsync("/r/x.mkv", CancellationToken.None);
    }

    [Fact]
    public async Task Prestage_Posts_Body_To_Endpoint()
    {
        string? bodySeen = null;
        var captor = new BodyCaptureHandler(req =>
        {
            bodySeen = req.Content!.ReadAsStringAsync().Result;
            return (HttpStatusCode.Accepted, null);
        });
        var http = new HttpClient(captor) { BaseAddress = null };
        var c = new GostreamClient(http, NullLogger<GostreamClient>.Instance, () => "http://gs.test:9080");

        await c.PrestageAsync("/r/x.mkv", 50, CancellationToken.None);

        Assert.Single(captor.Requests);
        Assert.EndsWith("/api/library/prestage", captor.Requests[0].RequestUri!.AbsolutePath, System.StringComparison.Ordinal);
        Assert.NotNull(bodySeen);
        Assert.Contains("\"stub_path\"", bodySeen!, System.StringComparison.Ordinal);
        Assert.Contains("/r/x.mkv", bodySeen!, System.StringComparison.Ordinal);
        Assert.Contains("\"priority\":50", bodySeen!, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prestage_5xx_Throws()
    {
        var h = new QueuedHandler().Enqueue(HttpStatusCode.InternalServerError, "{\"error\":\"oops\"}");
        var c = MakeClient(h);
        await Assert.ThrowsAsync<GostreamServerException>(
            () => c.PrestageAsync("/r/x.mkv", 50, CancellationToken.None));
    }

    [Fact]
    public async Task IsVaultModePresent_404_With_Json_Returns_True_And_Caches()
    {
        var h = new QueuedHandler()
            .Enqueue(HttpStatusCode.NotFound, "{\"error\":\"no such stub\"}");
        var c = MakeClient(h);

        Assert.True(await c.IsVaultModePresentAsync(CancellationToken.None));
        // Second call must not hit HTTP again (cached).
        Assert.True(await c.IsVaultModePresentAsync(CancellationToken.None));
        Assert.Single(h.Requests);
    }

    [Fact]
    public async Task IsVaultModePresent_405_Returns_False()
    {
        var h = new QueuedHandler().Enqueue(HttpStatusCode.MethodNotAllowed);
        var c = MakeClient(h);
        Assert.False(await c.IsVaultModePresentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task IsVaultModePresent_ConnectionError_Returns_False()
    {
        var failing = new ThrowingHandler();
        var http = new HttpClient(failing) { BaseAddress = null };
        var c = new GostreamClient(http, NullLogger<GostreamClient>.Instance, () => "http://gs.test:9080");
        Assert.False(await c.IsVaultModePresentAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Unprestage_204_Succeeds()
    {
        var h = new QueuedHandler().Enqueue(HttpStatusCode.NoContent);
        var c = MakeClient(h);
        await c.UnprestageAsync("/r/x.mkv", CancellationToken.None);
        Assert.EndsWith("/api/library/unprestage", h.Requests[0].RequestUri!.AbsolutePath, System.StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unprestage_404_Swallowed()
    {
        var h = new QueuedHandler().Enqueue(HttpStatusCode.NotFound, "{\"error\":\"not_found\"}");
        var c = MakeClient(h);
        await c.UnprestageAsync("/r/x.mkv", CancellationToken.None);
    }

    [Fact]
    public async Task Unprestage_5xx_Throws()
    {
        var h = new QueuedHandler().Enqueue(HttpStatusCode.InternalServerError, "{\"error\":\"oops\"}");
        var c = MakeClient(h);
        await Assert.ThrowsAsync<GostreamServerException>(
            () => c.UnprestageAsync("/r/x.mkv", CancellationToken.None));
    }

    private sealed class ThrowingHandler : System.Net.Http.HttpMessageHandler
    {
        protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
            => throw new System.Net.Http.HttpRequestException("connection refused");
    }

    private sealed class BodyCaptureHandler : System.Net.Http.HttpMessageHandler
    {
        private readonly System.Func<System.Net.Http.HttpRequestMessage, (HttpStatusCode, string?)> _resp;
        public System.Collections.Generic.List<System.Net.Http.HttpRequestMessage> Requests { get; } = new();
        public BodyCaptureHandler(System.Func<System.Net.Http.HttpRequestMessage, (HttpStatusCode, string?)> resp) { _resp = resp; }
        protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var (status, body) = _resp(request);
            var msg = new System.Net.Http.HttpResponseMessage(status);
            if (body is not null) msg.Content = new System.Net.Http.StringContent(body);
            return System.Threading.Tasks.Task.FromResult(msg);
        }
    }
}
