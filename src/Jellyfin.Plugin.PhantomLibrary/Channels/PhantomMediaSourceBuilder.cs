using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Channels;

internal static class PhantomMediaSourceBuilder
{
    private const int ProbeAnalyzeDurationMs = 3000;

    public static MediaSourceInfo CreateFileMediaSource(string path, string? name = null, string? liveStreamId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var ext = Path.GetExtension(path).TrimStart('.');
#pragma warning disable CA1308
        var container = string.IsNullOrEmpty(ext) ? "mkv" : ext.ToLowerInvariant();
#pragma warning restore CA1308
        return new MediaSourceInfo
        {
            Id = MediaSourceIds.ForFilePath(path),
            Name = name,
            Path = path,
            Container = container,
            Protocol = MediaProtocol.File,
            Type = MediaSourceType.Default,
            VideoType = VideoType.VideoFile,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true,
            SupportsProbing = true,
            RequiresOpening = false,
            RequiresClosing = liveStreamId is not null,
            LiveStreamId = liveStreamId,
            IsRemote = false,
            MediaStreams = new List<MediaStream>(),
        };
    }

    public static async Task<MediaSourceInfo> CreateFileMediaSourceAsync(
        string path,
        IMediaEncoder? mediaEncoder,
        ILogger logger,
        CancellationToken cancellationToken,
        string? name = null,
        string? liveStreamId = null)
    {
        var source = CreateFileMediaSource(path, name, liveStreamId);
        await EnsureAudioStreamsAsync(source, mediaEncoder, logger, cancellationToken).ConfigureAwait(false);
        return source;
    }

    public static async Task EnsureAudioStreamsAsync(
        MediaSourceInfo source,
        IMediaEncoder? mediaEncoder,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(logger);

        if (source.MediaStreams.Any(i => i.Type == MediaStreamType.Audio && i.Index != -1))
        {
            PhantomAudioStreamSelector.SetDefaultAudioStreamIndex(source, PhantomAudioSelectionOptions.Default);
            return;
        }

        if (mediaEncoder is null || string.IsNullOrWhiteSpace(source.Path) || !File.Exists(source.Path))
        {
            PhantomAudioStreamSelector.SetDefaultAudioStreamIndex(source, PhantomAudioSelectionOptions.Default);
            return;
        }

        source.AnalyzeDurationMs = ProbeAnalyzeDurationMs;
        var probed = await mediaEncoder.GetMediaInfo(
            new MediaInfoRequest
            {
                MediaSource = source,
                MediaType = DlnaProfileType.Video,
                ExtractChapters = false,
            },
            cancellationToken).ConfigureAwait(false);

        if (probed.MediaStreams.Count > 0)
        {
            source.MediaStreams = probed.MediaStreams;
            source.Container = string.IsNullOrWhiteSpace(probed.Container) ? source.Container : probed.Container;
            source.Formats = probed.Formats;
            source.Bitrate = probed.Bitrate;
            source.RunTimeTicks = probed.RunTimeTicks ?? source.RunTimeTicks;
            source.Size = probed.Size ?? source.Size;
            source.Timestamp = probed.Timestamp;
            source.Video3DFormat = probed.Video3DFormat;
            source.VideoType = probed.VideoType;
        }

        PhantomAudioStreamSelector.SetDefaultAudioStreamIndex(source, PhantomAudioSelectionOptions.Default);
    }
}
