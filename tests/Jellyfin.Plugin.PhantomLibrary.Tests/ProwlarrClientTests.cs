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
        // Both variants are dispatched CONCURRENTLY now, so request arrival order
        // is undefined — assert the union of issued queries, not their index order.
        Assert.Contains(handler.Requests, r => r.RequestUri!.ToString().Contains("tt0245429"));
        Assert.Contains(handler.Requests, r => r.RequestUri!.ToString().Contains("Spirited")
            && r.RequestUri!.ToString().Contains("2001"));
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
    public async Task Movie_Without_Imdb_Still_Issues_Title_Year_Text_Query()
    {
        // A no-imdb movie must NOT be skipped: Prowlarr is title-based and is the
        // capable indexer for items without an IMDB id. It should issue exactly one
        // text query (title + year), with no imdbid parameter.
        var titleBody = "[{\"title\":\"Spirited Away 2001 QxR\",\"size\":8580000000,\"seeders\":292,\"magnetUrl\":\"magnet:?xt=urn:btih:BBBB&dn=QxR\",\"indexer\":\"lime\"}]";
        var handler = new QueuedHandler().Enqueue(HttpStatusCode.OK, titleBody);
        var c = Make(handler);

        var res = await c.SearchAsync(new IndexerQuery
        {
            Type = "movie",
            Imdb = null,
            Title = "Spirited Away",
            Year = 2001,
        }, CancellationToken.None);

        Assert.Single(res);
        Assert.Equal("BBBB", res[0].InfoHash);
        var uri = handler.Requests.Single().RequestUri!.ToString();
        Assert.Contains("type=search", uri);
        Assert.Contains("Spirited", uri);
        Assert.Contains("2001", uri);
        Assert.DoesNotContain("imdbid", uri);
    }

    [Fact]
    public async Task Prefers_InfoHash_Over_Proxy_MagnetUrl()
    {
        // The real Prowlarr shape: magnetUrl is the /download PROXY URL (not a
        // magnet: URI) and the usable info-hash is in `infoHash`. The client must
        // synthesize the magnet from infoHash and NOT drop the candidate, and NOT
        // make any network round-trip to the proxy URL (only the one search call).
        var body = "[{\"title\":\"Batman 1080p\",\"size\":1556974120,\"seeders\":1609,\"leechers\":849,"
            + "\"infoHash\":\"3E4822F5C85E623AAC3FD85E849769736A595493\","
            + "\"magnetUrl\":\"http://prowlarr.test:9696/1/download?apikey=K&link=abc\","
            + "\"indexer\":\"The Pirate Bay\"}]";
        var handler = new QueuedHandler().Enqueue(HttpStatusCode.OK, body);
        var c = Make(handler);
        var res = await c.SearchAsync(new IndexerQuery { Type = "movie", Imdb = "tt1" }, CancellationToken.None);

        Assert.Single(res);
        Assert.Equal("3E4822F5C85E623AAC3FD85E849769736A595493", res[0].InfoHash);
        Assert.StartsWith("magnet:?xt=urn:btih:3E4822F5C85E623AAC3FD85E849769736A595493", res[0].Magnet);
        // Exactly one HTTP call (the search) — the proxy magnetUrl was never fetched.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Prefers_Guid_Magnet_When_Present()
    {
        // Prowlarr often carries a ready magnet: URI in `guid`; it is the richest
        // source (dn + the indexer's own trackers) and wins over the proxy magnetUrl.
        var body = "[{\"title\":\"X\",\"size\":1,\"seeders\":5,"
            + "\"guid\":\"magnet:?xt=urn:btih:ABCD1234&dn=X&tr=udp%3A%2F%2Ftr%3A1337\","
            + "\"magnetUrl\":\"http://prowlarr.test:9696/1/download?apikey=K\","
            + "\"infoHash\":\"ABCD1234\"}]";
        var handler = new QueuedHandler().Enqueue(HttpStatusCode.OK, body);
        var c = Make(handler);
        var res = await c.SearchAsync(new IndexerQuery { Type = "movie", Imdb = "tt1" }, CancellationToken.None);

        Assert.Single(res);
        Assert.Equal("ABCD1234", res[0].InfoHash);
        Assert.Contains("dn=X", res[0].Magnet); // came from guid, not synthesized bare
        Assert.Single(handler.Requests);
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
