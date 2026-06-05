using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using Jellyfin.Plugin.PhantomLibrary.Library;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class SeriesIngestorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PhantomDb _db;
    private readonly Mock<ILibraryManager> _lib = new();
    private readonly Mock<ITmdbClient> _tmdb = new();
    private readonly TestFolder _seriesParent;
    private readonly VirtualLibraryRoot _root;

    private sealed class TestFolder : Folder { }

    public SeriesIngestorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "si_" + Guid.NewGuid().ToString("N") + ".db");
        _db = new PhantomDb(_dbPath);
        _seriesParent = new TestFolder { Name = "TV" };
        _seriesParent.Id = Guid.NewGuid();
        var rootFolder = new RootWithChildren(new List<BaseItem> { _seriesParent });
        _lib.Setup(l => l.GetUserRootFolder()).Returns(rootFolder);
        _lib.Setup(l => l.GetContentType(_seriesParent)).Returns(CollectionType.tvshows);
        _lib.Setup(l => l.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns<string, Type>((s, _) =>
            {
                using var sha = System.Security.Cryptography.MD5.Create();
                return new Guid(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s)));
            });
        _root = new VirtualLibraryRoot(_lib.Object, NullLogger<VirtualLibraryRoot>.Instance,
            () => new Configuration.PluginConfiguration());
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

    private SeriesIngestor Build()
        => new SeriesIngestor(_lib.Object, _tmdb.Object, _root, _db, new NullPhantomStubManager(), NullLogger<SeriesIngestor>.Instance);

    private static TmdbSeriesDetails Series(int id) => new TmdbSeriesDetails(
        id, "Show", "Show", "ov", null, null, "2020-01-01", 8.0, 100,
        new[] { "Drama" }, "Ended", 1, 10, new[] { "US" }, "tt0944947");

    private static TmdbSeasonDetails Season(int seriesId, int seasonNo, int episodeCount)
    {
        var eps = Enumerable.Range(1, episodeCount).Select(i => new TmdbEpisodeSummary
        {
            Id = 1000 + i,
            EpisodeNumber = i,
            SeasonNumber = seasonNo,
            Name = $"E{i}",
            Runtime = 42,
        }).ToList();
        return new TmdbSeasonDetails { SeriesTmdbId = seriesId, SeasonNumber = seasonNo, Episodes = eps };
    }

    [Fact]
    public async Task EnsureEpisode_Creates_Whole_Chain_When_Nothing_Exists()
    {
        _tmdb.Setup(t => t.GetSeriesAsync(99, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Series(99));
        _tmdb.Setup(t => t.GetSeasonAsync(99, 1, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Season(99, 1, 3));
        _tmdb.Setup(t => t.GetEpisodeAsync(99, 1, 2, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TmdbEpisodeDetails
            {
                Id = 1002, EpisodeNumber = 2, SeasonNumber = 1, Name = "E2", ImdbId = "tt1234",
            });

        // No existing Series.
        _lib.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(Array.Empty<BaseItem>());
        var created = new List<(BaseItem item, BaseItem parent)>();
        _lib.Setup(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()))
            .Callback<BaseItem, BaseItem>((i, p) => created.Add((i, p)));

        var ing = Build();
        var ep = await ing.EnsureEpisodeAsync(99, 1, 2, CancellationToken.None);

        Assert.NotNull(ep);
        Assert.Equal(2, ep.IndexNumber);
        Assert.Equal(1, ep.ParentIndexNumber);
        // Should have created: Series, Season, Episode.
        Assert.Equal(3, created.Count);
        Assert.IsType<Series>(created[0].item);
        Assert.IsType<Season>(created[1].item);
        Assert.IsType<Episode>(created[2].item);
        // phantom_items row written for the episode.
        var row = await _db.GetPhantomItemAsync(ep.Id, CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal("episode", row!.Type);
        Assert.Equal(PhantomItemState.Virtual, row.State);
    }

    [Fact]
    public async Task EnsureEpisode_Reuses_Existing_Series_With_Matching_Tmdb()
    {
        var existingSeries = new Series { Name = "Show" };
        existingSeries.Id = Guid.NewGuid();
        existingSeries.ProviderIds["Tmdb"] = "99";
        existingSeries.ProviderIds["Imdb"] = "tt0944947";

        _tmdb.Setup(t => t.GetSeriesAsync(99, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Series(99));
        _tmdb.Setup(t => t.GetSeasonAsync(99, 1, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Season(99, 1, 3));
        _tmdb.Setup(t => t.GetEpisodeAsync(99, 1, 1, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TmdbEpisodeDetails?)null);

        _lib.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(new BaseItem[] { existingSeries });

        var created = new List<BaseItem>();
        _lib.Setup(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()))
            .Callback<BaseItem, BaseItem>((i, _) => created.Add(i));

        var ing = Build();
        await ing.EnsureEpisodeAsync(99, 1, 1, CancellationToken.None);

        // No Series created — only Season + Episode.
        Assert.DoesNotContain(created, c => c is Series);
        Assert.Contains(created, c => c is Season);
        Assert.Contains(created, c => c is Episode);
    }
}
