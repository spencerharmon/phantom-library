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
    public async Task Resolves_Http_DownloadUrl_MagnetRedirect()
    {
        var body = "[{\"title\":\"X\",\"size\":1,\"seeders\":1,\"downloadUrl\":\"https://t.example/x.torrent\"}]";
        var c = Make(new QueuedHandler()
            .Enqueue(HttpStatusCode.OK, body)
            .Enqueue(HttpStatusCode.MovedPermanently, mutate: r => r.Headers.Location = new Uri("magnet:?xt=urn:btih:FACEFEED&dn=X")));
        var res = await c.SearchAsync(new IndexerQuery { Type = "movie", Imdb = "tt1" }, CancellationToken.None);
        Assert.Single(res);
        Assert.Equal("FACEFEED", res[0].InfoHash);
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
    public async Task Movie_Search_Uses_Imdb_And_Title_Year_Union()
    {
        var imdbBody = "[{\"title\":\"YTS\",\"size\":2147483648,\"seeders\":100,\"magnetUrl\":\"magnet:?xt=urn:btih:AAAA&dn=YTS\",\"indexer\":\"yts\"}]";
        var titleBody = "["
            + "{\"title\":\"Spirited Away 2001 QxR\",\"size\":8580000000,\"seeders\":292,\"magnetUrl\":\"magnet:?xt=urn:btih:BBBB&dn=QxR\",\"indexer\":\"lime\"},"
            + "{\"title\":\"YTS duplicate\",\"size\":2147483648,\"seeders\":100,\"magnetUrl\":\"magnet:?xt=urn:btih:AAAA&dn=YTS\",\"indexer\":\"yts\"}"
            + "]";
        var handler = new QueuedHandler()
            .Enqueue(HttpStatusCode.OK, imdbBody)
            .Enqueue(HttpStatusCode.OK, titleBody);
        var c = Make(handler);

        var res = await c.SearchAsync(new IndexerQuery
        {
            Type = "movie",
            Imdb = "tt0245429",
            Title = "Spirited Away",
            Year = 2001,
        }, CancellationToken.None);

        Assert.Equal(2, res.Count);
        Assert.Contains(res, c => c.InfoHash == "AAAA");
        Assert.Contains(res, c => c.InfoHash == "BBBB");
        Assert.Contains("tt0245429", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("Spirited", handler.Requests[1].RequestUri!.ToString());
        Assert.Contains("2001", handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task Episode_Search_Uses_Text_Query_Not_Imdb_Tvsearch()
    {
        var body = "[]";
        var handler = new QueuedHandler().Enqueue(HttpStatusCode.OK, body);
        var c = Make(handler);

        _ = await c.SearchAsync(new IndexerQuery
        {
            Type = "episode",
            Title = "Avatar the Last Airbender",
            SeriesImdb = "tt9018736",
            Season = 1,
            Episode = 5,
        }, CancellationToken.None);

        var uri = handler.Requests.Single().RequestUri!.ToString();
        Assert.Contains("type=search", uri);
        Assert.Contains("Avatar the Last Airbender S01E05", uri);
        Assert.DoesNotContain("tvsearch", uri);
        Assert.DoesNotContain("imdbid", uri);
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
