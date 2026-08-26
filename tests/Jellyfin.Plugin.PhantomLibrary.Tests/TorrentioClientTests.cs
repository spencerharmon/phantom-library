using System.Net;
using System.Net.Http;
using System.Threading;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class TorrentioClientTests
{
    private static TorrentioClient Make(QueuedHandler h)
    {
        var http = new HttpClient(h);
        return new TorrentioClient(http, NullLogger<TorrentioClient>.Instance, () => "https://torrentio.test");
    }

    [Fact]
    public async Task Parses_Seeders_And_Size_From_Title()
    {
        var body = "{\"streams\":[{\"name\":\"Torrentio\\nGroup\",\"title\":\"X.2024.2160p\\n👤 42 💾 25.5 GB\",\"infoHash\":\"abcd\"}]}";
        var c = Make(new QueuedHandler().Enqueue(HttpStatusCode.OK, body));
        var r = await c.SearchAsync(new IndexerQuery { Type = "movie", Imdb = "tt2" }, CancellationToken.None);
        Assert.Single(r);
        Assert.Equal(42, r[0].Seeders);
        Assert.True(r[0].Size > 25L * 1024 * 1024 * 1024);
        Assert.Equal("abcd", r[0].InfoHash);
        Assert.StartsWith("magnet:?xt=urn:btih:abcd", r[0].Magnet);
    }

    [Fact]
    public async Task Missing_Imdb_Throws_NotApplicable()
    {
        var c = Make(new QueuedHandler());
        await Assert.ThrowsAsync<IndexerNotApplicableException>(() =>
            c.SearchAsync(new IndexerQuery { Type = "movie", Title = "X" }, CancellationToken.None));
    }

    [Fact]
    public async Task Server_Error_Throws_Transient()
    {
        var c = Make(new QueuedHandler().Enqueue(HttpStatusCode.BadGateway));
        await Assert.ThrowsAsync<IndexerTransientException>(() =>
            c.SearchAsync(new IndexerQuery { Type = "movie", Imdb = "tt2" }, CancellationToken.None));
    }

    [Fact]
    public async Task Transport_Error_Throws_Transient()
    {
        var c = Make(new QueuedHandler().EnqueueException(new HttpRequestException("network down")));
        await Assert.ThrowsAsync<IndexerTransientException>(() =>
            c.SearchAsync(new IndexerQuery { Type = "movie", Imdb = "tt2" }, CancellationToken.None));
    }

    [Fact]
    public async Task Malformed_Response_Throws_Transient()
    {
        var c = Make(new QueuedHandler().Enqueue(HttpStatusCode.OK, "{"));
        await Assert.ThrowsAsync<IndexerTransientException>(() =>
            c.SearchAsync(new IndexerQuery { Type = "movie", Imdb = "tt2" }, CancellationToken.None));
    }

    [Fact]
    public void Size_Parser_Handles_GB_MB_TB()
    {
        Assert.Equal((long)(1.5 * 1024 * 1024 * 1024), TorrentioClient.ParseSize("xx 💾 1.5 GB yy"));
        Assert.Equal(500L * 1024 * 1024, TorrentioClient.ParseSize("💾 500 MB"));
        Assert.Equal(0L, TorrentioClient.ParseSize("no size"));
    }

    [Fact]
    public void Seeders_Defaults_To_Zero_When_Absent()
    {
        Assert.Equal(0, TorrentioClient.ParseSeeders("no peers line"));
        Assert.Equal(7, TorrentioClient.ParseSeeders("blah 👤 7 💾 1 GB"));
    }
}
