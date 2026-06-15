using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Subscribes to <see cref="IUserDataManager.UserDataSaved"/>. When a
/// user's playback of a phantom-channel episode crosses the autopilot
/// threshold, hand off to <see cref="ISeriesAutopilot"/> to prefetch
/// upcoming episodes.
///
/// Splash guard: if the BaseItem still carries the <c>phantom</c> tag
/// the play was against the splash placeholder, not the real file, so
/// we ignore the event (per plan §4 footers + Stage 5.2 §"SPLASH
/// GUARD"). Once materialise completes the channel re-emits the item
/// without the tag and subsequent plays drive autopilot normally.
///
/// Heavy autopilot logic lands in Stage 5.2; this listener is the
/// channel-aware wiring that survives the rewrite.
/// </summary>
public sealed class UserDataSavedListener : IHostedService
{
    private const double PlayedPercentageThreshold = 80.0;

    private readonly IUserDataManager _userData;
    private readonly ISeriesAutopilot _autopilot;
    private readonly ILogger<UserDataSavedListener> _logger;

    public UserDataSavedListener(
        IUserDataManager userData,
        ISeriesAutopilot autopilot,
        ILogger<UserDataSavedListener> logger)
    {
        _userData = userData ?? throw new ArgumentNullException(nameof(userData));
        _autopilot = autopilot ?? throw new ArgumentNullException(nameof(autopilot));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        try
        {
            var item = e?.Item;
            if (item is null || e!.UserData is null)
            {
                return;
            }

            var played = ComputePlayedPercentage(item, e.UserData);
            if (played < PlayedPercentageThreshold)
            {
                return;
            }

            if (item.SourceType != SourceType.Channel || !ChannelIds.IsPhantom(item.ChannelId))
            {
                return;
            }

            // Splash guard: while the item is still phantom-tagged, the
            // play happened against the splash placeholder. Ignore.
            if (item.Tags is not null
                && item.Tags.Contains("phantom", StringComparer.OrdinalIgnoreCase))
            {
                return;
            }

            if (!ChannelItemId.TryParse(item.ExternalId, out var parsed)
                || parsed.Kind != ChannelItemId.KindEpisode)
            {
                return;
            }

            if (item is not Episode episode)
            {
                return;
            }

            // Fire-and-forget; autopilot handles its own errors.
            _ = _autopilot.OnEpisodePlaybackProgressAsync(
                e.UserId,
                episode,
                played,
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UserDataSavedListener handler threw; swallowing");
        }
    }

    private static double ComputePlayedPercentage(BaseItem item, MediaBrowser.Controller.Entities.UserItemData userData)
    {
        if (userData.Played)
        {
            return 100.0;
        }

        var runtime = item.RunTimeTicks ?? 0;
        if (runtime <= 0)
        {
            return 0.0;
        }

        return 100.0 * userData.PlaybackPositionTicks / runtime;
    }
}
