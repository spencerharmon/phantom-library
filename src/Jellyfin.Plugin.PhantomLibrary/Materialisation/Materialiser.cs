using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
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
    private readonly GostreamHeavyLimiter _gostreamLimiter;
    private readonly IChannelItemRefreshManager _refreshManager;
    private readonly ChannelStateProvider _state;
    private readonly Func<PluginConfiguration> _configProvider;
    private readonly ILogger<Materialiser> _logger;

    private sealed record CandidateAddRequest(GostreamAddRequest Request, MagnetCandidate Magnet, bool FromCache, int Rank = 0, SourceCandidateRow? SourceRow = null);
    private sealed record CandidatePlan(IReadOnlyList<CandidateAddRequest> InitialCandidates, Task<IReadOnlyList<CandidateAddRequest>>? FreshCandidatesTask);
    private sealed record CandidateValidation(CandidateAddRequest Candidate, GostreamValidateResult Result, string SessionId);
    private sealed record CandidateAddResult(GostreamAddResult AddResult, string FusePath);

    private static readonly string[] RequiredAudioLanguages = { "eng", "en", "english" };

    public Materialiser(
        ILibraryManager libraryManager,
        PhantomDb db,
        IGostreamClient gostream,
        MagnetSelector magnetSelector,
        TmdbExternalIdResolver externalIds,
        GostreamHeavyLimiter gostreamLimiter,
        IChannelItemRefreshManager refreshManager,
        ChannelStateProvider state,
        ILogger<Materialiser> logger)
        : this(libraryManager, db, gostream, magnetSelector, externalIds, gostreamLimiter, refreshManager, state, logger,
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
        : this(libraryManager, db, gostream, magnetSelector, externalIds, new GostreamHeavyLimiter(configProvider), refreshManager, state, logger, configProvider)
    {
    }

    internal Materialiser(
        ILibraryManager libraryManager,
        PhantomDb db,
        IGostreamClient gostream,
        MagnetSelector magnetSelector,
        TmdbExternalIdResolver externalIds,
        GostreamHeavyLimiter gostreamLimiter,
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
        _gostreamLimiter = gostreamLimiter ?? throw new ArgumentNullException(nameof(gostreamLimiter));
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

    public Task<MaterialisationOutcome> MaterialiseAsync(
        int tmdbId, string type, int? season, int? episode,
        MaterialiseTrigger trigger,
        CancellationToken ct)
        => MaterialiseCoreAsync(tmdbId, type, season, episode, selectedCandidate: null, trigger, ct);

    public Task<MaterialisationOutcome> MaterialiseAsync(
        int tmdbId, string type, int? season, int? episode,
        MagnetCandidate selectedCandidate,
        MaterialiseTrigger trigger,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(selectedCandidate);
        return MaterialiseCoreAsync(tmdbId, type, season, episode, selectedCandidate, trigger, ct);
    }

    private async Task<MaterialisationOutcome> MaterialiseCoreAsync(
        int tmdbId, string type, int? season, int? episode,
        MagnetCandidate? selectedCandidate,
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

        // User-initiated availability wiring (ROI P6): every materialise —
        // whether triggered by playback, an explicit materialise, autopilot
        // prefetch, or favourite ingest — is a user-initiated availability
        // action. Bump this item's availability-row priority and stamp the
        // user-activity yield marker so the background sweep (priority-first
        // + marker-honouring) preempts to it and backs off the UI. Best
        // effort: a promote failure must never block the materialise itself.
        try
        {
            await _db.PromoteForUserActivityAsync(tmdbId, type, season, episode, DateTimeOffset.UtcNow, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PromoteForUserActivity failed for {Type}/{Tmdb} (s={Season} e={Episode}); continuing materialise", type, tmdbId, season, episode);
        }

        // Opportunistic magnet-cache prefetch (ROI P6, item 2a): the SAME
        // materialise trigger — playback, explicit materialise, autopilot
        // prefetch, favourite ingest — also enqueues a HIGH-priority
        // magnet_cache_jobs row for this touched item, preempting any
        // competing low-priority background-sweep job. Best effort, same as
        // the promote above: never block the materialise itself.
        try
        {
            await _db.EnqueueOpportunisticMagnetCacheJobAsync(tmdbId, type, season, episode, _configProvider().SourcePickerPreset, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EnqueueOpportunisticMagnetCacheJob failed for {Type}/{Tmdb} (s={Season} e={Episode}); continuing materialise", type, tmdbId, season, episode);
        }

        var existingState = await _db.GetMaterialisedStateAsync(tmdbId, type, sSentinel, eSentinel, ct)
            .ConfigureAwait(false);
        if (existingState is not null)
        {
            var existingPath = type == "movie"
                ? GostreamPathResolver.ResolveMoviePath(existingState.FusePath)
                : GostreamPathResolver.ResolveEpisodePath(existingState.FusePath);
            if (File.Exists(existingPath))
            {
                return MaterialisationOutcome.Duplicate;
            }

            _logger.LogWarning(
                "Materialised state for {Type}/{Tmdb} (s={Season} e={Episode}) points at missing file {Path}; re-materialising",
                type,
                tmdbId,
                season,
                episode,
                existingPath);
            await _db.DeleteMaterialisedStateAsync(tmdbId, type, sSentinel, eSentinel, ct)
                .ConfigureAwait(false);
        }

        // Steal-if-stale: a claim older than MaterialiseInFlightStaleMinutes
        // can only be a leaked row from a hard-killed materialise (its
        // finally block never ran to delete it) — a genuinely still-
        // running materialise always has a fresh started_at. Reclaiming
        // it inline here means recovery no longer depends on the
        // once-at-startup MaterialiseInFlightSweeper; a leaked row is
        // unstuck on the very next retry past the threshold, without
        // requiring an external process restart.
        var cfg = _configProvider();
        var staleThreshold = TimeSpan.FromMinutes(Math.Max(1, cfg.MaterialiseInFlightStaleMinutes));
        var claimed = await _db.TryInsertMaterialiseInFlightAsync(tmdbId, type, sSentinel, eSentinel, ct, staleThreshold)
            .ConfigureAwait(false);
        if (!claimed)
        {
            return MaterialisationOutcome.AlreadyInProgress;
        }

        try
        {
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

            var candidatePlan = await BuildGostreamRequestsAsync(
                tmdbId, type, season, episode, imdb, unavailKey, selectedCandidate, ct).ConfigureAwait(false);
            var validatedCandidates = await ValidateCandidatesAsync(
                candidatePlan.InitialCandidates,
                candidatePlan.FreshCandidatesTask,
                tmdbId, type, season, episode, imdb, unavailKey, ct).ConfigureAwait(false);
            var addResult = await AddWithCandidateRetryAsync(
                validatedCandidates, tmdbId, type, season, episode, imdb, unavailKey, ct).ConfigureAwait(false);

            await _db.InsertMaterialisedStateAsync(
                tmdbId, type, sSentinel, eSentinel,
                stubPath: addResult.AddResult.StubPath,
                fusePath: addResult.FusePath,
                ct).ConfigureAwait(false);

            // Post-flight refresh: channel now emits real MediaSource.
            // Invalidate dynamic media-source cache before queueing the
            // ForceProbe refresh; Jellyfin's channel refresh queues the
            // metadata probe asynchronously, so invalidating after queueing
            // can let the probe reuse an older pack file's cached source.
            await TryInvalidateMediaInfoCacheAsync(channelId, externalId).ConfigureAwait(false);
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
                    "Post-flight RefreshChannelItem failed for {External}; forcing media-info cache invalidation so next play does not reuse stale opener",
                    externalId);
                await TryInvalidateMediaInfoCacheAsync(channelId, externalId).ConfigureAwait(false);
            }

            _state.BumpDataVersion(channelKind);

            var outcome = MaterialisationOutcome.Success(addResult.FusePath, addResult.AddResult.StubPath);
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

    private async Task TryInvalidateMediaInfoCacheAsync(Guid channelId, string externalId)
    {
        try
        {
            await _refreshManager.RefreshChannelItemAsync(
                channelId,
                externalId,
                new ChannelItemRefreshOptions
                {
                    ForceUpdate = false,
                    ForceProbe = false,
                    InvalidateMediaInfoCache = true,
                },
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Post-flight media-info cache invalidation failed for {External}; next browse/DataVersion refresh must heal stale source",
                externalId);
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
    private async Task<CandidatePlan> BuildGostreamRequestsAsync(
        int tmdbId,
        string type,
        int? season,
        int? episode,
        string? imdb,
        UnavailableKey unavailableKey,
        MagnetCandidate? selectedCandidate,
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

        var (sSentinel, eSentinel) = ChannelItemId.ToSentinels(season, episode);
        var candidates = new List<CandidateAddRequest>();
        if (selectedCandidate is not null)
        {
            await _db.UpsertSourceCandidatesAsync(
                tmdbId,
                type,
                sSentinel,
                eSentinel,
                cfg.SourcePickerPreset,
                new[] { selectedCandidate },
                "selected_source",
                TimeSpan.FromHours(Math.Max(1, cfg.MagnetCacheTtlHours)),
                ct).ConfigureAwait(false);
            candidates.Add(BuildCandidateRequest(meta, type, tmdbId, imdb, season, episode, selectedCandidate, cfg, fromCache: false, rank: 1));
            return new CandidatePlan(candidates, null);
        }

        var freshCandidatesTask = BuildFreshMaterialiseCandidatesAsync(
            tmdbId,
            type,
            season,
            episode,
            imdb,
            metadataType,
            meta,
            cfg,
            magnetKey,
            sSentinel,
            eSentinel,
            ct);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var cached = await _db.GetCachedMagnetAsync(magnetKey, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            var cachedCandidate = new MagnetCandidate(cached.Magnet, cached.InfoHash, cached.Size, cached.Seeders, cached.Indexer);
            if (await IsCandidateAllowedAsync(magnetKey, cachedCandidate, ct).ConfigureAwait(false))
            {
                await _db.UpsertSourceCandidatesAsync(
                    tmdbId,
                    type,
                    sSentinel,
                    eSentinel,
                    cfg.SourcePickerPreset,
                    new[] { cachedCandidate },
                    "magnet_cache",
                    TimeSpan.FromHours(Math.Max(1, cfg.MagnetCacheTtlHours)),
                    ct).ConfigureAwait(false);
                candidates.Add(BuildCandidateRequest(meta, type, tmdbId, imdb, season, episode, cachedCandidate, cfg, fromCache: true, rank: int.MaxValue));
                seen.Add(cachedCandidate.Magnet);
            }
        }

        var sourceCandidates = await _db.ListSourceCandidatesAsync(
            tmdbId,
            type,
            sSentinel,
            eSentinel,
            cfg.SourcePickerPreset,
            includeExpired: false,
            ct).ConfigureAwait(false);
        foreach (var sourceCandidate in sourceCandidates)
        {
            var magnet = new MagnetCandidate(
                sourceCandidate.Magnet,
                sourceCandidate.InfoHash,
                sourceCandidate.Size ?? 0,
                sourceCandidate.Seeders ?? 0,
                sourceCandidate.Indexer)
            {
                Title = sourceCandidate.Title,
            };
            if (string.IsNullOrWhiteSpace(magnet.Magnet) || !seen.Add(magnet.Magnet))
            {
                continue;
            }

            if (IsSourceCandidateValidationBlocked(sourceCandidate, cfg))
            {
                continue;
            }

            if (!await IsCandidateAllowedAsync(magnetKey, magnet, ct).ConfigureAwait(false))
            {
                continue;
            }

            candidates.Add(BuildCandidateRequest(meta, type, tmdbId, imdb, season, episode, magnet, cfg, fromCache: false, rank: sourceCandidate.Rank, sourceRow: sourceCandidate));
        }

        return new CandidatePlan(candidates, freshCandidatesTask);
    }

    private async Task<IReadOnlyList<CandidateAddRequest>> BuildFreshMaterialiseCandidatesAsync(
        int tmdbId,
        string type,
        int? season,
        int? episode,
        string? imdb,
        string metadataType,
        TmdbMetadataRow meta,
        PluginConfiguration cfg,
        MagnetCacheKey magnetKey,
        int sSentinel,
        int eSentinel,
        CancellationToken ct)
    {
        var probe = await _magnetSelector.ProbeAsync(
            tmdbId, imdb, type, season, episode,
            meta.Title, meta.Year,
            ct).ConfigureAwait(false);
        if (probe.Outcome == MagnetProbeOutcome.IndeterminateTransient)
        {
            throw new InvalidOperationException(
                $"Source availability transient for {metadataType}/{tmdbId} (season={season} episode={episode}): {probe.ErrorKind} {probe.ErrorMessage}");
        }

        if (probe.Outcome != MagnetProbeOutcome.Available)
        {
            return Array.Empty<CandidateAddRequest>();
        }

        await _db.UpsertSourceCandidatesAsync(
            tmdbId,
            type,
            sSentinel,
            eSentinel,
            cfg.SourcePickerPreset,
            probe.Candidates,
            "materialise_probe",
            TimeSpan.FromHours(Math.Max(1, cfg.MagnetCacheTtlHours)),
            ct).ConfigureAwait(false);

        var candidates = new List<CandidateAddRequest>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rank = 0;
        foreach (var magnet in probe.Candidates)
        {
            ct.ThrowIfCancellationRequested();
            rank++;
            if (string.IsNullOrWhiteSpace(magnet.Magnet) || !seen.Add(magnet.Magnet))
            {
                continue;
            }

            if (!await IsCandidateAllowedAsync(magnetKey, magnet, ct).ConfigureAwait(false))
            {
                continue;
            }

            candidates.Add(BuildCandidateRequest(meta, type, tmdbId, imdb, season, episode, magnet, cfg, fromCache: false, rank: rank));
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
        bool fromCache,
        int rank,
        SourceCandidateRow? sourceRow = null)
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
                AllowedVideoContainers = ToAllowedVideoContainers(cfg),
                RequiredAudioLanguages = RequiredAudioLanguages,
                PreferredAudioLanguage = "eng",
            },
            magnet,
            fromCache,
            rank,
            sourceRow);

    private static string[]? ToAllowedVideoContainers(PluginConfiguration cfg)
    {
        var normalized = PluginConfiguration.NormalizeAllowedVideoContainers(cfg.AllowedVideoContainers);
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private async Task<bool> IsCandidateAllowedAsync(MagnetCacheKey key, MagnetCandidate magnet, CancellationToken ct)
    {
        var policy = _configProvider().SourceValidationPolicyVersion;
        if (await _db.GetMagnetFailureAsync(ToFailureKey(key, magnet), policy, ct).ConfigureAwait(false) is not null)
        {
            return false;
        }

        return await _db.GetMagnetFailureByInfoHashAsync(key, magnet.InfoHash, policy, ct).ConfigureAwait(false) is null;
    }

    private static bool IsSourceCandidateValidationBlocked(SourceCandidateRow row, PluginConfiguration cfg)
    {
        if (row.ValidationExpiresAt is null || row.ValidationExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            return false;
        }

        if (!string.Equals(row.ValidationPolicyVersion, cfg.SourceValidationPolicyVersion, StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(row.ValidationStatus, "invalid", StringComparison.OrdinalIgnoreCase)
            || string.Equals(row.ValidationStatus, "transient", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSourceCandidateCachedValid(SourceCandidateRow? row, PluginConfiguration cfg)
    {
        if (row?.ValidationExpiresAt is null || row.ValidationExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            return false;
        }

        return string.Equals(row.ValidationStatus, "valid", StringComparison.OrdinalIgnoreCase)
            && string.Equals(row.ValidationPolicyVersion, cfg.SourceValidationPolicyVersion, StringComparison.Ordinal);
    }

    private async Task<IReadOnlyList<CandidateAddRequest>> ValidateCandidatesAsync(
        IReadOnlyList<CandidateAddRequest> candidates,
        Task<IReadOnlyList<CandidateAddRequest>>? freshCandidatesTask,
        int tmdbId,
        string type,
        int? season,
        int? episode,
        string? imdb,
        UnavailableKey unavailableKey,
        CancellationToken ct)
    {
        var cfg = _configProvider();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(cfg.SourceValidationTimeoutSeconds, 5, 300)));
        var validationCt = timeout.Token;
        var attempted = 0;
        var sawTransient = false;

        var initial = await ValidateCandidateSetAsync(
            candidates,
            tmdbId,
            type,
            season,
            episode,
            imdb,
            unavailableKey,
            cfg,
            validationCt,
            ct).ConfigureAwait(false);
        attempted += initial.Attempted;
        sawTransient |= initial.SawTransient;

        IReadOnlyList<CandidateAddRequest> freshCandidates = Array.Empty<CandidateAddRequest>();
        Exception? freshFailure = null;
        if (freshCandidatesTask is not null)
        {
            try
            {
                freshCandidates = await freshCandidatesTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                freshFailure = ex;
                sawTransient = true;
                _logger.LogWarning(
                    ex,
                    "Fresh materialisation source probe failed for {Type}/{Tmdb} s{Season}e{Episode}",
                    type,
                    tmdbId,
                    season,
                    episode);
            }
        }

        var freshOnly = DistinctFreshCandidates(candidates, freshCandidates);
        ValidatedCandidateSet fresh = ValidatedCandidateSet.Empty;
        if (freshOnly.Length > 0)
        {
            fresh = await ValidateCandidateSetAsync(
                freshOnly,
                tmdbId,
                type,
                season,
                episode,
                imdb,
                unavailableKey,
                cfg,
                validationCt,
                ct).ConfigureAwait(false);
            attempted += fresh.Attempted;
            sawTransient |= fresh.SawTransient;
        }

        var winnerSet = PickPreferredValidatedSet(initial, fresh);
        if (winnerSet.Result.Count > 0)
        {
            if (!ReferenceEquals(winnerSet, initial))
            {
                await ReleaseCandidateLeasesAsync(initial.Result, CancellationToken.None).ConfigureAwait(false);
            }

            if (!ReferenceEquals(winnerSet, fresh))
            {
                await ReleaseCandidateLeasesAsync(fresh.Result, CancellationToken.None).ConfigureAwait(false);
            }

            return winnerSet.Result;
        }

        if (freshFailure is not null && candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"Source availability transient for {type}/{tmdbId} s{season}e{episode}: {freshFailure.Message}",
                freshFailure);
        }

        if (sawTransient)
        {
            await _db.MarkUnavailableAsync(
                unavailableKey,
                retryAfter: TimeSpan.FromMinutes(Math.Clamp(cfg.SourceValidationTransientRetryMinutes, 1, 1440)),
                ct).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"No source candidate validated for {type}/{tmdbId} s{season}e{episode}; attempted {attempted} candidates; transient validation failure, retry scheduled in {Math.Clamp(cfg.SourceValidationTransientRetryMinutes, 1, 1440)} minutes");
        }

        await _db.MarkUnavailableAsync(
            unavailableKey,
            retryAfter: TimeSpan.FromHours(cfg.UnavailableRetryAfterHours),
            ct).ConfigureAwait(false);
        throw new InvalidOperationException(
            $"No source candidate validated for {type}/{tmdbId} s{season}e{episode}; attempted {attempted} candidates");
    }

    private sealed record ValidatedCandidateSet(
        IReadOnlyList<CandidateAddRequest> Result,
        int BestRank,
        int Attempted,
        bool SawTransient)
    {
        public static ValidatedCandidateSet Empty { get; } = new(Array.Empty<CandidateAddRequest>(), int.MaxValue, 0, false);
    }

    private async Task<ValidatedCandidateSet> ValidateCandidateSetAsync(
        IReadOnlyList<CandidateAddRequest> candidates,
        int tmdbId,
        string type,
        int? season,
        int? episode,
        string? imdb,
        UnavailableKey unavailableKey,
        PluginConfiguration cfg,
        CancellationToken validationCt,
        CancellationToken outerCt)
    {
        if (candidates.Count == 0)
        {
            return ValidatedCandidateSet.Empty;
        }

        var groups = candidates
            .OrderBy(c => ValidationGroup(c, type, season, episode))
            .ThenBy(c => c.Rank)
            .ThenByDescending(c => c.Magnet.Seeders)
            .GroupBy(c => ValidationGroup(c, type, season, episode))
            .OrderBy(g => g.Key);

        var attempted = 0;
        var sawTransient = false;
        foreach (var group in groups)
        {
            var ordered = group.ToArray();
            var windowSize = Math.Clamp(cfg.SourceValidationWindowSize, 1, 12);
            for (var offset = 0; offset < ordered.Length; offset += windowSize)
            {
                try
                {
                    validationCt.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
                {
                    await _db.MarkUnavailableAsync(
                        unavailableKey,
                        retryAfter: TimeSpan.FromMinutes(Math.Clamp(cfg.SourceValidationTransientRetryMinutes, 1, 1440)),
                        outerCt).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"Source validation timed out for {type}/{tmdbId} s{season}e{episode}; attempted {attempted} candidates");
                }

                var window = ordered.Skip(offset).Take(windowSize).ToArray();
                attempted += window.Length;
                IReadOnlyList<CandidateValidation> validations;
                try
                {
                    validations = await ValidateWindowAsync(window, tmdbId, type, season, episode, imdb, cfg, validationCt).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!outerCt.IsCancellationRequested)
                {
                    await _db.MarkUnavailableAsync(
                        unavailableKey,
                        retryAfter: TimeSpan.FromMinutes(Math.Clamp(cfg.SourceValidationTransientRetryMinutes, 1, 1440)),
                        outerCt).ConfigureAwait(false);
                    throw new InvalidOperationException(
                        $"Source validation timed out for {type}/{tmdbId} s{season}e{episode}; attempted {attempted} candidates");
                }

                sawTransient |= validations.Any(v => string.Equals(v.Result.Status, "transient", StringComparison.OrdinalIgnoreCase));
                var winner = validations
                    .Where(v => string.Equals(v.Result.Status, "valid", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(v => v.Candidate.Rank)
                    .FirstOrDefault();
                if (winner is not null)
                {
                    await ReleaseLosingValidationsAsync(validations, winner, CancellationToken.None).ConfigureAwait(false);
                    var validByMagnet = validations
                        .Where(v => string.Equals(v.Result.Status, "valid", StringComparison.OrdinalIgnoreCase))
                        .ToDictionary(v => v.Candidate.Magnet.Magnet, StringComparer.Ordinal);
                    var result = ordered
                        .Select(c =>
                        {
                            if (!validByMagnet.TryGetValue(c.Magnet.Magnet, out var v))
                            {
                                return c;
                            }

                            return c with
                            {
                                Request = c.Request with
                                {
                                    SelectedFileId = v.Result.SelectedFile?.Id,
                                    SelectedFilePath = v.Result.SelectedFile?.Path,
                                    ValidationSessionId = string.IsNullOrWhiteSpace(v.SessionId) ? null : v.SessionId,
                                },
                            };
                        })
                        .ToArray();
                    return new ValidatedCandidateSet(result, winner.Candidate.Rank, attempted, sawTransient);
                }
            }
        }

        return new ValidatedCandidateSet(Array.Empty<CandidateAddRequest>(), int.MaxValue, attempted, sawTransient);
    }

    private static CandidateAddRequest[] DistinctFreshCandidates(
        IReadOnlyList<CandidateAddRequest> existing,
        IReadOnlyList<CandidateAddRequest> fresh)
    {
        if (fresh.Count == 0)
        {
            return Array.Empty<CandidateAddRequest>();
        }

        var seen = new HashSet<string>(existing.Select(c => c.Magnet.Magnet), StringComparer.Ordinal);
        return fresh.Where(c => seen.Add(c.Magnet.Magnet)).ToArray();
    }

    private static ValidatedCandidateSet PickPreferredValidatedSet(ValidatedCandidateSet initial, ValidatedCandidateSet fresh)
    {
        if (initial.Result.Count == 0)
        {
            return fresh;
        }

        if (fresh.Result.Count == 0)
        {
            return initial;
        }

        return fresh.BestRank <= initial.BestRank ? fresh : initial;
    }

    private async Task<IReadOnlyList<CandidateValidation>> ValidateWindowAsync(
        IReadOnlyList<CandidateAddRequest> window,
        int tmdbId,
        string type,
        int? season,
        int? episode,
        string? imdb,
        PluginConfiguration cfg,
        CancellationToken ct)
    {
        var parallelism = Math.Clamp(cfg.SourceValidationParallelism, 1, 6);
#pragma warning disable CA2025 // tasks are awaited/observed before semaphore and CTS leave scope
        using var semaphore = new SemaphoreSlim(parallelism, parallelism);
        using var winnerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var results = new List<CandidateValidation>();
        var tasks = window.Select(candidate => ValidateOneCandidateWithSemaphoreAsync(
                candidate,
                tmdbId,
                type,
                season,
                episode,
                imdb,
                cfg,
                semaphore,
                winnerCts.Token))
            .ToList();
#pragma warning restore CA2025

        while (tasks.Count > 0)
        {
            var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(completed);

            CandidateValidation validation;
            try
            {
                validation = await completed.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                continue;
            }

            results.Add(validation);
            if (string.Equals(validation.Result.Status, "valid", StringComparison.OrdinalIgnoreCase))
            {
                winnerCts.Cancel();
                await ObserveCancelledValidationsAsync(tasks).ConfigureAwait(false);
                return results;
            }
        }

        return results;
    }

    private async Task<CandidateValidation> ValidateOneCandidateWithSemaphoreAsync(
        CandidateAddRequest candidate,
        int tmdbId,
        string type,
        int? season,
        int? episode,
        string? imdb,
        PluginConfiguration cfg,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        var acquired = false;
        try
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            acquired = true;
            return await ValidateOneCandidateAsync(candidate, tmdbId, type, season, episode, imdb, cfg, ct).ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
            {
                semaphore.Release();
            }
        }
    }

    private static async Task ObserveCancelledValidationsAsync(IEnumerable<Task<CandidateValidation>> tasks)
    {
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (GostreamException)
        {
        }
        catch (HttpRequestException)
        {
        }
    }

    private async Task<CandidateValidation> ValidateOneCandidateAsync(
        CandidateAddRequest candidate,
        int tmdbId,
        string type,
        int? season,
        int? episode,
        string? imdb,
        PluginConfiguration cfg,
        CancellationToken ct)
    {
        if (CandidateHasMismatchedYear(candidate))
        {
            var mismatchedYearResult = new GostreamValidateResult
            {
                Status = "invalid",
                Reason = "series_year_mismatch",
                Hash = candidate.Magnet.InfoHash,
                ValidationSessionId = string.Empty,
            };
            await PersistValidationResultAsync(candidate, tmdbId, imdb, type, season, episode, cfg, mismatchedYearResult, TimeSpan.Zero, ct).ConfigureAwait(false);
            return new CandidateValidation(candidate, mismatchedYearResult, string.Empty);
        }

        if (IsSourceCandidateCachedValid(candidate.SourceRow, cfg))
        {
            var cached = candidate.SourceRow!;
            return new CandidateValidation(
                candidate,
                new GostreamValidateResult
                {
                    Status = "valid",
                    Reason = cached.ValidationReason,
                    Hash = cached.InfoHash,
                    SelectedFile = new GostreamSelectedFile
                    {
                        Id = cached.SelectedFileId.HasValue ? checked((int)cached.SelectedFileId.Value) : null,
                        Path = cached.SelectedFilePath,
                        Size = cached.SelectedFileSize,
                    },
                },
                string.Empty);
        }

        var sessionId = Guid.NewGuid().ToString("N");
        var request = new GostreamValidateRequest
        {
            Type = candidate.Request.Type,
            Imdb = candidate.Request.Imdb,
            Tmdb = candidate.Request.Tmdb,
            Title = candidate.Request.Title,
            Year = candidate.Request.Year,
            Season = candidate.Request.Season,
            Episode = candidate.Request.Episode,
            SeriesImdb = candidate.Request.SeriesImdb,
            Magnet = candidate.Request.Magnet,
            RequiredAudioLanguages = RequiredAudioLanguages,
            PreferredAudioLanguage = "eng",
            AllowedVideoContainers = ToAllowedVideoContainers(cfg),
            ValidationSessionId = sessionId,
        };

        GostreamValidateResult result;
        var sw = Stopwatch.StartNew();
        try
        {
            using (await _gostreamLimiter.AcquireAsync(ct).ConfigureAwait(false))
            {
                result = await _gostream.ValidateAsync(request, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            result = new GostreamValidateResult { Status = "transient", Reason = "validation_cancelled", ValidationSessionId = sessionId };
        }
        catch (GostreamBadRequestException)
        {
            throw;
        }
        catch (GostreamException ex)
        {
            result = new GostreamValidateResult { Status = "transient", Reason = TransientReason(ex), ValidationSessionId = sessionId };
        }
        catch (HttpRequestException ex)
        {
            result = new GostreamValidateResult { Status = "transient", Reason = "transport_error: " + ex.Message, ValidationSessionId = sessionId };
        }

        sw.Stop();
        await PersistValidationResultAsync(candidate, tmdbId, imdb, type, season, episode, cfg, result, sw.Elapsed, ct).ConfigureAwait(false);
        return new CandidateValidation(candidate, result, result.ValidationSessionId ?? sessionId);
    }

    private async Task PersistValidationResultAsync(
        CandidateAddRequest candidate,
        int tmdbId,
        string? imdb,
        string type,
        int? season,
        int? episode,
        PluginConfiguration cfg,
        GostreamValidateResult result,
        TimeSpan duration,
        CancellationToken ct)
    {
        var status = NormalizeValidationStatus(result.Status);
        var reason = string.IsNullOrWhiteSpace(result.Reason) ? status : result.Reason!;
        var hard = string.Equals(status, "invalid", StringComparison.OrdinalIgnoreCase) || IsHardValidationReason(reason);
        var ttl = string.Equals(status, "valid", StringComparison.OrdinalIgnoreCase) || hard
            ? TimeSpan.FromHours(Math.Clamp(cfg.SourceValidationTtlHours, 1, 720))
            : TimeSpan.FromMinutes(Math.Clamp(cfg.SourceValidationTransientRetryMinutes, 1, 1440));
        var now = DateTimeOffset.UtcNow;
        var (sSentinel, eSentinel) = ChannelItemId.ToSentinels(season, episode);
        await _db.UpdateSourceCandidateValidationAsync(
            new SourceCandidateValidationUpdate(
                tmdbId,
                type,
                sSentinel,
                eSentinel,
                cfg.SourcePickerPreset,
                candidate.Magnet.Magnet,
                status,
                string.Equals(status, "valid", StringComparison.OrdinalIgnoreCase) ? null : reason,
                now,
                now.Add(ttl),
                (long)Math.Min(long.MaxValue, duration.TotalMilliseconds),
                cfg.SourceValidationPolicyVersion,
                result.SelectedFile?.Id,
                result.SelectedFile?.Path,
                result.SelectedFile?.Size),
            ct).ConfigureAwait(false);

        if (hard && !string.Equals(status, "valid", StringComparison.OrdinalIgnoreCase))
        {
            await MarkCandidateFailedAsync(candidate, tmdbId, imdb, type, season, episode, cfg, reason, ttl, ct).ConfigureAwait(false);
        }
    }

    private async Task ReleaseLosingValidationsAsync(
        IReadOnlyList<CandidateValidation> validations,
        CandidateValidation winner,
        CancellationToken ct)
    {
        foreach (var validation in validations)
        {
            if (ReferenceEquals(validation, winner) || string.IsNullOrWhiteSpace(validation.SessionId))
            {
                continue;
            }

            try
            {
                await _gostream.ReleaseValidationAsync(
                    new GostreamValidationReleaseRequest
                    {
                        ValidationSessionId = validation.SessionId,
                        Hash = validation.Result.Hash ?? validation.Candidate.Magnet.InfoHash,
                    },
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Validation lease release failed for {Hash}", validation.Candidate.Magnet.InfoHash);
            }
        }
    }

    private static int ValidationGroup(CandidateAddRequest candidate, string type, int? season, int? episode)
    {
        if (type != "episode" || !season.HasValue || !episode.HasValue)
        {
            return 0;
        }

        var title = candidate.Magnet.Title ?? string.Empty;
        var s = season.Value;
        var e = episode.Value;
        if (title.Contains($"S{s:00}E{e:00}", StringComparison.OrdinalIgnoreCase)
            || title.Contains($"{s}x{e:00}", StringComparison.OrdinalIgnoreCase)
            || title.Contains($"{s}{e:00}", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (title.Contains($"Season {s:00}", StringComparison.OrdinalIgnoreCase)
            || title.Contains($"Season {s}", StringComparison.OrdinalIgnoreCase)
            || title.Contains($"Book {s}", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (title.Contains("complete", StringComparison.OrdinalIgnoreCase)
            || title.Contains("series", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return 3;
    }

    private static bool CandidateHasMismatchedYear(CandidateAddRequest candidate)
    {
        if (candidate.Request.Type != "episode" || !candidate.Request.Year.HasValue)
        {
            return false;
        }

        var title = candidate.Magnet.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            return false;
        }

        var expected = candidate.Request.Year.Value;
        for (var i = 0; i <= title.Length - 4; i++)
        {
            if (!char.IsDigit(title[i])
                || !char.IsDigit(title[i + 1])
                || !char.IsDigit(title[i + 2])
                || !char.IsDigit(title[i + 3]))
            {
                continue;
            }

            var beforeOk = i == 0 || !char.IsLetterOrDigit(title[i - 1]);
            var afterOk = i + 4 >= title.Length || !char.IsLetterOrDigit(title[i + 4]);
            if (!beforeOk || !afterOk)
            {
                continue;
            }

            var year = int.Parse(title.AsSpan(i, 4), CultureInfo.InvariantCulture);
            if (year >= 1900 && year <= 2100 && year != expected)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsHardValidationReason(string reason)
        => reason is "target_episode_not_found" or "no_valid_files" or "container_not_allowed" or "no_english_audio" or "no_main_english_audio" or "audio_probe_unsupported_format" or "series_year_mismatch";

    private static string NormalizeValidationStatus(string? status)
    {
        if (string.Equals(status, "valid", StringComparison.OrdinalIgnoreCase))
        {
            return "valid";
        }

        if (string.Equals(status, "invalid", StringComparison.OrdinalIgnoreCase))
        {
            return "invalid";
        }

        return "transient";
    }

    private static string TransientReason(Exception ex)
        => ex is GostreamTimeoutException ? "metadata_timeout" : "validation_transient";

    private async Task<CandidateAddRequest?> EnsureCandidateValidatedForAddAsync(
        CandidateAddRequest candidate,
        int tmdbId,
        string type,
        int? season,
        int? episode,
        string? imdb,
        PluginConfiguration cfg,
        CancellationToken ct)
    {
        if (candidate.Request.SelectedFileId.HasValue
            || !string.IsNullOrWhiteSpace(candidate.Request.SelectedFilePath)
            || !string.IsNullOrWhiteSpace(candidate.Request.ValidationSessionId))
        {
            return candidate;
        }

        var validation = await ValidateOneCandidateAsync(candidate, tmdbId, type, season, episode, imdb, cfg, ct).ConfigureAwait(false);
        if (!string.Equals(validation.Result.Status, "valid", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return candidate with
        {
            Request = candidate.Request with
            {
                SelectedFileId = validation.Result.SelectedFile?.Id,
                SelectedFilePath = validation.Result.SelectedFile?.Path,
                ValidationSessionId = string.IsNullOrWhiteSpace(validation.SessionId) ? null : validation.SessionId,
            },
        };
    }

    private async Task<CandidateAddResult> AddWithCandidateRetryAsync(
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
                var validated = await EnsureCandidateValidatedForAddAsync(candidate, tmdbId, type, season, episode, imdb, cfg, ct).ConfigureAwait(false);
                if (validated is null)
                {
                    continue;
                }

                GostreamAddResult result;
                using (await _gostreamLimiter.AcquireAsync(ct).ConfigureAwait(false))
                {
                    result = await _gostream.AddAsync(validated.Request, ct).ConfigureAwait(false);
                }
                var fusePath = type == "movie"
                    ? GostreamPathResolver.ResolveMoviePath(result.FusePath)
                    : GostreamPathResolver.ResolveEpisodePath(result.FusePath);
                await WaitForFusePathAsync(fusePath, ct).ConfigureAwait(false);
                await ReleaseUnconsumedCandidateLeasesAsync(candidates, validated, CancellationToken.None).ConfigureAwait(false);
                if (!validated.FromCache)
                {
                    try
                    {
                        await _db.PutCachedMagnetAsync(
                            new MagnetCacheKey(tmdbId, imdb, type, season, episode, cfg.SourcePickerPreset),
                            new MagnetCacheEntry
                            {
                                Magnet = validated.Magnet.Magnet,
                                InfoHash = validated.Magnet.InfoHash,
                                Size = validated.Magnet.Size,
                                Seeders = validated.Magnet.Seeders,
                                Indexer = validated.Magnet.Indexer,
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

                return new CandidateAddResult(result, fusePath);
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
            catch (FileNotFoundException ex)
            {
                last = ex;
                await MarkCandidateFailedAsync(candidate, tmdbId, imdb, type, season, episode, cfg, "fuse_path_missing", TimeSpan.FromHours(cfg.UnavailableRetryAfterHours), ct).ConfigureAwait(false);
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

    private async Task ReleaseCandidateLeasesAsync(IReadOnlyList<CandidateAddRequest> candidates, CancellationToken ct)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Request.ValidationSessionId))
            {
                continue;
            }

            try
            {
                await _gostream.ReleaseValidationAsync(
                    new GostreamValidationReleaseRequest
                    {
                        ValidationSessionId = candidate.Request.ValidationSessionId!,
                        Hash = candidate.Magnet.InfoHash,
                    },
                    ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to release validation session {SessionId}", candidate.Request.ValidationSessionId);
            }
        }
    }

    private async Task ReleaseUnconsumedCandidateLeasesAsync(
        IReadOnlyList<CandidateAddRequest> candidates,
        CandidateAddRequest consumed,
        CancellationToken ct)
    {
        foreach (var candidate in candidates)
        {
            if (ReferenceEquals(candidate, consumed) || string.IsNullOrWhiteSpace(candidate.Request.ValidationSessionId))
            {
                continue;
            }

            try
            {
                await _gostream.ReleaseValidationAsync(
                    new GostreamValidationReleaseRequest
                    {
                        ValidationSessionId = candidate.Request.ValidationSessionId!,
                        Hash = candidate.Magnet.InfoHash,
                    },
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Validation lease release failed for {Hash}", candidate.Magnet.InfoHash);
            }
        }
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
                ValidationPolicyVersion = cfg.SourceValidationPolicyVersion,
            },
            ct).ConfigureAwait(false);

        var (sSentinel, eSentinel) = ChannelItemId.ToSentinels(season, episode);
        var status = IsHardValidationReason(reason) || reason == "fuse_path_missing" || reason == "bad_request" ? "invalid" : "transient";
        await _db.UpdateSourceCandidateValidationAsync(
            new SourceCandidateValidationUpdate(
                tmdbId,
                type,
                sSentinel,
                eSentinel,
                cfg.SourcePickerPreset,
                candidate.Magnet.Magnet,
                status,
                reason,
                now,
                now.Add(ttl),
                null,
                cfg.SourceValidationPolicyVersion,
                candidate.Request.SelectedFileId,
                candidate.Request.SelectedFilePath,
                null),
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
    /// configured timeout. Timeout rejects the current magnet candidate;
    /// we do not persist materialised_state for a path Jellyfin cannot
    /// open.
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

        throw new FileNotFoundException(
            $"FUSE path {fusePath} did not appear within {cfg.FusePathWaitTimeoutSeconds}s",
            fusePath);
    }
}
