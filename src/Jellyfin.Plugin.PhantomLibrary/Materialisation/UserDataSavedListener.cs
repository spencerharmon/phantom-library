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
    private readonly IMaterialiser _materialiser;
    private readonly IVaultManager _vault;
    private readonly ILogger<UserDataSavedListener> _logger;

    public UserDataSavedListener(
        IUserDataManager userData,
        ISeriesAutopilot autopilot,
        IMaterialiser materialiser,
        IVaultManager vault,
        ILogger<UserDataSavedListener> logger)
    {
        _userData = userData ?? throw new ArgumentNullException(nameof(userData));
        _autopilot = autopilot ?? throw new ArgumentNullException(nameof(autopilot));
        _materialiser = materialiser ?? throw new ArgumentNullException(nameof(materialiser));
        _vault = vault ?? throw new ArgumentNullException(nameof(vault));
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

            HandleSavedUserData(item, e.UserData, e.UserId, e.SaveReason);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UserDataSavedListener handler threw; swallowing");
        }
    }

    internal void HandleSavedUserData(
        BaseItem item,
        MediaBrowser.Controller.Entities.UserItemData userData,
        Guid userId,
        UserDataSaveReason? reason = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(userData);

        if (!ChannelItemId.TryParse(item.ExternalId, out var parsed))
        {
            return;
        }

        // Favourite → materialise (existing behaviour). Idempotent: repeated
        // saves on an already-materialised favourite return Duplicate. Fired
        // on every save while the item is favourited, not just on the toggle.
        Task<MaterialisationOutcome>? materialiseTask = null;
        if (userData.IsFavorite)
        {
            materialiseTask = TryTriggerFavouriteMaterialise(parsed);
        }

        // Vault Mode prestage/unprestage. Gate strictly on the discrete
        // user-metadata save reasons (favourite/rating toggle → UpdateUserRating,
        // bulk user-data API → UpdateUserData). We deliberately do NOT react to
        // playback ticks (PlaybackStart/Progress/Finished), TogglePlayed, or
        // Import: a single watch of a favourited item fires many PlaybackProgress
        // saves, and prestaging on each would spam gostream. A null reason
        // (test callers that don't exercise the vault path) is treated as
        // "unknown" and skipped.
        if (reason is UserDataSaveReason.UpdateUserRating or UserDataSaveReason.UpdateUserData
            && TryGetVaultIdentity(parsed, out var vTmdb, out var vType, out var vSeason, out var vEpisode))
        {
            if (userData.IsFavorite)
            {
                _ = PrestageAfterMaterialiseAsync(materialiseTask, vTmdb, vType, vSeason, vEpisode);
            }
            else
            {
                _ = SafeUnprestageAsync(vTmdb, vType, vSeason, vEpisode);
            }
        }

        var played = ComputePlayedPercentage(item, userData);
        if (played < PlayedPercentageThreshold)
        {
            return;
        }

        if (!ChannelIds.IsPhantom(item.ChannelId))
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

        if (parsed.Kind != ChannelItemId.KindEpisode)
        {
            return;
        }

        if (item is not Episode episode)
        {
            return;
        }

        // Fire-and-forget; autopilot handles its own errors.
        _ = _autopilot.OnEpisodePlaybackProgressAsync(
            userId,
            episode,
            played,
            CancellationToken.None);
    }

    private Task<MaterialisationOutcome>? TryTriggerFavouriteMaterialise(ChannelItemId parsed)
    {
        switch (parsed.Kind)
        {
            case ChannelItemId.KindMovie when parsed.TmdbId.HasValue:
                return _materialiser.MaterialiseAsync(
                    parsed.TmdbId.Value,
                    "movie",
                    null,
                    null,
                    MaterialiseTrigger.Favourite,
                    CancellationToken.None);
            case ChannelItemId.KindEpisode when parsed.TmdbId.HasValue && parsed.Season.HasValue && parsed.Episode.HasValue:
                return _materialiser.MaterialiseAsync(
                    parsed.TmdbId.Value,
                    "episode",
                    parsed.Season.Value,
                    parsed.Episode.Value,
                    MaterialiseTrigger.Favourite,
                    CancellationToken.None);
            default:
                return null;
        }
    }

    /// <summary>
    /// Maps a parsed channel id to the (tmdb, type, season, episode) tuple the
    /// vault manager keys on. Only movies and episodes have a vault footprint;
    /// series/season containers and orphans return false.
    /// </summary>
    private static bool TryGetVaultIdentity(
        ChannelItemId parsed,
        out int tmdbId,
        out string type,
        out int? season,
        out int? episode)
    {
        switch (parsed.Kind)
        {
            case ChannelItemId.KindMovie when parsed.TmdbId.HasValue:
                tmdbId = parsed.TmdbId.Value;
                type = "movie";
                season = null;
                episode = null;
                return true;
            case ChannelItemId.KindEpisode when parsed.TmdbId.HasValue && parsed.Season.HasValue && parsed.Episode.HasValue:
                tmdbId = parsed.TmdbId.Value;
                type = "episode";
                season = parsed.Season.Value;
                episode = parsed.Episode.Value;
                return true;
            default:
                tmdbId = 0;
                type = string.Empty;
                season = null;
                episode = null;
                return false;
        }
    }

    /// <summary>
    /// Waits for the favourite-triggered materialise to land its
    /// <c>materialised_state</c> row (Success or Duplicate), then asks the vault
    /// manager to prestage it. Awaiting first is what makes "favourite a virtual
    /// item → materialise → prestage" work in a single event: the vault manager
    /// resolves the stub path from that row, which does not exist until the
    /// materialise completes. Best-effort; all failures are swallowed.
    /// </summary>
    private async Task PrestageAfterMaterialiseAsync(
        Task<MaterialisationOutcome>? materialiseTask,
        int tmdbId,
        string type,
        int? season,
        int? episode)
    {
        try
        {
            if (materialiseTask is not null)
            {
                await materialiseTask.ConfigureAwait(false);
            }

            await _vault.PrestageAsync(tmdbId, type, season, episode, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutdown; ignore
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "[Vault] prestage-after-materialise failed for tmdb={Tmdb} type={Type}; swallowing",
                tmdbId, type);
        }
    }

    private async Task SafeUnprestageAsync(int tmdbId, string type, int? season, int? episode)
    {
        try
        {
            await _vault.UnprestageAsync(tmdbId, type, season, episode, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // shutdown; ignore
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "[Vault] unprestage failed for tmdb={Tmdb} type={Type}; swallowing",
                tmdbId, type);
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
