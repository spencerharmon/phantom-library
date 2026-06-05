using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Watches <see cref="IUserDataManager.UserDataSaved"/>:
/// <list type="bullet">
/// <item>Favourite transitions on Virtual items → enqueue materialisation.</item>
/// <item>Movie favourited → forward to <see cref="ISeriesAutopilot.OnMovieFavouritedAsync"/>.</item>
/// <item>Episode PlaybackFinished / PlaybackProgress ≥ threshold → forward to
///       <see cref="ISeriesAutopilot.OnEpisodePlaybackProgressAsync"/>.</item>
/// </list>
/// PlaybackProgress is debounced per-(user, episode) inside SeriesAutopilot
/// so noisy clients trigger at most once per playback session.
/// </summary>
public sealed class UserDataSavedListener : IHostedService
{
    private readonly IUserDataManager _userData;
    private readonly IMaterialisationQueue _queue;
    private readonly ISeriesAutopilot _autopilot;
    private readonly IGostreamClient _gostream;
    private readonly PhantomDb _db;
    private readonly ILogger<UserDataSavedListener> _logger;

    // Track last-seen favourite per (user, item) so we only act on transitions.
    private readonly ConcurrentDictionary<(Guid userId, Guid itemId), bool> _lastSeen = new();

    public UserDataSavedListener(
        IUserDataManager userData,
        IMaterialisationQueue queue,
        ISeriesAutopilot autopilot,
        IGostreamClient gostream,
        PhantomDb db,
        ILogger<UserDataSavedListener> logger)
    {
        _userData = userData;
        _queue = queue;
        _autopilot = autopilot;
        _gostream = gostream;
        _db = db;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _userData.UserDataSaved += OnUserDataSaved;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _userData.UserDataSaved -= OnUserDataSaved;
        return Task.CompletedTask;
    }

    private void OnUserDataSaved(object? sender, UserDataSaveEventArgs e)
    {
        if (e?.UserData is null || e.Item is null) return;

        // Playback progress / finish for episodes → autopilot.
        if (e.Item is Episode episode
            && (e.SaveReason == UserDataSaveReason.PlaybackFinished
                || e.SaveReason == UserDataSaveReason.PlaybackProgress
                || e.SaveReason == UserDataSaveReason.TogglePlayed))
        {
            if (e.SaveReason == UserDataSaveReason.PlaybackStart)
            {
                _autopilot.ResetPlaybackDebounce(e.UserId, episode.Id);
            }

            var percent = ComputePercent(episode, e.UserData.PlaybackPositionTicks,
                e.SaveReason == UserDataSaveReason.PlaybackFinished || e.UserData.Played);
            _ = Task.Run(async () =>
            {
                try
                {
                    await _autopilot.OnEpisodePlaybackProgressAsync(e.UserId, episode, percent, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SeriesAutopilot threw on episode progress for {Id}", episode.Id);
                }
            });
        }
        else if (e.SaveReason == UserDataSaveReason.PlaybackStart && e.Item is Episode startedEp)
        {
            _autopilot.ResetPlaybackDebounce(e.UserId, startedEp.Id);
        }

        var key = (e.UserId, e.Item.Id);
        var nowFav = e.UserData.IsFavorite;
        var prevFav = _lastSeen.TryGetValue(key, out var p) && p;
        _lastSeen[key] = nowFav;

        if (nowFav == prevFav)
        {
            return;
        }

        // Vault Mode persistence hand-off on favourite transitions for already-
        // Materialised items: prestage on true, unprestage on false. Best
        // effort; failures are logged and do not block.
        var item = e.Item;
        _ = Task.Run(async () =>
        {
            try
            {
                var row = await _db.GetPhantomItemAsync(item.Id, CancellationToken.None).ConfigureAwait(false);
                if (row is null || row.State != PhantomItemState.Materialised) return;
                if (string.IsNullOrWhiteSpace(row.StubPath)) return;
                if (!await _gostream.IsVaultModePresentAsync(CancellationToken.None).ConfigureAwait(false)) return;

                if (nowFav)
                {
                    await _gostream.PrestageAsync(row.StubPath!, 50, CancellationToken.None).ConfigureAwait(false);
                    _logger.LogDebug("Vault prestage requested for {Stub} (favourite=true)", row.StubPath);
                }
                else
                {
                    await _gostream.UnprestageAsync(row.StubPath!, CancellationToken.None).ConfigureAwait(false);
                    _logger.LogDebug("Vault unprestage requested for {Stub} (favourite=false)", row.StubPath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Vault prestage/unprestage hand-off failed for {Item}", item.Id);
            }
        });

        if (!nowFav)
        {
            // No immediate eviction on un-favourite; sweeper handles it.
            return;
        }

        if (!IsMaterialisable(e.Item))
        {
            return;
        }

        _logger.LogDebug("Favourite transition true for {Item} (user {User}); enqueueing", e.Item.Id, e.UserId);
        _queue.EnqueueUser(e.Item.Id, MaterialiseTrigger.Favourite);

        if (e.Item is Movie movie)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _autopilot.OnMovieFavouritedAsync(e.UserId, movie, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "SeriesAutopilot.OnMovieFavouritedAsync threw for {Id}", movie.Id);
                }
            });
        }
    }

    private static double ComputePercent(BaseItem item, long positionTicks, bool playedFlag)
    {
        if (playedFlag) return 1.0;
        var total = item.RunTimeTicks ?? 0;
        if (total <= 0 || positionTicks <= 0) return 0.0;
        return (double)positionTicks / total;
    }

    private static bool IsMaterialisable(BaseItem item)
    {
        if (item is not Movie && item is not Series && item is not Episode)
        {
            return false;
        }

        // Treat virtual / un-pathed items as materialisable. If the item
        // already has a real path, do nothing (it's Materialised).
        if (!string.IsNullOrWhiteSpace(item.Path))
        {
            return false;
        }

        return true;
    }
}
