using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Subscribes to <see cref="ISessionManager.PlaybackStart"/> and
/// <see cref="ISessionManager.PlaybackStopped"/>.
///
/// On PlaybackStart of a phantom (Path under the phantom-stub root):
///   enqueue the item for materialisation. The user clicked Play on
///   a Virtual item; M5's splash is what's playing right now; the
///   real torrent registration happens in the background while the
///   splash loops. Next play press hits the real fuse path.
///
/// On PlaybackStopped of a phantom:
///   reset UserData (PlayCount, Played, LastPlayedDate,
///   PlaybackPositionTicks) so the splash playback does not pollute
///   the user's watch history. Real played-state only counts after
///   materialisation, when the user plays the actual content.
///
/// Per PLAN §M11 issues #5 and #6.
/// </summary>
public sealed class PlaybackTriggerListener : IHostedService
{
    private readonly ISessionManager _sessions;
    private readonly IMaterialisationQueue _queue;
    private readonly IUserDataManager _userData;
    private readonly PhantomDb _db;
    private readonly ILogger<PlaybackTriggerListener> _logger;

    public PlaybackTriggerListener(
        ISessionManager sessions,
        IMaterialisationQueue queue,
        IUserDataManager userData,
        PhantomDb db,
        ILogger<PlaybackTriggerListener> logger)
    {
        _sessions = sessions;
        _queue = queue;
        _userData = userData;
        _db = db;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart += OnPlaybackStart;
        _sessions.PlaybackStopped += OnPlaybackStopped;
        _logger.LogInformation("[PlaybackTrigger] subscribed to ISessionManager events");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= OnPlaybackStart;
        _sessions.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    internal void HandlePlaybackStart(PlaybackProgressEventArgs e)
        => OnPlaybackStart(this, e);

    internal void HandlePlaybackStopped(PlaybackStopEventArgs e)
        => OnPlaybackStopped(this, e);

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        if (e?.Item is null) return;
        if (!IsPhantom(e.Item)) return;

        // Only enqueue materialise for content types we actually
        // resolve via gostream. Episodes and Movies map cleanly;
        // Series is a container — autopilot handles episode-by-
        // episode resolution on its own.
        if (e.Item is not Movie && e.Item is not Episode)
        {
            _logger.LogDebug(
                "[PlaybackTrigger] phantom {Name} ({Id}) is not Movie/Episode (type={Type}); skipping enqueue",
                e.Item.Name, e.Item.Id, e.Item.GetType().Name);
            return;
        }

        _logger.LogInformation(
            "[PlaybackTrigger] phantom Play pressed: {Name} ({Id}); enqueueing materialise",
            e.Item.Name, e.Item.Id);

        try
        {
            _queue.EnqueueUser(e.Item.Id, MaterialiseTrigger.Play);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[PlaybackTrigger] enqueue failed for {Id}", e.Item.Id);
        }
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        if (e?.Item is null) return;
        if (!IsPhantom(e.Item)) return;

        // The splash played, then ended. Jellyfin's session machinery
        // has by now incremented PlayCount and possibly set
        // PlayedDate / Played=true on each affected user's UserData.
        // Reset to zero so the phantom does not appear "watched".
        // After successful materialise + real playback, real
        // played-state from the real file will count normally.
        var users = e.Users ?? new System.Collections.Generic.List<Jellyfin.Database.Implementations.Entities.User>();
        if (users.Count == 0) return;

        _ = Task.Run(() =>
        {
            foreach (var user in users)
            {
                try
                {
                    var ud = _userData.GetUserData(user, e.Item);
                    if (ud is null) continue;

                    var changed = ud.PlayCount > 0 || ud.Played
                        || ud.PlaybackPositionTicks > 0 || ud.LastPlayedDate is not null;
                    if (!changed) continue;

                    ud.PlayCount = 0;
                    ud.Played = false;
                    ud.PlaybackPositionTicks = 0;
                    ud.LastPlayedDate = null;

                    _userData.SaveUserData(
                        user, e.Item, ud,
                        UserDataSaveReason.UpdateUserRating,
                        CancellationToken.None);

                    _logger.LogDebug(
                        "[PlaybackTrigger] reset UserData for phantom {Name} ({Id}) user {User}",
                        e.Item.Name, e.Item.Id, user.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "[PlaybackTrigger] UserData reset failed for {Id} user {User}",
                        e.Item.Id, user.Id);
                }
            }
        });
    }

    private static bool IsPhantom(BaseItem item)
        => UserDataSavedListener.IsPhantomPath(item.Path);
}
