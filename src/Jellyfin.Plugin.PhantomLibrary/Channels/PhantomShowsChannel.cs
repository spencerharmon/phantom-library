using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Channels;

/// <summary>
/// "Phantom Shows" channel — hierarchical Series → Season → Episode
/// folder navigation backed by:
///   1. discovery_cache (type="series") + materialised_state (type
///      ="episode", projected to distinct series tmdb_ids) for the
///      top-level series listing,
///   2. ITmdbClient.GetSeriesAsync for season counts,
///   3. ITmdbClient.GetSeasonAsync + tmdb_episode_cache for the
///      per-episode display metadata,
///   4. materialised_state for FUSE-path resolution vs splash.
///
/// Same single-id-per-logical-item discipline as PhantomMoviesChannel
/// (plan §3.3 + critic round 3 BLOCKER 1): series_&lt;tmdb&gt; folders,
/// season_&lt;tmdb&gt;_s&lt;NN&gt; folders, episode_&lt;tmdb&gt;_s&lt;NN&gt;e&lt;NN&gt;
/// media items, stable across the phantom → materialised transition so
/// UserData survives.
///
/// Stage 5.1 implementation per <c>docs/plans/channel-handoff.md</c>.
/// </summary>
public sealed class PhantomShowsChannel
    : IChannel, IRequiresMediaInfoCallback, ISupportsLatestMedia, IChannelItemRefresh
{
    private readonly PhantomDb _db;
    private readonly ITmdbClient _tmdb;
    private readonly SplashSourceProvider _splashSource;
    private readonly ChannelStateProvider _state;
    private readonly ILogger<PhantomShowsChannel> _logger;
    private readonly Func<string?> _languageProvider;

    public PhantomShowsChannel(
        PhantomDb db,
        ITmdbClient tmdb,
        SplashSourceProvider splashSource,
        ChannelStateProvider state,
        ILogger<PhantomShowsChannel> logger)
        : this(db, tmdb, splashSource, state, logger,
               () => Plugin.Instance?.Configuration?.DiscoveryLanguage)
    {
    }

    internal PhantomShowsChannel(
        PhantomDb db,
        ITmdbClient tmdb,
        SplashSourceProvider splashSource,
        ChannelStateProvider state,
        ILogger<PhantomShowsChannel> logger,
        Func<string?> languageProvider)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tmdb = tmdb ?? throw new ArgumentNullException(nameof(tmdb));
        _splashSource = splashSource ?? throw new ArgumentNullException(nameof(splashSource));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _languageProvider = languageProvider ?? throw new ArgumentNullException(nameof(languageProvider));
    }

    /// <inheritdoc />
    public string Name => ChannelIds.ShowsName;

    /// <inheritdoc />
    public string Description => "Phantom Library — TV discovery + on-demand materialise via gostream.";

    /// <inheritdoc />
    public string DataVersion => _state.DataVersion(ChannelStateProvider.KindShows);

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Episode },
            MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
        };
    }

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => true;

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (string.IsNullOrEmpty(query.FolderId))
        {
            return await GetTopLevelSeriesAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!ChannelItemId.TryParse(query.FolderId, out var parsed))
        {
            return EmptyResult();
        }

        return parsed.Kind switch
        {
            ChannelItemId.KindSeries =>
                await GetSeasonsForSeriesAsync(parsed.TmdbId!.Value, cancellationToken).ConfigureAwait(false),
            ChannelItemId.KindSeason =>
                await GetEpisodesForSeasonAsync(parsed.TmdbId!.Value, parsed.Season!.Value, cancellationToken).ConfigureAwait(false),
            _ => EmptyResult(),
        };
    }

    /// <inheritdoc />
    public async Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (!ChannelItemId.TryParse(id, out var parsed))
        {
            return Array.Empty<MediaSourceInfo>();
        }

        if (parsed.Kind != ChannelItemId.KindEpisode)
        {
            // Series/season folders carry no playable MediaSource; any
            // other kind is foreign to this channel.
            return Array.Empty<MediaSourceInfo>();
        }

        var state = await _db.GetMaterialisedStateAsync(
            parsed.TmdbId!.Value, "episode", parsed.Season!.Value, parsed.Episode!.Value,
            cancellationToken).ConfigureAwait(false);

        return state is not null
            ? new[] { FuseMediaSource(state.FusePath) }
            : new[] { _splashSource.CreateMediaSource() };
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ChannelItemInfo>> GetLatestMedia(ChannelLatestMediaSearch request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        const int defaultLimit = 20;

        var materialised = await _db.ListMaterialisedStateAsync("episode", cancellationToken).ConfigureAwait(false);
        var items = new List<ChannelItemInfo>(Math.Min(materialised.Count, defaultLimit));
        foreach (var row in materialised.OrderByDescending(r => r.MaterialisedAt).Take(defaultLimit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var built = await BuildEpisodeItemAsync(row.TmdbId, row.Season, row.Episode, row, cancellationToken).ConfigureAwait(false);
            if (built is not null)
            {
                items.Add(built);
            }
        }

        return items;
    }

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        return Task.FromResult(new DynamicImageResponse { HasImage = false });
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => Array.Empty<ImageType>();

    /// <inheritdoc />
    public async Task<ChannelItemInfo> GetChannelItemAsync(string channelItemExternalId, CancellationToken cancellationToken)
    {
        // Critic round 3 IMPORTANT 5 fix: explicit per-kind branching so
        // the patched RefreshChannelItemAsync post-flight refresh
        // resolves the exact external id the materialiser asked about,
        // instead of falling back to root paging (which only emits
        // series-level folders). Without this branch, an episode's
        // post-flight refresh silently no-ops, BaseItem.Path stays at
        // splash, and the user plays splash post-materialise.
        if (!ChannelItemId.TryParse(channelItemExternalId, out var parsed))
        {
            return null!;
        }

        switch (parsed.Kind)
        {
            case ChannelItemId.KindSeries:
                {
                    var item = await BuildSeriesItemAsync(parsed.TmdbId!.Value, cancellationToken).ConfigureAwait(false);
                    return item ?? null!;
                }

            case ChannelItemId.KindSeason:
                {
                    var item = await BuildSeasonItemAsync(parsed.TmdbId!.Value, parsed.Season!.Value, cancellationToken).ConfigureAwait(false);
                    return item ?? null!;
                }

            case ChannelItemId.KindEpisode:
                {
                    var materialised = await _db.GetMaterialisedStateAsync(
                        parsed.TmdbId!.Value, "episode", parsed.Season!.Value, parsed.Episode!.Value,
                        cancellationToken).ConfigureAwait(false);
                    var item = await BuildEpisodeItemAsync(
                        parsed.TmdbId.Value, parsed.Season.Value, parsed.Episode.Value,
                        materialised, cancellationToken).ConfigureAwait(false);
                    return item ?? null!;
                }

            default:
                return null!;
        }
    }

    // ----------------------------------------------------------------
    // Browse paths
    // ----------------------------------------------------------------

    private async Task<ChannelItemResult> GetTopLevelSeriesAsync(CancellationToken ct)
    {
        var seen = new HashSet<int>();
        var items = new List<ChannelItemInfo>();

        // 1. discovery_cache (type='series')
        var discovery = await _db.ListDiscoveryCacheAsync("series", ct).ConfigureAwait(false);
        foreach (var row in discovery)
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(row.TmdbId))
            {
                continue;
            }

            var built = await BuildSeriesItemAsync(row.TmdbId, ct).ConfigureAwait(false);
            if (built is not null)
            {
                items.Add(built);
            }
        }

        // 2. materialised_state (type='episode') projected to distinct series tmdb_ids.
        //    A series with at least one materialised episode but no discovery row
        //    still surfaces as a top-level tile so the user can navigate to the
        //    real file (e.g. autopilot pre-materialised something the user
        //    favourited from an external nav).
        var materialised = await _db.ListMaterialisedStateAsync("episode", ct).ConfigureAwait(false);
        foreach (var row in materialised)
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(row.TmdbId))
            {
                continue;
            }

            var built = await BuildSeriesItemAsync(row.TmdbId, ct).ConfigureAwait(false);
            if (built is not null)
            {
                items.Add(built);
            }
        }

        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count,
        };
    }

    private async Task<ChannelItemResult> GetSeasonsForSeriesAsync(int seriesTmdb, CancellationToken ct)
    {
        // Need NumberOfSeasons. Skip the folder silently if TMDB doesn't
        // resolve — better than emitting a partial / lying listing.
        var details = await SafeGetSeriesAsync(seriesTmdb, ct).ConfigureAwait(false);
        if (details is null || details.NumberOfSeasons <= 0)
        {
            return EmptyResult();
        }

        var items = new List<ChannelItemInfo>(details.NumberOfSeasons);
        for (var n = 1; n <= details.NumberOfSeasons; n++)
        {
            ct.ThrowIfCancellationRequested();
            var built = await BuildSeasonItemAsync(seriesTmdb, n, ct).ConfigureAwait(false);
            if (built is not null)
            {
                items.Add(built);
            }
        }

        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count,
        };
    }

    private async Task<ChannelItemResult> GetEpisodesForSeasonAsync(int seriesTmdb, int season, CancellationToken ct)
    {
        // Always re-fetch the season from TMDB on browse so newly aired
        // episodes appear; upsert each into tmdb_episode_cache so the
        // refresh path (BuildEpisodeItemAsync via IChannelItemRefresh)
        // can read them back without another TMDB hit.
        var seasonDetails = await SafeGetSeasonAsync(seriesTmdb, season, ct).ConfigureAwait(false);
        if (seasonDetails is null || seasonDetails.Episodes.Count == 0)
        {
            return EmptyResult();
        }

        var seriesMeta = await _db.GetTmdbMetadataAsync(seriesTmdb, "series", ct).ConfigureAwait(false);
        var seriesName = seriesMeta?.Title;

        // Upsert all episodes into tmdb_episode_cache and collect the
        // canonical row shape for use below.
        var rows = new List<TmdbEpisodeRow>(seasonDetails.Episodes.Count);
        foreach (var e in seasonDetails.Episodes)
        {
            if (e.EpisodeNumber <= 0)
            {
                continue;
            }

            var title = string.IsNullOrWhiteSpace(e.Name)
                ? $"Episode {e.EpisodeNumber}"
                : e.Name;
            var row = new TmdbEpisodeRow(
                SeriesTmdbId: seriesTmdb,
                Season: season,
                Episode: e.EpisodeNumber,
                Title: title,
                Overview: e.Overview,
                StillUrl: BuildImageUrl(e.StillPath),
                AirDate: e.AirDate,
                RuntimeMinutes: e.Runtime,
                FetchedAt: DateTimeOffset.UtcNow);
            await _db.UpsertTmdbEpisodeAsync(row, ct).ConfigureAwait(false);
            rows.Add(row);
        }

        var items = new List<ChannelItemInfo>(rows.Count);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var materialised = await _db.GetMaterialisedStateAsync(
                seriesTmdb, "episode", season, row.Episode, ct).ConfigureAwait(false);
            items.Add(BuildEpisodeItemFromRow(row, materialised, seriesName));
        }

        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count,
        };
    }

    // ----------------------------------------------------------------
    // Per-item builders (shared between browse + refresh paths)
    // ----------------------------------------------------------------

    private async Task<ChannelItemInfo?> BuildSeriesItemAsync(int seriesTmdb, CancellationToken ct)
    {
        var meta = await _db.GetTmdbMetadataAsync(seriesTmdb, "series", ct).ConfigureAwait(false);
        if (meta is null)
        {
            // Cold-cache miss; DiscoveryRefreshTask warms next tick.
            _logger.LogDebug("Skipping series tmdb={Tmdb} (no metadata row yet)", seriesTmdb);
            return null;
        }

        var id = ChannelItemId.ForSeries(seriesTmdb).Encode();
        var item = new ChannelItemInfo
        {
            Id = id,
            Name = meta.Title,
            OriginalTitle = meta.OriginalTitle,
            Overview = meta.Overview,
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Series,
            ImageUrl = meta.PosterUrl,
            ProductionYear = meta.Year,
            PremiereDate = meta.Year is { } y ? new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null,
            CommunityRating = meta.CommunityRating is { } cr ? (float)cr : null,
            OfficialRating = meta.OfficialRating,
            Tags = new List<string>(),
        };

        if (meta.Genres is { Length: > 0 })
        {
            item.Genres = meta.Genres.ToList();
        }

        item.ProviderIds["Tmdb"] = seriesTmdb.ToString(CultureInfo.InvariantCulture);
        return item;
    }

    private async Task<ChannelItemInfo?> BuildSeasonItemAsync(int seriesTmdb, int season, CancellationToken ct)
    {
        // Season tiles share the parent series poster — fine for Stage
        // 5.1; per-season posters are a follow-up enrichment.
        var seriesMeta = await _db.GetTmdbMetadataAsync(seriesTmdb, "series", ct).ConfigureAwait(false);
        var id = ChannelItemId.ForSeason(seriesTmdb, season).Encode();
        return new ChannelItemInfo
        {
            Id = id,
            Name = "Season " + season.ToString(CultureInfo.InvariantCulture),
            SeriesName = seriesMeta?.Title,
            IndexNumber = season,
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Season,
            ImageUrl = seriesMeta?.PosterUrl,
            Tags = new List<string>(),
        };
    }

    /// <summary>
    /// Build an episode ChannelItemInfo. <paramref name="materialised"/>
    /// when non-null gives a real FUSE MediaSource; null produces a
    /// splash MediaSource and a <c>phantom</c> tag. The episode row is
    /// fetched from <c>tmdb_episode_cache</c>; if the cache is cold,
    /// the per-season TMDB call warms the cache for this and all
    /// sibling episodes before retrying the read.
    /// </summary>
    private async Task<ChannelItemInfo?> BuildEpisodeItemAsync(
        int seriesTmdb, int season, int episode,
        MaterialisedStateRow? materialised, CancellationToken ct)
    {
        var seriesMeta = await _db.GetTmdbMetadataAsync(seriesTmdb, "series", ct).ConfigureAwait(false);
        // We don't hard-require seriesMeta here: even without a series-
        // level metadata row, the episode itself can be rebuilt from
        // tmdb_episode_cache + a TMDB season fetch as fallback. The
        // post-flight refresh path must not silently no-op on a cold
        // series_metadata cache (critic IMPORTANT 5).
        var row = await _db.GetTmdbEpisodeAsync(seriesTmdb, season, episode, ct).ConfigureAwait(false);
        if (row is null)
        {
            // Warm the whole season; this also populates sibling rows
            // so a subsequent autopilot prefetch refresh doesn't re-hit
            // TMDB per episode.
            await WarmSeasonCacheAsync(seriesTmdb, season, ct).ConfigureAwait(false);
            row = await _db.GetTmdbEpisodeAsync(seriesTmdb, season, episode, ct).ConfigureAwait(false);
        }

        if (row is null)
        {
            _logger.LogDebug(
                "Episode metadata unavailable for tmdb={Tmdb} s{Season}e{Episode}; skipping",
                seriesTmdb, season, episode);
            return null;
        }

        return BuildEpisodeItemFromRow(row, materialised, seriesMeta?.Title);
    }

    private ChannelItemInfo BuildEpisodeItemFromRow(
        TmdbEpisodeRow row, MaterialisedStateRow? materialised, string? seriesName)
    {
        var id = ChannelItemId.ForEpisode(row.SeriesTmdbId, row.Season, row.Episode).Encode();
        MediaSourceInfo source;
        var tags = new List<string>();
        if (materialised is not null)
        {
            source = FuseMediaSource(materialised.FusePath);
        }
        else
        {
            source = _splashSource.CreateMediaSource();
            tags.Add("phantom");
        }

        DateTime? premiere = null;
        if (!string.IsNullOrWhiteSpace(row.AirDate)
            && DateTime.TryParse(row.AirDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedAir))
        {
            premiere = DateTime.SpecifyKind(parsedAir, DateTimeKind.Utc);
        }

        var item = new ChannelItemInfo
        {
            Id = id,
            Name = row.Title,
            Overview = row.Overview,
            ImageUrl = row.StillUrl,
            Type = ChannelItemType.Media,
            ContentType = ChannelMediaContentType.Episode,
            MediaType = ChannelMediaType.Video,
            ParentIndexNumber = row.Season,
            IndexNumber = row.Episode,
            SeriesName = seriesName,
            PremiereDate = premiere,
            Tags = tags,
            MediaSources = new List<MediaSourceInfo> { source },
        };

        if (row.RuntimeMinutes is { } rt && rt > 0)
        {
            item.RunTimeTicks = TimeSpan.FromMinutes(rt).Ticks;
        }

        item.ProviderIds["Tmdb"] = row.SeriesTmdbId.ToString(CultureInfo.InvariantCulture);
        return item;
    }

    // ----------------------------------------------------------------
    // helpers
    // ----------------------------------------------------------------

    private async Task WarmSeasonCacheAsync(int seriesTmdb, int season, CancellationToken ct)
    {
        var seasonDetails = await SafeGetSeasonAsync(seriesTmdb, season, ct).ConfigureAwait(false);
        if (seasonDetails is null)
        {
            return;
        }

        foreach (var e in seasonDetails.Episodes)
        {
            if (e.EpisodeNumber <= 0)
            {
                continue;
            }

            var title = string.IsNullOrWhiteSpace(e.Name)
                ? $"Episode {e.EpisodeNumber}"
                : e.Name;
            await _db.UpsertTmdbEpisodeAsync(
                new TmdbEpisodeRow(
                    SeriesTmdbId: seriesTmdb,
                    Season: season,
                    Episode: e.EpisodeNumber,
                    Title: title,
                    Overview: e.Overview,
                    StillUrl: BuildImageUrl(e.StillPath),
                    AirDate: e.AirDate,
                    RuntimeMinutes: e.Runtime,
                    FetchedAt: DateTimeOffset.UtcNow),
                ct).ConfigureAwait(false);
        }
    }

    private async Task<Clients.Models.TmdbSeriesDetails?> SafeGetSeriesAsync(int seriesTmdb, CancellationToken ct)
    {
        try
        {
            return await _tmdb.GetSeriesAsync(seriesTmdb, _languageProvider(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetSeriesAsync failed for tmdb={Tmdb}; treating as cold cache", seriesTmdb);
            return null;
        }
    }

    private async Task<Clients.Models.TmdbSeasonDetails?> SafeGetSeasonAsync(int seriesTmdb, int season, CancellationToken ct)
    {
        try
        {
            return await _tmdb.GetSeasonAsync(seriesTmdb, season, _languageProvider(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetSeasonAsync failed for tmdb={Tmdb} season={Season}", seriesTmdb, season);
            return null;
        }
    }

    private static ChannelItemResult EmptyResult() => new()
    {
        Items = Array.Empty<ChannelItemInfo>(),
        TotalRecordCount = 0,
    };

    private static string? BuildImageUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        // Matches DiscoveryRefreshTask.BuildImageUrl: w500 is the
        // Jellyfin TMDB provider's poster/still default.
        var prefixed = path.StartsWith('/') ? path : "/" + path;
        return "https://image.tmdb.org/t/p/w500" + prefixed;
    }

    private static MediaSourceInfo FuseMediaSource(string path)
    {
        var ext = Path.GetExtension(path).TrimStart('.');
#pragma warning disable CA1308
        var container = string.IsNullOrEmpty(ext) ? "mkv" : ext.ToLowerInvariant();
#pragma warning restore CA1308
        return new MediaSourceInfo
        {
            Path = path,
            Container = container,
            Protocol = MediaProtocol.File,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            IsRemote = false,
            MediaStreams = new List<MediaStream>(),
        };
    }
}
