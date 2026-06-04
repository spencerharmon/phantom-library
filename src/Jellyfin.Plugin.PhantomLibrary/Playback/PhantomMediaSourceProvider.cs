using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Playback;

/// <summary>
/// Owns the "fake play button" for Phantom Library items. For any video
/// item that has no real file on disk yet, returns a short looping
/// MP4 splash MediaSource so the play button always does something,
/// and as a side effect enqueues materialisation at user-trigger
/// priority. Materialised items return nothing here so Jellyfin's
/// default file MediaSource takes over.
/// </summary>
public sealed class PhantomMediaSourceProvider : IMediaSourceProvider
{
    private readonly IApplicationPaths _appPaths;
    private readonly IMaterialisationQueue _queue;
    private readonly ILogger<PhantomMediaSourceProvider> _logger;

    public PhantomMediaSourceProvider(
        IApplicationPaths appPaths,
        IMaterialisationQueue queue,
        ILogger<PhantomMediaSourceProvider> logger)
    {
        _appPaths = appPaths;
        _queue = queue;
        _logger = logger;
    }

    public string Name => "Phantom Library splash";

    public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(BaseItem item, CancellationToken cancellationToken)
    {
        if (item is null)
        {
            return Array.Empty<MediaSourceInfo>();
        }

        // 1. Only attach to video items.
        if (item is not Movie and not Series and not Episode)
        {
            return Array.Empty<MediaSourceInfo>();
        }

        // 2. If the item is already materialised, do nothing.
        if (!item.IsVirtualItem && !string.IsNullOrEmpty(item.Path))
        {
            return Array.Empty<MediaSourceInfo>();
        }

        // 3. Build the splash MediaSource pointing at the extracted local file.
        string splashPath;
        try
        {
            splashPath = await SplashStream.GetLocalPathAsync(_appPaths, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Per SYSTEM.md: no silent failure caching. Log and return empty
            // for this call; subsequent calls will retry extraction.
            _logger.LogError(ex, "Failed to extract splash MP4 for item {Id}", item.Id);
            return Array.Empty<MediaSourceInfo>();
        }

        var source = BuildSplashSource(item, splashPath);

        // 4. Side effect: enqueue materialisation. Never fail GetMediaSources
        //    because the queue is unhappy.
        try
        {
            _queue.EnqueueUser(item.Id, MaterialiseTrigger.Play);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EnqueueUser failed for {Id} (continuing with splash)", item.Id);
        }

        return new[] { source };
    }

    public Task<ILiveStream> OpenMediaSource(
        string openToken, List<ILiveStream> currentLiveStreams, CancellationToken cancellationToken)
    {
        // File-protocol sources never call OpenMediaSource; only Live/HLS
        // sources with RequiresOpening=true do. Surfacing a clear error if
        // some future client path drops in here is preferable to silently
        // returning a placeholder.
        throw new NotImplementedException(
            "PhantomMediaSourceProvider only returns local-file splash sources; OpenMediaSource is not used.");
    }

    private static MediaSourceInfo BuildSplashSource(BaseItem item, string splashPath)
    {
        var videoStream = new MediaStream
        {
            Type = MediaStreamType.Video,
            Index = 0,
            Codec = SplashStreamMetadata.VideoCodec,
            Profile = SplashStreamMetadata.VideoProfile,
            Level = SplashStreamMetadata.VideoLevel,
            Width = SplashStreamMetadata.Width,
            Height = SplashStreamMetadata.Height,
            AverageFrameRate = SplashStreamMetadata.VideoFps,
            RealFrameRate = SplashStreamMetadata.VideoFps,
            PixelFormat = SplashStreamMetadata.PixelFormat,
            BitRate = SplashStreamMetadata.VideoBitRate,
            IsDefault = true,
        };

        var audioStream = new MediaStream
        {
            Type = MediaStreamType.Audio,
            Index = 1,
            Codec = SplashStreamMetadata.AudioCodec,
            Profile = SplashStreamMetadata.AudioProfile,
            SampleRate = SplashStreamMetadata.AudioSampleRate,
            Channels = SplashStreamMetadata.AudioChannels,
            ChannelLayout = SplashStreamMetadata.AudioChannelLayout,
            BitRate = SplashStreamMetadata.AudioBitRate,
            IsDefault = true,
        };

        var runTimeTicks = (long)(TimeSpan.TicksPerSecond * SplashStreamMetadata.DurationSeconds);

        return new MediaSourceInfo
        {
            Id = string.Create(CultureInfo.InvariantCulture, $"phantom-splash-{item.Id:N}"),
            Name = "Phantom Library — materialising…",
            Path = splashPath,
            Protocol = MediaProtocol.File,
            Container = SplashStreamMetadata.Container,
            Type = MediaSourceType.Default,
            IsRemote = false,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            RequiresOpening = false,
            RequiresClosing = false,
            RequiresLooping = false,
            RunTimeTicks = runTimeTicks,
            MediaStreams = new[] { videoStream, audioStream },
            Formats = Array.Empty<string>(),
            Bitrate = SplashStreamMetadata.VideoBitRate + SplashStreamMetadata.AudioBitRate,
        };
    }
}
