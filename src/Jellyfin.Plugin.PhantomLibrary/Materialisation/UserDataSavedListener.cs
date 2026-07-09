using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Library;
using Jellyfin.Plugin.PhantomLibrary.State;
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
    private readonly IFavouriteRecommendationIngestor _recommendationIngestor;
    private readonly PhantomDb _db;
    private readonly ILogger<UserDataSavedListener> _logger;

    public UserDataSavedListener(
        IUserDataManager userData,
        ISeriesAutopilot autopilot,
        IMaterialiser materialiser,
        IFavouriteRecommendationIngestor recommendationIngestor,
        PhantomDb db,
        ILogger<UserDataSavedListener> logger)
    {
        _userData = userData ?? throw new ArgumentNullException(nameof(userData));
        _autopilot = autopilot ?? throw new ArgumentNullException(nameof(autopilot));
        _materialiser = materialiser ?? throw new ArgumentNullException(nameof(materialiser));
        _recommendationIngestor = recommendationIngestor ?? throw new ArgumentNullException(nameof(recommendationIngestor));
        _db = db ?? throw new ArgumentNullException(nameof(db));
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

            HandleSavedUserData(item, e.UserData, e.UserId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UserDataSavedListener handler threw; swallowing");
        }
    }

    internal void HandleSavedUserData(BaseItem item, MediaBrowser.Controller.Entities.UserItemData userData, Guid userId)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(userData);

        if (!ChannelItemId.TryParse(item.ExternalId, out _))
        {
            return;
        }

        // Per-user eager-probe gate (REQ-M14-PER-USER, Surface 4): a user's own
        // interactions drive eager source probing / materialise only when their
        // allow_eager toggle is on. Evaluated at most once per event, lazily
        // (only when a probe is actually about to fire) and fail-open — a pref
        // read that throws is treated as "allowed" so a transient DB hiccup
        // never silently suppresses probing.
        bool? allowEagerCache = null;
        bool AllowEager()
        {
            allowEagerCache ??= ReadAllowEager(userId);
            return allowEagerCache.Value;
        }

        if (userData.IsFavorite)
        {
            if (AllowEager())
            {
                TryTriggerFavouriteMaterialise(item);
            }

            // Recommendations are catalogue expansion off an explicit taste
            // signal, not this user's own source-probe budget — left ungated.
            TryTriggerFavouriteRecommendations(item);
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

        if (!ChannelItemId.TryParse(item.ExternalId, out var parsed)
            || parsed.Kind != ChannelItemId.KindEpisode)
        {
            return;
        }

        if (item is not Episode episode)
        {
            return;
        }

        // Autopilot prefetch is eager source probing driven by this user's
        // playback — same per-user allow_eager gate as favourite-materialise.
        if (!AllowEager())
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

    /// <summary>
    /// Read the acting user's <c>allow_eager</c> toggle via the async
    /// <see cref="PhantomDb"/> accessor, bridged synchronously (the
    /// UserDataSaved event handler is sync). Microsoft.Data.Sqlite's async
    /// API runs synchronously, so this does not deadlock (same pattern as
    /// <c>ChannelStateProvider</c>). Fails OPEN: a read error returns
    /// <see langword="true"/> so probing is never silently disabled.
    /// </summary>
    private bool ReadAllowEager(Guid userId)
    {
        try
        {
            return _db.GetUserPrefsAsync(userId, CancellationToken.None)
                .GetAwaiter().GetResult().AllowEager;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "UserDataSavedListener could not read allow_eager for {User}; treating as allowed", userId);
            return true;
        }
    }

    private void TryTriggerFavouriteMaterialise(BaseItem item)
    {
        if (!ChannelItemId.TryParse(item.ExternalId, out var parsed))
        {
            return;
        }

        switch (parsed.Kind)
        {
            case ChannelItemId.KindMovie when parsed.TmdbId.HasValue:
                _ = _materialiser.MaterialiseAsync(
                    parsed.TmdbId.Value,
                    "movie",
                    null,
                    null,
                    MaterialiseTrigger.Favourite,
                    CancellationToken.None);
                break;
            case ChannelItemId.KindEpisode when parsed.TmdbId.HasValue && parsed.Season.HasValue && parsed.Episode.HasValue:
                _ = _materialiser.MaterialiseAsync(
                    parsed.TmdbId.Value,
                    "episode",
                    parsed.Season.Value,
                    parsed.Episode.Value,
                    MaterialiseTrigger.Favourite,
                    CancellationToken.None);
                break;
        }
    }

    /// <summary>
    /// On a favourite, expand the catalogue toward the user's taste by
    /// ingesting TMDB similar/recommendations for the favourited title.
    /// Movies seed movie recommendations; series, season, and episode
    /// favourites all seed series recommendations off the parent series id
    /// (an episode's <see cref="ChannelItemId.TmdbId"/> is the series id).
    /// Fires regardless of the splash guard — a favourite is an explicit
    /// taste signal even when the play was against the placeholder.
    /// </summary>
    private void TryTriggerFavouriteRecommendations(BaseItem item)
    {
        if (!ChannelItemId.TryParse(item.ExternalId, out var parsed) || !parsed.TmdbId.HasValue)
        {
            return;
        }

        var type = parsed.Kind switch
        {
            ChannelItemId.KindMovie => "movie",
            ChannelItemId.KindSeries => "series",
            ChannelItemId.KindSeason => "series",
            ChannelItemId.KindEpisode => "series",
            _ => null,
        };

        if (type is null)
        {
            return;
        }

        _ = IngestFavouriteRecommendationsAsync(parsed.TmdbId.Value, type);
    }

    private async Task IngestFavouriteRecommendationsAsync(int tmdbId, string type)
    {
        try
        {
            await _recommendationIngestor.IngestForFavouriteAsync(tmdbId, type, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Favourite-recommendation ingest failed for {Type} {Tmdb}", type, tmdbId);
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
