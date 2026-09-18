using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using MediaBrowser.Common.Extensions;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Channels;

/// <summary>
/// Native-client materialise-on-play media source provider.
///
/// Phantom channel rows emit a RequiresOpening source. Jellyfin clients then
/// show their native loading UI while MediaSourceManager calls this provider's
/// OpenMediaSource method. We materialise the item, wait for the FUSE path, and
/// return the real file source; playback starts on the real media rather than a
/// finite splash video.
/// </summary>
public sealed class PhantomMaterialisingMediaSourceProvider : IMediaSourceProvider
{
    private const string TokenPrefix = "phantom:";

    private readonly PhantomDb _db;
    private readonly IMaterialiser _materialiser;
    private readonly IMediaEncoder? _mediaEncoder;
    private readonly Func<PluginConfiguration> _configProvider;
    private readonly ILogger<PhantomMaterialisingMediaSourceProvider> _logger;

    public PhantomMaterialisingMediaSourceProvider(
        PhantomDb db,
        IMaterialiser materialiser,
        IMediaEncoder mediaEncoder,
        ILogger<PhantomMaterialisingMediaSourceProvider> logger)
        : this(db, materialiser, mediaEncoder, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal PhantomMaterialisingMediaSourceProvider(
        PhantomDb db,
        IMaterialiser materialiser,
        ILogger<PhantomMaterialisingMediaSourceProvider> logger,
        Func<PluginConfiguration> configProvider)
        : this(db, materialiser, null, logger, configProvider)
    {
    }

    internal PhantomMaterialisingMediaSourceProvider(
        PhantomDb db,
        IMaterialiser materialiser,
        IMediaEncoder? mediaEncoder,
        ILogger<PhantomMaterialisingMediaSourceProvider> logger,
        Func<PluginConfiguration> configProvider)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _materialiser = materialiser ?? throw new ArgumentNullException(nameof(materialiser));
        _mediaEncoder = mediaEncoder;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    public static string ProviderPrefix =>
        typeof(PhantomMaterialisingMediaSourceProvider).FullName!.GetMD5().ToString("N", CultureInfo.InvariantCulture) + "_";

    public static MediaSourceInfo CreateOpeningMediaSource(ChannelItemId id, bool prefixedToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        var encoded = id.Encode();
        var token = TokenPrefix + encoded;
        if (prefixedToken)
        {
            token = ProviderPrefix + token;
        }

        return new MediaSourceInfo
        {
            Id = MediaSourceIds.ForPhantomOpenToken(encoded),
            Name = "Materialise on play",
            Path = string.Empty,
            Protocol = MediaProtocol.File,
            Container = "mkv",
            Type = MediaSourceType.Default,
            VideoType = VideoType.VideoFile,
            RequiresOpening = true,
            OpenToken = token,
            SupportsDirectPlay = false,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            SupportsProbing = true,
            IsRemote = false,
            MediaStreams = new List<MediaStream>
            {
                new()
                {
                    Type = MediaStreamType.Video,
                    Index = -1,
                    Width = 9999,
                    Height = 9999,
                    IsDefault = true,
                },
            },
        };
    }

    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        if (item.SourceType != SourceType.Channel)
        {
            return Array.Empty<MediaSourceInfo>();
        }

        if (!ChannelItemId.TryParse(item.ExternalId, out var parsed))
        {
            return Array.Empty<MediaSourceInfo>();
        }

        // Static ChannelItemInfo.MediaSources already carry the prefixed
        // Phantom opener. Do not add the same opener again via dynamic
        // provider fanout.
        if (item is IHasMediaSources hasMediaSources
            && hasMediaSources.GetMediaSources(enablePathSubstitution: false)
                .Any(s => (s.OpenToken ?? string.Empty).Contains(TokenPrefix, StringComparison.Ordinal)))
        {
            return Array.Empty<MediaSourceInfo>();
        }

        // Existing gostream files enriched as movie_<tmdb> have no
        // materialised_state row by design, but they already carry a concrete
        // playable file path under the configured gostream root. Do not add a
        // phantom opener beside that real source. Do NOT treat arbitrary
        // existing files (notably stale splash.mp4 paths from older builds) as
        // real gostream sources.
        if (IsConfiguredGostreamPath(item.Path, parsed.Kind) && File.Exists(item.Path))
        {
            return Array.Empty<MediaSourceInfo>();
        }

        switch (parsed.Kind)
        {
            case ChannelItemId.KindMovie:
                {
                    var state = await _db.GetMaterialisedStateAsync(
                        parsed.TmdbId!.Value, "movie", ChannelItemId.Sentinel, ChannelItemId.Sentinel, cancellationToken)
                        .ConfigureAwait(false);
                    return state is null || !File.Exists(GostreamPathResolver.ResolveMoviePath(state.FusePath))
                        ? new[] { CreateOpeningMediaSource(parsed, prefixedToken: false) }
                        : Array.Empty<MediaSourceInfo>();
                }

            case ChannelItemId.KindEpisode:
                {
                    var state = await _db.GetMaterialisedStateAsync(
                        parsed.TmdbId!.Value, "episode", parsed.Season!.Value, parsed.Episode!.Value, cancellationToken)
                        .ConfigureAwait(false);
                    return state is null || !File.Exists(GostreamPathResolver.ResolveEpisodePath(state.FusePath))
                        ? new[] { CreateOpeningMediaSource(parsed, prefixedToken: false) }
                        : Array.Empty<MediaSourceInfo>();
                }

            default:
                return Array.Empty<MediaSourceInfo>();
        }
    }

    public async Task<ILiveStream> OpenMediaSource(string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(openToken);
        _ = currentLiveStreams;

        var parsed = ParseToken(openToken);
        var (type, season, episode) = parsed.Kind switch
        {
            ChannelItemId.KindMovie => ("movie", (int?)null, (int?)null),
            ChannelItemId.KindEpisode => ("episode", parsed.Season, parsed.Episode),
            _ => throw new InvalidOperationException("Unsupported Phantom open token kind: " + parsed.Kind),
        };

        // item_type tag for the definitive per-attempt playback-outcome metric
        // (movie AND episode parity — every terminal path records exactly once).
        var itemTypeTag = type == "episode"
            ? Diagnostics.PhantomFlowMetrics.ItemTypeEpisode
            : Diagnostics.PhantomFlowMetrics.ItemTypeMovie;

        var (seasonKey, episodeKey) = ChannelItemId.ToSentinels(season, episode);
        var existing = await _db.GetMaterialisedStateAsync(parsed.TmdbId!.Value, type, seasonKey, episodeKey, cancellationToken)
            .ConfigureAwait(false);

        // flow tag: an item already materialised on disk is play_already_materialised;
        // otherwise this attempt materialises then plays. Resolved here (before the
        // stale-file re-materialise below) so the flow reflects the attempt's real shape.
        var alreadyMaterialised = existing is not null && File.Exists(ResolveMaterialisedPath(type, existing));
        var flowTag = alreadyMaterialised
            ? Diagnostics.PhantomFlowMetrics.PlaybackFlowPlayAlreadyMaterialised
            : Diagnostics.PhantomFlowMetrics.PlaybackFlowMaterialiseThenPlay;

        try
        {
            var stream = await OpenMediaSourceCore(parsed, type, season, episode, seasonKey, episodeKey, existing, flowTag, itemTypeTag, cancellationToken)
                .ConfigureAwait(false);
            Diagnostics.PhantomFlowMetrics.RecordPlaybackOutcome(
                flowTag, itemTypeTag, Diagnostics.PhantomFlowMetrics.CauseSuccess);
            return stream;
        }
        catch (MaterialiseOutcomeSignal signal)
        {
            // A classified materialise-side failure: record the definitive cause,
            // then surface the original failure to the caller unchanged.
            Diagnostics.PhantomFlowMetrics.RecordPlaybackOutcome(flowTag, itemTypeTag, signal.Cause);
            throw new InvalidOperationException(signal.Message);
        }
        catch (TimeoutException)
        {
            Diagnostics.PhantomFlowMetrics.RecordPlaybackOutcome(
                flowTag, itemTypeTag, Diagnostics.PhantomFlowMetrics.CauseFirstByteTimeout);
            throw;
        }
        catch (FileNotFoundException)
        {
            Diagnostics.PhantomFlowMetrics.RecordPlaybackOutcome(
                flowTag, itemTypeTag, Diagnostics.PhantomFlowMetrics.CauseGostreamCannotFetch);
            throw;
        }
        catch (Exception)
        {
            // catch-all: any other host-path failure is plugin_host_error so no
            // attempt is ever silently unclassified.
            Diagnostics.PhantomFlowMetrics.RecordPlaybackOutcome(
                flowTag, itemTypeTag, Diagnostics.PhantomFlowMetrics.CausePluginHostError);
            throw;
        }
    }

    /// <summary>
    /// Internal signal carrying a classified materialise-side playback-outcome
    /// <c>cause</c> from <see cref="OpenMediaSourceCore"/> up to
    /// <see cref="OpenMediaSource"/>, which records the outcome and re-throws the
    /// failure to the caller. Never escapes this class.
    /// </summary>
#pragma warning disable CA1064 // internal-only control signal, deliberately not public
#pragma warning disable CA1032 // only the (cause) form is ever constructed
    private sealed class MaterialiseOutcomeSignal : Exception
    {
        public MaterialiseOutcomeSignal(string cause, string message)
            : base(message)
        {
            Cause = cause;
        }

        public string Cause { get; }
    }
#pragma warning restore CA1032
#pragma warning restore CA1064

    private async Task<ILiveStream> OpenMediaSourceCore(
        ChannelItemId parsed,
        string type,
        int? season,
        int? episode,
        int seasonKey,
        int episodeKey,
        MaterialisedStateRow? existing,
        string flowTag,
        string itemTypeTag,
        CancellationToken cancellationToken)
    {
        _ = flowTag;
        _ = itemTypeTag;
        if (existing is not null)
        {
            var existingPath = ResolveMaterialisedPath(type, existing);
            if (!File.Exists(existingPath))
            {
                _logger.LogWarning(
                    "Phantom materialised_state for {Type}/{Tmdb} s{Season}e{Episode} points at missing file {Path}; re-materialising",
                    type,
                    parsed.TmdbId,
                    season,
                    episode,
                    existingPath);
                await _db.DeleteMaterialisedStateAsync(parsed.TmdbId!.Value, type, seasonKey, episodeKey, cancellationToken)
                    .ConfigureAwait(false);
                existing = null;
            }
        }

        if (existing is null)
        {
            var outcome = await _materialiser.MaterialiseAsync(
                parsed.TmdbId!.Value,
                type,
                season,
                episode,
                MaterialiseTrigger.Play,
                cancellationToken).ConfigureAwait(false);

            if (outcome.Status == MaterialisationStatus.Success || outcome.Status == MaterialisationStatus.Duplicate)
            {
                existing = await _db.GetMaterialisedStateAsync(parsed.TmdbId.Value, type, seasonKey, episodeKey, cancellationToken)
                    .ConfigureAwait(false);
            }
            else if (outcome.Status == MaterialisationStatus.AlreadyInProgress)
            {
                existing = await WaitForMaterialisedStateAsync(parsed.TmdbId.Value, type, seasonKey, episodeKey, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                throw new MaterialiseOutcomeSignal(
                    ClassifyMaterialiseFailure(outcome),
                    outcome.Error ?? ("Materialise failed with status " + outcome.Status));
            }
        }

        existing ??= await WaitForMaterialisedStateAsync(parsed.TmdbId!.Value, type, seasonKey, episodeKey, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            throw new TimeoutException(
                string.Create(CultureInfo.InvariantCulture, $"Timed out waiting for Phantom materialise state for {type}/{parsed.TmdbId}"));
        }

        var path = ResolveMaterialisedPath(type, existing);

        await WaitForFileAsync(path, parsed, type, season, episode, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Phantom native-open resolved {Type}/{Tmdb} to {Path}", type, parsed.TmdbId, path);
        var source = await FuseMediaSourceAsync(path, parsed.Encode(), cancellationToken).ConfigureAwait(false);
        return new PhantomOpenedLiveStream(source);
    }

    /// <summary>
    /// Maps a failed <see cref="MaterialisationOutcome"/> to a single definitive
    /// playback-outcome cause. A materialise that produced no usable candidate is
    /// <c>no_candidate</c>; the availability oracle abstaining is
    /// <c>availability_abstain</c>; a resolved-but-unfetchable candidate is
    /// <c>magnet_dead_stale</c>; a gostream register failure is
    /// <c>gostream_register_fail</c>; anything else is <c>plugin_host_error</c>.
    /// </summary>
    private static string ClassifyMaterialiseFailure(MaterialisationOutcome outcome)
    {
        var err = (outcome.Error ?? string.Empty).ToUpperInvariant();
        if (outcome.Status == MaterialisationStatus.Unavailable
            || err.Contains("ABSTAIN", StringComparison.Ordinal)
            || err.Contains("NOT AVAILABLE", StringComparison.Ordinal)
            || err.Contains("AVAILABILITY", StringComparison.Ordinal))
        {
            return Diagnostics.PhantomFlowMetrics.CauseAvailabilityAbstain;
        }

        if (err.Contains("NO CANDIDATE", StringComparison.Ordinal)
            || err.Contains("NO SOURCE", StringComparison.Ordinal)
            || err.Contains("NO SURVIVING", StringComparison.Ordinal)
            || err.Contains("INVALID", StringComparison.Ordinal))
        {
            return Diagnostics.PhantomFlowMetrics.CauseNoCandidate;
        }

        if (err.Contains("REGISTER", StringComparison.Ordinal))
        {
            return Diagnostics.PhantomFlowMetrics.CauseGostreamRegisterFail;
        }

        if (err.Contains("MAGNET", StringComparison.Ordinal)
            || err.Contains("DEAD", StringComparison.Ordinal)
            || err.Contains("STALE", StringComparison.Ordinal)
            || err.Contains("FETCH", StringComparison.Ordinal))
        {
            return Diagnostics.PhantomFlowMetrics.CauseMagnetDeadStale;
        }

        return Diagnostics.PhantomFlowMetrics.CausePluginHostError;
    }


    private bool IsConfiguredGostreamPath(string? path, string kind)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var cfg = _configProvider();
        var root = kind == ChannelItemId.KindEpisode ? cfg.GostreamShowsRoot : cfg.GostreamMoviesRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var normalizedPath = Path.GetFullPath(path);
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.Ordinal);
    }

    private async Task<MaterialisedStateRow?> WaitForMaterialisedStateAsync(
        int tmdbId,
        string type,
        int season,
        int episode,
        CancellationToken ct)
    {
        var cfg = _configProvider();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, cfg.FusePathWaitTimeoutSeconds));
        var pollMs = Math.Max(50, cfg.FusePathPollIntervalMilliseconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var state = await _db.GetMaterialisedStateAsync(tmdbId, type, season, episode, ct).ConfigureAwait(false);
            if (state is not null)
            {
                return state;
            }

            await Task.Delay(pollMs, ct).ConfigureAwait(false);
        }

        return null;
    }

    private static string ResolveMaterialisedPath(string type, MaterialisedStateRow state)
        => type == "movie"
            ? GostreamPathResolver.ResolveMoviePath(state.FusePath)
            : GostreamPathResolver.ResolveEpisodePath(state.FusePath);

    /// <summary>
    /// Wait for the FUSE path gostream is expected to expose after a successful
    /// register. Mirrors <c>GostreamClient.AddAsync</c>'s own one-retry discipline
    /// one step earlier in the same handoff: if the path never appears within one
    /// bounded poll window, issue exactly ONE eager re-register call to gostream
    /// for the same item and, on success, extend the wait by one additional
    /// bounded poll window before finally raising <see cref="FileNotFoundException"/>.
    /// If the eager re-register call itself fails, fail fast — no stacked retries,
    /// no unbounded/exponential backoff.
    /// </summary>
    private async Task WaitForFileAsync(
        string path,
        ChannelItemId parsed,
        string type,
        int? season,
        int? episode,
        CancellationToken ct)
    {
        if (await PollForFileAsync(path, ct).ConfigureAwait(false))
        {
            return;
        }

        // First bounded poll window elapsed with no FUSE path. Issue exactly ONE
        // eager re-register to gostream for the same item (the register call one
        // step earlier already retries once on transient HTTP failure; this is the
        // symmetric single retry at the FUSE-wait step).
        _logger.LogWarning(
            "Phantom FUSE path {Path} for {Type}/{Tmdb} did not appear within the wait window; issuing one eager re-register",
            path,
            type,
            parsed.TmdbId);

        MaterialisationOutcome reregister;
        try
        {
            reregister = await _materialiser.MaterialiseAsync(
                parsed.TmdbId!.Value,
                type,
                season,
                episode,
                MaterialiseTrigger.Play,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The eager re-register call itself failed: fail fast to
            // gostream_cannot_fetch. No stacked retries.
            _logger.LogWarning(ex, "Phantom eager re-register for {Type}/{Tmdb} threw; failing fast", type, parsed.TmdbId);
            throw new FileNotFoundException("Materialised FUSE path did not appear and eager re-register failed", path, ex);
        }

        if (reregister.Status != MaterialisationStatus.Success
            && reregister.Status != MaterialisationStatus.Duplicate
            && reregister.Status != MaterialisationStatus.AlreadyInProgress)
        {
            // The eager re-register call itself failed: fail fast. No second window.
            _logger.LogWarning(
                "Phantom eager re-register for {Type}/{Tmdb} returned {Status}; failing fast",
                type,
                parsed.TmdbId,
                reregister.Status);
            throw new FileNotFoundException("Materialised FUSE path did not appear and eager re-register failed", path);
        }

        // Eager re-register succeeded: extend the wait by exactly one additional
        // bounded poll window (same config, no new knob).
        if (await PollForFileAsync(path, ct).ConfigureAwait(false))
        {
            return;
        }

        throw new FileNotFoundException("Materialised FUSE path did not appear before playback open timeout", path);
    }

    /// <summary>
    /// One bounded poll window over <see cref="PluginConfiguration.FusePathWaitTimeoutSeconds"/>
    /// at <see cref="PluginConfiguration.FusePathPollIntervalMilliseconds"/> cadence.
    /// Returns true if the file appeared, false if the window elapsed without it.
    /// </summary>
    private async Task<bool> PollForFileAsync(string path, CancellationToken ct)
    {
        var cfg = _configProvider();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, cfg.FusePathWaitTimeoutSeconds));
        var pollMs = Math.Max(50, cfg.FusePathPollIntervalMilliseconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                return true;
            }

            await Task.Delay(pollMs, ct).ConfigureAwait(false);
        }

        return false;
    }

    private static ChannelItemId ParseToken(string openToken)
    {
        var token = openToken;
        var delimiter = token.IndexOf('_', StringComparison.Ordinal);
        if (delimiter == 32)
        {
            token = token[(delimiter + 1)..];
        }

        if (!token.StartsWith(TokenPrefix, StringComparison.Ordinal))
        {
            throw new FormatException("Not a Phantom open token: " + openToken);
        }

        var encoded = token[TokenPrefix.Length..];
        return ChannelItemId.Parse(encoded);
    }

    private Task<MediaSourceInfo> FuseMediaSourceAsync(string path, string logicalId, CancellationToken cancellationToken)
        => PhantomMediaSourceBuilder.CreateFileMediaSourceAsync(
            path,
            _mediaEncoder,
            _logger,
            cancellationToken,
            name: "Materialised",
            liveStreamId: "phantom-open:" + logicalId + ":" + MediaSourceIds.ForFilePath(path));

    private sealed class PhantomOpenedLiveStream : ILiveStream
    {
        public PhantomOpenedLiveStream(MediaSourceInfo mediaSource)
        {
            MediaSource = mediaSource ?? throw new ArgumentNullException(nameof(mediaSource));
            OriginalStreamId = mediaSource.Id;
        }

        public int ConsumerCount { get; set; } = 1;

        public string OriginalStreamId { get; set; }

        public string TunerHostId => string.Empty;

        public bool EnableStreamSharing => false;

        public MediaSourceInfo MediaSource { get; set; }

        public string UniqueId => MediaSource.LiveStreamId;

        public Task Open(CancellationToken openCancellationToken) => Task.CompletedTask;

        public Task Close() => Task.CompletedTask;

        public Stream GetStream() => File.OpenRead(MediaSource.Path);

        public void Dispose()
        {
        }
    }
}
