using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Library;

public interface ISuggestionsContributor
{
    Task<int> RefreshTrendingAsync(CancellationToken ct);
    Task<int> RefreshCatalogueAsync(CancellationToken ct);
    Task<int> RefreshSimilarToAsync(Guid itemId, CancellationToken ct);
    Task<int> RefreshRecommendedForUserAsync(Guid userId, CancellationToken ct);
    Task<int> RefreshAllAsync(CancellationToken ct);
}

/// <summary>
/// Drives TMDB recommendation surfaces (Trending / Similar / per-user
/// Recommended) into the Jellyfin library as Virtual items. See PLAN §M6.
/// </summary>
public sealed class SuggestionsContributor : ISuggestionsContributor
{
    /// <summary>Maximum number of Virtual items materialised per type per Trending refresh.</summary>
    public const int TrendingCapPerType = 40;

    /// <summary>Maximum number of Virtual items materialised per Similar-To refresh.</summary>
    public const int SimilarCap = 20;

    /// <summary>Maximum total Virtual items materialised per user-recommended refresh.</summary>
    public const int UserRecommendedCap = 40;

    /// <summary>Per-user-recommended favourite fan-out: take last N favourited movies + N series.</summary>
    public const int FavouritesFanOutPerType = 5;

    /// <summary>Inter-page delay to stay under the TMDB 40-req/10s rate limit.</summary>
    private static readonly TimeSpan DiscoverPageDelay = TimeSpan.FromMilliseconds(300);

    private const string TrendingWindow = "week";
    private const string TmdbProvider = "Tmdb";

    private readonly CachedTmdbReader _reader;
    private readonly VirtualLibraryRoot _root;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly PhantomDb _db;
    private readonly IEagerHintSink _hintSink;
    private readonly IPhantomStubManager _stubs;
    private readonly ILogger<SuggestionsContributor> _logger;

    public SuggestionsContributor(
        CachedTmdbReader reader,
        VirtualLibraryRoot root,
        ILibraryManager libraryManager,
        IUserManager userManager,
        PhantomDb db,
        IEagerHintSink hintSink,
        IPhantomStubManager stubs,
        ILogger<SuggestionsContributor> logger)
    {
        _reader = reader;
        _root = root;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _db = db;
        _hintSink = hintSink;
        _stubs = stubs;
        _logger = logger;
    }

    public async Task<int> RefreshTrendingAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var created = 0;
        var cachedHits = 0;
        var skipped = 0;
        var cacheUsed = false;

        var movies = await SafeFetchAsync(
            () => _reader.TrendingMoviesAsync(TrendingWindow, null, ct), "Trending/Movies").ConfigureAwait(false);
        if (movies.FromCache) cacheUsed = true;
        var (createdM, skippedM) = await MaterialiseHitsAsync(
            movies.Hits, ItemKind.Movie, TrendingCapPerType, EagerHint.Trending, ct).ConfigureAwait(false);
        created += createdM;
        skipped += skippedM;
        if (movies.FromCache) cachedHits += movies.Hits.Count;

        var series = await SafeFetchAsync(
            () => _reader.TrendingSeriesAsync(TrendingWindow, null, ct), "Trending/Series").ConfigureAwait(false);
        if (series.FromCache) cacheUsed = true;
        var (createdS, skippedS) = await MaterialiseHitsAsync(
            series.Hits, ItemKind.Series, TrendingCapPerType, EagerHint.Trending, ct).ConfigureAwait(false);
        created += createdS;
        skipped += skippedS;
        if (series.FromCache) cachedHits += series.Hits.Count;

        _logger.LogInformation(
            "[Suggestions] Trending refresh: created={Created} cached={Cached} skipped={Skipped} duration={Ms}ms (cacheUsed={CacheUsed})",
            created, cachedHits, skipped, sw.ElapsedMilliseconds, cacheUsed);
        return created;
    }

    public async Task<int> RefreshSimilarToAsync(Guid itemId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var item = _libraryManager.GetItemById(itemId);
        if (item is null) return 0;

        if (!TryGetTmdbId(item, out var tmdbId)) return 0;

        ItemKind kind;
        (IReadOnlyList<TmdbSearchHit> Hits, bool FromCache) hits;
        if (item is Movie)
        {
            kind = ItemKind.Movie;
            hits = await SafeFetchAsync(
                () => _reader.SimilarMoviesAsync(tmdbId, null, ct), $"Similar/Movies/{tmdbId}").ConfigureAwait(false);
        }
        else if (item is Series)
        {
            kind = ItemKind.Series;
            hits = await SafeFetchAsync(
                () => _reader.SimilarSeriesAsync(tmdbId, null, ct), $"Similar/Series/{tmdbId}").ConfigureAwait(false);
        }
        else
        {
            return 0;
        }

        var isFavourite = IsAnyUsersFavourite(item);
        var hint = isFavourite ? EagerHint.SimilarToFavourite : EagerHint.None;
        var (created, skipped) = await MaterialiseHitsAsync(
            hits.Hits, kind, SimilarCap, hint, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "[Suggestions] SimilarTo({Id}) refresh: created={Created} cached={Cached} skipped={Skipped} duration={Ms}ms",
            itemId, created, hits.FromCache ? hits.Hits.Count : 0, skipped, sw.ElapsedMilliseconds);
        return created;
    }

    public async Task<int> RefreshRecommendedForUserAsync(Guid userId, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var user = _userManager.GetUserById(userId);
        if (user is null) return 0;

        var favMovies = GetFavouriteIds(user, BaseItemKind.Movie, FavouritesFanOutPerType);
        var favSeries = GetFavouriteIds(user, BaseItemKind.Series, FavouritesFanOutPerType);

        if (favMovies.Count == 0 && favSeries.Count == 0)
        {
            _logger.LogInformation(
                "[Suggestions] Recommended/User({User}): no favourites → falling back to Trending",
                userId);
            return await RefreshTrendingAsync(ct).ConfigureAwait(false);
        }

        var seenIds = new HashSet<int>();
        var dedupedMovies = new List<TmdbSearchHit>();
        var dedupedSeries = new List<TmdbSearchHit>();
        var anyCache = false;

        foreach (var favId in favMovies)
        {
            if (dedupedMovies.Count + dedupedSeries.Count >= UserRecommendedCap) break;
            var movie = _libraryManager.GetItemById(favId);
            if (movie is null || !TryGetTmdbId(movie, out var tmdbId)) continue;
            var hits = await SafeFetchAsync(
                () => _reader.MovieRecommendationsAsync(tmdbId, null, ct),
                $"Recommendations/Movies/{tmdbId}").ConfigureAwait(false);
            if (hits.FromCache) anyCache = true;
            foreach (var hit in hits.Hits)
            {
                if (seenIds.Add(hit.Id)) dedupedMovies.Add(hit);
                if (dedupedMovies.Count + dedupedSeries.Count >= UserRecommendedCap) break;
            }
        }

        foreach (var favId in favSeries)
        {
            if (dedupedMovies.Count + dedupedSeries.Count >= UserRecommendedCap) break;
            var series = _libraryManager.GetItemById(favId);
            if (series is null || !TryGetTmdbId(series, out var tmdbId)) continue;
            var hits = await SafeFetchAsync(
                () => _reader.SeriesRecommendationsAsync(tmdbId, null, ct),
                $"Recommendations/Series/{tmdbId}").ConfigureAwait(false);
            if (hits.FromCache) anyCache = true;
            foreach (var hit in hits.Hits)
            {
                if (seenIds.Add(hit.Id)) dedupedSeries.Add(hit);
                if (dedupedMovies.Count + dedupedSeries.Count >= UserRecommendedCap) break;
            }
        }

        var (createdM, skippedM) = await MaterialiseHitsAsync(
            dedupedMovies, ItemKind.Movie, int.MaxValue, EagerHint.UserRecommendation, ct).ConfigureAwait(false);
        var (createdS, skippedS) = await MaterialiseHitsAsync(
            dedupedSeries, ItemKind.Series, int.MaxValue, EagerHint.UserRecommendation, ct).ConfigureAwait(false);

        var created = createdM + createdS;
        var skipped = skippedM + skippedS;
        _logger.LogInformation(
            "[Suggestions] Recommended/User({User}) refresh: created={Created} cached={Cached} skipped={Skipped} duration={Ms}ms",
            userId, created, anyCache ? (dedupedMovies.Count + dedupedSeries.Count) : 0, skipped, sw.ElapsedMilliseconds);
        return created;
    }

    public async Task<int> RefreshAllAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var total = await RefreshTrendingAsync(ct).ConfigureAwait(false);

        foreach (var user in _userManager.GetUsers())
        {
            ct.ThrowIfCancellationRequested();
            if (IsUserDisabled(user)) continue;
            try
            {
                total += await RefreshRecommendedForUserAsync(user.Id, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[Suggestions] RefreshAll: per-user refresh for {User} failed; continuing",
                    user.Id);
            }
        }

        try
        {
            total += await RefreshCatalogueAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Suggestions] RefreshAll: catalogue walk failed; continuing");
        }

        _logger.LogInformation(
            "[Suggestions] RefreshAll: total created={Total} duration={Ms}ms",
            total, sw.ElapsedMilliseconds);
        return total;
    }

    /// <summary>
    /// Walks TMDB /discover/movie and /discover/tv page-by-page,
    /// materialising Virtual items until either an empty page is
    /// returned or the per-kind cap (floor(SuggestionsCatalogueMaxItems/2))
    /// is reached. See PLAN §M11 issue #1.
    /// </summary>
    public async Task<int> RefreshCatalogueAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var cfg = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();
        var perKindCap = Math.Max(0, cfg.SuggestionsCatalogueMaxItems / 2);
        var totalCreated = 0;

        totalCreated += await WalkDiscoverAsync(
            ItemKind.Movie, perKindCap, ct).ConfigureAwait(false);
        totalCreated += await WalkDiscoverAsync(
            ItemKind.Series, perKindCap, ct).ConfigureAwait(false);

        _logger.LogInformation(
            "[Suggestions] Catalogue refresh: created={Created} perKindCap={Cap} duration={Ms}ms",
            totalCreated, perKindCap, sw.ElapsedMilliseconds);
        return totalCreated;
    }

    private async Task<int> WalkDiscoverAsync(ItemKind kind, int cap, CancellationToken ct)
    {
        if (cap <= 0) return 0;
        var created = 0;
        var page = 1;
        while (created < cap)
        {
            ct.ThrowIfCancellationRequested();
            var label = $"Discover/{kind}/p{page}";
            var fetch = kind == ItemKind.Movie
                ? await SafeFetchAsync(() => _reader.GetDiscoverMoviesAsync(page, null, ct), label).ConfigureAwait(false)
                : await SafeFetchAsync(() => _reader.GetDiscoverSeriesAsync(page, null, ct), label).ConfigureAwait(false);

            if (fetch.Hits.Count == 0)
            {
                _logger.LogInformation(
                    "[Suggestions] Catalogue page {Page} for {Kind}: empty page, stopping walk",
                    page, kind);
                break;
            }

            var remaining = cap - created;
            var (c, s) = await MaterialiseHitsAsync(
                fetch.Hits, kind, remaining, EagerHint.None, ct).ConfigureAwait(false);
            created += c;

            _logger.LogInformation(
                "[Suggestions] Catalogue page {Page} for {Kind}: created={N} skipped={K}",
                page, kind, c, s);

            page++;
            if (created < cap)
            {
                try
                {
                    await Task.Delay(DiscoverPageDelay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
            }
        }

        return created;
    }

    private async Task<(IReadOnlyList<TmdbSearchHit> Hits, bool FromCache)> SafeFetchAsync(
        Func<Task<(IReadOnlyList<TmdbSearchHit> Hits, bool FromCache)>> fetch, string label)
    {
        try
        {
            return await fetch().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Suggestions] {Label} fetch failed; returning empty surface", label);
            return (Array.Empty<TmdbSearchHit>(), false);
        }
    }

    private async Task<(int Created, int Skipped)> MaterialiseHitsAsync(
        IReadOnlyList<TmdbSearchHit> hits, ItemKind kind, int cap, EagerHint hint, CancellationToken ct)
    {
        var created = 0;
        var skipped = 0;
        var parent = kind == ItemKind.Movie ? _root.ResolveMoviesParent() : _root.ResolveSeriesParent();
        if (parent is null)
        {
            _logger.LogWarning(
                "[Suggestions] no parent Folder resolved for {Kind}; cannot materialise {Count} hits",
                kind, hits.Count);
            return (0, hits.Count);
        }

        var takeCount = Math.Min(cap, hits.Count);
        for (var i = 0; i < takeCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            var hit = hits[i];
            if (hit.Id <= 0) { skipped++; continue; }

            var tmdbIdStr = hit.Id.ToString(CultureInfo.InvariantCulture);
            var existing = FindExistingByTmdbId(tmdbIdStr, kind);
            if (existing is not null)
            {
                // Duplicate — do not create. Upsert phantom_items row + bump last_touched.
                await UpsertPhantomRowAsync(existing.Id, hit.Id, kind, PhantomItemState.Virtual, ct).ConfigureAwait(false);

                // Heal if broken: detect rows that have lost their
                // Name / IsLocked / ProviderIds (legacy from M10/M11
                // era when the persistence layer or scanner clobbered
                // them). The dedupe-hit branch is the only place we
                // re-encounter these rows; if we don't heal here, they
                // stay broken forever because nothing else re-touches
                // them. Per PLAN M12 + docs/plans/M12-investigation-
                // results.md.
                var nameIsStem = PhantomPathUtilities.IsPhantomStubPath(existing.Name);
                var hasTmdbProvider = existing.ProviderIds.ContainsKey(TmdbProvider);
                // A materialised item points at a real gostream file
                // (not a phantom sentinel) and is no longer locked.
                // Heal targets virtual-stage broken rows; do not
                // "heal" a materialised row — that would clobber its
                // real Path and the scanner would cull the row.
                var isMaterialised = !string.IsNullOrEmpty(existing.Path)
                    && !PhantomPathUtilities.IsPhantomStubPath(existing.Path);
                if (!isMaterialised && (nameIsStem || !existing.IsLocked || !hasTmdbProvider))
                {
                    await HealBrokenPhantomAsync(existing, hit, kind, parent, ct).ConfigureAwait(false);
                }
                skipped++;
                continue;
            }

            BaseItem newItem = kind == ItemKind.Movie
                ? VirtualItemFactory.CreateVirtualMovieFromHit(hit)
                : VirtualItemFactory.CreateVirtualSeriesFromHit(hit);

            // Stable id from name+type so we don't churn ids across restarts;
            // matches Jellyfin's own newItem-id derivation conventions.
            newItem.Id = _libraryManager.GetNewItemId(
                $"phantom_{(kind == ItemKind.Movie ? "movie" : "series")}_{hit.Id}",
                newItem.GetType());

            // Attach phantom stub symlink + lock so the scanner cannot
            // rename us via TMDB fuzzy match. See PLAN §M10.
            if (_stubs.IsReady)
            {
                try
                {
                    var stubKind = kind == ItemKind.Movie ? PhantomMediaKind.Movie : PhantomMediaKind.Series;
                    var stubPath = await _stubs.CreateAsync(newItem.Name ?? string.Empty, newItem.ProductionYear, hit.Id, stubKind, ct).ConfigureAwait(false);
                    newItem.Path = stubPath;
                    newItem.IsLocked = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[Suggestions] stub create failed for tmdb={Tmdb}; item will be path-less Virtual",
                        hit.Id);
                }
            }

            if (hint != EagerHint.None)
            {
                _hintSink.RegisterHint(newItem.Id, hint);
            }

            try
            {
                // 10.11: ILibraryManager.CreateItem(item, parent) does NOT wire ParentId for
                // Path-less Virtual items. SetParent + CreateItem ensures the in-memory parent
                // pointer is set BEFORE persistence; CreateItem then writes the ParentId column.
                if (parent is Folder parentFolder)
                {
                    newItem.SetParent(parentFolder);
                }
                _libraryManager.CreateItem(newItem, parent);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[Suggestions] CreateItem failed for tmdb={Tmdb} ({Name}); skipping",
                    hit.Id, hit.Title);
                _hintSink.ConsumeHint(newItem.Id); // discard stale hint
                skipped++;
                continue;
            }

            // Re-stamp Name / IsLocked / ProviderIds / images after CreateItem.
            // CreateItem triggers a scanner pass that can resolve the stub path
            // back to a TMDB fuzzy match and clobber Name + ProviderIds with
            // garbage. UpdateItemAsync flushes the in-memory values back to disk.
            // IsLocked is set unconditionally (even without a stub path) so
            // metadata providers will not overwrite our title.
            newItem.IsLocked = true;
            try
            {
                await _libraryManager.UpdateItemAsync(
                    newItem, parent, ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[Suggestions] UpdateItemAsync re-stamp failed for tmdb={Tmdb} ({Name}); row created but may show scanner-clobbered metadata",
                    hit.Id, hit.Title);
            }

            await UpsertPhantomRowAsync(newItem.Id, hit.Id, kind, PhantomItemState.Virtual, ct).ConfigureAwait(false);
            created++;
        }

        if (hits.Count > takeCount) skipped += hits.Count - takeCount;
        return (created, skipped);
    }

    private BaseItem? FindExistingByTmdbId(string tmdbId, ItemKind kind)
    {
        try
        {
            // First pass: standard provider-based lookup. Catches rows
            // we created where the Tmdb provider survived persistence.
            var byProvider = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { kind == ItemKind.Movie ? BaseItemKind.Movie : BaseItemKind.Series },
                HasAnyProviderId = new Dictionary<string, string> { [TmdbProvider] = tmdbId },
                Limit = 1,
            });
            if (byProvider.Count > 0)
            {
                return byProvider[0];
            }

            // Second pass: legacy broken-row recovery. If an earlier
            // run stripped the row's providers (M11-era + earlier),
            // the row is invisible to the provider-based query above.
            // Match against the Name (which the scanner fell back to
            // from the filename stem) for our sentinel
            // `__phantom_tmdb<id>`. This is unique to our plugin and
            // cannot collide with real media filenames.
            var sentinel = "__phantom_tmdb" + tmdbId;
            var byName = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { kind == ItemKind.Movie ? BaseItemKind.Movie : BaseItemKind.Series },
                NameContains = sentinel,
                Limit = 2,
            });
            // NameContains is substring; be defensive about partial
            // overlap (e.g. tmdb=1234 should NOT match tmdb=12345).
            // Require the sentinel be followed by a non-digit or end
            // of string.
            foreach (var candidate in byName)
            {
                var name = candidate.Name ?? string.Empty;
                var idx = name.IndexOf(sentinel, StringComparison.Ordinal);
                if (idx < 0)
                {
                    continue;
                }
                var after = idx + sentinel.Length;
                if (after >= name.Length || !char.IsDigit(name[after]))
                {
                    return candidate;
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Suggestions] duplicate-lookup query failed for tmdb={Tmdb}", tmdbId);
            return null;
        }
    }

    /// <summary>
    /// Repairs a legacy phantom BaseItem whose Name / IsLocked /
    /// ProviderIds got stripped by an earlier persistence-layer
    /// or scanner interaction. Restamps from the live TMDB hit
    /// data, then commits via UpdateItemAsync. Also tries to
    /// re-create the phantom stub symlink if it's missing on disk.
    /// Called from the dedupe-hit branch in MaterialiseHitsAsync.
    /// </summary>
    private async Task HealBrokenPhantomAsync(
        BaseItem existing, TmdbSearchHit hit, ItemKind kind, BaseItem parent, CancellationToken ct)
    {
        try
        {
            // Rebuild fresh metadata from the hit (factory output).
            BaseItem template = kind == ItemKind.Movie
                ? VirtualItemFactory.CreateVirtualMovieFromHit(hit)
                : VirtualItemFactory.CreateVirtualSeriesFromHit(hit);

            // Mutate the existing item in place so we keep its Id
            // (and therefore its UserData associations).
            existing.Name = template.Name;
            existing.OriginalTitle = template.OriginalTitle;
            existing.Overview = template.Overview;
            existing.ProductionYear = template.ProductionYear;
            existing.PremiereDate = template.PremiereDate;
            existing.Genres = template.Genres;
            existing.CommunityRating = template.CommunityRating;
            existing.ForcedSortName = template.ForcedSortName;
            existing.PresentationUniqueKey = template.PresentationUniqueKey;
            existing.ProviderIds[TmdbProvider] = hit.Id.ToString(CultureInfo.InvariantCulture);
            if (template.ImageInfos is { Length: > 0 })
            {
                existing.ImageInfos = template.ImageInfos;
            }
            existing.IsLocked = true;

            // Re-create the stub symlink if needed. CreateAsync is
            // idempotent — returns the existing path if already there.
            // Only reset Path back to the stub for items that are NOT
            // already materialised. Once an item points at a real
            // gostream file (post-Materialiser), heal must not yank
            // it back to the phantom symlink — that re-virtualises a
            // materialised row, then the scanner culls it on the
            // next folder validation. Detect materialised by
            // "Path is not empty AND not a phantom sentinel".
            //
            // PLAN §M13: virtual Series rows now have Path =
            // <shows>/<SafeName>__phantom_tmdb<id>/ — the per-series
            // directory. The leaf still carries the `__phantom_tmdb`
            // sentinel, so the substring check below correctly
            // classifies the row as "phantom" (i.e. NOT materialised)
            // and heal proceeds.
            var alreadyMaterialised = !string.IsNullOrEmpty(existing.Path)
                && !PhantomPathUtilities.IsPhantomStubPath(existing.Path);
            if (_stubs.IsReady && !alreadyMaterialised)
            {
                try
                {
                    var stubKind = kind == ItemKind.Movie ? PhantomMediaKind.Movie : PhantomMediaKind.Series;
                    var stubPath = await _stubs.CreateAsync(existing.Name ?? string.Empty, existing.ProductionYear, hit.Id, stubKind, ct).ConfigureAwait(false);
                    if (string.IsNullOrEmpty(existing.Path))
                    {
                        existing.Path = stubPath;
                    }
                }
                catch (Exception stubEx)
                {
                    _logger.LogDebug(stubEx,
                        "[Suggestions] heal: stub create failed for tmdb={Tmdb}; proceeding with metadata-only heal",
                        hit.Id);
                }
            }

            await _libraryManager.UpdateItemAsync(
                existing, parent, ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);

            _logger.LogInformation(
                "[Suggestions] healed broken phantom row tmdb={Tmdb} ({Title}) item={ItemId}",
                hit.Id, hit.Title, existing.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[Suggestions] heal failed for tmdb={Tmdb} item={ItemId}; row remains broken",
                hit.Id, existing.Id);
        }
    }

    private async Task UpsertPhantomRowAsync(Guid jellyfinId, int tmdbId, ItemKind kind, PhantomItemState state, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var existing = await _db.GetPhantomItemAsync(jellyfinId, ct).ConfigureAwait(false);
        await _db.UpsertPhantomItemAsync(jellyfinId, new PhantomItemRow
        {
            TmdbId = tmdbId,
            ImdbId = existing?.ImdbId,
            Type = kind == ItemKind.Movie ? "movie" : "series",
            State = existing?.State ?? state,
            FirstSeen = existing?.FirstSeen ?? now,
            LastTouched = now,
            EvictionProtected = existing?.EvictionProtected ?? false,
            OriginalOverview = existing?.OriginalOverview,
        }, ct).ConfigureAwait(false);
    }

    private IReadOnlyList<Guid> GetFavouriteIds(
        Jellyfin.Database.Implementations.Entities.User user, BaseItemKind kind, int take)
    {
        try
        {
            var q = new InternalItemsQuery(user)
            {
                IncludeItemTypes = new[] { kind },
                IsFavorite = true,
                Limit = take,
                OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
                Recursive = true,
            };
            var items = _libraryManager.GetItemList(q);
            return items.Select(i => i.Id).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "[Suggestions] favourites lookup for user {User} kind {Kind} failed",
                user.Id, kind);
            return Array.Empty<Guid>();
        }
    }

    private bool IsAnyUsersFavourite(BaseItem item)
    {
        try
        {
            foreach (var user in _userManager.GetUsers())
            {
                var q = new InternalItemsQuery(user)
                {
                    ItemIds = new[] { item.Id },
                    IsFavorite = true,
                    Limit = 1,
                };
                if (_libraryManager.GetItemList(q).Count > 0) return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Suggestions] favourite-of-any check failed for {Id}", item.Id);
        }

        return false;
    }

    private static bool TryGetTmdbId(BaseItem item, out int tmdbId)
    {
        tmdbId = 0;
        if (item.ProviderIds is null) return false;
        if (!item.ProviderIds.TryGetValue(TmdbProvider, out var raw)) return false;
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out tmdbId);
    }

    private static bool IsUserDisabled(Jellyfin.Database.Implementations.Entities.User user)
    {
        foreach (var p in user.Permissions)
        {
            if (p.Kind == PermissionKind.IsDisabled) return p.Value;
        }

        return false;
    }

    private enum ItemKind
    {
        Movie,
        Series,
    }
}
