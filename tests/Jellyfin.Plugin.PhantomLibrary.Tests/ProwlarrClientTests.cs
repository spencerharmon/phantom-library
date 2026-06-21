using System.Net;
using System.Net.Http;
using System.Threading;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class ProwlarrClientTests
{
    private static ProwlarrClient Make(QueuedHandler h)
    {
        var http = new HttpClient(h);
        return new ProwlarrClient(http, NullLogger<ProwlarrClient>.Instance, () => ("http://prowlarr.test:9696", "K"));
    }

    [Fact]
    public async Task Parses_MagnetUrl_And_InfoHash()
    {
        var body = "[{\"title\":\"X 1080p\",\"size\":4000000000,\"seeders\":100,\"leechers\":2,\"magnetUrl\":\"magnet:?xt=urn:btih:DEADBEEF&dn=X\",\"indexer\":\"src\"}]";
        var c = Make(new QueuedHandler().Enqueue(HttpStatusCode.OK, body));
        var res = await c.SearchAsync(new IndexerQuery { Type = "movie", Imdb = "tt1" }, CancellationToken.None);
        Assert.Single(res);
        Assert.Equal("DEADBEEF", res[0].InfoHash);
        Assert.Equal(100, res[0].Seeders);
        Assert.Equal("Prowlarr", res[0].IndexerName);
    }

    [Fact]
    public async Task Skips_Http_Only_DownloadUrl()
    {
        var body = "[{\"title\":\"X\",\"size\":1,\"seeders\":1,\"downloadUrl\":\"https://t.example/x.torrent\"}]";
        var c = Make(new QueuedHandler().Enqueue(HttpStatusCode.OK, body));
        var res = await c.SearchAsync(new IndexerQuery { Type = "movie", Imdb = "tt1" }, CancellationToken.None);
        Assert.Empty(res);
    }

    [Fact]
    public async Task Accepts_Magnet_DownloadUrl_When_MagnetUrl_Missing()
    {
        var body = "[{\"title\":\"X\",\"size\":1,\"seeders\":1,\"downloadUrl\":\"magnet:?xt=urn:btih:CAFEBABE\"}]";
        var c = Make(new QueuedHandler().Enqueue(HttpStatusCode.OK, body));
        var res = await c.SearchAsync(new IndexerQuery { Type = "movie", Imdb = "tt1" }, CancellationToken.None);
        Assert.Single(res);
        Assert.Equal("CAFEBABE", res[0].InfoHash);
    }

    [Fact]
    public async Task Auth_Failure_Throws()
    {
        var c = Make(new QueuedHandler().Enqueue(HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<IndexerAuthException>(() =>
            c.SearchAsync(new IndexerQuery { Type = "movie", Imdb = "tt1" }, CancellationToken.None));
    }

    [Fact]
    public async Task Server_Error_Throws_Transient()
    {
        var c = Make(new QueuedHandler().Enqueue(HttpStatusCode.BadGateway));
        await Assert.ThrowsAsync<IndexerTransientException>(() =>
            c.SearchAsync(new IndexerQuery { Type = "movie", Imdb = "tt1" }, CancellationToken.None));
    }

    [Fact]
    public async Task Transport_Error_Throws_Transient()
    {
        var c = Make(new QueuedHandler().EnqueueException(new HttpRequestException("network down")));
        await Assert.ThrowsAsync<IndexerTransientException>(() =>
            c.SearchAsync(new IndexerQuery { Type = "movie", Imdb = "tt1" }, CancellationToken.None));
    }

    [Fact]
    public async Task Malformed_Response_Throws_Transient()
    {
        var c = Make(new QueuedHandler().Enqueue(HttpStatusCode.OK, "{"));
        await Assert.ThrowsAsync<IndexerTransientException>(() =>
            c.SearchAsync(new IndexerQuery { Type = "movie", Imdb = "tt1" }, CancellationToken.None));
    }
}
