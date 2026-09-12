using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Belt-and-braces fix for the operator bug "recently played is not working
/// for phantom items" (docs/tasks/recently-played-fix.md).
///
/// Root cause: a materialised phantom movie/episode is a REAL, playable
/// <see cref="BaseItem"/> whose <c>Id</c> is stable across the phantom →
/// materialised transition, but it STAYS <see cref="SourceType.Channel"/>
/// forever (channel architecture — see <c>PhantomMoviesChannel</c> header
/// comment). Jellyfin's own <c>SessionManager.OnPlaybackStopped</c> derives
/// "played to completion" from <c>UserDataManager.UpdatePlayState</c>, which
/// in turn needs a POSITIVE, STABLE <c>RunTimeTicks</c> read off the exact
/// <see cref="BaseItem"/> instance cached on the session
/// (<c>SessionInfo.FullNowPlayingItem</c>) at PLAYBACK START. For the
/// "materialise on play" flow (native <c>RequiresOpening</c> open token,
/// <see cref="PhantomMaterialisingMediaSourceProvider"/>), that instance is
/// captured while the item is still the phantom/opening placeholder — before
/// the channel's background enrichment (TMDB runtime backfill / gostream
/// probe) has necessarily landed a final <c>RunTimeTicks</c> on THAT cached
/// copy. When it hasn't, <c>UpdatePlayState</c> silently treats the report as
/// "no known runtime" and neither marks <c>Played</c> nor bumps
/// <c>LastPlayedDate</c> — so the title never surfaces in the standard
/// recently-played / resume-history surfaces (<c>Filters=IsPlayed</c>,
/// <c>SortBy=DatePlayed</c>) even though the file played back fine.
///
/// Fix: listen directly to <see cref="ISessionManager.PlaybackStopped"/> (an
/// O(1), per-event hook — never an O(catalogue) scan, preserving the
/// badges/Continue-Watching fast path and NOT reintroducing the deliberately
/// removed <c>ISupportsLatestMedia</c> "Latest" row) and, for a
/// phantom-channel movie/episode that finished playback, RE-RESOLVE the
/// authoritative BaseItem fresh via <see cref="ILibraryManager.GetItemById"/>
/// (picking up whatever RunTimeTicks/materialise state has landed by the time
/// playback actually stopped) and explicitly persist the played state via
/// <see cref="IUserDataManager.SaveUserData"/> — mirroring exactly what core
/// Jellyfin does for an ordinary library item, so the plugin's own channel
/// architecture never regresses that guarantee.
///
/// Splash guard: while the item still carries the <c>phantom</c> tag the
/// play was against the "materialise on play" splash/opening source, not
/// real content, so this listener defers to core's own bookkeeping and does
/// nothing extra — same convention as <see cref="UserDataSavedListener"/> and
/// <see cref="PlaybackTriggerListener"/>.
/// </summary>
public sealed class RecentlyPlayedSyncListener : IHostedService
{
    /// <summary>
    /// Mirrors <c>MediaBrowser.Model.Configuration.ServerConfiguration.MaxResumePct</c>'s
    /// effective default (90%): a stop reported at or above this percentage of
    /// the runtime is treated as "watched" for recently-played purposes even
    /// when the client failed to report full completion.
    /// </summary>
    private const double PlayedPercentageThreshold = 90.0;

    private readonly ISessionManager _sessions;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserDataManager _userData;
    private readonly ILogger<RecentlyPlayedSyncListener> _logger;

    public RecentlyPlayedSyncListener(
        ISessionManager sessions,
        ILibraryManager libraryManager,
        IUserDataManager userData,
        ILogger<RecentlyPlayedSyncListener> logger)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _userData = userData ?? throw new ArgumentNullException(nameof(userData));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStopped += OnPlaybackStopped;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStopped -= OnPlaybackStopped;
        return Task.CompletedTask;
    }

    private void OnPlaybackStopped(object? sender, PlaybackStopEventArgs e)
    {
        try
        {
            var stoppedItem = e?.Item;
            if (stoppedItem is null || e!.Users is null || e.Users.Count == 0)
            {
                return;
            }

            if (stoppedItem.SourceType != SourceType.Channel || !ChannelIds.IsPhantom(stoppedItem.ChannelId))
            {
                return;
            }

            if (!ChannelItemId.TryParse(stoppedItem.ExternalId, out var parsed)
                || (parsed.Kind != ChannelItemId.KindMovie && parsed.Kind != ChannelItemId.KindEpisode))
            {
                return;
            }

            // Splash guard: still-phantom means this was the "materialise on
            // play" splash/opening source, not real content playback.
            if (stoppedItem.Tags is not null
                && Array.Exists(stoppedItem.Tags, t => string.Equals(t, "phantom", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            // Re-resolve fresh — never trust a BaseItem snapshot cached back
            // at PlaybackStart, which may predate the channel's runtime
            // backfill / materialise completing.
            var freshItem = _libraryManager.GetItemById(stoppedItem.Id);
            if (freshItem is null)
            {
                return;
            }

            var runtimeTicks = freshItem.RunTimeTicks ?? 0;
            var positionTicks = e.PlaybackPositionTicks ?? 0;
            var playedToCompletion = e.PlayedToCompletion
                || (runtimeTicks > 0 && positionTicks > 0
                    && (100.0 * positionTicks / runtimeTicks) >= PlayedPercentageThreshold);

            if (!playedToCompletion)
            {
                // Not a completed watch — leave partial-progress bookkeeping
                // (resume position) to core; this listener only reinforces
                // the COMPLETED-watch → recently-played path.
                return;
            }

            foreach (var user in e.Users)
            {
                var data = _userData.GetUserData(user, freshItem);
                if (data is null)
                {
                    continue;
                }

                data.Played = true;
                data.PlayCount = Math.Max(1, data.PlayCount);
                data.PlaybackPositionTicks = 0;
                data.LastPlayedDate = DateTime.UtcNow;

                _userData.SaveUserData(user, freshItem, data, UserDataSaveReason.PlaybackFinished, CancellationToken.None);
            }

            _logger.LogInformation(
                "RecentlyPlayedSyncListener persisted completed-watch state for phantom {Kind} {External}",
                parsed.Kind,
                stoppedItem.ExternalId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RecentlyPlayedSyncListener handler threw; swallowing");
        }
    }
}
