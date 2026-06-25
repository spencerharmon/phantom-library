using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Channels;

namespace Jellyfin.Plugin.PhantomLibrary.Sources;

public sealed record PhantomSourceCandidateDto
{
    public required string Magnet { get; init; }
    public required string InfoHash { get; init; }
    public required string? Indexer { get; init; }
    public string? Title { get; init; }
    public required int? Seeders { get; init; }
    public required long? Size { get; init; }
    public required int Rank { get; init; }
    public required bool IsCurrent { get; init; }
    public required bool IsRejected { get; init; }
    public string? FailureReason { get; init; }
    public DateTimeOffset? RetryAfter { get; init; }
}

public sealed record PhantomCurrentSourceDto
{
    public string? Magnet { get; init; }
    public string? InfoHash { get; init; }
    public string? Indexer { get; init; }
    public int? Seeders { get; init; }
    public long? Size { get; init; }
    public required string StubPath { get; init; }
    public required string FusePath { get; init; }
    public required DateTimeOffset MaterialisedAt { get; init; }
}

public sealed record PhantomSourcesResponse
{
    public required string ExternalId { get; init; }
    public required string Type { get; init; }
    public required int TmdbId { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public required string Status { get; init; }
    public PhantomCurrentSourceDto? CurrentSource { get; init; }
    public IReadOnlyList<PhantomSourceCandidateDto> Candidates { get; init; } = Array.Empty<PhantomSourceCandidateDto>();
    public required bool CanRejectCurrent { get; init; }
    public required bool CanMaterialiseSelected { get; init; }
    public required string Message { get; init; }
    public string? ProbeErrorKind { get; init; }
    public string? ProbeErrorMessage { get; init; }
}

public sealed record PhantomMaterialiseCandidateRequest
{
    public string? Magnet { get; init; }
    public string? InfoHash { get; init; }
    public string? Indexer { get; init; }
    public string? Title { get; init; }
    public long? Size { get; init; }
    public int? Seeders { get; init; }
    public bool OverrideRejected { get; init; }
}

public enum PhantomSourceOperationStatus
{
    Success,
    NotFound,
    NoCurrent,
    InFlight,
    NoAlternate,
    CandidateNotFound,
    Error,
}

public sealed record PhantomSourceOperationResult
{
    public required PhantomSourceOperationStatus Status { get; init; }
    public required string Code { get; init; }
    public required string Message { get; init; }
    public MaterialisationOutcome? Outcome { get; init; }
    public PhantomSourceCandidateDto? Candidate { get; init; }

    public static PhantomSourceOperationResult FromOutcome(MaterialisationOutcome outcome, PhantomSourceCandidateDto candidate)
        => outcome.Status switch
        {
            MaterialisationStatus.Success or MaterialisationStatus.Duplicate => new()
            {
                Status = PhantomSourceOperationStatus.Success,
                Code = "materialised",
                Message = "Source materialised",
                Outcome = outcome,
                Candidate = candidate,
            },
            MaterialisationStatus.AlreadyInProgress => new()
            {
                Status = PhantomSourceOperationStatus.InFlight,
                Code = "in_flight",
                Message = "Materialisation already in flight",
                Outcome = outcome,
                Candidate = candidate,
            },
            _ => new()
            {
                Status = PhantomSourceOperationStatus.Error,
                Code = "materialise_failed",
                Message = outcome.Error ?? "Materialise failed",
                Outcome = outcome,
                Candidate = candidate,
            },
        };
}

public sealed class PhantomSourceManager
{
    private static readonly TimeSpan OperatorRejectedRetry = TimeSpan.FromDays(3650);

    private readonly PhantomDb _db;
    private readonly MagnetSelector _magnetSelector;
    private readonly IMaterialiser _materialiser;
    private readonly IGostreamClient _gostream;
    private readonly TmdbExternalIdResolver _externalIds;
    private readonly IChannelItemRefreshManager _refreshManager;
    private readonly ChannelStateProvider _state;
    private readonly Func<PluginConfiguration> _configProvider;

    private sealed record SourceKey(
        string ExternalId,
        int TmdbId,
        string Type,
        int? Season,
        int? Episode,
        int SeasonSentinel,
        int EpisodeSentinel,
        string MetadataType);

    private sealed record CandidateWithFailure(MagnetCandidate Candidate, MagnetFailureEntry? Failure, SourceCandidateRow? SourceRow, int Rank, bool IsCurrent);

    public PhantomSourceManager(
        PhantomDb db,
        MagnetSelector magnetSelector,
        IMaterialiser materialiser,
        IGostreamClient gostream,
        TmdbExternalIdResolver externalIds,
        IChannelItemRefreshManager refreshManager,
        ChannelStateProvider state)
        : this(db, magnetSelector, materialiser, gostream, externalIds, refreshManager, state,
               () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal PhantomSourceManager(
        PhantomDb db,
        MagnetSelector magnetSelector,
        IMaterialiser materialiser,
        IGostreamClient gostream,
        TmdbExternalIdResolver externalIds,
        IChannelItemRefreshManager refreshManager,
        ChannelStateProvider state,
        Func<PluginConfiguration> configProvider)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _magnetSelector = magnetSelector ?? throw new ArgumentNullException(nameof(magnetSelector));
        _materialiser = materialiser ?? throw new ArgumentNullException(nameof(materialiser));
        _gostream = gostream ?? throw new ArgumentNullException(nameof(gostream));
        _externalIds = externalIds ?? throw new ArgumentNullException(nameof(externalIds));
        _refreshManager = refreshManager ?? throw new ArgumentNullException(nameof(refreshManager));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    public async Task<PhantomSourcesResponse?> GetSourcesAsync(string externalId, CancellationToken ct)
    {
        if (!TryResolveKey(externalId, out var key))
        {
            return null;
        }

        var imdb = await ResolveImdbAsync(key, ct).ConfigureAwait(false);
        var meta = await _db.GetTmdbMetadataAsync(key.TmdbId, key.MetadataType, ct).ConfigureAwait(false);
        if (meta is null)
        {
            return null;
        }

        await ClearStaleInFlightAsync(key, ct).ConfigureAwait(false);
        var current = await GetCurrentAsync(key, imdb, ct).ConfigureAwait(false);
        var inFlight = await _db.IsMaterialiseInFlightAsync(
            key.TmdbId, key.Type, key.SeasonSentinel, key.EpisodeSentinel, ct).ConfigureAwait(false);

        var currentMagnet = current is null ? null : current.Value.Entry?.Magnet;
        var (candidates, errorKind, errorMessage) = await GetRankedCandidatesAsync(key, imdb, meta, includeRejected: true, currentMagnet: currentMagnet, ct)
            .ConfigureAwait(false);

        var status = inFlight
            ? "materialising"
            : current is not null
                ? "materialised"
                : errorKind is null && candidates.Count == 0
                    ? "unavailable"
                    : "unmaterialised";
        var canReject = current is not null && current.Value.Entry is not null && !inFlight;
        var canMaterialise = !inFlight && candidates.Any(c => c.Failure is null);
        var message = inFlight
            ? "Materialisation already in flight"
            : canReject || canMaterialise
                ? "Source actions available"
                : errorMessage ?? (candidates.Count == 0 ? "No source candidates available" : "No enabled source action");

        return new PhantomSourcesResponse
        {
            ExternalId = key.ExternalId,
            Type = key.Type,
            TmdbId = key.TmdbId,
            Season = key.Season,
            Episode = key.Episode,
            Status = status,
            CurrentSource = current is null ? null : ToCurrentDto(current.Value.State, current.Value.Entry),
            Candidates = candidates.Select(ToCandidateDto).ToArray(),
            CanRejectCurrent = canReject,
            CanMaterialiseSelected = canMaterialise,
            Message = message,
            ProbeErrorKind = errorKind,
            ProbeErrorMessage = errorMessage,
        };
    }

    public async Task<PhantomSourceOperationResult> ResetCurrentAsync(string externalId, CancellationToken ct)
    {
        if (!TryResolveKey(externalId, out var key))
        {
            return Result(PhantomSourceOperationStatus.NotFound, "not_found", "Phantom movie or episode external id not found");
        }

        await ClearStaleInFlightAsync(key, ct).ConfigureAwait(false);
        if (await _db.IsMaterialiseInFlightAsync(key.TmdbId, key.Type, key.SeasonSentinel, key.EpisodeSentinel, ct).ConfigureAwait(false))
        {
            return Result(PhantomSourceOperationStatus.InFlight, "in_flight", "Materialisation already in flight");
        }

        var imdb = await ResolveImdbAsync(key, ct).ConfigureAwait(false);
        var current = await GetCurrentAsync(key, imdb, ct).ConfigureAwait(false);
        if (current is not null)
        {
            await _db.MarkAvailabilityAvailableAsync(
                key.TmdbId,
                key.Type,
                key.SeasonSentinel,
                key.EpisodeSentinel,
                current.Value.Entry,
                ct).ConfigureAwait(false);
            await DeleteCurrentStateAndMaybeRemoveAsync(key, current.Value.State, current.Value.Entry?.InfoHash, ct).ConfigureAwait(false);
        }

        var cfg = _configProvider();
        await _db.DeleteMagnetFailuresAsync(
            CacheKey(key, imdb, cfg.SourcePickerPreset),
            ct).ConfigureAwait(false);
        await _db.ClearSourceCandidateValidationAsync(
            key.TmdbId,
            key.Type,
            key.SeasonSentinel,
            key.EpisodeSentinel,
            cfg.SourcePickerPreset,
            ct).ConfigureAwait(false);
        await _db.DeleteUnavailableAsync(
            new UnavailableKey(key.TmdbId, imdb, key.Type, key.Season, key.Episode),
            ct).ConfigureAwait(false);
        await RefreshItemAsync(key, forceProbe: false, ct).ConfigureAwait(false);

        return Result(
            PhantomSourceOperationStatus.Success,
            "reset",
            current is null
                ? "Phantom item was already in base state; unavailable marker cleared"
                : "Phantom materialisation state reset");
    }

    public async Task<PhantomSourceOperationResult> RejectCurrentAsync(string externalId, CancellationToken ct)
    {
        if (!TryResolveKey(externalId, out var key))
        {
            return Result(PhantomSourceOperationStatus.NotFound, "not_found", "Phantom movie or episode external id not found");
        }

        await ClearStaleInFlightAsync(key, ct).ConfigureAwait(false);
        if (await _db.IsMaterialiseInFlightAsync(key.TmdbId, key.Type, key.SeasonSentinel, key.EpisodeSentinel, ct).ConfigureAwait(false))
        {
            return Result(PhantomSourceOperationStatus.InFlight, "in_flight", "Materialisation already in flight");
        }

        var imdb = await ResolveImdbAsync(key, ct).ConfigureAwait(false);
        var current = await GetCurrentAsync(key, imdb, ct).ConfigureAwait(false);
        if (current is null || current.Value.Entry is null)
        {
            return Result(PhantomSourceOperationStatus.NoCurrent, "no_current", "No current materialised source with stored magnet exists");
        }

        var cfg = _configProvider();
        var cacheKey = CacheKey(key, imdb, cfg.SourcePickerPreset);
        var now = DateTimeOffset.UtcNow;
        await _db.MarkMagnetFailedAsync(
            new MagnetFailureKey(cacheKey.TmdbId, cacheKey.ImdbId, cacheKey.Type, cacheKey.Season, cacheKey.Episode, cacheKey.Preset, current.Value.Entry.Magnet),
            new MagnetFailureEntry
            {
                InfoHash = current.Value.Entry.InfoHash,
                Reason = "operator_rejected",
                FailedAt = now,
                RetryAfter = now.Add(OperatorRejectedRetry),
                ValidationPolicyVersion = cfg.SourceValidationPolicyVersion,
            },
            ct).ConfigureAwait(false);

        var meta = await _db.GetTmdbMetadataAsync(key.TmdbId, key.MetadataType, ct).ConfigureAwait(false);
        if (meta is null)
        {
            await DeleteCurrentStateAndMaybeRemoveAsync(key, current.Value.State, current.Value.Entry.InfoHash, ct).ConfigureAwait(false);
            await RefreshItemAsync(key, forceProbe: false, ct).ConfigureAwait(false);
            return Result(PhantomSourceOperationStatus.NoAlternate, "no_alternate", "No metadata is cached for this item, so no alternate source can be selected");
        }

        var (ranked, _, _) = await GetRankedCandidatesAsync(key, imdb, meta, includeRejected: false, currentMagnet: current.Value.Entry.Magnet, ct).ConfigureAwait(false);
        var next = ranked.FirstOrDefault(c => !string.Equals(c.Candidate.Magnet, current.Value.Entry.Magnet, StringComparison.Ordinal));

        await DeleteCurrentStateAndMaybeRemoveAsync(key, current.Value.State, current.Value.Entry.InfoHash, ct).ConfigureAwait(false);

        if (next is null)
        {
            await RefreshItemAsync(key, forceProbe: false, ct).ConfigureAwait(false);
            return Result(PhantomSourceOperationStatus.NoAlternate, "no_alternate", "Current source rejected; no non-rejected alternate candidate is available");
        }

        var outcome = await _materialiser.MaterialiseAsync(
            key.TmdbId, key.Type, key.Season, key.Episode,
            next.Candidate,
            MaterialiseTrigger.Manual,
            ct).ConfigureAwait(false);
        return PhantomSourceOperationResult.FromOutcome(outcome, ToCandidateDto(next));
    }

    public async Task<PhantomSourceOperationResult> MaterialiseCandidateAsync(
        string externalId,
        PhantomMaterialiseCandidateRequest? request,
        CancellationToken ct)
    {
        if (!TryResolveKey(externalId, out var key))
        {
            return Result(PhantomSourceOperationStatus.NotFound, "not_found", "Phantom movie or episode external id not found");
        }

        if (string.IsNullOrWhiteSpace(request?.Magnet))
        {
            return Result(PhantomSourceOperationStatus.CandidateNotFound, "candidate_not_found", "Request must include a candidate magnet");
        }

        await ClearStaleInFlightAsync(key, ct).ConfigureAwait(false);
        if (await _db.IsMaterialiseInFlightAsync(key.TmdbId, key.Type, key.SeasonSentinel, key.EpisodeSentinel, ct).ConfigureAwait(false))
        {
            return Result(PhantomSourceOperationStatus.InFlight, "in_flight", "Materialisation already in flight");
        }

        var imdb = await ResolveImdbAsync(key, ct).ConfigureAwait(false);
        var meta = await _db.GetTmdbMetadataAsync(key.TmdbId, key.MetadataType, ct).ConfigureAwait(false);
        if (meta is null)
        {
            return Result(PhantomSourceOperationStatus.NotFound, "not_found", "Cached metadata for this item was not found");
        }

        var (ranked, _, _) = await GetRankedCandidatesAsync(key, imdb, meta, includeRejected: request.OverrideRejected, currentMagnet: null, ct).ConfigureAwait(false);
        var selected = ranked.FirstOrDefault(c => string.Equals(c.Candidate.Magnet, request.Magnet, StringComparison.Ordinal));
        if (selected is null)
        {
            var requested = TryBuildRequestedCandidate(request);
            if (requested is null)
            {
                return Result(PhantomSourceOperationStatus.CandidateNotFound, "candidate_not_found", "Selected candidate is not in the current list and request did not include exact candidate metadata");
            }

            var cfg = _configProvider();
            var failure = await _db.GetMagnetFailureAsync(
                new MagnetFailureKey(key.TmdbId, imdb, key.Type, key.Season, key.Episode, cfg.SourcePickerPreset, requested.Magnet),
                cfg.SourceValidationPolicyVersion,
                ct).ConfigureAwait(false);
            selected = new CandidateWithFailure(requested, failure, null, 1, false);
        }

        if ((selected.Failure is not null || IsValidationRejected(selected.SourceRow, _configProvider())) && !request.OverrideRejected)
        {
            return Result(PhantomSourceOperationStatus.CandidateNotFound, "candidate_not_found", "Selected candidate is currently rejected");
        }

        var current = await GetCurrentAsync(key, imdb, ct).ConfigureAwait(false);
        if (current is not null)
        {
            if (string.Equals(current.Value.Entry?.Magnet, selected.Candidate.Magnet, StringComparison.Ordinal))
            {
                return new PhantomSourceOperationResult
                {
                    Status = PhantomSourceOperationStatus.Success,
                    Code = "already_current",
                    Message = "Selected candidate is already the current source",
                    Outcome = MaterialisationOutcome.Duplicate,
                    Candidate = ToCandidateDto(selected),
                };
            }

            await DeleteCurrentStateAndMaybeRemoveAsync(key, current.Value.State, current.Value.Entry?.InfoHash, ct).ConfigureAwait(false);
        }

        var outcome = await _materialiser.MaterialiseAsync(
            key.TmdbId, key.Type, key.Season, key.Episode,
            selected.Candidate,
            MaterialiseTrigger.Manual,
            ct).ConfigureAwait(false);
        return PhantomSourceOperationResult.FromOutcome(outcome, ToCandidateDto(selected));
    }

    private async Task ClearStaleInFlightAsync(SourceKey key, CancellationToken ct)
    {
        var started = await _db.GetMaterialiseInFlightStartedAtAsync(
            key.TmdbId, key.Type, key.SeasonSentinel, key.EpisodeSentinel, ct).ConfigureAwait(false);
        if (!started.HasValue)
        {
            return;
        }

        var staleAfter = TimeSpan.FromMinutes(Math.Max(1, _configProvider().MaterialiseInFlightStaleMinutes));
        if (DateTimeOffset.UtcNow - started.Value <= staleAfter)
        {
            return;
        }

        await _db.DeleteMaterialiseInFlightAsync(
            key.TmdbId, key.Type, key.SeasonSentinel, key.EpisodeSentinel, ct).ConfigureAwait(false);
    }

    private static MagnetCandidate? TryBuildRequestedCandidate(PhantomMaterialiseCandidateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Magnet)
            || string.IsNullOrWhiteSpace(request.InfoHash)
            || string.IsNullOrWhiteSpace(request.Indexer)
            || !request.Size.HasValue
            || !request.Seeders.HasValue)
        {
            return null;
        }

        return new MagnetCandidate(
            request.Magnet,
            request.InfoHash,
            request.Size.Value,
            request.Seeders.Value,
            request.Indexer)
        {
            Title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title,
        };
    }

    private async Task DeleteCurrentStateAndMaybeRemoveAsync(SourceKey key, MaterialisedStateRow state, string? infoHash, CancellationToken ct)
    {
        var refs = await _db.CountOtherMaterialisedReferencesAsync(
            key.TmdbId, key.Type, key.SeasonSentinel, key.EpisodeSentinel,
            state.StubPath,
            infoHash,
            ct).ConfigureAwait(false);
        if (refs == 0)
        {
            await _gostream.RemoveAsync(state.StubPath, ct).ConfigureAwait(false);
        }

        await _db.DeleteMaterialisedStateAsync(key.TmdbId, key.Type, key.SeasonSentinel, key.EpisodeSentinel, ct)
            .ConfigureAwait(false);
    }

    private async Task RefreshItemAsync(SourceKey key, bool forceProbe, CancellationToken ct)
    {
        var channelKind = key.Type == "movie" ? ChannelStateProvider.KindMovies : ChannelStateProvider.KindShows;
        _state.BumpDataVersion(channelKind);
        await _refreshManager.RefreshChannelItemAsync(
            ChannelIds.For(channelKind),
            key.ExternalId,
            new ChannelItemRefreshOptions
            {
                ForceUpdate = true,
                ForceProbe = forceProbe,
                InvalidateMediaInfoCache = true,
            },
            ct).ConfigureAwait(false);
    }

    private async Task<(IReadOnlyList<CandidateWithFailure> Candidates, string? ErrorKind, string? ErrorMessage)> GetRankedCandidatesAsync(
        SourceKey key,
        string? imdb,
        TmdbMetadataRow meta,
        bool includeRejected,
        string? currentMagnet,
        CancellationToken ct)
    {
        var cfg = _configProvider();
        var cacheKey = CacheKey(key, imdb, cfg.SourcePickerPreset);
        var result = new List<CandidateWithFailure>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var rank = 0;

        var cachedCandidates = await _db.ListSourceCandidatesAsync(
            key.TmdbId,
            key.Type,
            key.SeasonSentinel,
            key.EpisodeSentinel,
            cfg.SourcePickerPreset,
            includeExpired: false,
            ct).ConfigureAwait(false);
        foreach (var cached in cachedCandidates)
        {
            rank++;
            var candidate = ToMagnetCandidate(cached);
            var failure = await _db.GetMagnetFailureAsync(
                new MagnetFailureKey(cacheKey.TmdbId, cacheKey.ImdbId, cacheKey.Type, cacheKey.Season, cacheKey.Episode, cacheKey.Preset, candidate.Magnet),
                cfg.SourceValidationPolicyVersion,
                ct).ConfigureAwait(false);
            var validationRejected = IsValidationRejected(cached, cfg);
            if ((failure is null && !validationRejected || includeRejected) && seen.Add(candidate.Magnet))
            {
                result.Add(new CandidateWithFailure(
                    candidate,
                    failure,
                    cached,
                    rank,
                    string.Equals(candidate.Magnet, currentMagnet, StringComparison.Ordinal)));
            }
        }

        if (result.Count > 0)
        {
            return (result, null, null);
        }

        var probe = await _magnetSelector.ProbeAsync(
            key.TmdbId, imdb, key.Type, key.Season, key.Episode,
            meta.Title, meta.Year,
            ct).ConfigureAwait(false);

        if (probe.Outcome == MagnetProbeOutcome.Available)
        {
            await _db.UpsertSourceCandidatesAsync(
                key.TmdbId,
                key.Type,
                key.SeasonSentinel,
                key.EpisodeSentinel,
                cfg.SourcePickerPreset,
                probe.Candidates,
                "details_probe",
                TimeSpan.FromHours(Math.Max(1, cfg.MagnetCacheTtlHours)),
                ct).ConfigureAwait(false);
        }

        var availability = await _db.GetAvailabilityItemAsync(key.TmdbId, key.Type, key.SeasonSentinel, key.EpisodeSentinel, ct)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(availability?.CandidateMagnet)
            && !string.IsNullOrWhiteSpace(availability.CandidateInfoHash)
            && availability.CandidateSize.HasValue
            && availability.CandidateSeeders.HasValue
            && !string.IsNullOrWhiteSpace(availability.CandidateIndexer))
        {
            rank++;
            var candidate = new MagnetCandidate(
                availability.CandidateMagnet,
                availability.CandidateInfoHash,
                availability.CandidateSize.Value,
                availability.CandidateSeeders.Value,
                availability.CandidateIndexer)
            {
                Title = meta.Title,
            };
            var failure = await _db.GetMagnetFailureAsync(
                new MagnetFailureKey(cacheKey.TmdbId, cacheKey.ImdbId, cacheKey.Type, cacheKey.Season, cacheKey.Episode, cacheKey.Preset, candidate.Magnet),
                cfg.SourceValidationPolicyVersion,
                ct).ConfigureAwait(false);
            if ((failure is null || includeRejected) && seen.Add(candidate.Magnet))
            {
                result.Add(new CandidateWithFailure(
                    candidate,
                    failure,
                    null,
                    rank,
                    string.Equals(candidate.Magnet, currentMagnet, StringComparison.Ordinal)));
            }
        }

        if (probe.Outcome == MagnetProbeOutcome.Available)
        {
            foreach (var candidate in probe.Candidates)
            {
                ct.ThrowIfCancellationRequested();
                rank++;
                var failure = await _db.GetMagnetFailureAsync(
                    new MagnetFailureKey(cacheKey.TmdbId, cacheKey.ImdbId, cacheKey.Type, cacheKey.Season, cacheKey.Episode, cacheKey.Preset, candidate.Magnet),
                    cfg.SourceValidationPolicyVersion,
                    ct).ConfigureAwait(false);
                if ((failure is null || includeRejected) && seen.Add(candidate.Magnet))
                {
                    result.Add(new CandidateWithFailure(
                        candidate,
                        failure,
                        null,
                        rank,
                        string.Equals(candidate.Magnet, currentMagnet, StringComparison.Ordinal)));
                }
            }
        }

        return result.Count > 0
            ? (result, null, null)
            : (result, probe.ErrorKind, probe.ErrorMessage);
    }

    private static MagnetCandidate ToMagnetCandidate(SourceCandidateRow row)
        => new(
            row.Magnet,
            row.InfoHash,
            row.Size ?? 0,
            row.Seeders ?? 0,
            row.Indexer)
        {
            Title = row.Title,
        };

    private async Task<(MaterialisedStateRow State, MagnetCacheEntry? Entry)?> GetCurrentAsync(SourceKey key, string? imdb, CancellationToken ct)
    {
        var state = await _db.GetMaterialisedStateAsync(key.TmdbId, key.Type, key.SeasonSentinel, key.EpisodeSentinel, ct)
            .ConfigureAwait(false);
        if (state is null)
        {
            return null;
        }

        var entry = await _db.GetCachedMagnetAsync(CacheKey(key, imdb, _configProvider().SourcePickerPreset), ct)
            .ConfigureAwait(false);
        return (state, entry);
    }

    private async Task<string?> ResolveImdbAsync(SourceKey key, CancellationToken ct)
    {
        var imdbLookupType = key.Type == "episode" ? "series" : "movie";
        return await _externalIds.GetImdbIdAsync(key.TmdbId, imdbLookupType, ct).ConfigureAwait(false);
    }

    private static MagnetCacheKey CacheKey(SourceKey key, string? imdb, string preset)
        => new(key.TmdbId, imdb, key.Type, key.Season, key.Episode, preset);

    private static PhantomCurrentSourceDto ToCurrentDto(MaterialisedStateRow state, MagnetCacheEntry? entry)
        => new()
        {
            Magnet = entry?.Magnet,
            InfoHash = entry?.InfoHash,
            Size = entry?.Size,
            Seeders = entry?.Seeders,
            Indexer = entry?.Indexer,
            StubPath = state.StubPath,
            FusePath = state.FusePath,
            MaterialisedAt = state.MaterialisedAt,
        };

    private static PhantomSourceCandidateDto ToCandidateDto(CandidateWithFailure candidate)
    {
        var validationRejected = IsValidationRejected(candidate.SourceRow, null);
        return new()
        {
            Magnet = candidate.Candidate.Magnet,
            InfoHash = candidate.Candidate.InfoHash,
            Size = candidate.Candidate.Size,
            Seeders = candidate.Candidate.Seeders,
            Indexer = candidate.Candidate.Indexer,
            Title = candidate.Candidate.Title,
            Rank = candidate.Rank,
            IsCurrent = candidate.IsCurrent,
            IsRejected = candidate.Failure is not null || validationRejected,
            FailureReason = candidate.Failure?.Reason ?? (validationRejected ? candidate.SourceRow?.ValidationReason ?? candidate.SourceRow?.ValidationStatus : null),
            RetryAfter = candidate.Failure?.RetryAfter ?? (validationRejected ? candidate.SourceRow?.ValidationExpiresAt : null),
        };
    }

    private static bool IsValidationRejected(SourceCandidateRow? row, PluginConfiguration? cfg)
    {
        if (row?.ValidationExpiresAt is null || row.ValidationExpiresAt.Value < DateTimeOffset.UtcNow)
        {
            return false;
        }

        if (cfg is not null && !string.Equals(row.ValidationPolicyVersion, cfg.SourceValidationPolicyVersion, StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(row.ValidationStatus, "invalid", StringComparison.OrdinalIgnoreCase);
    }

    private static PhantomSourceOperationResult Result(PhantomSourceOperationStatus status, string code, string message)
        => new()
        {
            Status = status,
            Code = code,
            Message = message,
        };

    private static bool TryResolveKey(string externalId, out SourceKey key)
    {
        key = new SourceKey(string.Empty, 0, string.Empty, null, null, ChannelItemId.Sentinel, ChannelItemId.Sentinel, string.Empty);
        if (!ChannelItemId.TryParse(externalId, out var parsed) || parsed.TmdbId is null)
        {
            return false;
        }

        string type;
        int? season;
        int? episode;
        string metadataType;
        switch (parsed.Kind)
        {
            case ChannelItemId.KindMovie:
                type = "movie";
                season = null;
                episode = null;
                metadataType = "movie";
                break;
            case ChannelItemId.KindEpisode:
                if (!parsed.Season.HasValue || !parsed.Episode.HasValue)
                {
                    return false;
                }

                type = "episode";
                season = parsed.Season;
                episode = parsed.Episode;
                metadataType = "series";
                break;
            default:
                return false;
        }

        var (sSentinel, eSentinel) = ChannelItemId.ToSentinels(season, episode);
        key = new SourceKey(externalId, parsed.TmdbId.Value, type, season, episode, sSentinel, eSentinel, metadataType);
        return true;
    }
}
