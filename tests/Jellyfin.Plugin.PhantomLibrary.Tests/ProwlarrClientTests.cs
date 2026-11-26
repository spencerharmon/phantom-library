using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// ProwlarrClient per-indexer Torznab parsing + query shape
/// (p13-parallel-indexer-search replaced the aggregate <c>/api/v1/search</c> path).
/// </summary>
public class ProwlarrClientTests
{
    private static readonly DiscoveredIndexer Ix = new(7, "lime", "torrent");

    private static ProwlarrClient Make(QueuedHandler h)
    {
        var http = new HttpClient(h);
        return new ProwlarrClient(http, NullLogger<ProwlarrClient>.Instance, () => ("http://prowlarr.test:9696", "K"));
    }

    private static string Rss(string items)
        => "<?xml version=\"1.0\" encoding=\"UTF-8\"?><rss version=\"2.0\" xmlns:torznab=\"http://torznab.com/schemas/2015/feed\"><channel>"
            + items + "</channel></rss>";

    [Fact]
    public async Task Parses_Torznab_MagnetUrl_Seeders_Peers_Size()
    {
        var body = Rss("<item><title>X 1080p</title><size>4000000000</size><link>https://p/dl/1</link>"
            + "<torznab:attr name=\"seeders\" value=\"100\"/><torznab:attr name=\"peers\" value=\"102\"/>"
            + "<torznab:attr name=\"magneturl\" value=\"magnet:?xt=urn:btih:DEADBEEF&amp;dn=X\"/></item>");
        var c = Make(new QueuedHandler().Enqueue(HttpStatusCode.OK, body));
        var res = await c.SearchIndexerAsync(Ix, new IndexerQuery { Type = "movie", Imdb = "tt1" }, CancellationToken.None);
        var hit = Assert.Single(res);
        Assert.Equal("DEADBEEF", hit.InfoHash);
        Assert.Equal(100, hit.Seeders);
        Assert.Equal(2, hit.Leechers);
        Assert.Equal(4000000000L, hit.Size);
        Assert.Equal("Prowlarr", hit.IndexerName);
        Assert.Equal("lime", hit.Source);
    }

    [Fact]
    public async Task Builds_Magnet_From_InfoHash_Attr_When_No_Magnet()
    {
        var body = Rss("<item><title>X</title><size>1</size>"
            + "<torznab:attr name=\"seeders\" value=\"3\"/><torznab:attr name=\"infohash\" value=\"CAFEBABE\"/></item>");
        var c = Make(new QueuedHandler().Enqueue(HttpStatusCode.OK, body));
        var res = await c.SearchIndexerAsync(Ix, new IndexerQuery { Type = "movie", Imdb = "tt1" }, CancellationToken.None);
        var hit = Assert.Single(res);
        Assert.Equal("CAFEBABE", hit.InfoHash);
        Assert.StartsWith("magnet:?xt=urn:btih:CAFEBABE", hit.Magnet, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Resolves_Http_Link_MagnetRedirect()
    {
        var body = Rss("<item><title>X</title><size>1</size><link>https://t.example/x.torrent</link>"
            + "<torznab:attr name=\"seeders\" value=\"1\"/></item>");
        var c = Make(new QueuedHandler()
            .Enqueue(HttpStatusCode.OK, body)
            .Enqueue(HttpStatusCode.MovedPermanently, mutate: r => r.Headers.Location = new Uri("magnet:?xt=urn:btih:FACEFEED&dn=X")));
        var res = await c.SearchIndexerAsync(Ix, new IndexerQuery { Type = "movie", Imdb = "tt1" }, CancellationToken.None);
        Assert.Equal("FACEFEED", Assert.Single(res).InfoHash);
    }

    [Fact]
    public async Task Movie_Search_Uses_Imdb_And_Title_Year_Union_On_PerIndexer_Torznab()
    {
        var imdbBody = Rss("<item><title>YTS</title><size>2147483648</size><torznab:attr name=\"seeders\" value=\"100\"/>"
            + "<torznab:attr name=\"magneturl\" value=\"magnet:?xt=urn:btih:AAAA&amp;dn=YTS\"/></item>");
        var titleBody = Rss(
            "<item><title>Spirited Away 2001 QxR</title><size>8580000000</size><torznab:attr name=\"seeders\" value=\"292\"/>"
            + "<torznab:attr name=\"magneturl\" value=\"magnet:?xt=urn:btih:BBBB&amp;dn=QxR\"/></item>"
            + "<item><title>YTS duplicate</title><size>2147483648</size><torznab:attr name=\"seeders\" value=\"100\"/>"
            + "<torznab:attr name=\"magneturl\" value=\"magnet:?xt=urn:btih:AAAA&amp;dn=YTS\"/></item>");
        var handler = new RoutingHandler(req =>
            Uri.UnescapeDataString(req.RequestUri!.Query).Contains("q=tt0245429", StringComparison.Ordinal) ? imdbBody : titleBody);
        var c = new ProwlarrClient(new HttpClient(handler), NullLogger<ProwlarrClient>.Instance, () => ("http://prowlarr.test:9696", "K"));

        var res = await c.SearchIndexerAsync(Ix, new IndexerQuery
        {
            Type = "movie",
            Imdb = "tt0245429",
            Title = "Spirited Away",
            Year = 2001,
        }, CancellationToken.None);

        Assert.Equal(2, res.Count);
        Assert.Contains(res, x => x.InfoHash == "AAAA");
        Assert.Contains(res, x => x.InfoHash == "BBBB");
        var uris = handler.Uris;
        Assert.Equal(2, uris.Count);
        Assert.All(uris, u => Assert.Equal("/7/api", u.AbsolutePath));
        Assert.All(uris, u => Assert.Contains("t=search", u.Query, StringComparison.Ordinal));
        Assert.All(uris, u => Assert.Contains("cat=2000", u.Query, StringComparison.Ordinal));
        Assert.Contains(uris, u => u.Query.Contains("tt0245429", StringComparison.Ordinal));
        Assert.Contains(uris, u => Uri.UnescapeDataString(u.Query).Contains("Spirited Away 2001", StringComparison.Ordinal));
        Assert.DoesNotContain(uris, u => u.AbsolutePath.Contains("/api/v1/search", StringComparison.Ordinal));
    }

    private sealed class RoutingHandler(Func<HttpRequestMessage, string> body) : HttpMessageHandler
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<Uri> _uris = new();

        public System.Collections.Generic.List<Uri> Uris => _uris.ToList();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _uris.Enqueue(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body(request), System.Text.Encoding.UTF8, "application/rss+xml"),
            });
        }
    }

    [Fact]
    public async Task Episode_Search_Uses_Text_Query_Tv_Category()
    {
        var handler = new QueuedHandler().Enqueue(HttpStatusCode.OK, Rss(string.Empty));
        var c = Make(handler);

        _ = await c.SearchIndexerAsync(Ix, new IndexerQuery
        {
            Type = "episode",
            Title = "Avatar the Last Airbender",
            SeriesImdb = "tt9018736",
            Season = 1,
            Episode = 5,
        }, CancellationToken.None);

        var uri = handler.Requests.Single().RequestUri!;
        Assert.Equal("/7/api", uri.AbsolutePath);
        Assert.Contains("t=search", uri.Query, StringComparison.Ordinal);
        Assert.Contains("cat=5000", uri.Query, StringComparison.Ordinal);
        Assert.Contains("Avatar the Last Airbender S01E05", Uri.UnescapeDataString(uri.Query), StringComparison.Ordinal);
        Assert.DoesNotContain("tvsearch", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("tt9018736", uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discovery_Lists_Only_Enabled_Torrent_Indexers()
    {
        var json = "[{\"id\":1,\"name\":\"a\",\"enable\":true,\"protocol\":\"torrent\"},"
            + "{\"id\":2,\"name\":\"b\",\"enable\":false,\"protocol\":\"torrent\"},"
            + "{\"id\":3,\"name\":\"c\",\"enable\":true,\"protocol\":\"usenet\"}]";
        var handler = new QueuedHandler().Enqueue(HttpStatusCode.OK, json);
        var c = Make(handler);
        var list = await c.DiscoverIndexersAsync(new IndexerQuery { Type = "movie" }, CancellationToken.None);
        Assert.Equal(new[] { 1 }, list.Select(i => i.Id));
        Assert.Equal("/api/v1/indexer", handler.Requests.Single().RequestUri!.AbsolutePath);
        Assert.Equal("K", handler.Requests.Single().Headers.GetValues("X-Api-Key").Single());
    }

    [Fact]
    public async Task Auth_Failure_Throws()
    {
        var c = Make(new QueuedHandler().Enqueue(HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<IndexerAuthException>(() =>
            c.SearchIndexerAsync(Ix, new IndexerQuery { Type = "movie", Imdb = "tt1" }, CancellationToken.None));
    }

    [Fact]
    public async Task Torznab_Error_Element_Is_Transient()
    {
        var c = Make(new QueuedHandler().Enqueue(HttpStatusCode.OK, "<error code=\"900\" description=\"indexer down\"/>"));
        await Assert.ThrowsAsync<IndexerTransientException>(() =>
            c.SearchIndexerAsync(Ix, new IndexerQuery { Type = "movie", Imdb = "tt1" }, CancellationToken.None));
    }

    [Fact]
    public async Task Server_Error_Throws_Transient()
    {
        var c = Make(new QueuedHandler().Enqueue(HttpStatusCode.BadGateway));
        await Assert.ThrowsAsync<IndexerTransientException>(() =>
            c.SearchIndexerAsync(Ix, new IndexerQuery { Type = "movie", Imdb = "tt1" }, CancellationToken.None));
    }

    [Fact]
    public async Task Transport_Error_Throws_Transient()
    {
        var c = Make(new QueuedHandler().EnqueueException(new HttpRequestException("network down")));
        await Assert.ThrowsAsync<IndexerTransientException>(() =>
            c.SearchIndexerAsync(Ix, new IndexerQuery { Type = "movie", Imdb = "tt1" }, CancellationToken.None));
    }

    [Fact]
    public async Task Malformed_Response_Throws_Transient()
    {
        var c = Make(new QueuedHandler().Enqueue(HttpStatusCode.OK, "<rss><channel>"));
        await Assert.ThrowsAsync<IndexerTransientException>(() =>
            c.SearchIndexerAsync(Ix, new IndexerQuery { Type = "movie", Imdb = "tt1" }, CancellationToken.None));
    }

    [Fact]
    public async Task SearchAsync_FansOut_Over_Discovered_Indexers_Never_Aggregate_Endpoint()
    {
        var json = "[{\"id\":4,\"name\":\"a\",\"enable\":true,\"protocol\":\"torrent\"}]";
        var body = Rss("<item><title>X 1080p</title><size>5</size><torznab:attr name=\"seeders\" value=\"9\"/>"
            + "<torznab:attr name=\"magneturl\" value=\"magnet:?xt=urn:btih:ABCD\"/></item>");
        var handler = new QueuedHandler().Enqueue(HttpStatusCode.OK, json).Enqueue(HttpStatusCode.OK, body);
        var c = Make(handler);
        var res = await c.SearchAsync(new IndexerQuery { Type = "episode", Title = "Show", Season = 1, Episode = 1 }, CancellationToken.None);
        Assert.Equal("ABCD", Assert.Single(res).InfoHash);
        Assert.Equal("/4/api", handler.Requests[1].RequestUri!.AbsolutePath);
    }
}
