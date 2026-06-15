using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Channel-arch materialiser. Replaces the legacy file-on-disk
/// promote flow with:
///
///   1. Reject unsupported types (series-level, season-level).
///   2. Idempotency: skip if a materialised_state row already exists
///      for the (tmdb,type,season,episode) tuple, or if another
///      materialise is in flight for that tuple.
///   3. Unavailable-marker gate to short-circuit recently-failed
///      attempts.
///   4. Resolve series IMDB via <see cref="TmdbExternalIdResolver"/>
///      (only required for episodes; gostream wants series_imdb).
///   5. Pre-flight DataVersion bump + RefreshChannelItem so the
///      channel surfaces 'Materialising' state.
///   6. Build a gostream add request from tmdb_metadata + a magnet
///      chosen by <see cref="MagnetSelector"/>; cache the magnet for
///      future runs.
///   7. Wait for the FUSE path to appear; insert materialised_state.
///   8. Post-flight RefreshChannelItem with ForceProbe=true so the
///      channel re-emits the item with the real MediaSource and the
///      probe re-runs. Second DataVersion bump.
///   9. Always delete the in-flight row in finally (sweeper handles
///      crashes).
///
/// Plan §4.2.
/// </summary>
public sealed class Materialiser : IMaterialiser
{
    private readonly ILibraryManager _libraryManager;
    private readonly PhantomDb _db;
    private readonly IGostreamClient _gostream;
    private readonly MagnetSelector _magnetSelector;
    private readonly TmdbExternalIdResolver _externalIds;
    private readonly IChannelItemRefreshManager _refreshManager;
    private readonly ChannelStateProvider _state;
    private readonly Func<PluginConfiguration> _configProvider;
    private readonly ILogger<Materialiser> _logger;

    public Materialiser(
        ILibraryManager libraryManager,
        PhantomDb db,
        IGostreamClient gostream,
        MagnetSelector magnetSelector,
        TmdbExternalIdResolver externalIds,
        IChannelItemRefreshManager refreshManager,
        ChannelStateProvider state,
        ILogger<Materialiser> logger)
        : this(libraryManager, db, gostream, magnetSelector, externalIds, refreshManager, state, logger,
               () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal Materialiser(
        ILibraryManager libraryManager,
        PhantomDb db,
        IGostreamClient gostream,
        MagnetSelector magnetSelector,
        TmdbExternalIdResolver externalIds,
        IChannelItemRefreshManager refreshManager,
        ChannelStateProvider state,
        ILogger<Materialiser> logger,
        Func<PluginConfiguration> configProvider)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _gostream = gostream ?? throw new ArgumentNullException(nameof(gostream));
        _magnetSelector = magnetSelector ?? throw new ArgumentNullException(nameof(magnetSelector));
        _externalIds = externalIds ?? throw new ArgumentNullException(nameof(externalIds));
        _refreshManager = refreshManager ?? throw new ArgumentNullException(nameof(refreshManager));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    public event EventHandler<MaterialisationLifecycleEvent>? LifecycleChanged;

    public async Task<MaterialisationOutcome> MaterialiseAsync(
        Guid jellyfinItemId, MaterialiseTrigger trigger, CancellationToken ct)
    {
        var item = _libraryManager.GetItemById(jellyfinItemId);
        if (item is null)
        {
            return MaterialisationOutcome.ErrorResult($"BaseItem {jellyfinItemId} not found");
        }

        if (item.SourceType != SourceType.Channel)
        {
            return MaterialisationOutcome.ErrorResult("Item is not a channel item");
        }

        if (!ChannelIds.IsPhantom(item.ChannelId))
        {
            return MaterialisationOutcome.ErrorResult("Item is not in a phantom-library channel");
        }

        if (!ChannelItemId.TryParse(item.ExternalId, out var parsed))
        {
            return MaterialisationOutcome.ErrorResult(
                $"Unparseable channel external id: {item.ExternalId}");
        }

        return parsed.Kind switch
        {
            ChannelItemId.KindMovie =>
                await MaterialiseAsync(parsed.TmdbId!.Value, "movie", null, null, trigger, ct)
                    .ConfigureAwait(false),
            ChannelItemId.KindEpisode =>
                await MaterialiseAsync(
                        parsed.TmdbId!.Value, "episode",
                        parsed.Season, parsed.Episode,
                        trigger, ct)
                    .ConfigureAwait(false),
            ChannelItemId.KindSeries =>
                MaterialisationOutcome.ErrorResult(
                    "Series-level materialise not supported; materialise individual episodes"),
            ChannelItemId.KindSeason =>
                MaterialisationOutcome.ErrorResult(
                    "Season-level materialise not supported; materialise individual episodes"),
            ChannelItemId.KindOrphan =>
                MaterialisationOutcome.ErrorResult(
                    "Orphan gostream files are already materialised"),
            _ => MaterialisationOutcome.ErrorResult($"Unknown item kind: {parsed.Kind}"),
        };
    }

    public async Task<MaterialisationOutcome> MaterialiseAsync(
        int tmdbId, string type, int? season, int? episode,
        MaterialiseTrigger trigger,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        if (type == "series" || type == "season")
        {
            return MaterialisationOutcome.ErrorResult(
                "Series-level materialise not supported; materialise individual episodes");
        }

        if (type != "movie" && type != "episode")
        {
            return MaterialisationOutcome.ErrorResult($"Unsupported type: {type}");
        }

        if (type == "episode" && (!season.HasValue || !episode.HasValue))
        {
            return MaterialisationOutcome.ErrorResult(
                "Episode materialise requires both season and episode numbers");
        }

        var (sSentinel, eSentinel) = ChannelItemId.ToSentinels(season, episode);

        if (await _db.GetMaterialisedStateAsync(tmdbId, type, sSentinel, eSentinel, ct)
                .ConfigureAwait(false) is not null)
        {
            return MaterialisationOutcome.Duplicate;
        }

        if (await _db.IsMaterialiseInFlightAsync(tmdbId, type, sSentinel, eSentinel, ct)
                .ConfigureAwait(false))
        {
            return MaterialisationOutcome.AlreadyInProgress;
        }

        // Resolve IMDB. Movies use their own (optional for some indexers);
        // episodes use the parent SERIES IMDB (gostream requires it).
        var imdbLookupType = type == "episode" ? "series" : "movie";
        var imdb = await _externalIds.GetImdbIdAsync(tmdbId, imdbLookupType, ct)
            .ConfigureAwait(false);

        if (type == "episode" && string.IsNullOrEmpty(imdb))
        {
            return MaterialisationOutcome.ErrorResult(
                $"Could not resolve IMDB id for series tmdb={tmdbId}; gostream requires series_imdb for episodes");
        }

        // Unavailable-marker gate. The DB helper uses BindKey which
        // collapses nullable season/episode (NULL → 0); we match by
        // passing nullable variants through verbatim so a movie's key
        // (null, null, ...) and an episode's key (1, 2, ...) compare
        // correctly against marker rows that were written via the same
        // helper.
        var unavailKey = new UnavailableKey(
            TmdbId: tmdbId,
            ImdbId: imdb,
            Type: type,
            Season: season,
            Episode: episode);
        var marker = await _db.IsMarkedUnavailableAsync(unavailKey, ct).ConfigureAwait(false);
        if (marker.HasValue)
        {
            return MaterialisationOutcome.ErrorResult(
                $"Marked unavailable until {marker.Value:O}; skipping {type}/{tmdbId} s{season} e{episode}");
        }

        var channelKind = type == "movie" ? ChannelStateProvider.KindMovies : ChannelStateProvider.KindShows;
        var channelId = ChannelIds.For(channelKind);
        var externalId = type == "movie"
            ? ChannelItemId.ForMovie(tmdbId).Encode()
            : ChannelItemId.ForEpisode(tmdbId, season!.Value, episode!.Value).Encode();

        LifecycleChanged?.Invoke(this, new MaterialisationLifecycleEvent(
            Guid.Empty, MaterialisationLifecyclePhase.Started, null));

        // BLOCKER 2 fix: the in-flight write + pre-flight refresh sit
        // inside the try/finally so a throw from either still deletes
        // the in-flight row.
        await _db.UpsertMaterialiseInFlightAsync(tmdbId, type, sSentinel, eSentinel, ct)
            .ConfigureAwait(false);
        try
        {
            _state.BumpDataVersion(channelKind);

            try
            {
                await _refreshManager.RefreshChannelItemAsync(
                    channelId,
                    externalId,
                    new ChannelItemRefreshOptions
                    {
                        ForceUpdate = true,
                        ForceProbe = false,
                        InvalidateMediaInfoCache = true,
                    },
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception refreshEx)
            {
                _logger.LogWarning(
                    refreshEx,
                    "Pre-flight RefreshChannelItem failed for {External}; badge may stay 'Phantom' during materialise",
                    externalId);
            }

            var addRequest = await BuildGostreamRequestAsync(
                tmdbId, type, season, episode, imdb, unavailKey, ct).ConfigureAwait(false);
            var addResult = await _gostream.AddAsync(addRequest, ct).ConfigureAwait(false);
            await WaitForFusePathAsync(addResult.FusePath, ct).ConfigureAwait(false);

            await _db.InsertMaterialisedStateAsync(
                tmdbId, type, sSentinel, eSentinel,
                stubPath: addResult.StubPath,
                fusePath: addResult.FusePath,
                ct).ConfigureAwait(false);

            // Post-flight refresh: channel now emits real MediaSource;
            // probe runs against the FUSE path. If this throws we still
            // wrote materialised_state — next browse picks up the new
            // path even without an immediate refresh.
            await _refreshManager.RefreshChannelItemAsync(
                channelId,
                externalId,
                new ChannelItemRefreshOptions
                {
                    ForceUpdate = true,
                    ForceProbe = true,
                    InvalidateMediaInfoCache = true,
                },
                ct).ConfigureAwait(false);

            _state.BumpDataVersion(channelKind);

            var outcome = MaterialisationOutcome.Success(addResult.FusePath, addResult.StubPath);
            LifecycleChanged?.Invoke(this, new MaterialisationLifecycleEvent(
                Guid.Empty, MaterialisationLifecyclePhase.Finished, outcome));
            _ = trigger; // kept for symmetry with the legacy ctor and for future logging hooks
            return outcome;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "MaterialiseAsync failed for {Type}/{Tmdb} (s={Season} e={Episode})",
                type, tmdbId, season, episode);
            var outcome = MaterialisationOutcome.ErrorResult(ex.Message);
            LifecycleChanged?.Invoke(this, new MaterialisationLifecycleEvent(
                Guid.Empty, MaterialisationLifecyclePhase.Finished, outcome));
            return outcome;
        }
        finally
        {
            try
            {
                await _db.DeleteMaterialiseInFlightAsync(tmdbId, type, sSentinel, eSentinel, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Failed to delete in-flight row for {Type}/{Tmdb}; will be swept on next startup",
                    type, tmdbId);
            }
        }
    }

    /// <summary>
    /// Builds the gostream add request. Sources Title/Year from
    /// <c>tmdb_metadata</c> (the channel-arch ground truth; never from
    /// a BaseItem). Picks the magnet from <c>magnet_cache</c> if present
    /// and unexpired, otherwise via <see cref="MagnetSelector"/>; writes
    /// an <c>unavailable_marker</c> row + throws when no magnet is
    /// available so future attempts within the configured retry window
    /// short-circuit at the gate above (BLOCKER 2 fix).
    /// </summary>
    private async Task<GostreamAddRequest> BuildGostreamRequestAsync(
        int tmdbId,
        string type,
        int? season,
        int? episode,
        string? imdb,
        UnavailableKey unavailableKey,
        CancellationToken ct)
    {
        var metadataType = type == "movie" ? "movie" : "series";
        var meta = await _db.GetTmdbMetadataAsync(tmdbId, metadataType, ct).ConfigureAwait(false);
        if (meta is null)
        {
            throw new InvalidOperationException(
                $"tmdb_metadata miss for {metadataType}/{tmdbId}; cannot build gostream request");
        }

        if (string.IsNullOrEmpty(meta.Title))
        {
            throw new InvalidOperationException(
                $"tmdb_metadata row for {metadataType}/{tmdbId} has empty Title");
        }

        if (!meta.Year.HasValue)
        {
            throw new InvalidOperationException(
                $"tmdb_metadata row for {metadataType}/{tmdbId} has null Year; gostream requires year");
        }

        var cfg = _configProvider();
        var magnetKey = new MagnetCacheKey(
            TmdbId: tmdbId,
            ImdbId: imdb,
            Type: type,
            Season: season,
            Episode: episode,
            Preset: cfg.SourcePickerPreset);

        var cachedMagnet = await _db.GetCachedMagnetAsync(magnetKey, ct).ConfigureAwait(false);
        MagnetCandidate? magnet = cachedMagnet is not null
            ? new MagnetCandidate(
                cachedMagnet.Magnet,
                cachedMagnet.InfoHash,
                cachedMagnet.Size,
                cachedMagnet.Seeders,
                cachedMagnet.Indexer)
            : await _magnetSelector.SelectAsync(
                tmdbId, imdb, type, season, episode,
                meta.Title, meta.Year,
                ct).ConfigureAwait(false);

        if (magnet is null || string.IsNullOrEmpty(magnet.Magnet))
        {
            await _db.MarkUnavailableAsync(
                unavailableKey,
                retryAfter: TimeSpan.FromHours(cfg.UnavailableRetryAfterHours),
                ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"MagnetSelector returned no magnet for {metadataType}/{tmdbId} (season={season} episode={episode}); marked unavailable for {cfg.UnavailableRetryAfterHours}h");
        }

        if (cachedMagnet is null)
        {
            await _db.PutCachedMagnetAsync(
                magnetKey,
                new MagnetCacheEntry
                {
                    Magnet = magnet.Magnet,
                    InfoHash = magnet.InfoHash,
                    Size = magnet.Size,
                    Seeders = magnet.Seeders,
                    Indexer = magnet.Indexer,
                    CachedAt = DateTimeOffset.UtcNow,
                    Ttl = TimeSpan.FromHours(cfg.MagnetCacheTtlHours),
                    Source = "user",
                },
                ct).ConfigureAwait(false);
        }

        return new GostreamAddRequest
        {
            Type = type,
            Tmdb = tmdbId,
            Imdb = type == "movie" ? imdb : null,
            SeriesImdb = type == "episode" ? imdb : null,
            Title = meta.Title,
            Year = meta.Year,
            Season = season,
            Episode = episode,
            Magnet = magnet.Magnet,
            MinQuality = string.IsNullOrWhiteSpace(cfg.GostreamMinQuality) ? null : cfg.GostreamMinQuality,
        };
    }

    /// <summary>
    /// Poll the FUSE mount until the file appears, bounded by the
    /// configured timeout. Best-effort — a timeout does not roll the
    /// materialise back; the post-flight RefreshChannelItem still fires
    /// and on next browse the channel emits the new path. We log and
    /// continue.
    /// </summary>
    private async Task WaitForFusePathAsync(string fusePath, CancellationToken ct)
    {
        var cfg = _configProvider();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, cfg.FusePathWaitTimeoutSeconds));
        var pollMs = Math.Max(50, cfg.FusePathPollIntervalMilliseconds);

        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (System.IO.File.Exists(fusePath))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "FUSE path check threw for {Path}; will retry", fusePath);
            }

            await Task.Delay(pollMs, ct).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "FUSE path {Path} did not appear within {Seconds}s; persisting materialised_state anyway",
            fusePath,
            cfg.FusePathWaitTimeoutSeconds);
    }
}
