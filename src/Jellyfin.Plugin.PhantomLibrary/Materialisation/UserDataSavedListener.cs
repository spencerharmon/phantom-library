using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Watches <see cref="IUserDataManager.UserDataSaved"/> for favourite
/// transitions on Virtual items and enqueues materialisation.
/// </summary>
public sealed class UserDataSavedListener : IHostedService
{
    private readonly IUserDataManager _userData;
    private readonly IMaterialisationQueue _queue;
    private readonly ILogger<UserDataSavedListener> _logger;

    // Track last-seen favourite per (user, item) so we only act on transitions.
    private readonly ConcurrentDictionary<(Guid userId, Guid itemId), bool> _lastSeen = new();

    public UserDataSavedListener(
        IUserDataManager userData,
        IMaterialisationQueue queue,
        ILogger<UserDataSavedListener> logger)
    {
        _userData = userData;
        _queue = queue;
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

        var key = (e.UserId, e.Item.Id);
        var nowFav = e.UserData.IsFavorite;
        var prevFav = _lastSeen.TryGetValue(key, out var p) && p;
        _lastSeen[key] = nowFav;

        if (nowFav == prevFav)
        {
            return;
        }

        if (!nowFav)
        {
            // M7: unmark-favourite handling. No-op in M4.
            return;
        }

        if (!IsMaterialisable(e.Item))
        {
            return;
        }

        _logger.LogDebug("Favourite transition true for {Item} (user {User}); enqueueing", e.Item.Id, e.UserId);
        _queue.EnqueueUser(e.Item.Id, MaterialiseTrigger.Favourite);
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
