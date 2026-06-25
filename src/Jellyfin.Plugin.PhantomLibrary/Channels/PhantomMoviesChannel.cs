using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Channels;

/// <summary>
/// "Phantom Movies" channel — flat channel that emits a union of:
///   1. materialised_state movies (real FUSE-backed MediaSources),
///   2. discovery_cache movies (phantom items, MediaSource = native opening source),
///   3. orphan files on the gostream FUSE mount (raw filename).
///
/// Dedup: materialised wins over phantom for the same tmdb_id.
/// (Plan §3.3 + critic round 3 BLOCKER 1.) The id stays the same
/// across the phantom → materialised transition so UserData
/// (favourites, watched, playback position) is preserved.
/// </summary>
public sealed class PhantomMoviesChannel
    : IChannel, ISupportsLatestMedia, IChannelItemRefresh, ISupportsMediaProbe
{
    private readonly PhantomDb _db;
    private readonly GostreamFilesystemEnumerator _enumerator;
    private readonly SplashSourceProvider _splashSource;
    private readonly ChannelStateProvider _state;
    private readonly ITmdbClient _tmdbClient;
    private readonly IMediaEncoder? _mediaEncoder;
    private readonly ILogger<PhantomMoviesChannel> _logger;
    private readonly Dictionary<string, int> _gostreamMovieTmdbByPath = new(StringComparer.Ordinal);

    public PhantomMoviesChannel(
        PhantomDb db,
        GostreamFilesystemEnumerator enumerator,
        SplashSourceProvider splashSource,
        ChannelStateProvider state,
        ITmdbClient tmdbClient,
        ILogger<PhantomMoviesChannel> logger)
        : this(db, enumerator, splashSource, state, tmdbClient, null, logger)
    {
    }

    public PhantomMoviesChannel(
        PhantomDb db,
        GostreamFilesystemEnumerator enumerator,
        SplashSourceProvider splashSource,
        ChannelStateProvider state,
        ITmdbClient tmdbClient,
        IMediaEncoder? mediaEncoder,
        ILogger<PhantomMoviesChannel> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _enumerator = enumerator ?? throw new ArgumentNullException(nameof(enumerator));
        _splashSource = splashSource ?? throw new ArgumentNullException(nameof(splashSource));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _tmdbClient = tmdbClient ?? throw new ArgumentNullException(nameof(tmdbClient));
        _mediaEncoder = mediaEncoder;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public string Name => ChannelIds.MoviesName;

    /// <inheritdoc />
    public string Description => "Phantom Library — movie discovery + on-demand materialise via gostream.";

    /// <inheritdoc />
    public string DataVersion => _state.DataVersion(ChannelStateProvider.KindMovies) + ":fs:" + _enumerator.MoviesVersion();

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Movie },
            MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
        };
    }

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => true;

    /// <inheritdoc />
    public async Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        // Movies channel is flat: no folder navigation.
        if (!string.IsNullOrEmpty(query.FolderId))
        {
            return new ChannelItemResult
            {
                Items = Array.Empty<ChannelItemInfo>(),
                TotalRecordCount = 0,
            };
        }

        var items = new List<ChannelItemInfo>();
        var emittedTmdbs = new HashSet<int>();

        IReadOnlyList<GostreamFileEntry> orphans;
        try
        {
            orphans = await _enumerator.EnumerateOrphanMoviesAsync(emittedTmdbs, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Orphan enumeration failed; skipping orphans this tick");
            orphans = Array.Empty<GostreamFileEntry>();
        }

        var unresolvedOrphans = new List<GostreamFileEntry>();
        var variantsByTmdb = new Dictionary<int, List<MediaSourceInfo>>();
        var metadataByTmdb = new Dictionary<int, TmdbMetadataRow>();
        foreach (var o in orphans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var enriched = await TryResolveEnrichedGostreamMovieAsync(o.Path, cancellationToken).ConfigureAwait(false);
            if (enriched is null)
            {
                unresolvedOrphans.Add(o);
                continue;
            }

            if (!variantsByTmdb.TryGetValue(enriched.TmdbId, out var sources))
            {
                sources = new List<MediaSourceInfo>();
                variantsByTmdb[enriched.TmdbId] = sources;
                metadataByTmdb[enriched.TmdbId] = enriched.Metadata;
            }

            if (!sources.Any(s => string.Equals(s.Path, enriched.Source.Path, StringComparison.Ordinal)))
            {
                sources.Add(enriched.Source);
            }
        }

        // --- 1. Visible catalogue movies. Materialised rows are always visible;
        // unmaterialised phantoms are visible only after availability probing
        // found a viable candidate. ---
        var visible = await _db.ListVisibleMovieRowsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in visible)
        {
            cancellationToken.ThrowIfCancellationRequested();
            variantsByTmdb.TryGetValue(row.Metadata.TmdbId, out var variants);
            var sources = new List<MediaSourceInfo>();
            var tags = new List<string>();
            if (row.Materialised is not null)
            {
                var materialisedPath = GostreamPathResolver.ResolveMoviePath(row.Materialised.FusePath);
                if (File.Exists(materialisedPath))
                {
                    sources.Add(await FuseMediaSourceAsync(materialisedPath, cancellationToken).ConfigureAwait(false));
                }
                else
                {
                    sources.Add(PhantomMaterialisingMediaSourceProvider.CreateOpeningMediaSource(ChannelItemId.ForMovie(row.Metadata.TmdbId), prefixedToken: true));
                    tags.Add("phantom");
                }
            }
            else if (variants is { Count: > 0 })
            {
                sources.Add(SelectDefaultVariant(variants));
                tags.Add("external");
            }
            else
            {
                sources.Add(PhantomMaterialisingMediaSourceProvider.CreateOpeningMediaSource(ChannelItemId.ForMovie(row.Metadata.TmdbId), prefixedToken: true));
                tags.Add("phantom");
            }

            items.Add(BuildMovieItemFromMetadata(row.Metadata, sources, tags));
            emittedTmdbs.Add(row.Metadata.TmdbId);
        }

        // --- 2. Gostream files with TMDB hits (real media; outrank unprobed discovery phantoms). Group variants by TMDB. ---
        foreach (var kvp in variantsByTmdb.OrderBy(k => metadataByTmdb[k.Key].Title, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (emittedTmdbs.Contains(kvp.Key))
            {
                continue;
            }

            var item = BuildMovieItemFromMetadata(metadataByTmdb[kvp.Key], new[] { SelectDefaultVariant(kvp.Value) }, tags: new List<string> { "external" });
            items.Add(item);
            emittedTmdbs.Add(kvp.Key);
        }

        // --- 3. Raw orphan fallback for files that could not be enriched. ---
        foreach (var o in unresolvedOrphans)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(await BuildOrphanMovieItemAsync(o, cancellationToken).ConfigureAwait(false));
        }

        return PageResult(items, query.StartIndex, query.Limit);
    }

    private static ChannelItemResult PageResult(List<ChannelItemInfo> items, int? startIndex, int? limit)
    {
        var total = items.Count;
        var start = Math.Max(0, startIndex ?? 0);
        if (start >= total)
        {
            return new ChannelItemResult
            {
                Items = Array.Empty<ChannelItemInfo>(),
                TotalRecordCount = total,
            };
        }

        var count = limit.HasValue ? Math.Max(0, Math.Min(limit.Value, total - start)) : total - start;
        return new ChannelItemResult
        {
            Items = items.Skip(start).Take(count).ToArray(),
            TotalRecordCount = total,
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

        switch (parsed.Kind)
        {
            case ChannelItemId.KindMovie:
                {
                    var state = await _db.GetMaterialisedStateAsync(
                        parsed.TmdbId!.Value, "movie", -1, -1, cancellationToken).ConfigureAwait(false);
                    if (state is not null)
                    {
                        var path = GostreamPathResolver.ResolveMoviePath(state.FusePath);
                        if (File.Exists(path))
                        {
                            return new[] { await FuseMediaSourceAsync(path, cancellationToken).ConfigureAwait(false) };
                        }
                    }

                    return Array.Empty<MediaSourceInfo>();
                }

            case ChannelItemId.KindOrphan:
                {
                    var orphan = await _enumerator.LookupOrphanByHashAsync(
                        parsed.OrphanHash!, cancellationToken).ConfigureAwait(false);
                    if (orphan is null)
                    {
                        return Array.Empty<MediaSourceInfo>();
                    }

                    return new[] { await FuseMediaSourceAsync(orphan.Path, cancellationToken).ConfigureAwait(false) };
                }

            default:
                return Array.Empty<MediaSourceInfo>();
        }
    }

    /// <inheritdoc />
    public async Task<IEnumerable<ChannelItemInfo>> GetLatestMedia(ChannelLatestMediaSearch request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // ChannelLatestMediaSearch (Jellyfin 10.11) carries UserId only;
        // no Limit field. Default to 20 as the conventional "Latest" row size.
        const int defaultLimit = 20;

        var materialised = await _db.ListMaterialisedStateAsync("movie", cancellationToken).ConfigureAwait(false);
        var items = new List<ChannelItemInfo>(Math.Min(materialised.Count, defaultLimit));
        foreach (var row in materialised.OrderByDescending(r => r.MaterialisedAt).Take(defaultLimit))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var built = await BuildMovieItemAsync(row.TmdbId, row, variants: null, cancellationToken).ConfigureAwait(false);
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
        if (!ChannelItemId.TryParse(channelItemExternalId, out var parsed))
        {
            return null!;
        }

        switch (parsed.Kind)
        {
            case ChannelItemId.KindMovie:
                {
                    var state = await _db.GetMaterialisedStateAsync(
                        parsed.TmdbId!.Value, "movie", -1, -1, cancellationToken).ConfigureAwait(false);
                    var built = await BuildMovieItemAsync(parsed.TmdbId!.Value, state, variants: null, cancellationToken).ConfigureAwait(false);
                    return built!;
                }

            case ChannelItemId.KindOrphan:
                {
                    var orphan = await _enumerator.LookupOrphanByHashAsync(
                        parsed.OrphanHash!, cancellationToken).ConfigureAwait(false);
                    return orphan is null ? null! : await BuildOrphanMovieItemAsync(orphan, cancellationToken).ConfigureAwait(false);
                }

            default:
                return null!;
        }
    }

    /// <summary>
    /// Build a single movie ChannelItemInfo. <paramref name="materialised"/>
    /// when non-null gives a real FUSE MediaSource; null produces a phantom
    /// item with a native opening MediaSource and Tags=["phantom"]. Returns null
    /// if no <c>tmdb_metadata</c> row exists yet — the caller skips the item
    /// for this tick and the next DiscoveryRefreshTask warms the metadata.
    /// </summary>
    private async Task<ChannelItemInfo?> BuildMovieItemAsync(int tmdb, MaterialisedStateRow? materialised, IReadOnlyList<MediaSourceInfo>? variants, CancellationToken ct)
    {
        var meta = await _db.GetTmdbMetadataAsync(tmdb, "movie", ct).ConfigureAwait(false);
        if (meta is null)
        {
            _logger.LogDebug("Skipping tmdb={Tmdb} (no metadata row yet)", tmdb);
            return null;
        }

        var id = ChannelItemId.ForMovie(tmdb).Encode();
        var sources = new List<MediaSourceInfo>();
        var tags = new List<string>();
        if (materialised is not null)
        {
            var materialisedPath = GostreamPathResolver.ResolveMoviePath(materialised.FusePath);
            if (File.Exists(materialisedPath))
            {
                sources.Add(await FuseMediaSourceAsync(materialisedPath, ct).ConfigureAwait(false));
            }
            else
            {
                sources.Add(PhantomMaterialisingMediaSourceProvider.CreateOpeningMediaSource(ChannelItemId.ForMovie(tmdb), prefixedToken: true));
                tags.Add("phantom");
            }
        }
        else if (variants is { Count: > 0 })
        {
            sources.Add(SelectDefaultVariant(variants));
            tags.Add("external");
        }
        else
        {
            sources.Add(PhantomMaterialisingMediaSourceProvider.CreateOpeningMediaSource(ChannelItemId.ForMovie(tmdb), prefixedToken: true));
            tags.Add("phantom");
        }

        return BuildMovieItemFromMetadata(meta, sources, tags);
    }

    private sealed record EnrichedGostreamMovie(int TmdbId, TmdbMetadataRow Metadata, MediaSourceInfo Source);

    private async Task<EnrichedGostreamMovie?> TryResolveEnrichedGostreamMovieAsync(string path, CancellationToken ct)
    {
        if (!TryParseGostreamMovieName(path, out var title, out var year))
        {
            return null;
        }

        int tmdbId;
        lock (_gostreamMovieTmdbByPath)
        {
            _gostreamMovieTmdbByPath.TryGetValue(path, out tmdbId);
        }

        if (tmdbId == 0)
        {
            IReadOnlyList<TmdbSearchHit> hits;
            try
            {
                hits = await _tmdbClient.SearchMoviesAsync(title, year, null, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TMDB search failed for gostream file {Path}", path);
                return null;
            }

            if (hits.Count == 0)
            {
                return null;
            }

            tmdbId = hits[0].Id;
            lock (_gostreamMovieTmdbByPath)
            {
                _gostreamMovieTmdbByPath[path] = tmdbId;
            }
        }

        var details = await _db.GetTmdbMetadataAsync(tmdbId, "movie", ct).ConfigureAwait(false);
        if (details is null)
        {
            try
            {
                var movie = await _tmdbClient.GetMovieAsync(tmdbId, null, ct).ConfigureAwait(false);
                if (movie is null)
                {
                    return null;
                }

                details = MapMovieDetails(movie);
                await _db.UpsertTmdbMetadataAsync(details, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "TMDB details fetch failed for gostream file {Path} tmdb={Tmdb}", path, tmdbId);
                return null;
            }
        }

        return new EnrichedGostreamMovie(details.TmdbId, details, await FuseMediaSourceAsync(path, ct).ConfigureAwait(false));
    }

    private static MediaSourceInfo SelectDefaultVariant(IReadOnlyList<MediaSourceInfo> sources)
        => sources
            .OrderByDescending(s => ScoreVariantPath(s.Path ?? string.Empty))
            .ThenBy(s => s.Path, StringComparer.Ordinal)
            .First();

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

    private static ChannelItemInfo BuildMovieItemFromMetadata(TmdbMetadataRow meta, IReadOnlyList<MediaSourceInfo> sources, List<string> tags)
    {
        var item = new ChannelItemInfo
        {
            Id = ChannelItemId.ForMovie(meta.TmdbId).Encode(),
            Name = meta.Title,
            OriginalTitle = meta.OriginalTitle,
            Overview = meta.Overview,
            Type = ChannelItemType.Media,
            ContentType = ChannelMediaContentType.Movie,
            MediaType = ChannelMediaType.Video,
            ImageUrl = meta.PosterUrl,
            ProductionYear = meta.Year,
            PremiereDate = meta.Year is { } y ? new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc) : null,
            CommunityRating = meta.CommunityRating is { } cr ? (float)cr : null,
            OfficialRating = meta.OfficialRating,
            Tags = tags,
            MediaSources = sources.ToList(),
        };

        if (meta.Genres is { Length: > 0 })
        {
            item.Genres = meta.Genres.ToList();
        }

        if (meta.RuntimeMinutes is { } rt && rt > 0)
        {
            item.RunTimeTicks = TimeSpan.FromMinutes(rt).Ticks;
        }

        item.ProviderIds["Tmdb"] = meta.TmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return item;
    }

    private static TmdbMetadataRow MapMovieDetails(TmdbMovieDetails movie)
    {
        var title = !string.IsNullOrWhiteSpace(movie.Title) ? movie.Title! : (movie.OriginalTitle ?? string.Empty);
        var year = ParseYear(movie.ReleaseDate);
        return new TmdbMetadataRow(
            movie.Id,
            "movie",
            title,
            year,
            movie.Overview,
            BuildPosterUrl(movie.PosterPath),
            BuildPosterUrl(movie.BackdropPath),
            movie.Genres,
            null,
            movie.VoteAverage,
            movie.OriginalTitle,
            DateTimeOffset.UtcNow,
            movie.Runtime > 0 ? movie.Runtime : null);
    }

    private static bool TryParseGostreamMovieName(string path, out string title, out int? year)
    {
        var stem = Path.GetFileNameWithoutExtension(path).Replace('_', ' ');
        stem = Regex.Replace(stem, @"\b(480p|720p|1080p|2160p|4k|hdr|dv|atmos|remux|x264|x265|h264|h265|hevc|aac|5\.1|7\.1)\b", string.Empty, RegexOptions.IgnoreCase).Trim();
        stem = Regex.Replace(stem, @"\b[a-f0-9]{8}\b$", string.Empty, RegexOptions.IgnoreCase).Trim();
        var m = Regex.Match(stem, @"^(?<title>.+?)\s+(?<year>(19|20)\d{2})(\b|$)");
        if (m.Success)
        {
            title = Regex.Replace(m.Groups["title"].Value, @"\s+", " ").Trim();
            year = int.Parse(m.Groups["year"].Value, System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            title = Regex.Replace(stem, @"\s+", " ").Trim();
            year = null;
        }

        return !string.IsNullOrWhiteSpace(title);
    }

    private static int? ParseYear(string? date)
        => !string.IsNullOrWhiteSpace(date) && date.Length >= 4 && int.TryParse(date[..4], out var y) ? y : null;

    private static string? BuildPosterUrl(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : "https://image.tmdb.org/t/p/w500" + path;

    private async Task<ChannelItemInfo> BuildOrphanMovieItemAsync(GostreamFileEntry o, CancellationToken ct)
    {
        var id = ChannelItemId.ForOrphanPath(o.Path).Encode();
        return new ChannelItemInfo
        {
            Id = id,
            Name = Path.GetFileNameWithoutExtension(o.Path),
            Type = ChannelItemType.Media,
            ContentType = ChannelMediaContentType.Movie,
            MediaType = ChannelMediaType.Video,
            Tags = new List<string> { "external" },
            MediaSources = new List<MediaSourceInfo> { await FuseMediaSourceAsync(o.Path, ct).ConfigureAwait(false) },
        };
    }

    private Task<MediaSourceInfo> FuseMediaSourceAsync(string path, CancellationToken ct)
        => PhantomMediaSourceBuilder.CreateFileMediaSourceAsync(path, _mediaEncoder, _logger, ct);
}
