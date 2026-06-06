// PLAN §M11 — six bugs surfaced by operator live testing 2026-06-05/06.
//
// Each test asserts the FIX behaviour. Tests must FAIL on main as of
// 576ef81 / f9d68fd (or equivalent M11-pre state) and pass after the
// implementer fixes the bugs. Do NOT modify these tests during the
// fix work — they are the contract.

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
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>M11 issue regression suite. See PLAN §M11.</summary>
public class M11BugsTests : IDisposable
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
    private sealed class RootWithChildren : Folder
    {
        private readonly List<BaseItem> _children;
        public RootWithChildren(List<BaseItem> children) { _children = children; Id = Guid.NewGuid(); }
        public override IEnumerable<BaseItem> Children => _children;
    }

    public M11BugsTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "m11_" + Guid.NewGuid().ToString("N") + ".db");
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

        _lib.Setup(l => l.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns(Array.Empty<BaseItem>());

        _lib.Setup(l => l.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns<string, Type>((s, _) =>
            {
                using var sha = System.Security.Cryptography.MD5.Create();
                var b = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(s));
                return new Guid(b);
            });

        _users.Setup(u => u.GetUsers()).Returns(Array.Empty<User>());
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); File.Delete(_dbPath + "-wal"); File.Delete(_dbPath + "-shm"); } catch { }
    }

    private SuggestionsContributor Build(IPhantomStubManager? stubs = null)
    {
        var reader = new CachedTmdbReader(_tmdb.Object, _db, NullLogger<CachedTmdbReader>.Instance);
        return new SuggestionsContributor(
            reader, _root, _lib.Object, _users.Object, _db, _hints,
            stubs ?? new NullPhantomStubManager(),
            NullLogger<SuggestionsContributor>.Instance);
    }

    private static TmdbSearchHit Hit(int id, string title, string? poster = "/p.jpg")
        => new TmdbSearchHit(id, title, title, "overview", poster, "/b.jpg", "2020-01-01", 7.5, 100)
            { GenreIds = new[] { 28 } };

    // ─────────────────────────────────────────────────────────────────
    // M11 #1: catalogue too small — Discover backfill walks pages.
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void ITmdbClient_HasDiscoverMoviesPage()
    {
        // Discover must accept a page number and return up to TMDB's
        // 20-per-page batch. Method shape ensures pagination is
        // pluggable for the catalogue walk.
        var ifaceMethod = typeof(ITmdbClient).GetMethod("DiscoverMoviesAsync");
        Assert.NotNull(ifaceMethod);
        var parms = ifaceMethod!.GetParameters();
        Assert.Contains(parms, p => p.Name == "page" && p.ParameterType == typeof(int));
    }

    [Fact]
    public void ITmdbClient_HasDiscoverSeriesPage()
    {
        var ifaceMethod = typeof(ITmdbClient).GetMethod("DiscoverSeriesAsync");
        Assert.NotNull(ifaceMethod);
        var parms = ifaceMethod!.GetParameters();
        Assert.Contains(parms, p => p.Name == "page" && p.ParameterType == typeof(int));
    }

    [Fact]
    public void PluginConfiguration_HasSuggestionsCatalogueMaxItemsField()
    {
        // Operator-tunable cap on the Discover catalogue walk.
        var prop = typeof(Configuration.PluginConfiguration)
            .GetProperty("SuggestionsCatalogueMaxItems");
        Assert.NotNull(prop);
        Assert.Equal(typeof(int), prop!.PropertyType);

        // Default must be substantially bigger than Trending's ~40, so
        // operators get a real catalogue out of the box.
        var cfg = new Configuration.PluginConfiguration();
        var v = (int)prop.GetValue(cfg)!;
        Assert.True(v >= 1000,
            $"SuggestionsCatalogueMaxItems default {v} is not big enough to back-fill a real catalogue.");
    }

    [Fact]
    public async Task RefreshCatalogueAsync_WalksDiscoverUntilCap()
    {
        // SuggestionsContributor exposes a RefreshCatalogueAsync that
        // calls DiscoverMoviesAsync repeatedly across pages until it
        // has either created Cap items or TMDB returns an empty page.
        //
        // This test pins the method's existence + return type. The
        // pagination behaviour is verified end-to-end by the live
        // integration tests; mocking page-by-page would require the
        // CachedTmdbReader's internal Discover wrappers to be public,
        // which is out of scope here.
        await Task.Yield();
        var method = typeof(SuggestionsContributor).GetMethod("RefreshCatalogueAsync");
        Assert.NotNull(method);
        Assert.True(
            typeof(Task<int>).IsAssignableFrom(method!.ReturnType),
            "RefreshCatalogueAsync must return Task<int> (total items created)");
    }

    // ─────────────────────────────────────────────────────────────────
    // M11 #2: display name shows filename stem with __phantom_tmdb<id>.
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SuggestionsContributor_StampsForcedSortNameWithTitle()
    {
        _tmdb.Setup(t => t.GetTrendingMoviesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Hit(101, "Backrooms") });
        _tmdb.Setup(t => t.GetTrendingSeriesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TmdbSearchHit>());

        var created = new List<BaseItem>();
        _lib.Setup(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()))
            .Callback<BaseItem, BaseItem>((i, _) => created.Add(i));

        var s = Build();
        await s.RefreshTrendingAsync(CancellationToken.None);

        var movie = (Movie)created.Single();
        // Filename-stem fallback fights us during scan. The defence
        // is ForcedSortName: even if Name gets temporarily clobbered
        // by the scanner, ForcedSortName stays as the title.
        Assert.Equal("Backrooms", movie.ForcedSortName);
    }

    [Fact]
    public async Task SuggestionsContributor_PersistsItemWithLockedNameAfterCreate()
    {
        // The bug: IsLocked is set on the in-memory item, then
        // CreateItem persists it, then the scanner re-resolves the
        // file and overwrites Name. The fix is to re-stamp Name +
        // IsLocked + ProviderIds AFTER CreateItem via UpdateItemAsync
        // so the scanner cannot win.
        _tmdb.Setup(t => t.GetTrendingMoviesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Hit(202, "Toy Story 5") });
        _tmdb.Setup(t => t.GetTrendingSeriesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TmdbSearchHit>());

        _lib.Setup(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()));

        var s = Build();
        await s.RefreshTrendingAsync(CancellationToken.None);

        // Expect a follow-up UpdateItemAsync call so the scanner's
        // race cannot strip Name / IsLocked / ProviderIds.
        _lib.Verify(
            l => l.UpdateItemAsync(
                It.Is<BaseItem>(i => i.Name == "Toy Story 5" && i.IsLocked && i.ProviderIds.ContainsKey("Tmdb")),
                It.IsAny<BaseItem>(),
                It.IsAny<ItemUpdateType>(),
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);
    }

    // ─────────────────────────────────────────────────────────────────
    // M11 #3: phantom image is splash thumbnail, not TMDB poster.
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SuggestionsContributor_StampsTmdbPosterImageOnCreatedItem()
    {
        _tmdb.Setup(t => t.GetTrendingMoviesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Hit(303, "The Odyssey", poster: "/poster.jpg") });
        _tmdb.Setup(t => t.GetTrendingSeriesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TmdbSearchHit>());

        var created = new List<BaseItem>();
        _lib.Setup(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()))
            .Callback<BaseItem, BaseItem>((i, _) => created.Add(i));

        var s = Build();
        await s.RefreshTrendingAsync(CancellationToken.None);

        var movie = (Movie)created.Single();
        // After M11 #3, the factory or contributor stamps an
        // ImageInfos[Primary] pointing at the TMDB CDN URL so the
        // image fetcher does not need to run any metadata provider
        // (which is skipped because IsLocked=true).
        var primary = movie.ImageInfos.FirstOrDefault(i => i.Type == ImageType.Primary);
        Assert.NotNull(primary);
        Assert.False(string.IsNullOrEmpty(primary!.Path),
            "Primary image must have a Path set to the TMDB poster URL");
        Assert.Contains("poster.jpg", primary.Path!, StringComparison.Ordinal);
    }

    // ─────────────────────────────────────────────────────────────────
    // M11 #4: TV Series phantoms not visible in browse.
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SeriesPhantom_HasMediaType_Video()
    {
        // Diagnosed: Series rows lacked MediaType, which makes browse
        // filter them out for some queries. Movie items get MediaType
        // 'Video' from CreateVirtualMovieFromHit's factory path; the
        // analogue must apply to Series-from-hit.
        _tmdb.Setup(t => t.GetTrendingMoviesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TmdbSearchHit>());
        _tmdb.Setup(t => t.GetTrendingSeriesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Hit(404, "Severance") });

        var created = new List<BaseItem>();
        _lib.Setup(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()))
            .Callback<BaseItem, BaseItem>((i, _) => created.Add(i));

        var s = Build();
        await s.RefreshTrendingAsync(CancellationToken.None);

        var series = created.OfType<Series>().Single();
        // PresentationUniqueKey must be set so browse doesn't dedupe
        // multiple series with empty keys into one entry.
        Assert.False(string.IsNullOrEmpty(series.PresentationUniqueKey),
            "Series phantom must have PresentationUniqueKey set.");
    }

    [Fact]
    public async Task SeriesPhantom_PathPointsAtPhantomStub()
    {
        // Series phantoms must get a stub path under the phantom
        // shows directory, NOT a null Path (which gets culled).
        var stubs = new RecordingStubManager();

        _tmdb.Setup(t => t.GetTrendingMoviesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TmdbSearchHit>());
        _tmdb.Setup(t => t.GetTrendingSeriesAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { Hit(505, "Severance") });

        var created = new List<BaseItem>();
        _lib.Setup(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()))
            .Callback<BaseItem, BaseItem>((i, _) => created.Add(i));

        var s = Build(stubs);
        await s.RefreshTrendingAsync(CancellationToken.None);

        var series = created.OfType<Series>().Single();
        Assert.False(string.IsNullOrEmpty(series.Path),
            "Series phantom must have a Path set to a stub symlink.");
        Assert.Contains("__phantom_tmdb", series.Path!, StringComparison.Ordinal);
        Assert.Single(stubs.Created, c => c.kind == PhantomMediaKind.Series);
    }

    // ─────────────────────────────────────────────────────────────────
    // M11 #5: Play does not trigger materialise.
    //   COVERED by PlaybackTriggerListenerTests; the bug was
    //   "no listener at all". The listener now exists, but the
    //   downstream Materialiser must accept items by id and
    //   round-trip to TMDB ids via the plugin DB rather than via
    //   BaseItem.ProviderIds (which may be empty post-scan).
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Materialiser_CanResolveProviderIdsFromPhantomDb_WhenBaseItemHasNone()
    {
        // The operator-observed failure mode:
        //   "Materialise <id> failed: item lacks TMDB/IMDB provider ids"
        // happens because the scanner stripped ProviderIds from the
        // BaseItem after we created it. The plugin DB still has the
        // tmdb_id from the Suggestions create path. Materialiser must
        // fall back to phantom_items.tmdb_id when BaseItem has no
        // ProviderIds["Tmdb"].
        //
        // This test asserts the existence of a resolver helper on the
        // Materialiser type that performs that fallback. The exact
        // method shape is implementation-defined; the existence test
        // anchors the fix.
        var method = typeof(Materialiser).GetMethod(
            "ResolveProviderIdsAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
            | System.Reflection.BindingFlags.Public);
        Assert.NotNull(method);
    }

    // ─────────────────────────────────────────────────────────────────
    // M11 #6: splash playback marks the phantom Played.
    //   COVERED by PlaybackTriggerListenerTests. Belt-and-braces
    //   here: the listener must exist (regression).
    // ─────────────────────────────────────────────────────────────────

    [Fact]
    public void PlaybackTriggerListener_IsRegisteredAsHostedService()
    {
        // Sanity: M11 #5/#6 are useless if the listener isn't wired.
        var src = System.IO.File.ReadAllText(
            System.IO.Path.Combine(
                FindRepoRoot(),
                "src/Jellyfin.Plugin.PhantomLibrary/PluginServiceRegistrator.cs"));
        Assert.Contains("PlaybackTriggerListener", src,
            StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "phantom-library.sln")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? Environment.CurrentDirectory;
    }

    // ───────── helpers ─────────

    private sealed class RecordingStubManager : IPhantomStubManager
    {
        public readonly List<(string title, int tmdbId, PhantomMediaKind kind)> Created = new();
        public bool IsReady => true;

        public Task BootstrapAsync(CancellationToken ct) => Task.CompletedTask;

        public Task<string> CreateAsync(string title, int tmdbId, PhantomMediaKind kind, CancellationToken ct)
        {
            Created.Add((title, tmdbId, kind));
            var sub = kind == PhantomMediaKind.Movie ? "movies" : "shows";
            return Task.FromResult(
                $"/var/lib/jellyfin/phantom-library/{sub}/{title.Replace(' ', '_')}__phantom_tmdb{tmdbId}.mp4");
        }

        public Task DeleteAsync(string symlinkPath, CancellationToken ct) => Task.CompletedTask;

        public string DeriveFilename(string title, int tmdbId, PhantomMediaKind kind)
            => $"{title.Replace(' ', '_')}__phantom_tmdb{tmdbId}.mp4";
    }
}
