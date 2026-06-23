using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
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
    private const string DataVersionSalt = "rich-season-v1";
    private static readonly string[] ExternalTags = { "external" };

    [GeneratedRegex(@"[sS](?<season>\d{1,3})[eE](?<episode>\d{1,4})")]
    private static partial Regex EpisodeNumberRegex();

    private readonly PhantomDb _db;
    private readonly ITmdbClient _tmdb;
    private readonly SplashSourceProvider _splashSource;
    private readonly ChannelStateProvider _state;
    private readonly GostreamFilesystemEnumerator _enumerator;
    private readonly Dictionary<string, int> _gostreamSeriesTmdbByPath = new(StringComparer.Ordinal);
    private readonly ILogger<PhantomShowsChannel> _logger;
    private readonly Func<string?> _languageProvider;
    private readonly Func<PluginConfiguration> _configProvider;

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
        _configProvider = () => Plugin.Instance?.Configuration ?? new PluginConfiguration();
    }

    /// <inheritdoc />
    public string Name => ChannelIds.ShowsName;

    /// <inheritdoc />
    public string Description => "Phantom Library — TV discovery + on-demand materialise via gostream.";

    /// <inheritdoc />
    public string DataVersion => _state.DataVersion(ChannelStateProvider.KindShows) + ":fs:" + _enumerator.ShowsVersion() + ":" + DataVersionSalt;

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

    private int SeriesMinAvailableEpisodes()
        => Math.Max(1, _configProvider().SeriesMinAvailableEpisodes);

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
        var visible = await _db.ListVisibleSeriesRowsAsync(SeriesMinAvailableEpisodes(), ct).ConfigureAwait(false);
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

            var enriched = await TryResolveEnrichedGostreamSeriesAsync(series, ct).ConfigureAwait(false);
            if (enriched is not null)
            {
                if (seen.Contains(enriched.TmdbId))
                {
                    continue;
                }

                items.Add(BuildExternalSeriesItemFromMetadata(enriched.Metadata));
                seen.Add(enriched.TmdbId);
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
        var externalSeries = await FindExternalSeriesByTmdbAsync(seriesTmdb, ct).ConfigureAwait(false);
        var externalSeasonNumbers = await ListExternalSeasonNumbersAsync(externalSeries, ct).ConfigureAwait(false);
        var seasonNumbers = visibleSeasons.Select(s => s.Season).Concat(externalSeasonNumbers).ToHashSet();
        if (await _db.IsSeriesVisibleAsync(seriesTmdb, SeriesMinAvailableEpisodes(), ct).ConfigureAwait(false))
        {
            var details = await SafeGetSeriesAsync(seriesTmdb, ct).ConfigureAwait(false);
            if (details is not null)
            {
                for (var seasonNumber = 1; seasonNumber <= details.NumberOfSeasons; seasonNumber++)
                {
                    seasonNumbers.Add(seasonNumber);
                }
            }
        }

        var items = new List<ChannelItemInfo>(seasonNumbers.Count);
        var emitted = new HashSet<int>();
        foreach (var seasonNumber in seasonNumbers.Order())
        {
            if (emitted.Contains(seasonNumber))
            {
                continue;
            }

            var external = externalSeries.FirstOrDefault(e => e.Series.Seasons.Any(s => s.SeasonNumber == seasonNumber));
            if (external is not null)
            {
                var seasonEntry = external.Series.Seasons.FirstOrDefault(s => s.SeasonNumber == seasonNumber)
                    ?? new GostreamSeasonEntry(seasonNumber, Array.Empty<GostreamFileEntry>());
                items.Add(await BuildExternalSeasonItemAsync(external.Metadata, seasonEntry, ct).ConfigureAwait(false));
            }
            else
            {
                var built = await BuildSeasonItemAsync(seriesTmdb, seasonNumber, ct).ConfigureAwait(false);
                if (built is null)
                {
                    continue;
                }

                items.Add(built);
            }

            emitted.Add(seasonNumber);
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

        var externalSeries = await FindExternalSeriesByTmdbAsync(seriesTmdb, ct).ConfigureAwait(false);
        var externalEpisodes = externalSeries
            .SelectMany(e => e.Series.Seasons.Where(s => s.SeasonNumber == season).SelectMany(s => s.Episodes))
            .ToList();
        var externalByEpisode = SelectExternalEpisodeVariants(externalEpisodes)
            .Select(e => (Entry: e, Parsed: TryParseEpisodeNumber(Path.GetFileNameWithoutExtension(e.Path), out _, out var episodeNumber), Episode: episodeNumber))
            .Where(e => e.Parsed)
            .GroupBy(e => e.Episode)
            .ToDictionary(g => g.Key, g => g.First().Entry);
        var hasExternalSeries = externalSeries.Count > 0;
        var exposeFullSeries = hasExternalSeries || await _db.IsSeriesVisibleAsync(seriesTmdb, SeriesMinAvailableEpisodes(), ct).ConfigureAwait(false);
        var externalSeriesName = hasExternalSeries ? externalSeries[0].Metadata.Title : null;
        var items = new List<ChannelItemInfo>(rows.Count + externalEpisodes.Count);
        var emittedEpisodes = new HashSet<int>();
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (!exposeFullSeries)
            {
                continue;
            }

            if (externalByEpisode.TryGetValue(row.Episode, out var externalEpisode))
            {
                items.Add(BuildExternalEpisodeItemFromRow(row, externalEpisode, seriesName ?? externalSeriesName));
            }
            else
            {
                var materialised = await _db.GetMaterialisedStateAsync(
                    seriesTmdb, "episode", season, row.Episode, ct).ConfigureAwait(false);
                items.Add(BuildEpisodeItemFromRow(row, materialised, seriesName));
            }

            emittedEpisodes.Add(row.Episode);
        }

        foreach (var externalEpisode in SelectExternalEpisodeVariants(externalEpisodes))
        {
            var hasEpisodeNumber = TryParseEpisodeNumber(Path.GetFileNameWithoutExtension(externalEpisode.Path), out _, out var episodeNumber);
            if (hasEpisodeNumber && emittedEpisodes.Contains(episodeNumber))
            {
                continue;
            }

            items.Add(BuildOrphanEpisodeItem(externalEpisode));
            if (hasEpisodeNumber)
            {
                emittedEpisodes.Add(episodeNumber);
            }
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

        var enriched = await TryResolveEnrichedGostreamSeriesAsync(series, ct).ConfigureAwait(false);
        if (enriched is not null)
        {
            var seasonNumbers = await ListExternalSeasonNumbersAsync(new[] { enriched }, ct).ConfigureAwait(false);
            var enrichedItems = new List<ChannelItemInfo>();
            foreach (var seasonNumber in seasonNumbers.Order())
            {
                enrichedItems.Add(await BuildExternalSeasonItemAsync(
                    enriched.Metadata,
                    series.Seasons.FirstOrDefault(s => s.SeasonNumber == seasonNumber)
                        ?? new GostreamSeasonEntry(seasonNumber, Array.Empty<GostreamFileEntry>()),
                    ct).ConfigureAwait(false));
            }

            return new ChannelItemResult { Items = enrichedItems, TotalRecordCount = enrichedItems.Count };
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

        var enriched = await TryResolveEnrichedGostreamSeriesAsync(series, ct).ConfigureAwait(false);
        if (enriched is not null)
        {
            return await GetEpisodesForSeasonAsync(enriched.TmdbId, seasonNumber, ct).ConfigureAwait(false);
        }

        var items = SelectExternalEpisodeVariants(season.Episodes)
            .Select(BuildOrphanEpisodeItem)
            .OrderBy(i => i.IndexNumber ?? int.MaxValue)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
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

    private sealed record EnrichedGostreamSeries(int TmdbId, TmdbMetadataRow Metadata, GostreamSeriesEntry Series);

    private async Task<HashSet<int>> ListExternalSeasonNumbersAsync(IReadOnlyList<EnrichedGostreamSeries> externalSeries, CancellationToken ct)
    {
        var seasons = externalSeries
            .SelectMany(e => e.Series.Seasons)
            .Where(s => s.Episodes.Count > 0)
            .Select(s => s.SeasonNumber)
            .ToHashSet();
        foreach (var external in externalSeries)
        {
            var details = await SafeGetSeriesAsync(external.TmdbId, ct).ConfigureAwait(false);
            if (details is null)
            {
                continue;
            }

            for (var season = 1; season <= details.NumberOfSeasons; season++)
            {
                seasons.Add(season);
            }
        }

        return seasons;
    }

    private async Task<IReadOnlyList<EnrichedGostreamSeries>> FindExternalSeriesByTmdbAsync(int tmdbId, CancellationToken ct)
    {
        var matches = new List<EnrichedGostreamSeries>();
        foreach (var series in await _enumerator.EnumerateSeriesAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var enriched = await TryResolveEnrichedGostreamSeriesAsync(series, ct).ConfigureAwait(false);
            if (enriched is not null && enriched.TmdbId == tmdbId)
            {
                matches.Add(enriched);
            }
        }

        return matches;
    }

    private async Task<EnrichedGostreamSeries?> TryResolveEnrichedGostreamSeriesAsync(GostreamSeriesEntry series, CancellationToken ct)
    {
        var (title, year) = ParseSeriesDirectoryName(Path.GetFileName(series.DirectoryPath));
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        int tmdbId;
        lock (_gostreamSeriesTmdbByPath)
        {
            _gostreamSeriesTmdbByPath.TryGetValue(series.DirectoryPath, out tmdbId);
        }

        TmdbMetadataRow? metadata = null;
        if (tmdbId != 0)
        {
            metadata = await _db.GetTmdbMetadataAsync(tmdbId, "series", ct).ConfigureAwait(false);
        }
        else
        {
            metadata = await _db.FindTmdbMetadataByTitleYearAsync("series", title, year, ct).ConfigureAwait(false);
            if (metadata is null)
            {
                return null;
            }

            tmdbId = metadata.TmdbId;
            lock (_gostreamSeriesTmdbByPath)
            {
                _gostreamSeriesTmdbByPath[series.DirectoryPath] = tmdbId;
            }
        }

        return metadata is null ? null : new EnrichedGostreamSeries(tmdbId, metadata, series);
    }

    private static TmdbMetadataRow MapSeriesDetails(TmdbSeriesDetails details)
    {
        return new TmdbMetadataRow(
            details.Id,
            "series",
            details.Name,
            ParseYear(details.FirstAirDate),
            details.Overview,
            BuildImageUrl(details.PosterPath),
            BuildImageUrl(details.BackdropPath),
            details.Genres,
            null,
            details.VoteAverage,
            details.OriginalName,
            DateTimeOffset.UtcNow);
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
        var seriesMeta = await _db.GetTmdbMetadataAsync(seriesTmdb, "series", ct).ConfigureAwait(false);
        var details = await SafeGetSeasonAsync(seriesTmdb, season, ct).ConfigureAwait(false);
        if (details is not null)
        {
            await UpsertSeasonEpisodeRowsAsync(details, ct).ConfigureAwait(false);
        }

        var summary = await _db.GetSeasonAvailabilitySummaryAsync(seriesTmdb, season, ct).ConfigureAwait(false);
        return BuildSeasonItemCore(seriesTmdb, season, seriesMeta, details, summary, tags: Array.Empty<string>());
    }

    private async Task<ChannelItemInfo> BuildExternalSeasonItemAsync(TmdbMetadataRow meta, GostreamSeasonEntry season, CancellationToken ct)
    {
        var details = await SafeGetSeasonAsync(meta.TmdbId, season.SeasonNumber, ct).ConfigureAwait(false);
        if (details is not null)
        {
            await UpsertSeasonEpisodeRowsAsync(details, ct).ConfigureAwait(false);
        }

        var summary = await _db.GetSeasonAvailabilitySummaryAsync(meta.TmdbId, season.SeasonNumber, ct).ConfigureAwait(false);
        if (season.Episodes.Count > 0 && season.Episodes.Count > summary.PlayableCount)
        {
            var playable = season.Episodes.Count;
            var unknown = Math.Max(0, summary.KnownCount - playable - summary.UnavailableCount);
            summary = summary with { PlayableCount = playable, UnknownCount = unknown };
        }

        return BuildSeasonItemCore(meta.TmdbId, season.SeasonNumber, meta, details, summary, tags: ExternalTags);
    }

    private static ChannelItemInfo BuildSeasonItemCore(
        int seriesTmdb,
        int season,
        TmdbMetadataRow? seriesMeta,
        TmdbSeasonDetails? details,
        SeasonAvailabilitySummary summary,
        IReadOnlyList<string> tags)
    {
        var id = ChannelItemId.ForSeason(seriesTmdb, season).Encode();
        var baseName = !string.IsNullOrWhiteSpace(details?.Name)
            ? details!.Name!
            : "Season " + season.ToString(CultureInfo.InvariantCulture);
        var name = FormatSeasonTitle(baseName, summary);
        var overviewParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(details?.Overview))
        {
            overviewParts.Add(details!.Overview!);
        }

        var counts = FormatSeasonSummary(summary);
        if (!string.IsNullOrWhiteSpace(counts))
        {
            overviewParts.Add(counts);
        }
        else if (!string.IsNullOrWhiteSpace(seriesMeta?.Overview))
        {
            overviewParts.Add(seriesMeta!.Overview!);
        }

        var premiere = ParseSeasonAirDate(details?.AirDate);
        return new ChannelItemInfo
        {
            Id = id,
            Name = name,
            SeriesName = seriesMeta?.Title,
            Overview = overviewParts.Count == 0 ? null : string.Join("\n\n", overviewParts),
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
            ImageUrl = BuildImageUrl(details?.PosterPath) ?? seriesMeta?.PosterUrl,
            PremiereDate = premiere,
            ProductionYear = premiere?.Year ?? seriesMeta?.Year,
            Tags = tags.ToList(),
        };
    }

    private static string FormatSeasonTitle(string baseName, SeasonAvailabilitySummary summary)
    {
        if (summary.KnownCount <= 0 && summary.PlayableCount <= 0 && summary.UnavailableCount <= 0)
        {
            return baseName;
        }

        var known = summary.KnownCount > 0 ? summary.KnownCount : summary.PlayableCount + summary.UnknownCount + summary.UnavailableCount;
        var ready = summary.PlayableCount;
        var unavailable = summary.UnavailableCount;
        var suffix = known.ToString(CultureInfo.InvariantCulture) + (known == 1 ? " episode" : " episodes");
        if (ready > 0)
        {
            suffix += ", " + ready.ToString(CultureInfo.InvariantCulture) + " ready";
        }

        if (unavailable > 0)
        {
            suffix += ", " + unavailable.ToString(CultureInfo.InvariantCulture) + " unavailable";
        }

        return baseName + " (" + suffix + ")";
    }

    private static string FormatSeasonSummary(SeasonAvailabilitySummary summary)
    {
        if (summary.KnownCount <= 0 && summary.PlayableCount <= 0 && summary.UnavailableCount <= 0)
        {
            return string.Empty;
        }

        var known = summary.KnownCount > 0 ? summary.KnownCount : summary.PlayableCount + summary.UnknownCount + summary.UnavailableCount;
        var parts = new List<string> { known.ToString(CultureInfo.InvariantCulture) + (known == 1 ? " episode" : " episodes") };
        if (summary.PlayableCount > 0)
        {
            parts.Add(summary.PlayableCount.ToString(CultureInfo.InvariantCulture) + " available/materialised");
        }

        if (summary.UnknownCount > 0)
        {
            parts.Add(summary.UnknownCount.ToString(CultureInfo.InvariantCulture) + " unknown");
        }

        if (summary.UnavailableCount > 0)
        {
            parts.Add(summary.UnavailableCount.ToString(CultureInfo.InvariantCulture) + " unavailable");
        }

        return string.Join(" · ", parts);
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

    private static ChannelItemInfo BuildExternalSeriesItemFromMetadata(TmdbMetadataRow meta)
    {
        var item = BuildSeriesItemFromMetadata(meta);
        item.Tags = new List<string> { "external" };
        return item;
    }

    private ChannelItemInfo BuildExternalEpisodeItemFromRow(TmdbEpisodeRow row, GostreamFileEntry externalEpisode, string? seriesName)
    {
        var item = BuildEpisodeItemFromRow(row, materialised: null, seriesName);
        item.Tags = new List<string> { "external" };
        item.MediaSources = new List<MediaSourceInfo> { FuseMediaSource(externalEpisode.Path) };
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
            Tags = new List<string> { "external" },
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
            Tags = new List<string> { "external" },
        };
    }

    private static List<GostreamFileEntry> SelectExternalEpisodeVariants(IReadOnlyList<GostreamFileEntry> episodes)
        => episodes
            .GroupBy(EpisodeVariantKey, StringComparer.Ordinal)
            .Select(g => g.OrderByDescending(e => ScoreVariantPath(e.Path)).ThenBy(e => e.Path, StringComparer.Ordinal).First())
            .ToList();

    private static string EpisodeVariantKey(GostreamFileEntry episode)
    {
        var fileName = Path.GetFileNameWithoutExtension(episode.Path);
        return TryParseEpisodeNumber(fileName, out var season, out var episodeNumber)
            ? $"s{season:00}e{episodeNumber:00}"
            : "path:" + episode.Path;
    }

    private static int ScoreVariantPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var score = 0;
        if (name.Contains("2160p", StringComparison.OrdinalIgnoreCase) || name.Contains("4k", StringComparison.OrdinalIgnoreCase)) score += 100;
        if (name.Contains("1080p", StringComparison.OrdinalIgnoreCase)) score += 50;
        if (name.Contains("remux", StringComparison.OrdinalIgnoreCase)) score += 30;
        if (name.Contains("dv", StringComparison.OrdinalIgnoreCase)) score += 12;
        if (name.Contains("hdr", StringComparison.OrdinalIgnoreCase)) score += 8;
        if (name.Contains("atmos", StringComparison.OrdinalIgnoreCase)) score += 6;
        if (name.Contains("5.1", StringComparison.OrdinalIgnoreCase)) score += 3;
        return score;
    }

    private static ChannelItemInfo BuildOrphanEpisodeItem(GostreamFileEntry episode)
    {
        var fileName = Path.GetFileNameWithoutExtension(episode.Path);
        var hasEpisodeNumber = TryParseEpisodeNumber(fileName, out var season, out var episodeNumber);
        if (!hasEpisodeNumber)
        {
            var seasonDirName = Directory.GetParent(episode.Path)?.Name ?? string.Empty;
            if (!TryParseSeasonNumberFromText(seasonDirName, out season))
            {
                season = 0;
            }
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
            ParentIndexNumber = season > 0 ? season : null,
            IndexNumber = hasEpisodeNumber ? episodeNumber : null,
            SeriesName = seriesName,
            Tags = new List<string> { "external" },
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

    private static bool TryParseSeasonNumberFromText(string text, out int season)
    {
        season = 0;
        var s = text.Trim().Replace('.', ' ').Replace('_', ' ');
        if (s.StartsWith("Season ", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(s.AsSpan("Season ".Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out season);
        }

        if (s.StartsWith("Season", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(s.AsSpan("Season".Length).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out season);
        }

        return s.Length > 1
            && (s[0] == 'S' || s[0] == 's')
            && int.TryParse(s.AsSpan(1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out season);
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
        if (seasonDetails is not null)
        {
            await UpsertSeasonEpisodeRowsAsync(seasonDetails, ct).ConfigureAwait(false);
        }
    }

    private async Task UpsertSeasonEpisodeRowsAsync(TmdbSeasonDetails seasonDetails, CancellationToken ct)
    {
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
                    SeriesTmdbId: seasonDetails.SeriesTmdbId,
                    Season: seasonDetails.SeasonNumber,
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

    private static int? ParseYear(string? date)
        => !string.IsNullOrWhiteSpace(date) && date.Length >= 4 && int.TryParse(date[..4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : null;

    private static DateTime? ParseSeasonAirDate(string? date)
        => !string.IsNullOrWhiteSpace(date)
           && DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
            : null;

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
