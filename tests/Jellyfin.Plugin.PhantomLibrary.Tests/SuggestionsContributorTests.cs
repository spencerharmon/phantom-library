using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
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

public class SuggestionsContributorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PhantomDb _db;
    private readonly Mock<ITmdbClient> _tmdb = new();
    private readonly Mock<ILibraryManager> _lib = new();
    private readonly Mock<IUserManager> _users = new();
    private readonly EagerHintSink _hints = new();
    private readonly TestFolder _moviesParent;
    private readonly TestFolder _seriesParent;
    private readonly VirtualLibraryRoot _root;

    private sealed class TestFolder : Folder { }

    public SuggestionsContributorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "sugg_" + Guid.NewGuid().ToString("N") + ".db");
        _db = new PhantomDb(_dbPath);

        _moviesParent = new TestFolder { Name = "Movies" };
        _moviesParent.Id = Guid.NewGuid();
        _seriesParent = new TestFolder { Name = "TV" };
        _seriesParent.Id = Guid.NewGuid();

        var libRoot = new RootWithChildren(new List<BaseItem> { _moviesParent, _seriesParent });
        _lib.Setup(l => l.GetUserRootFolder()).Returns(libRoot);
        _lib.Setup(l => l.GetContentType(_moviesParent)).Returns(CollectionType.movies);
        _lib.Setup(l => l.GetContentType(_seriesParent)).Returns(CollectionType.tvshows);

        _root = new VirtualLibraryRoot(_lib.Object, NullLogger<VirtualLibraryRoot>.Instance,
            () => new Configuration.PluginConfiguration());

        // Default duplicate-lookup returns nothing.
        _lib.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(Array.Empty<BaseItem>());

        // Default GetNewItemId returns a deterministic-but-unique guid per name.
        _lib.Setup(l => l.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns<string, Type>((s, _) =>
            {
                using var sha = System.Security.Cryptography.MD5.Create();
                var b = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
                return new Guid(b);
            });

        // Default IUserManager: no users.
        _users.Setup(u => u.GetUsers()).Returns(Array.Empty<User>());
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

    private SuggestionsContributor Build()
    {
        var reader = new CachedTmdbReader(_tmdb.Object, _db, NullLogger<CachedTmdbReader>.Instance);
        return new SuggestionsContributor(
            reader, _root, _lib.Object, _users.Object, _db, _hints, new NullPhantomStubManager(),
            NullLogger<SuggestionsContributor>.Instance);
    }

    private static TmdbSearchHit Hit(int id, string title) => new TmdbSearchHit(
        id, title, title, "overview text", "/p.jpg", "/b.jpg", "2020-01-01", 7.5, 100)
    { GenreIds = new[] { 28 } };

    [Fact]
    public async Task RefreshTrending_CreatesNewItems()
    {
        _tmdb.Setup(t => t.GetTrendingMoviesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Hit(1, "A"), Hit(2, "B"), Hit(3, "C") });
        _tmdb.Setup(t => t.GetTrendingSeriesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Hit(10, "S1") });

        var created = new List<BaseItem>();
        _lib.Setup(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()))
            .Callback<BaseItem, BaseItem>((i, _) => created.Add(i));

        var s = Build();
        var n = await s.RefreshTrendingAsync(CancellationToken.None);

        Assert.Equal(4, n);
        Assert.Equal(4, created.Count);
        Assert.Contains(created, c => c is Movie && c.Name == "A");
        Assert.Contains(created, c => c is Series && c.Name == "S1");
        // Verify Genre populated from GenreIds (28 -> Action for movies, ?? for series)
        var movie = (Movie)created.First(c => c is Movie);
        Assert.Contains("Action", movie.Genres);
        Assert.Equal("overview text", movie.Overview);
        Assert.Equal(2020, movie.ProductionYear);
        Assert.Equal("1", movie.ProviderIds["Tmdb"]);
    }

    [Fact]
    public async Task RefreshTrending_HitsCache_OnSecondCall()
    {
        _tmdb.Setup(t => t.GetTrendingMoviesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Hit(1, "A") });
        _tmdb.Setup(t => t.GetTrendingSeriesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TmdbSearchHit>());

        var s = Build();
        await s.RefreshTrendingAsync(CancellationToken.None);
        await s.RefreshTrendingAsync(CancellationToken.None);

        _tmdb.Verify(t => t.GetTrendingMoviesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _tmdb.Verify(t => t.GetTrendingSeriesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RefreshSimilarTo_NoTmdbId_ReturnsZero()
    {
        var id = Guid.NewGuid();
        var m = new Movie { Name = "X" };
        m.Id = id;
        _lib.Setup(l => l.GetItemById(id)).Returns(m);

        var s = Build();
        var n = await s.RefreshSimilarToAsync(id, CancellationToken.None);
        Assert.Equal(0, n);
        _tmdb.Verify(t => t.GetSimilarMoviesAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RefreshTrending_DuplicateExisting_NoCreate_UpsertsPhantomRow()
    {
        var existingId = Guid.NewGuid();
        var existing = new Movie { Name = "Existing" };
        existing.Id = existingId;
        existing.ProviderIds["Tmdb"] = "42";

        _tmdb.Setup(t => t.GetTrendingMoviesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Hit(42, "Existing") });
        _tmdb.Setup(t => t.GetTrendingSeriesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TmdbSearchHit>());

        // Duplicate-lookup returns the existing movie when Tmdb=42.
        _lib.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.HasAnyProviderId != null && q.HasAnyProviderId.ContainsKey("Tmdb") && q.HasAnyProviderId["Tmdb"] == "42")))
            .Returns(new[] { (BaseItem)existing });

        var createdCount = 0;
        _lib.Setup(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()))
            .Callback(() => createdCount++);

        var s = Build();
        var n = await s.RefreshTrendingAsync(CancellationToken.None);
        Assert.Equal(0, n);
        Assert.Equal(0, createdCount);

        // phantom_items row created for the existing item id.
        var row = await _db.GetPhantomItemAsync(existingId, default);
        Assert.NotNull(row);
        Assert.Equal(42, row!.TmdbId);
    }

    [Fact]
    public async Task RefreshRecommendedForUser_NoFavourites_FallsBackToTrending()
    {
        var userId = Guid.NewGuid();
        var user = new User("u", "auth", "reset") { };
        // Set the id reflectively (User.Id has private setter via EF).
        typeof(User).GetProperty("Id")!.SetValue(user, userId);

        _users.Setup(u => u.GetUserById(userId)).Returns(user);

        // Favourites lookup returns empty.
        _lib.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q => q.IsFavorite == true)))
            .Returns(Array.Empty<BaseItem>());

        _tmdb.Setup(t => t.GetTrendingMoviesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Hit(99, "Trend") });
        _tmdb.Setup(t => t.GetTrendingSeriesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TmdbSearchHit>());

        var s = Build();
        var n = await s.RefreshRecommendedForUserAsync(userId, CancellationToken.None);
        Assert.Equal(1, n);
        _tmdb.Verify(t => t.GetTrendingMoviesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ----- M12: dedupe-gap heal-on-rediscovery -----

    [Fact]
    public async Task FindExistingByTmdbId_FallsBackToNameContains_WhenProviderMissing()
    {
        // The bug: legacy rows had their providers stripped by an
        // earlier persistence-layer interaction. HasAnyProviderId
        // dedupe misses them. The fix adds a NameContains fallback
        // for our sentinel.
        var legacyId = Guid.NewGuid();
        var legacy = new Movie
        {
            Name = "Some_Title__phantom_tmdb777",
            IsLocked = false,
        };
        legacy.Id = legacyId;
        // No ProviderIds populated.

        _tmdb.Setup(t => t.GetTrendingMoviesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Hit(777, "Some Title") });
        _tmdb.Setup(t => t.GetTrendingSeriesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TmdbSearchHit>());

        // Provider lookup returns nothing (the bug). Name-contains
        // lookup returns the legacy row (the fix).
        _lib.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.HasAnyProviderId != null && q.HasAnyProviderId.ContainsKey("Tmdb") && q.HasAnyProviderId["Tmdb"] == "777")))
            .Returns(Array.Empty<BaseItem>());
        _lib.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.NameContains == "__phantom_tmdb777")))
            .Returns(new[] { (BaseItem)legacy });

        var createdCount = 0;
        _lib.Setup(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()))
            .Callback(() => createdCount++);

        var s = Build();
        var n = await s.RefreshTrendingAsync(CancellationToken.None);

        // The legacy row was matched; no new row created.
        Assert.Equal(0, n);
        Assert.Equal(0, createdCount);
    }

    [Fact]
    public async Task HealBrokenPhantom_StampsNameLockProviders_AndCallsUpdateItemAsync()
    {
        // Dedupe-hit on a broken legacy row triggers a re-stamp via
        // UpdateItemAsync with corrected Name + IsLocked + ProviderIds.
        var legacyId = Guid.NewGuid();
        var legacy = new Movie
        {
            Name = "Some_Title__phantom_tmdb888",
            IsLocked = false,
        };
        legacy.Id = legacyId;

        _tmdb.Setup(t => t.GetTrendingMoviesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Hit(888, "Some Title") });
        _tmdb.Setup(t => t.GetTrendingSeriesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TmdbSearchHit>());

        _lib.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.HasAnyProviderId != null && q.HasAnyProviderId.ContainsKey("Tmdb") && q.HasAnyProviderId["Tmdb"] == "888")))
            .Returns(Array.Empty<BaseItem>());
        _lib.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.NameContains == "__phantom_tmdb888")))
            .Returns(new[] { (BaseItem)legacy });

        var s = Build();
        await s.RefreshTrendingAsync(CancellationToken.None);

        // The legacy row should have been mutated in place.
        Assert.Equal("Some Title", legacy.Name);
        Assert.True(legacy.IsLocked);
        Assert.Equal("888", legacy.ProviderIds["Tmdb"]);

        // UpdateItemAsync was called for the same item id.
        _lib.Verify(l => l.UpdateItemAsync(
            It.Is<BaseItem>(i => i.Id == legacyId && i.Name == "Some Title" && i.IsLocked),
            It.IsAny<BaseItem>(),
            It.IsAny<ItemUpdateType>(),
            It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    [Fact]
    public async Task FindExistingByTmdbId_NameContains_AvoidsPartialIdMatch()
    {
        // tmdb=12 must not match a row with Name containing
        // __phantom_tmdb12345. The fallback checks the next char
        // after the sentinel.
        var unrelatedId = Guid.NewGuid();
        var unrelated = new Movie { Name = "Other__phantom_tmdb12345" };
        unrelated.Id = unrelatedId;

        _tmdb.Setup(t => t.GetTrendingMoviesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Hit(12, "Target") });
        _tmdb.Setup(t => t.GetTrendingSeriesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TmdbSearchHit>());

        _lib.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.HasAnyProviderId != null && q.HasAnyProviderId.ContainsKey("Tmdb") && q.HasAnyProviderId["Tmdb"] == "12")))
            .Returns(Array.Empty<BaseItem>());
        _lib.Setup(l => l.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.NameContains == "__phantom_tmdb12")))
            .Returns(new[] { (BaseItem)unrelated });

        var createdCount = 0;
        _lib.Setup(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()))
            .Callback(() => createdCount++);

        var s = Build();
        await s.RefreshTrendingAsync(CancellationToken.None);

        // Substring rejected. New row CREATED for the real tmdb=12.
        Assert.Equal(1, createdCount);
    }
}
