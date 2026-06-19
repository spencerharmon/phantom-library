using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Library;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Scheduled;

/// <summary>
/// Periodic scheduled task that populates the channel-arch
/// discovery_cache + tmdb_metadata tables.
///
/// On each tick:
///   1. Pull /trending/movie/week and /trending/tv/week via
///      <see cref="CachedTmdbReader"/> (which round-trips through the
///      tmdb_cache table for cheap idempotency).
///   2. Walk /discover/movie and /discover/tv up to the configured
///      <see cref="PluginConfiguration.SuggestionsCatalogueMaxItems"/>
///      cap so the channel surface is catalogue-sized, not just trending.
///   3. Upsert TMDB hit metadata into <c>tmdb_metadata</c>, then upsert
///      each (tmdb_id, type) into <c>discovery_cache</c>. Metadata is
///      written before discovery rows so concurrent channel refreshes never
///      see cold rows and incorrectly sweep existing channel items.
///   4. Stale-prune discovery_cache rows older than
///      <see cref="PluginConfiguration.DiscoveryCacheTtlDays"/>, but
///      preserve rows that have a matching materialised_state row
///      (we want to keep the discovery surface alive for items the
///      operator has already bothered to materialise).
///   5. Bump the movies + shows channel DataVersion so the next browse
///      sees the new contents.
///
/// Replaces the deleted M11-era SuggestionsRefreshTask. Same role,
/// channel-arch shape.
/// </summary>
public sealed class DiscoveryRefreshTask : IScheduledTask
{
    /// <summary>Stable key used by Dashboard scheduled-tasks endpoints.</summary>
    public const string TaskKey = "PhantomLibrary.DiscoveryRefresh";

    private const int DefaultDiscoveryRefreshIntervalHours = 6;
    private const int DefaultCatalogueMaxItems = 5000;
    private const int TmdbMaxDiscoverPage = 500;
    private const int SourceTrending = 1;
    private const int SourceDiscover = 2;

    private readonly CachedTmdbReader _tmdb;
    private readonly ITmdbClient _tmdbClient;
    private readonly PhantomDb _db;
    private readonly ILogger<DiscoveryRefreshTask> _logger;
    private readonly Func<PluginConfiguration?> _configurationProvider;

    public DiscoveryRefreshTask(
        CachedTmdbReader tmdb,
        ITmdbClient tmdbClient,
        PhantomDb db,
        ChannelStateProvider state,
        ILogger<DiscoveryRefreshTask> logger)
        : this(tmdb, tmdbClient, db, state, logger, () => Plugin.Instance?.Configuration)
    {
    }

    internal DiscoveryRefreshTask(
        CachedTmdbReader tmdb,
        ITmdbClient tmdbClient,
        PhantomDb db,
        ChannelStateProvider state,
        ILogger<DiscoveryRefreshTask> logger,
        Func<PluginConfiguration?> configurationProvider)
    {
        _tmdb = tmdb;
        _tmdbClient = tmdbClient;
        _db = db;
        _ = state;
        _logger = logger;
        ArgumentNullException.ThrowIfNull(configurationProvider);
        _configurationProvider = configurationProvider;
    }

    /// <inheritdoc />
    public string Name => "Phantom Library — Refresh Discovery";

    /// <inheritdoc />
    public string Key => TaskKey;

    /// <inheritdoc />
    public string Description =>
        "Refreshes the phantom-channel discovery cache from TMDB trending and Discover.";

    /// <inheritdoc />
    public string Category => "Phantom Library";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var hours = _configurationProvider()?.DiscoveryRefreshIntervalHours ?? DefaultDiscoveryRefreshIntervalHours;
        if (hours <= 0)
        {
            hours = DefaultDiscoveryRefreshIntervalHours;
        }

        return new[]
        {
            new TaskTriggerInfo
            {
                Type = TaskTriggerInfoType.IntervalTrigger,
                IntervalTicks = TimeSpan.FromHours(hours).Ticks,
            },
        };
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var config = _configurationProvider();
        var language = NormaliseLanguage(config?.DiscoveryLanguage);
        var catalogueMaxItems = config?.SuggestionsCatalogueMaxItems ?? DefaultCatalogueMaxItems;
        if (catalogueMaxItems < 0)
        {
            catalogueMaxItems = 0;
        }

        var discoverMovieCap = (catalogueMaxItems / 2) + (catalogueMaxItems % 2);
        var discoverSeriesCap = catalogueMaxItems / 2;

        var totalSeen = 0;
        var totalInserted = 0;
        var totalAvailabilityInserted = 0;
        var totalSeriesExpansionInserted = 0;

        // --- Phase 1: trending ----------------------------------------------
        progress.Report(0);
        try
        {
            var (movies, fromCacheM) = await _tmdb.TrendingMoviesAsync("week", language, cancellationToken).ConfigureAwait(false);
            var write = await UpsertHitsAsync(movies, "movie", SourceTrending, cancellationToken).ConfigureAwait(false);
            totalSeen += write.Seen;
            totalInserted += write.Inserted;
            totalAvailabilityInserted += write.AvailabilityInserted;
            totalSeriesExpansionInserted += write.SeriesExpansionInserted;

            _logger.LogInformation("Trending movies: {Count} hits (cached={Cached}) inserted={Inserted}", movies.Count, fromCacheM, write.Inserted);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Trending movies fetch failed; continuing");
        }

        progress.Report(15);
        try
        {
            var (series, fromCacheS) = await _tmdb.TrendingSeriesAsync("week", language, cancellationToken).ConfigureAwait(false);
            var write = await UpsertHitsAsync(series, "series", SourceTrending, cancellationToken).ConfigureAwait(false);
            totalSeen += write.Seen;
            totalInserted += write.Inserted;
            totalAvailabilityInserted += write.AvailabilityInserted;
            totalSeriesExpansionInserted += write.SeriesExpansionInserted;

            _logger.LogInformation("Trending series: {Count} hits (cached={Cached}) inserted={Inserted}", series.Count, fromCacheS, write.Inserted);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Trending series fetch failed; continuing");
        }

        progress.Report(30);

        // --- Phase 2: paginated TMDB Discover catalogue walk ----------------
        await WalkDiscoverAsync(
            kind: "movie",
            maxItems: discoverMovieCap,
            fetchPage: (page, ct) => _tmdb.GetDiscoverMoviesAsync(page, language, ct),
            counters: result => { totalSeen += result.Seen; totalInserted += result.Inserted; totalAvailabilityInserted += result.AvailabilityInserted; totalSeriesExpansionInserted += result.SeriesExpansionInserted; },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        progress.Report(37.5);

        await WalkDiscoverAsync(
            kind: "series",
            maxItems: discoverSeriesCap,
            fetchPage: (page, ct) => _tmdb.GetDiscoverSeriesAsync(page, language, ct),
            counters: result => { totalSeen += result.Seen; totalInserted += result.Inserted; totalAvailabilityInserted += result.AvailabilityInserted; totalSeriesExpansionInserted += result.SeriesExpansionInserted; },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        progress.Report(45);

        // --- Phase 3: favourite-similar enrichment --------------------------
        // TODO(stage-future): wire favourite-similar enrichment per plan §3.1.
        // Needs IUserManager + ILibraryManager.GetItemList(IsFavorite=true,
        // user=...) joined against ProviderIds["Tmdb"], then
        // SimilarMoviesAsync / SimilarSeriesAsync per favourited id, with
        // the results upserted into discovery_cache. Deferred for v0.3.0:
        // TMDB Discover now supplies the bulk catalogue surface, and wiring
        // the ILibraryManager / favourite query without producing BaseItem-load
        // churn deserves its own design pass. To re-enable, inject IUserManager
        // + ILibraryManager here and walk users → favourites →
        // SimilarMoviesAsync/SimilarSeriesAsync
        // → UpsertDiscoveryCacheAsync.

        progress.Report(50);

        _logger.LogInformation(
            "Discovery refresh complete. Seen={Seen} NewCatalogue={New} NewAvailability={Avail} NewSeriesExpansion={SeriesExpansion}. Channel DataVersion not bumped until availability visibility changes.",
            totalSeen,
            totalInserted,
            totalAvailabilityInserted,
            totalSeriesExpansionInserted);
        progress.Report(100);
    }

    private async Task WalkDiscoverAsync(
        string kind,
        int maxItems,
        Func<int, CancellationToken, Task<(IReadOnlyList<TmdbSearchHit> Hits, bool FromCache)>> fetchPage,
        Action<CatalogueHitWriteResult> counters,
        CancellationToken cancellationToken)
    {
        if (maxItems <= 0)
        {
            _logger.LogInformation("Discover {Kind}: disabled by catalogue cap", kind);
            return;
        }

        var page = 1;
        var processed = 0;
        var inserted = 0;
        var cachedPages = 0;
        while (processed < maxItems && page <= TmdbMaxDiscoverPage)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<TmdbSearchHit> hits;
            bool fromCache;
            try
            {
                (hits, fromCache) = await fetchPage(page, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Discover {Kind}: page {Page} fetch failed; stopping discover walk for this kind", kind, page);
                break;
            }

            if (fromCache)
            {
                cachedPages++;
            }

            if (hits.Count == 0)
            {
                _logger.LogInformation("Discover {Kind}: page {Page} returned no hits; stopping", kind, page);
                break;
            }

            var pageHits = hits.Take(Math.Max(0, maxItems - processed)).ToList();
            var write = await UpsertHitsAsync(pageHits, kind, SourceDiscover, cancellationToken).ConfigureAwait(false);
            counters(write);
            inserted += write.Inserted;
            processed += pageHits.Count;
            page++;
        }

        if (page > TmdbMaxDiscoverPage && processed < maxItems)
        {
            _logger.LogInformation(
                "Discover {Kind}: reached TMDB max page {MaxPage}; stopping before configured cap {Cap}",
                kind,
                TmdbMaxDiscoverPage,
                maxItems);
        }

        _logger.LogInformation(
            "Discover {Kind}: processed={Processed} inserted={Inserted} pages={Pages} cachedPages={CachedPages} cap={Cap}",
            kind,
            processed,
            inserted,
            page - 1,
            cachedPages,
            maxItems);
    }

    private async Task<CatalogueHitWriteResult> UpsertHitsAsync(IReadOnlyList<TmdbSearchHit> hits, string type, int sourceMask, CancellationToken ct)
    {
        var rows = new List<TmdbMetadataRow>(hits.Count);
        foreach (var hit in hits)
        {
            var row = MapSearchHitToMetadata(hit, type);
            if (string.IsNullOrWhiteSpace(row.Title))
            {
                _logger.LogDebug("Skipping discovery hit {Type}:{Tmdb} because TMDB returned no title", type, hit.Id);
                continue;
            }

            rows.Add(row);
        }

        return await _db.UpsertCatalogueHitsAsync(rows, sourceMask, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
    }

    private static TmdbMetadataRow MapSearchHitToMetadata(TmdbSearchHit hit, string type)
    {
        var title = !string.IsNullOrWhiteSpace(hit.Title) ? hit.Title! : (hit.OriginalTitle ?? string.Empty);
        return new TmdbMetadataRow(
            TmdbId: hit.Id,
            Type: type,
            Title: title,
            Year: ParseYear(hit.ReleaseDate),
            Overview: hit.Overview,
            PosterUrl: BuildImageUrl(hit.PosterPath),
            BackdropUrl: BuildImageUrl(hit.BackdropPath),
            Genres: null,
            OfficialRating: null,
            CommunityRating: hit.VoteAverage,
            OriginalTitle: hit.OriginalTitle,
            FetchedAt: DateTimeOffset.UtcNow);
    }

    private async Task WarmMetadataAsync(int tmdb, string type, string? language, CancellationToken ct)
    {
        if (type == "movie")
        {
            var details = await _tmdbClient.GetMovieAsync(tmdb, language, ct).ConfigureAwait(false);
            if (details is null)
            {
                return;
            }

            var row = new TmdbMetadataRow(
                TmdbId: details.Id,
                Type: "movie",
                Title: !string.IsNullOrWhiteSpace(details.Title) ? details.Title!
                       : (details.OriginalTitle ?? string.Empty),
                Year: ParseYear(details.ReleaseDate),
                Overview: details.Overview,
                PosterUrl: BuildImageUrl(details.PosterPath),
                BackdropUrl: BuildImageUrl(details.BackdropPath),
                Genres: details.Genres is { Length: > 0 } ? details.Genres : null,
                OfficialRating: null, // TMDB /movie/{id} does not expose certifications without /release_dates.
                CommunityRating: details.VoteAverage,
                OriginalTitle: details.OriginalTitle,
                FetchedAt: DateTimeOffset.UtcNow);

            if (string.IsNullOrWhiteSpace(row.Title))
            {
                _logger.LogDebug("Skipping warm for tmdb={Tmdb} (no title)", tmdb);
                return;
            }

            await _db.UpsertTmdbMetadataAsync(row, ct).ConfigureAwait(false);
        }
        else if (type == "series")
        {
            var details = await _tmdbClient.GetSeriesAsync(tmdb, language, ct).ConfigureAwait(false);
            if (details is null)
            {
                return;
            }

            var row = new TmdbMetadataRow(
                TmdbId: details.Id,
                Type: "series",
                Title: !string.IsNullOrWhiteSpace(details.Name) ? details.Name : (details.OriginalName ?? string.Empty),
                Year: ParseYear(details.FirstAirDate),
                Overview: details.Overview,
                PosterUrl: BuildImageUrl(details.PosterPath),
                BackdropUrl: BuildImageUrl(details.BackdropPath),
                Genres: details.Genres is { Length: > 0 } ? details.Genres : null,
                OfficialRating: null,
                CommunityRating: details.VoteAverage,
                OriginalTitle: details.OriginalName,
                FetchedAt: DateTimeOffset.UtcNow);

            if (string.IsNullOrWhiteSpace(row.Title))
            {
                _logger.LogDebug("Skipping warm for series tmdb={Tmdb} (no name)", tmdb);
                return;
            }

            await _db.UpsertTmdbMetadataAsync(row, ct).ConfigureAwait(false);
        }
    }

    private static string? NormaliseLanguage(string? raw)
    {
        return string.IsNullOrWhiteSpace(raw) ? null : raw;
    }

    private static int? ParseYear(string? releaseDate)
    {
        if (string.IsNullOrWhiteSpace(releaseDate) || releaseDate.Length < 4)
        {
            return null;
        }

        if (int.TryParse(releaseDate.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
        {
            return y;
        }

        return null;
    }

    private static string? BuildImageUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // TMDB CDN convention; w500 is the standard size used by the
        // Jellyfin TMDB metadata provider for posters / backdrops. We
        // bypass /configuration since the URL is stable in practice;
        // the CachedTmdbReader path already absorbs the API calls.
        var prefixed = path.StartsWith('/') ? path : "/" + path;
        return "https://image.tmdb.org/t/p/w500" + prefixed;
    }
}
