using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Jellyfin.Plugin.PhantomLibrary.Playback;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.PhantomLibrary.Channels;

/// <summary>
/// Wraps the extracted splash.mp4 path as a <see cref="MediaSourceInfo"/>
/// suitable for emission by phantom channels for not-yet-materialised
/// items. Singleton; performs the synchronous splash extraction on
/// first <see cref="CreateMediaSource"/> call, behind a lazy gate, so
/// the file is guaranteed to exist on disk before any channel emits
/// a MediaSourceInfo pointing at it.
/// </summary>
public sealed class SplashSourceProvider
{
    private readonly IApplicationPaths _paths;
    private string? _resolved;
    private readonly object _lock = new();

    public SplashSourceProvider(IApplicationPaths paths)
    {
        _paths = paths;
    }

    /// <summary>
    /// Absolute path to the extracted splash file on disk. Extracts
    /// on first access (synchronous, idempotent).
    /// </summary>
    public string ResolveSplashPath()
    {
        if (_resolved is not null)
        {
            return _resolved;
        }

        lock (_lock)
        {
            if (_resolved is null)
            {
                _resolved = SplashStream.GetLocalPath(_paths);
            }

            return _resolved;
        }
    }

    /// <summary>
    /// Build a fresh <see cref="MediaSourceInfo"/> pointing at the
    /// splash file. Triggers extraction if not yet resolved. Channel
    /// callers should return a NEW instance per emit — Jellyfin's
    /// serializers may mutate it.
    /// </summary>
    public MediaSourceInfo CreateMediaSource()
    {
        var path = ResolveSplashPath();
        return new MediaSourceInfo
        {
            Id = MediaSourceIds.ForSplashPath(path),
            Path = path,
            Container = "mp4",
            Protocol = MediaProtocol.File,
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            IsRemote = false,
            MediaStreams = new List<MediaStream>(),
        };
    }
}
