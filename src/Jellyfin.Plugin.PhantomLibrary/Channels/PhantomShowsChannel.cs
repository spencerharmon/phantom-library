using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
///   4. materialised_state for FUSE-path resolution vs native opening source.
///
/// Same single-id-per-logical-item discipline as PhantomMoviesChannel
/// (plan §3.3 + critic round 3 BLOCKER 1): series_&lt;tmdb&gt; folders,
/// season_&lt;tmdb&gt;_s&lt;NN&gt; folders, episode_&lt;tmdb&gt;_s&lt;NN&gt;e&lt;NN&gt;
/// media items, stable across the phantom → materialised transition so
/// UserData survives.
///
/// Stage 5.1 implementation per <c>docs/plans/channel-handoff.md</c>.
/// </summary>
public sealed partial class PhantomShowsChannel
    : IChannel, ISupportsLatestMedia, IChannelItemRefresh, ISupportsMediaProbe
{
    private const string OrphanSeriesPrefix = "orphanseries_";
    private const string OrphanSeasonPrefix = "orphanseason_";
    private const string OrphanEpisodePrefix = "orphanepisode_";

    [GeneratedRegex(@"[sS](?<season>\d{1,3})[eE](?<episode>\d{1,4})")]
    private static partial Regex EpisodeNumberRegex();

    private readonly PhantomDb _db;
    private readonly ITmdbClient _tmdb;
    private readonly SplashSourceProvider _splashSource;
    private readonly ChannelStateProvider _state;
    private readonly GostreamFilesystemEnumerator _enumerator;
    private readonly ILogger<PhantomShowsChannel> _logger;
    private readonly Func<string?> _languageProvider;

    public PhantomShowsChannel(
        PhantomDb db,
        ITmdbClient tmdb,
        SplashSourceProvider splashSource,
        ChannelStateProvider state,
        GostreamFilesystemEnumerator enumerator,
        ILogger<PhantomShowsChannel> logger)
        : this(db, tmdb, splashSource, state, enumerator, logger,
               () => Plugin.Instance?.Configuration?.DiscoveryLanguage)
    {
    }

    internal PhantomShowsChannel(
        PhantomDb db,
        ITmdbClient tmdb,
        SplashSourceProvider splashSource,
        ChannelStateProvider state,
        GostreamFilesystemEnumerator enumerator,
        ILogger<PhantomShowsChannel> logger,
        Func<string?> languageProvider)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tmdb = tmdb ?? throw new ArgumentNullException(nameof(tmdb));
        _splashSource = splashSource ?? throw new ArgumentNullException(nameof(splashSource));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
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

        if (TryParseOrphanSeriesId(query.FolderId, out var orphanSeriesHash))
        {
            return await GetOrphanSeasonsAsync(orphanSeriesHash, cancellationToken).ConfigureAwait(false);
        }

        if (TryParseOrphanSeasonId(query.FolderId, out var orphanSeasonSeriesHash, out var orphanSeason))
        {
            return await GetOrphanEpisodesAsync(orphanSeasonSeriesHash, orphanSeason, cancellationToken).ConfigureAwait(false);
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
        if (TryParseOrphanEpisodeId(id, out var orphanEpisodeHash))
        {
            var orphan = await _enumerator.LookupOrphanEpisodeByHashAsync(orphanEpisodeHash, cancellationToken).ConfigureAwait(false);
            return orphan is null ? Array.Empty<MediaSourceInfo>() : new[] { FuseMediaSource(orphan.Path) };
        }

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

        if (state is null)
        {
            return Array.Empty<MediaSourceInfo>();
        }

        var path = GostreamPathResolver.ResolveEpisodePath(state.FusePath);
        return File.Exists(path)
            ? new[] { FuseMediaSource(path) }
            : Array.Empty<MediaSourceInfo>();
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
        if (TryParseOrphanEpisodeId(channelItemExternalId, out var orphanEpisodeHash))
        {
            var orphan = await _enumerator.LookupOrphanEpisodeByHashAsync(orphanEpisodeHash, cancellationToken).ConfigureAwait(false);
            return orphan is null ? null! : BuildOrphanEpisodeItem(orphan) ?? null!;
        }

        if (TryParseOrphanSeriesId(channelItemExternalId, out var orphanSeriesHash))
        {
            var series = await FindOrphanSeriesAsync(orphanSeriesHash, cancellationToken).ConfigureAwait(false);
            return series is null ? null! : BuildOrphanSeriesItem(series);
        }

        if (TryParseOrphanSeasonId(channelItemExternalId, out var orphanSeasonSeriesHash, out var orphanSeason))
        {
            var series = await FindOrphanSeriesAsync(orphanSeasonSeriesHash, cancellationToken).ConfigureAwait(false);
            var season = series?.Seasons.FirstOrDefault(s => s.SeasonNumber == orphanSeason);
            return series is null || season is null ? null! : BuildOrphanSeasonItem(series, season);
        }

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

        // Visible series are derived from available episode phantoms plus
        // materialised episodes. Raw discovery-only series stay hidden until
        // the availability worker finds at least one playable episode.
        var visible = await _db.ListVisibleSeriesRowsAsync(ct).ConfigureAwait(false);
        foreach (var row in visible)
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(row.Metadata.TmdbId))
            {
                continue;
            }

            items.Add(BuildSeriesItemFromMetadata(row.Metadata));
        }

        IReadOnlyList<GostreamSeriesEntry> orphanSeries;
        try
        {
            orphanSeries = await _enumerator.EnumerateSeriesAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orphan TV enumeration failed; skipping gostream-only TV series this tick");
            orphanSeries = Array.Empty<GostreamSeriesEntry>();
        }

        foreach (var series in orphanSeries)
        {
            ct.ThrowIfCancellationRequested();
            if (series.Seasons.Count == 0 || series.Seasons.All(s => s.Episodes.Count == 0))
            {
                continue;
            }

            items.Add(BuildOrphanSeriesItem(series));
        }

        return new ChannelItemResult
        {
            Items = items,
            TotalRecordCount = items.Count,
        };
    }

    private async Task<ChannelItemResult> GetSeasonsForSeriesAsync(int seriesTmdb, CancellationToken ct)
    {
        var visibleSeasons = await _db.ListVisibleSeasonsAsync(seriesTmdb, ct).ConfigureAwait(false);
        var items = new List<ChannelItemInfo>(visibleSeasons.Count);
        foreach (var row in visibleSeasons)
        {
            ct.ThrowIfCancellationRequested();
            var built = await BuildSeasonItemAsync(seriesTmdb, row.Season, ct).ConfigureAwait(false);
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
        // Re-fetch the season from TMDB on browse so newly aired episodes
        // appear, but fall back to tmdb_episode_cache when TMDB is down or
        // rate-limiting. A transient season fetch failure must not make an
        // already-known season appear empty.
        var seasonDetails = await SafeGetSeasonAsync(seriesTmdb, season, ct).ConfigureAwait(false);

        var seriesMeta = await _db.GetTmdbMetadataAsync(seriesTmdb, "series", ct).ConfigureAwait(false);
        var seriesName = seriesMeta?.Title;

        var rows = new List<TmdbEpisodeRow>(seasonDetails?.Episodes.Count ?? 0);
        if (seasonDetails is not null && seasonDetails.Episodes.Count > 0)
        {
            // Upsert all episodes into tmdb_episode_cache and collect the
            // canonical row shape for use below.
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
        }
        else
        {
            rows.AddRange(await _db.ListEpisodesForSeasonAsync(seriesTmdb, season, ct).ConfigureAwait(false));
        }

        if (rows.Count == 0)
        {
            return EmptyResult();
        }

        var items = new List<ChannelItemInfo>(rows.Count);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (!await _db.IsEpisodeVisibleAsync(seriesTmdb, season, row.Episode, ct).ConfigureAwait(false))
            {
                continue;
            }

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

    private async Task<ChannelItemResult> GetOrphanSeasonsAsync(string seriesHash, CancellationToken ct)
    {
        var series = await FindOrphanSeriesAsync(seriesHash, ct).ConfigureAwait(false);
        if (series is null)
        {
            return EmptyResult();
        }

        var items = series.Seasons
            .Where(s => s.Episodes.Count > 0)
            .OrderBy(s => s.SeasonNumber)
            .Select(s => BuildOrphanSeasonItem(series, s))
            .ToList();
        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    private async Task<ChannelItemResult> GetOrphanEpisodesAsync(string seriesHash, int seasonNumber, CancellationToken ct)
    {
        var series = await FindOrphanSeriesAsync(seriesHash, ct).ConfigureAwait(false);
        var season = series?.Seasons.FirstOrDefault(s => s.SeasonNumber == seasonNumber);
        if (series is null || season is null)
        {
            return EmptyResult();
        }

        var items = season.Episodes
            .Select(BuildOrphanEpisodeItem)
            .Where(i => i is not null)
            .OrderBy(i => i!.IndexNumber ?? int.MaxValue)
            .ThenBy(i => i!.Name, StringComparer.OrdinalIgnoreCase)
            .Cast<ChannelItemInfo>()
            .ToList();
        return new ChannelItemResult { Items = items, TotalRecordCount = items.Count };
    }

    private async Task<GostreamSeriesEntry?> FindOrphanSeriesAsync(string seriesHash, CancellationToken ct)
    {
        foreach (var series in await _enumerator.EnumerateSeriesAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var id = ChannelItemId.ForOrphanPath(series.DirectoryPath);
            if (string.Equals(id.OrphanHash, seriesHash, StringComparison.Ordinal))
            {
                return series;
            }
        }

        return null;
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

        return BuildSeriesItemFromMetadata(meta);
    }

    private static ChannelItemInfo BuildSeriesItemFromMetadata(TmdbMetadataRow meta)
    {
        var id = ChannelItemId.ForSeries(meta.TmdbId).Encode();
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

        item.ProviderIds["Tmdb"] = meta.TmdbId.ToString(CultureInfo.InvariantCulture);
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
            // Use a generic channel container instead of ChannelFolderType.Season.
            // Jellyfin web treats real Season BaseItems as normal TV seasons and
            // routes episode browse through /Shows/{id}/Episodes, which reads
            // already-materialised BaseItem children and can show an empty season
            // before the channel has been asked for that season's episodes. Keeping
            // this as a channel container forces child browse back through
            // IChannel.GetChannelItems where episodes are synthesised on demand.
            FolderType = ChannelFolderType.Container,
            ImageUrl = seriesMeta?.PosterUrl,
            Tags = new List<string>(),
        };
    }

    /// <summary>
    /// Build an episode ChannelItemInfo. <paramref name="materialised"/>
    /// when non-null gives a real FUSE MediaSource; null produces a
    /// native opening MediaSource and a <c>phantom</c> tag. The episode row is
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
            var materialisedPath = GostreamPathResolver.ResolveEpisodePath(materialised.FusePath);
            if (File.Exists(materialisedPath))
            {
                source = FuseMediaSource(materialisedPath);
            }
            else
            {
                source = PhantomMaterialisingMediaSourceProvider.CreateOpeningMediaSource(
                    ChannelItemId.ForEpisode(row.SeriesTmdbId, row.Season, row.Episode),
                    prefixedToken: true);
                tags.Add("phantom");
            }
        }
        else
        {
            source = PhantomMaterialisingMediaSourceProvider.CreateOpeningMediaSource(
                ChannelItemId.ForEpisode(row.SeriesTmdbId, row.Season, row.Episode),
                prefixedToken: true);
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

    private static ChannelItemInfo BuildOrphanSeriesItem(GostreamSeriesEntry series)
    {
        var (title, year) = ParseSeriesDirectoryName(Path.GetFileName(series.DirectoryPath));
        return new ChannelItemInfo
        {
            Id = OrphanSeriesPrefix + ChannelItemId.ForOrphanPath(series.DirectoryPath).OrphanHash,
            Name = title,
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Container,
            ProductionYear = year,
            PremiereDate = year is { } y ? new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null,
            Tags = new List<string> { "orphan" },
        };
    }

    private static ChannelItemInfo BuildOrphanSeasonItem(GostreamSeriesEntry series, GostreamSeasonEntry season)
    {
        var (title, _) = ParseSeriesDirectoryName(Path.GetFileName(series.DirectoryPath));
        var hash = ChannelItemId.ForOrphanPath(series.DirectoryPath).OrphanHash;
        return new ChannelItemInfo
        {
            Id = $"{OrphanSeasonPrefix}{hash}_s{season.SeasonNumber:00}",
            Name = "Season " + season.SeasonNumber.ToString(CultureInfo.InvariantCulture),
            SeriesName = title,
            IndexNumber = season.SeasonNumber,
            Type = ChannelItemType.Folder,
            FolderType = ChannelFolderType.Container,
            Tags = new List<string> { "orphan" },
        };
    }

    private static ChannelItemInfo? BuildOrphanEpisodeItem(GostreamFileEntry episode)
    {
        var fileName = Path.GetFileNameWithoutExtension(episode.Path);
        if (!TryParseEpisodeNumber(fileName, out var season, out var episodeNumber))
        {
            return null;
        }

        var seriesDir = Directory.GetParent(Directory.GetParent(episode.Path)!.FullName)!.FullName;
        var (seriesName, _) = ParseSeriesDirectoryName(Path.GetFileName(seriesDir));
        var name = HumanizeFileName(fileName);
        return new ChannelItemInfo
        {
            Id = OrphanEpisodePrefix + ChannelItemId.ForOrphanPath(episode.Path).OrphanHash,
            Name = name,
            Type = ChannelItemType.Media,
            ContentType = ChannelMediaContentType.Episode,
            MediaType = ChannelMediaType.Video,
            ParentIndexNumber = season,
            IndexNumber = episodeNumber,
            SeriesName = seriesName,
            Tags = new List<string> { "orphan" },
            MediaSources = new List<MediaSourceInfo> { FuseMediaSource(episode.Path) },
        };
    }

    private static (string Title, int? Year) ParseSeriesDirectoryName(string dirName)
    {
        var title = dirName.Replace('_', ' ').Trim();
        int? year = null;
        var open = title.LastIndexOf('(');
        var close = title.LastIndexOf(')');
        if (open >= 0 && close > open && int.TryParse(title.AsSpan(open + 1, close - open - 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedYear))
        {
            year = parsedYear;
            title = title[..open].Trim();
        }

        return (string.IsNullOrWhiteSpace(title) ? dirName : title, year);
    }

    private static string HumanizeFileName(string fileName)
    {
        var withoutHash = fileName;
        var lastUnderscore = withoutHash.LastIndexOf('_');
        if (lastUnderscore > 0 && lastUnderscore + 1 < withoutHash.Length)
        {
            var suffix = withoutHash[(lastUnderscore + 1)..];
            if (suffix.Length is >= 7 and <= 16 && suffix.All(Uri.IsHexDigit))
            {
                withoutHash = withoutHash[..lastUnderscore];
            }
        }

        return withoutHash.Replace('_', ' ').Trim();
    }

    private static bool TryParseEpisodeNumber(string fileName, out int season, out int episode)
    {
        season = 0;
        episode = 0;
        var match = EpisodeNumberRegex().Match(fileName);
        return match.Success
            && int.TryParse(match.Groups["season"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out season)
            && int.TryParse(match.Groups["episode"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out episode);
    }

    private static bool TryParseOrphanSeriesId(string? id, out string hash)
    {
        hash = string.Empty;
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith(OrphanSeriesPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        hash = id[OrphanSeriesPrefix.Length..];
        return hash.Length > 0 && hash.All(Uri.IsHexDigit);
    }

    private static bool TryParseOrphanSeasonId(string? id, out string hash, out int season)
    {
        hash = string.Empty;
        season = 0;
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith(OrphanSeasonPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = id[OrphanSeasonPrefix.Length..];
        var sep = rest.LastIndexOf("_s", StringComparison.Ordinal);
        if (sep <= 0)
        {
            return false;
        }

        hash = rest[..sep];
        return hash.Length > 0
            && hash.All(Uri.IsHexDigit)
            && int.TryParse(rest[(sep + 2)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out season);
    }

    private static bool TryParseOrphanEpisodeId(string? id, out string hash)
    {
        hash = string.Empty;
        if (string.IsNullOrWhiteSpace(id) || !id.StartsWith(OrphanEpisodePrefix, StringComparison.Ordinal))
        {
            return false;
        }

        hash = id[OrphanEpisodePrefix.Length..];
        return hash.Length > 0 && hash.All(Uri.IsHexDigit);
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
            Id = MediaSourceIds.ForFilePath(path),
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
