using System.Net;
using System.Net.Http;
using System.Threading;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class GostreamClientTests
{
    private static GostreamClient MakeClient(QueuedHandler h)
    {
        var http = new HttpClient(h) { BaseAddress = null };
        return new GostreamClient(http, NullLogger<GostreamClient>.Instance, () => "http://gs.test:9080");
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

}
