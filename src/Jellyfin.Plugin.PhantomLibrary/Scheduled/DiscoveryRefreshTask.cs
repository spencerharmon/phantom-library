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
using Jellyfin.Plugin.PhantomLibrary.Diagnostics;
using Jellyfin.Plugin.PhantomLibrary.Library;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Scheduled;

/// <summary>
/// Periodic scheduled task that feeds the append-only Phantom catalogue
/// from TMDB trending and Discover.
///
/// On each tick:
///   1. Pull /trending/movie/week and /trending/tv/week via
///      <see cref="CachedTmdbReader"/>.
///   2. Walk /discover/movie and /discover/tv from persisted per-kind
///      cursors, bounded by <see cref="PluginConfiguration.DiscoverPagesPerRun"/>
///      so a post-wipe cold start does not stampede Jellyfin.
///   3. Insert new catalogue rows and missing metadata only; rediscovery
///      does not rewrite metadata, prune catalogue rows, or bump channel
///      DataVersions.
///   4. Enqueue new movies for availability probing and new series for
///      bounded series expansion. Channel visibility changes only after
///      availability/materialised state changes.
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

        var configuredMovieCap = (catalogueMaxItems / 2) + (catalogueMaxItems % 2);
        var configuredSeriesCap = catalogueMaxItems / 2;
        var existingMovies = await _db.CountCatalogueItemsAsync("movie", SourceDiscover, cancellationToken).ConfigureAwait(false);
        var existingSeries = await _db.CountCatalogueItemsAsync("series", SourceDiscover, cancellationToken).ConfigureAwait(false);
        var discoverMovieCap = Math.Max(0, configuredMovieCap - existingMovies);
        var discoverSeriesCap = Math.Max(0, configuredSeriesCap - existingSeries);
        var discoverPagesPerRun = config?.DiscoverPagesPerRun ?? 50;
        if (discoverPagesPerRun < 0)
        {
            discoverPagesPerRun = 50;
        }

        var discoverPageDelay = TimeSpan.FromMilliseconds(Math.Max(0, config?.DiscoverPageDelayMilliseconds ?? 100));

        var totalSeen = 0;
        var totalInserted = 0;
        var totalAvailabilityInserted = 0;
        var totalSeriesExpansionInserted = 0;

        using var discoveryTimer = PhantomMetrics.TimeDiscoveryRun();

        // --- Phase 1: trending ----------------------------------------------
        progress.Report(0);
        try
        {
            var (movies, fromCacheM) = await _tmdb.TrendingMoviesAsync("week", language, cancellationToken).ConfigureAwait(false);
            var write = await UpsertHitsAsync(movies, "movie", SourceTrending, cancellationToken).ConfigureAwait(false);
            PhantomMetrics.DiscoveryRows("movie", write.Seen, write.Inserted, write.AvailabilityInserted, write.SeriesExpansionInserted);
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
            PhantomMetrics.DiscoveryRows("series", write.Seen, write.Inserted, write.AvailabilityInserted, write.SeriesExpansionInserted);
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
            pagesPerRun: discoverPagesPerRun,
            pageDelay: discoverPageDelay,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        progress.Report(37.5);

        await WalkDiscoverAsync(
            kind: "series",
            maxItems: discoverSeriesCap,
            fetchPage: (page, ct) => _tmdb.GetDiscoverSeriesAsync(page, language, ct),
            counters: result => { totalSeen += result.Seen; totalInserted += result.Inserted; totalAvailabilityInserted += result.AvailabilityInserted; totalSeriesExpansionInserted += result.SeriesExpansionInserted; },
            pagesPerRun: discoverPagesPerRun,
            pageDelay: discoverPageDelay,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        progress.Report(45);

        // --- Phase 3: favourite-similar enrichment --------------------------
        // Favourite → TMDB similar/recommendations ingestion is NOT driven from
        // this periodic task. It is event-driven: UserDataSavedListener reacts to
        // a user favouriting a movie/series and calls
        // IFavouriteRecommendationIngestor, which fans out via
        // CachedTmdbReader.SimilarMoviesAsync / MovieRecommendationsAsync (and the
        // series equivalents) and upserts the hits into the append-only catalogue
        // under SourceFavouriteRecommendation. That reacts to the actual favourite
        // signal at the moment it happens rather than re-scanning every user's
        // favourites on a timer, and reuses the same 24h similar/recommendations
        // cache this task's trending/discover phases populate. See
        // FavouriteRecommendationIngestor and REQ-M14-RECOMMENDATIONS.

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
        int pagesPerRun,
        TimeSpan pageDelay,
        CancellationToken cancellationToken)
    {
        if (maxItems <= 0)
        {
            _logger.LogInformation("Discover {Kind}: disabled by catalogue cap", kind);
            return;
        }

        var cursorKey = $"discovery.cursor.{kind}";
        var offsetKey = $"discovery.cursor.{kind}.offset";
        var cursorText = await _db.GetMetaAsync(cursorKey, cancellationToken).ConfigureAwait(false);
        var offsetText = await _db.GetMetaAsync(offsetKey, cancellationToken).ConfigureAwait(false);
        var page = int.TryParse(cursorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedCursor)
            ? Math.Clamp(parsedCursor, 1, TmdbMaxDiscoverPage)
            : 1;
        var offset = int.TryParse(offsetText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedOffset)
            ? Math.Max(0, parsedOffset)
            : 0;
        var processed = 0;
        var inserted = 0;
        var cachedPages = 0;
        var pages = 0;
        var stopAfterPages = pagesPerRun == 0 ? int.MaxValue : pagesPerRun;
        while (processed < maxItems && page <= TmdbMaxDiscoverPage && pages < stopAfterPages)
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

            PhantomMetrics.DiscoveryPage(kind, fromCache);
            if (fromCache)
            {
                cachedPages++;
            }

            if (hits.Count == 0)
            {
                _logger.LogInformation("Discover {Kind}: page {Page} returned no hits; resetting cursor", kind, page);
                page = 1;
                offset = 0;
                await PersistDiscoverCursorAsync(cursorKey, offsetKey, page, offset, cancellationToken).ConfigureAwait(false);
                PhantomMetrics.DiscoveryCursor(kind, page);
                break;
            }

            if (offset >= hits.Count)
            {
                offset = 0;
            }

            var remaining = Math.Max(0, maxItems - processed);
            var pageHits = hits.Skip(offset).Take(remaining).ToList();
            var write = await UpsertHitsAsync(pageHits, kind, SourceDiscover, cancellationToken).ConfigureAwait(false);
            PhantomMetrics.DiscoveryRows(kind, write.Seen, write.Inserted, write.AvailabilityInserted, write.SeriesExpansionInserted);
            counters(write);
            inserted += write.Inserted;
            processed += pageHits.Count;
            offset += pageHits.Count;
            pages++;
            var reachedTmdbPageLimit = false;
            if (offset >= hits.Count)
            {
                page++;
                offset = 0;
                if (page > TmdbMaxDiscoverPage)
                {
                    page = 1;
                    reachedTmdbPageLimit = true;
                }
            }

            await PersistDiscoverCursorAsync(cursorKey, offsetKey, page, offset, cancellationToken).ConfigureAwait(false);
            PhantomMetrics.DiscoveryCursor(kind, page);
            if (reachedTmdbPageLimit)
            {
                break;
            }
            if (pageDelay > TimeSpan.Zero && processed < maxItems && pages < stopAfterPages)
            {
                await Task.Delay(pageDelay, cancellationToken).ConfigureAwait(false);
            }
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
            "Discover {Kind}: processed={Processed} inserted={Inserted} pages={Pages} cachedPages={CachedPages} cap={Cap} nextPage={NextPage} nextOffset={NextOffset} pagesPerRun={PagesPerRun}",
            kind,
            processed,
            inserted,
            pages,
            cachedPages,
            maxItems,
            page,
            offset,
            pagesPerRun);
    }

    private async Task PersistDiscoverCursorAsync(string cursorKey, string offsetKey, int page, int offset, CancellationToken ct)
    {
        await _db.SetMetaAsync(cursorKey, page.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        await _db.SetMetaAsync(offsetKey, offset.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
    }

    private async Task<CatalogueHitWriteResult> UpsertHitsAsync(IReadOnlyList<TmdbSearchHit> hits, string type, int sourceMask, CancellationToken ct)
    {
        var rows = new List<TmdbMetadataRow>(hits.Count);
        foreach (var hit in hits)
        {
            var row = TmdbHitMapper.MapSearchHitToMetadata(hit, type);
            if (string.IsNullOrWhiteSpace(row.Title))
            {
                _logger.LogDebug("Skipping discovery hit {Type}:{Tmdb} because TMDB returned no title", type, hit.Id);
                continue;
            }

            rows.Add(row);
        }

        return await _db.UpsertCatalogueHitsAsync(rows, sourceMask, DateTimeOffset.UtcNow, ct).ConfigureAwait(false);
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
                Year: TmdbHitMapper.ParseYear(details.ReleaseDate),
                Overview: details.Overview,
                PosterUrl: TmdbHitMapper.BuildImageUrl(details.PosterPath),
                BackdropUrl: TmdbHitMapper.BuildImageUrl(details.BackdropPath),
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
                Year: TmdbHitMapper.ParseYear(details.FirstAirDate),
                Overview: details.Overview,
                PosterUrl: TmdbHitMapper.BuildImageUrl(details.PosterPath),
                BackdropUrl: TmdbHitMapper.BuildImageUrl(details.BackdropPath),
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
}
