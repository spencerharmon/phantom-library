using System;
using System.Collections.Generic;
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

    private sealed record CandidateAddRequest(GostreamAddRequest Request, MagnetCandidate Magnet, bool FromCache);

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

        if (!ChannelItemId.TryParse(item.ExternalId, out var parsed))
        {
            return MaterialisationOutcome.ErrorResult(
                $"Unparseable channel external id: {item.ExternalId}");
        }

        // Jellyfin's channel item DTO/DB row can carry the correct Phantom
        // ChannelId while ILibraryManager.GetItemById returns a BaseItem whose
        // ChannelId does not round-trip reliably. SourceType=Channel plus our
        // stable ExternalId is the authoritative materialise contract.
        if (!ChannelIds.IsPhantom(item.ChannelId))
        {
            _logger.LogDebug(
                "Materialise accepting channel item {ExternalId} with non-phantom runtime ChannelId={ChannelId}; ExternalId parsed as Phantom item",
                item.ExternalId,
                item.ChannelId);
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

            var candidates = await BuildGostreamRequestsAsync(
                tmdbId, type, season, episode, imdb, unavailKey, ct).ConfigureAwait(false);
            var addResult = await AddWithCandidateRetryAsync(
                candidates, tmdbId, type, season, episode, imdb, unavailKey, ct).ConfigureAwait(false);
            var fusePath = type == "movie"
                ? GostreamPathResolver.ResolveMoviePath(addResult.FusePath)
                : GostreamPathResolver.ResolveEpisodePath(addResult.FusePath);
            await WaitForFusePathAsync(fusePath, ct).ConfigureAwait(false);

            await _db.InsertMaterialisedStateAsync(
                tmdbId, type, sSentinel, eSentinel,
                stubPath: addResult.StubPath,
                fusePath: fusePath,
                ct).ConfigureAwait(false);

            // Post-flight refresh: channel now emits real MediaSource;
            // probe runs against the FUSE path. If this throws we still
            // wrote materialised_state — next browse picks up the new
            // path even without an immediate refresh.
            try
            {
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
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception refreshEx)
            {
                _logger.LogWarning(
                    refreshEx,
                    "Post-flight RefreshChannelItem failed for {External}; channel will refresh on next browse",
                    externalId);
            }

            _state.BumpDataVersion(channelKind);

            var outcome = MaterialisationOutcome.Success(fusePath, addResult.StubPath);
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
                await _db.DeleteMaterialiseInFlightAsync(tmdbId, type, sSentinel, eSentinel, CancellationToken.None)
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
    /// Builds candidate gostream add requests. Sources Title/Year from
    /// <c>tmdb_metadata</c> (the channel-arch ground truth; never from
    /// a BaseItem). Tries an unfailed cached magnet first, then ranked
    /// indexer candidates, skipping live candidate-level failures. Writes
    /// an item-level <c>unavailable_marker</c> only when no candidate is
    /// available.
    /// </summary>
    private async Task<IReadOnlyList<CandidateAddRequest>> BuildGostreamRequestsAsync(
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

        var candidates = new List<CandidateAddRequest>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cached = await _db.GetCachedMagnetAsync(magnetKey, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            var cachedCandidate = new MagnetCandidate(cached.Magnet, cached.InfoHash, cached.Size, cached.Seeders, cached.Indexer);
            if (await IsCandidateAllowedAsync(magnetKey, cachedCandidate, ct).ConfigureAwait(false))
            {
                candidates.Add(BuildCandidateRequest(meta, type, tmdbId, imdb, season, episode, cachedCandidate, cfg, fromCache: true));
                seen.Add(cachedCandidate.Magnet);
            }
        }

        var probe = await _magnetSelector.ProbeAsync(
            tmdbId, imdb, type, season, episode,
            meta.Title, meta.Year,
            ct).ConfigureAwait(false);
        if (probe.Outcome == MagnetProbeOutcome.Available)
        {
            foreach (var magnet in probe.Candidates)
            {
                if (string.IsNullOrWhiteSpace(magnet.Magnet) || !seen.Add(magnet.Magnet))
                {
                    continue;
                }

                if (!await IsCandidateAllowedAsync(magnetKey, magnet, ct).ConfigureAwait(false))
                {
                    continue;
                }

                candidates.Add(BuildCandidateRequest(meta, type, tmdbId, imdb, season, episode, magnet, cfg, fromCache: false));
            }
        }

        if (candidates.Count == 0)
        {
            if (probe.Outcome == MagnetProbeOutcome.IndeterminateTransient)
            {
                throw new InvalidOperationException(
                    $"Source availability transient for {metadataType}/{tmdbId} (season={season} episode={episode}): {probe.ErrorKind} {probe.ErrorMessage}");
            }

            await _db.MarkUnavailableAsync(
                unavailableKey,
                retryAfter: TimeSpan.FromHours(cfg.UnavailableRetryAfterHours),
                ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"No unfailed magnet candidates for {metadataType}/{tmdbId} (season={season} episode={episode}); marked unavailable for {cfg.UnavailableRetryAfterHours}h");
        }

        return candidates;
    }

    private static CandidateAddRequest BuildCandidateRequest(
        TmdbMetadataRow meta,
        string type,
        int tmdbId,
        string? imdb,
        int? season,
        int? episode,
        MagnetCandidate magnet,
        PluginConfiguration cfg,
        bool fromCache)
        => new(
            new GostreamAddRequest
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
            },
            magnet,
            fromCache);

    private async Task<bool> IsCandidateAllowedAsync(MagnetCacheKey key, MagnetCandidate magnet, CancellationToken ct)
        => await _db.GetMagnetFailureAsync(ToFailureKey(key, magnet), ct).ConfigureAwait(false) is null;

    private async Task<GostreamAddResult> AddWithCandidateRetryAsync(
        IReadOnlyList<CandidateAddRequest> candidates,
        int tmdbId,
        string type,
        int? season,
        int? episode,
        string? imdb,
        UnavailableKey unavailableKey,
        CancellationToken ct)
    {
        Exception? last = null;
        var cfg = _configProvider();
        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await _gostream.AddAsync(candidate.Request, ct).ConfigureAwait(false);
                if (!candidate.FromCache)
                {
                    try
                    {
                        await _db.PutCachedMagnetAsync(
                            new MagnetCacheKey(tmdbId, imdb, type, season, episode, cfg.SourcePickerPreset),
                            new MagnetCacheEntry
                            {
                                Magnet = candidate.Magnet.Magnet,
                                InfoHash = candidate.Magnet.InfoHash,
                                Size = candidate.Magnet.Size,
                                Seeders = candidate.Magnet.Seeders,
                                Indexer = candidate.Magnet.Indexer,
                                CachedAt = DateTimeOffset.UtcNow,
                                Ttl = TimeSpan.FromHours(cfg.MagnetCacheTtlHours),
                                Source = "user",
                            },
                            ct).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Failed to cache successful magnet for {Type}/{Tmdb}; materialised_state write will still proceed",
                            type,
                            tmdbId);
                    }
                }

                return result;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GostreamBadRequestException ex)
            {
                last = ex;
                await MarkCandidateFailedAsync(candidate, tmdbId, imdb, type, season, episode, cfg, "bad_request", TimeSpan.FromHours(cfg.MagnetCacheTtlHours), ct).ConfigureAwait(false);
            }
            catch (GostreamNoValidFilesException ex)
            {
                last = ex;
                await MarkCandidateFailedAsync(candidate, tmdbId, imdb, type, season, episode, cfg, CandidateRejectReason(ex), TimeSpan.FromHours(cfg.MagnetCacheTtlHours), ct).ConfigureAwait(false);
            }
            catch (GostreamTimeoutException ex)
            {
                last = ex;
                await MarkCandidateFailedAsync(candidate, tmdbId, imdb, type, season, episode, cfg, "metadata_timeout", TimeSpan.FromHours(cfg.UnavailableRetryAfterHours), ct).ConfigureAwait(false);
            }
            catch (GostreamServerException)
            {
                throw;
            }
        }

        await _db.MarkUnavailableAsync(
            unavailableKey,
            retryAfter: TimeSpan.FromHours(cfg.UnavailableRetryAfterHours),
            ct).ConfigureAwait(false);
        throw new InvalidOperationException(
            $"All {candidates.Count} magnet candidates failed for {type}/{tmdbId} s{season}e{episode}; marked unavailable for {cfg.UnavailableRetryAfterHours}h",
            last);
    }

    private static string CandidateRejectReason(GostreamNoValidFilesException ex)
        => ex.Message.Contains("target_episode_not_found", StringComparison.OrdinalIgnoreCase)
            ? "target_episode_not_found"
            : "no_valid_files";

    private async Task MarkCandidateFailedAsync(
        CandidateAddRequest candidate,
        int tmdbId,
        string? imdb,
        string type,
        int? season,
        int? episode,
        PluginConfiguration cfg,
        string reason,
        TimeSpan ttl,
        CancellationToken ct)
    {
        var key = new MagnetCacheKey(tmdbId, imdb, type, season, episode, cfg.SourcePickerPreset);
        var now = DateTimeOffset.UtcNow;
        await _db.MarkMagnetFailedAsync(
            ToFailureKey(key, candidate.Magnet),
            new MagnetFailureEntry
            {
                InfoHash = candidate.Magnet.InfoHash,
                Reason = reason,
                FailedAt = now,
                RetryAfter = now.Add(ttl),
            },
            ct).ConfigureAwait(false);

        if (candidate.FromCache)
        {
            await _db.DeleteCachedMagnetAsync(key, ct).ConfigureAwait(false);
        }

        _logger.LogWarning(
            "Marked magnet candidate failed for {Type}/{Tmdb} s{Season}e{Episode}: {Reason} ({Indexer}, {InfoHash})",
            type,
            tmdbId,
            season,
            episode,
            reason,
            candidate.Magnet.Indexer,
            candidate.Magnet.InfoHash);
    }

    private static MagnetFailureKey ToFailureKey(MagnetCacheKey key, MagnetCandidate magnet)
        => new(key.TmdbId, key.ImdbId, key.Type, key.Season, key.Episode, key.Preset, magnet.Magnet);

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
