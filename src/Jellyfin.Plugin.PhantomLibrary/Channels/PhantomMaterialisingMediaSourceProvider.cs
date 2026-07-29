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

        var (seasonKey, episodeKey) = ChannelItemId.ToSentinels(season, episode);
        var existing = await _db.GetMaterialisedStateAsync(parsed.TmdbId!.Value, type, seasonKey, episodeKey, cancellationToken)
            .ConfigureAwait(false);
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
                await _db.DeleteMaterialisedStateAsync(parsed.TmdbId.Value, type, seasonKey, episodeKey, cancellationToken)
                    .ConfigureAwait(false);
                existing = null;
            }
        }

        if (existing is null)
        {
            var outcome = await _materialiser.MaterialiseAsync(
                parsed.TmdbId.Value,
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
                throw new InvalidOperationException(outcome.Error ?? ("Materialise failed with status " + outcome.Status));
            }
        }

        existing ??= await WaitForMaterialisedStateAsync(parsed.TmdbId.Value, type, seasonKey, episodeKey, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            throw new TimeoutException(
                string.Create(CultureInfo.InvariantCulture, $"Timed out waiting for Phantom materialise state for {type}/{parsed.TmdbId}"));
        }

        var path = ResolveMaterialisedPath(type, existing);

        await WaitForFileAsync(path, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Phantom native-open resolved {Type}/{Tmdb} to {Path}", type, parsed.TmdbId, path);
        var source = await FuseMediaSourceAsync(path, parsed.Encode(), cancellationToken).ConfigureAwait(false);
        return new PhantomOpenedLiveStream(source);
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

    private async Task WaitForFileAsync(string path, CancellationToken ct)
    {
        var cfg = _configProvider();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, cfg.FusePathWaitTimeoutSeconds));
        var pollMs = Math.Max(50, cfg.FusePathPollIntervalMilliseconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                return;
            }

            await Task.Delay(pollMs, ct).ConfigureAwait(false);
        }

        throw new FileNotFoundException("Materialised FUSE path did not appear before playback open timeout", path);
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
