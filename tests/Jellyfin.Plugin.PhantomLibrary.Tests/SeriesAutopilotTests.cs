using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Library;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class SeriesAutopilotTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PhantomDb _db;
    private readonly Mock<ITmdbClient> _tmdb = new();
    private readonly Mock<ISeriesIngestor> _ingestor = new();
    private readonly Mock<IMaterialisationQueue> _queue = new();
    private readonly Mock<IMaterialiser> _materialiser = new();
    private readonly Mock<IGostreamClient> _gostream = new();
    private readonly Mock<ILibraryManager> _lib = new();
    private readonly VirtualLibraryRoot _root;
    private readonly TestFolder _moviesParent;
    private readonly TestFolder _seriesParent;

    private sealed class TestFolder : Folder { }

    public SeriesAutopilotTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "ap_" + Guid.NewGuid().ToString("N") + ".db");
        _db = new PhantomDb(_dbPath);
        _moviesParent = new TestFolder { Name = "Movies" };
        _moviesParent.Id = Guid.NewGuid();
        _seriesParent = new TestFolder { Name = "TV" };
        _seriesParent.Id = Guid.NewGuid();
        var libRoot = new RootWithChildren(new List<BaseItem> { _moviesParent, _seriesParent });
        _lib.Setup(l => l.GetUserRootFolder()).Returns(libRoot);
        _lib.Setup(l => l.GetContentType(_moviesParent)).Returns(Jellyfin.Data.Enums.CollectionType.movies);
        _lib.Setup(l => l.GetContentType(_seriesParent)).Returns(Jellyfin.Data.Enums.CollectionType.tvshows);
        _lib.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(Array.Empty<BaseItem>());
        _lib.Setup(l => l.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns<string, Type>((s, _) =>
            {
                using var sha = System.Security.Cryptography.MD5.Create();
                return new Guid(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s)));
            });
        _root = new VirtualLibraryRoot(_lib.Object, NullLogger<VirtualLibraryRoot>.Instance,
            () => new PluginConfiguration());
    }

    private sealed class RootWithChildren : Folder
    {
        private readonly List<BaseItem> _children;
        public RootWithChildren(List<BaseItem> children) { _children = children; Id = Guid.NewGuid(); }
        public override IEnumerable<BaseItem> Children => _children;
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); File.Delete(_dbPath + "-wal"); File.Delete(_dbPath + "-shm"); } catch { }
    }

    private SeriesAutopilot Build(PluginConfiguration? cfg = null)
    {
        cfg ??= new PluginConfiguration { SeriesAutopilotEnabled = true, SeriesAutopilotPrefetchEpisodes = 1 };
        return new SeriesAutopilot(
            _tmdb.Object, _ingestor.Object, _queue.Object, _materialiser.Object,
            _gostream.Object, _db, _root, _lib.Object,
            NullLogger<SeriesAutopilot>.Instance, () => cfg);
    }

    private static TmdbSeasonDetails Season(int seriesId, int seasonNo, int episodeCount)
    {
        var eps = Enumerable.Range(1, episodeCount).Select(i => new TmdbEpisodeSummary
        {
            Id = 1000 * seasonNo + i,
            EpisodeNumber = i,
            SeasonNumber = seasonNo,
        }).ToList();
        return new TmdbSeasonDetails { SeriesTmdbId = seriesId, SeasonNumber = seasonNo, Episodes = eps };
    }

    private static Series MakeSeries(int tmdb = 99, string imdb = "tt0944947")
    {
        var s = new Series { Name = "Show" };
        s.Id = Guid.NewGuid();
        s.ProviderIds["Tmdb"] = tmdb.ToString(System.Globalization.CultureInfo.InvariantCulture);
        s.ProviderIds["Imdb"] = imdb;
        return s;
    }

    private static Episode MakeEpisode(int s, int e)
    {
        var ep = new Episode { Name = $"E{e}", ParentIndexNumber = s, IndexNumber = e };
        ep.Id = Guid.NewGuid();
        return ep;
    }

    [Fact]
    public async Task EnsureUpcoming_Enqueues_Next_Episode_Within_Season()
    {
        _tmdb.Setup(t => t.GetSeasonAsync(99, 1, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Season(99, 1, 10));
        var nextEp = MakeEpisode(1, 4);
        _ingestor.Setup(i => i.EnsureEpisodeAsync(99, 1, 4, It.IsAny<CancellationToken>()))
            .ReturnsAsync(nextEp);

        var series = MakeSeries();
        var ap = Build();
        await ap.EnsureUpcomingMaterialisedAsync(Guid.NewGuid(), series, 1, 3, 1, CancellationToken.None);

        _ingestor.Verify(i => i.EnsureEpisodeAsync(99, 1, 4, It.IsAny<CancellationToken>()), Times.Once);
        _queue.Verify(q => q.EnqueueUser(nextEp.Id, MaterialiseTrigger.Autopilot), Times.Once);
    }

    [Fact]
    public async Task OnEpisodePlaybackProgress_Below_Threshold_NoOp()
    {
        var ep = MakeEpisode(1, 3);
        var ap = Build();
        await ap.OnEpisodePlaybackProgressAsync(Guid.NewGuid(), ep, 0.50, CancellationToken.None);
        _queue.Verify(q => q.EnqueueUser(It.IsAny<Guid>(), It.IsAny<MaterialiseTrigger>()), Times.Never);
        _ingestor.Verify(i => i.EnsureEpisodeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OnEpisodePlaybackProgress_Disabled_NoOp()
    {
        var ep = MakeEpisode(1, 3);
        var ap = Build(new PluginConfiguration { SeriesAutopilotEnabled = false });
        await ap.OnEpisodePlaybackProgressAsync(Guid.NewGuid(), ep, 0.95, CancellationToken.None);
        _queue.Verify(q => q.EnqueueUser(It.IsAny<Guid>(), It.IsAny<MaterialiseTrigger>()), Times.Never);
    }

    [Fact]
    public async Task EnsureUpcoming_SeasonFinale_Advances_To_Next_Season_Episode_1()
    {
        _tmdb.Setup(t => t.GetSeasonAsync(99, 1, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Season(99, 1, 3));
        _tmdb.Setup(t => t.GetSeasonAsync(99, 2, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Season(99, 2, 5));

        var nextEp = MakeEpisode(2, 1);
        _ingestor.Setup(i => i.EnsureEpisodeAsync(99, 2, 1, It.IsAny<CancellationToken>())).ReturnsAsync(nextEp);

        var series = MakeSeries();
        var ap = Build();
        await ap.EnsureUpcomingMaterialisedAsync(Guid.NewGuid(), series, 1, 3, 1, CancellationToken.None);

        _ingestor.Verify(i => i.EnsureEpisodeAsync(99, 2, 1, It.IsAny<CancellationToken>()), Times.Once);
        _queue.Verify(q => q.EnqueueUser(nextEp.Id, MaterialiseTrigger.Autopilot), Times.Once);
    }

    [Fact]
    public async Task EnsureUpcoming_SeriesEnd_DoesNotEnqueue()
    {
        _tmdb.Setup(t => t.GetSeasonAsync(99, 1, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Season(99, 1, 3));
        _tmdb.Setup(t => t.GetSeasonAsync(99, 2, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TmdbSeasonDetails?)null);

        var series = MakeSeries();
        var ap = Build();
        await ap.EnsureUpcomingMaterialisedAsync(Guid.NewGuid(), series, 1, 3, 1, CancellationToken.None);

        _ingestor.Verify(i => i.EnsureEpisodeAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        _queue.Verify(q => q.EnqueueUser(It.IsAny<Guid>(), It.IsAny<MaterialiseTrigger>()), Times.Never);
    }

    [Fact]
    public async Task OnMovieFavourited_WithSequel_Creates_Virtual_And_Enqueues_Eager()
    {
        _tmdb.Setup(t => t.GetMovieCollectionSequelAsync(671, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TmdbMovieDetails(
                672, "Sequel", "Sequel", "ov", null, null, "2002-11-15", 7.7, 1000,
                161, Array.Empty<string>(), "Released", null, "tt0295297", null, null));
        BaseItem? createdItem = null;
        _lib.Setup(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()))
            .Callback<BaseItem, BaseItem>((i, _) => createdItem = i);

        var movie = new Movie { Name = "Original" };
        movie.Id = Guid.NewGuid();
        movie.ProviderIds["Tmdb"] = "671";

        var ap = Build();
        await ap.OnMovieFavouritedAsync(Guid.NewGuid(), movie, CancellationToken.None);

        Assert.NotNull(createdItem);
        Assert.IsType<Movie>(createdItem);
        Assert.Equal("672", createdItem!.ProviderIds["Tmdb"]);
        _queue.Verify(q => q.EnqueueEager(createdItem.Id), Times.Once);
    }

    [Fact]
    public async Task OnMovieFavourited_NoCollection_NoOp()
    {
        _tmdb.Setup(t => t.GetMovieCollectionSequelAsync(999, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TmdbMovieDetails?)null);

        var movie = new Movie { Name = "Standalone" };
        movie.Id = Guid.NewGuid();
        movie.ProviderIds["Tmdb"] = "999";

        var ap = Build();
        await ap.OnMovieFavouritedAsync(Guid.NewGuid(), movie, CancellationToken.None);

        _lib.Verify(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()), Times.Never);
        _queue.Verify(q => q.EnqueueEager(It.IsAny<Guid>()), Times.Never);
    }

    [Fact]
    public async Task OnMovieFavourited_Disabled_NoOp()
    {
        var movie = new Movie { Name = "X" };
        movie.Id = Guid.NewGuid();
        movie.ProviderIds["Tmdb"] = "1";
        var ap = Build(new PluginConfiguration { SeriesAutopilotEnabled = false });
        await ap.OnMovieFavouritedAsync(Guid.NewGuid(), movie, CancellationToken.None);
        _tmdb.Verify(t => t.GetMovieCollectionSequelAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }
}
