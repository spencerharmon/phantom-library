using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
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
///   2. Upsert each (tmdb_id, type) into <c>discovery_cache</c>.
///   3. Warm <c>tmdb_metadata</c> for every (tmdb_id, type) just
///      discovered, so the channel browse pipeline can synthesise
///      ChannelItemInfos without TMDB on the hot path. (Plan §3.1
///      IMPORTANT 4 fix.)
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

    private readonly CachedTmdbReader _tmdb;
    private readonly ITmdbClient _tmdbClient;
    private readonly PhantomDb _db;
    private readonly ChannelStateProvider _state;
    private readonly ILogger<DiscoveryRefreshTask> _logger;

    public DiscoveryRefreshTask(
        CachedTmdbReader tmdb,
        ITmdbClient tmdbClient,
        PhantomDb db,
        ChannelStateProvider state,
        ILogger<DiscoveryRefreshTask> logger)
    {
        _tmdb = tmdb;
        _tmdbClient = tmdbClient;
        _db = db;
        _state = state;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "Phantom Library — Refresh Discovery";

    /// <inheritdoc />
    public string Key => TaskKey;

    /// <inheritdoc />
    public string Description =>
        "Refreshes the phantom-channel discovery cache from TMDB trending + favourite-similar.";

    /// <inheritdoc />
    public string Category => "Phantom Library";

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        var hours = Plugin.Instance?.Configuration.DiscoveryRefreshIntervalHours ?? 6;
        if (hours <= 0)
        {
            hours = 6;
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
        var language = NormaliseLanguage(Plugin.Instance?.Configuration.DiscoveryLanguage);
        var ttlDays = Plugin.Instance?.Configuration.DiscoveryCacheTtlDays ?? 30;
        if (ttlDays <= 0)
        {
            ttlDays = 30;
        }

        var discovered = new HashSet<(int Tmdb, string Type)>();

        // --- Phase 1: trending ----------------------------------------------
        progress.Report(0);
        try
        {
            var (movies, fromCacheM) = await _tmdb.TrendingMoviesAsync("week", language, cancellationToken).ConfigureAwait(false);
            foreach (var hit in movies)
            {
                await _db.UpsertDiscoveryCacheAsync(hit.Id, "movie", cancellationToken).ConfigureAwait(false);
                discovered.Add((hit.Id, "movie"));
            }

            _logger.LogInformation("Trending movies: {Count} hits (cached={Cached})", movies.Count, fromCacheM);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Trending movies fetch failed; continuing");
        }

        progress.Report(15);
        try
        {
            var (series, fromCacheS) = await _tmdb.TrendingSeriesAsync("week", language, cancellationToken).ConfigureAwait(false);
            foreach (var hit in series)
            {
                await _db.UpsertDiscoveryCacheAsync(hit.Id, "series", cancellationToken).ConfigureAwait(false);
                discovered.Add((hit.Id, "series"));
            }

            _logger.LogInformation("Trending series: {Count} hits (cached={Cached})", series.Count, fromCacheS);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Trending series fetch failed; continuing");
        }

        progress.Report(30);

        // --- Phase 2: favourite-similar enrichment --------------------------
        // TODO(stage-future): wire favourite-similar enrichment per plan §3.1.
        // Needs IUserManager + ILibraryManager.GetItemList(IsFavorite=true,
        // user=...) joined against ProviderIds["Tmdb"], then
        // SimilarMoviesAsync / SimilarSeriesAsync per favourited id, with
        // the results upserted into discovery_cache. Deferred for v0.3.0:
        // trending alone is sufficient to demonstrate the channel pipeline
        // end-to-end, and wiring the ILibraryManager / favourite query
        // without producing BaseItem-load churn deserves its own design
        // pass. To re-enable, inject IUserManager + ILibraryManager here
        // and walk users → favourites → SimilarMoviesAsync/SimilarSeriesAsync
        // → UpsertDiscoveryCacheAsync.

        progress.Report(45);

        // --- Phase 3: warm tmdb_metadata for everything just discovered ----
        var warmed = 0;
        var warmFailed = 0;
        var idx = 0;
        foreach (var (tmdb, type) in discovered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await WarmMetadataAsync(tmdb, type, language, cancellationToken).ConfigureAwait(false);
                warmed++;
            }
            catch (Exception ex)
            {
                warmFailed++;
                _logger.LogDebug(ex, "tmdb_metadata warm failed for {Type}:{Tmdb}", type, tmdb);
            }

            idx++;
            if (discovered.Count > 0)
            {
                progress.Report(45 + (idx * 40.0 / discovered.Count));
            }
        }

        _logger.LogInformation("tmdb_metadata warm: {Warmed} ok, {Failed} failed", warmed, warmFailed);

        // --- Phase 4: TTL eviction with materialise protection --------------
        progress.Report(90);
        var cutoff = DateTimeOffset.UtcNow.Subtract(TimeSpan.FromDays(ttlDays));
        var totalEvicted = 0;
        foreach (var type in new[] { "movie", "series" })
        {
            // Materialised rows use type='movie' (movies channel) and
            // type='episode' (per-episode rows in series). For discovery
            // protection we treat 'series' protected if any episode of the
            // same series tmdb has been materialised — but episode rows are
            // keyed on the *episode* tmdb id, not series tmdb id. The
            // plumbing for series-level materialised summary isn't in place
            // yet (Stage 5.2); for now we use a conservative rule: protect
            // movie discovery rows that have a materialised_state movie row.
            // Series rows fall through to pure TTL pruning, which is
            // acceptable for v0.3.0 (worst case: re-discovered next tick).
            var protectedTmdbs = new HashSet<int>();
            if (type == "movie")
            {
                var materialised = await _db.ListMaterialisedStateAsync("movie", cancellationToken).ConfigureAwait(false);
                foreach (var m in materialised)
                {
                    protectedTmdbs.Add(m.TmdbId);
                }
            }

            // TODO(stage-future): plan §3.1 also wants favourite-protection
            // on top of materialise-protection. Same blocker as the
            // enrichment pass above (needs ILibraryManager favourite
            // lookup). Deferred.

            var rows = await _db.ListDiscoveryCacheAsync(type, cancellationToken).ConfigureAwait(false);
            var stale = rows.Where(r => r.LastRefreshed < cutoff && !protectedTmdbs.Contains(r.TmdbId)).ToList();
            foreach (var s in stale)
            {
                await _db.DeleteDiscoveryCacheRowAsync(s.TmdbId, type, cancellationToken).ConfigureAwait(false);
            }

            totalEvicted += stale.Count;
            _logger.LogInformation(
                "TTL evict: kind={Kind} candidates={Rows} stale={Stale} evicted={Evicted}",
                type,
                rows.Count,
                stale.Count,
                stale.Count);
        }

        _logger.LogInformation("Discovery refresh complete. Discovered={Disc} EvictedTotal={Ev}",
            discovered.Count, totalEvicted);

        // --- Phase 5: bump DataVersions so channels re-query ---------------
        _state.BumpDataVersion(ChannelStateProvider.KindMovies);
        _state.BumpDataVersion(ChannelStateProvider.KindShows);
        progress.Report(100);
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
