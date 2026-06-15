using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Channel-aware autopilot — when a user finishes a real (materialised)
/// episode of a phantom-channel series, fire-and-forget materialise
/// the next N episodes so the post-credits "play next" experience hits
/// a real file instead of the splash.
///
/// Wired from <see cref="UserDataSavedListener"/>, which handles the
/// 80%-played threshold and the channel/tag splash guard before
/// forwarding here.
///
/// Plan §5.2 rewrite.
/// </summary>
public interface ISeriesAutopilot
{
    Task OnEpisodePlaybackProgressAsync(Guid userId, Episode episode, double percentWatched, CancellationToken ct);

    // Retained no-op surface; the channel-arch listeners only drive
    // OnEpisodePlaybackProgressAsync today. The remaining methods are
    // kept for backward compatibility with any external caller that
    // bound to the Stage 2.1 interface; future stages will prune them.
    Task OnMovieFavouritedAsync(Guid userId, Movie movie, CancellationToken ct);
    Task EnsureUpcomingMaterialisedAsync(Guid userId, Series series, int currentSeason, int currentEpisode, int prefetchWindow, CancellationToken ct);
    void ResetPlaybackDebounce(Guid userId, Guid episodeId);
}

/// <inheritdoc />
public sealed class SeriesAutopilot : ISeriesAutopilot, IHostedService
{
    private const double PlayedPercentageThreshold = 80.0;

    private readonly IMaterialiser _materialiser;
    private readonly PhantomDb _db;
    private readonly ITmdbClient _tmdb;
    private readonly ILogger<SeriesAutopilot> _logger;
    private readonly Func<PluginConfiguration> _configProvider;

    public SeriesAutopilot(
        IMaterialiser materialiser,
        PhantomDb db,
        ITmdbClient tmdb,
        ILogger<SeriesAutopilot> logger)
        : this(materialiser, db, tmdb, logger,
               () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal SeriesAutopilot(
        IMaterialiser materialiser,
        PhantomDb db,
        ITmdbClient tmdb,
        ILogger<SeriesAutopilot> logger,
        Func<PluginConfiguration> configProvider)
    {
        _materialiser = materialiser ?? throw new ArgumentNullException(nameof(materialiser));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tmdb = tmdb ?? throw new ArgumentNullException(nameof(tmdb));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public async Task OnEpisodePlaybackProgressAsync(Guid userId, Episode episode, double percentWatched, CancellationToken ct)
    {
        _ = userId;
        if (episode is null)
        {
            return;
        }

        var cfg = _configProvider();
        if (!cfg.SeriesAutopilotEnabled)
        {
            return;
        }

        if (percentWatched < PlayedPercentageThreshold)
        {
            return;
        }

        // Defence-in-depth splash guard: the UserDataSavedListener
        // already filters phantom-tagged items, but autopilot guards
        // again so a future caller can't bypass it. A play of a still-
        // phantom item means the user was watching splash; do not let
        // a 10-second splash play trigger materialise storms.
        if (episode.Tags is not null
            && episode.Tags.Any(t => string.Equals(t, "phantom", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        // Episode must be a phantom-channel episode item. The id was
        // synthesised by PhantomShowsChannel.ChannelItemId.ForEpisode.
        if (!ChannelIds.IsPhantom(episode.ChannelId))
        {
            return;
        }

        if (!ChannelItemId.TryParse(episode.ExternalId, out var parsed)
            || parsed.Kind != ChannelItemId.KindEpisode)
        {
            return;
        }

        var seriesTmdb = parsed.TmdbId!.Value;
        var currentSeason = parsed.Season!.Value;
        var currentEpisode = parsed.Episode!.Value;

        var prefetch = Math.Max(0, cfg.SeriesAutopilotPrefetchEpisodes);
        if (prefetch == 0)
        {
            return;
        }

        IReadOnlyList<(int Season, int Episode)> slots;
        try
        {
            slots = await ComputeNextUpAsync(seriesTmdb, currentSeason, currentEpisode, prefetch, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Autopilot: failed to compute next-up for series tmdb={Tmdb} s{Season}e{Episode}",
                seriesTmdb, currentSeason, currentEpisode);
            return;
        }

        foreach (var (nextSeason, nextEpisode) in slots)
        {
            ct.ThrowIfCancellationRequested();
            var (sSentinel, eSentinel) = ChannelItemId.ToSentinels(nextSeason, nextEpisode);

            if (await _db.GetMaterialisedStateAsync(seriesTmdb, "episode", sSentinel, eSentinel, ct)
                    .ConfigureAwait(false) is not null)
            {
                continue;
            }

            if (await _db.IsMaterialiseInFlightAsync(seriesTmdb, "episode", sSentinel, eSentinel, ct)
                    .ConfigureAwait(false))
            {
                continue;
            }

            _logger.LogDebug(
                "Autopilot: prefetching next-up tmdb={Tmdb} s{Season}e{Episode}",
                seriesTmdb, nextSeason, nextEpisode);

            // Fire-and-forget materialise; CancellationToken.None so the
            // work outlives the playback session that triggered it.
            _ = _materialiser.MaterialiseAsync(
                seriesTmdb, "episode", nextSeason, nextEpisode,
                MaterialiseTrigger.Autopilot, CancellationToken.None);
        }
    }

    public Task OnMovieFavouritedAsync(Guid userId, Movie movie, CancellationToken ct)
        => Task.CompletedTask;

    public Task EnsureUpcomingMaterialisedAsync(Guid userId, Series series, int currentSeason, int currentEpisode, int prefetchWindow, CancellationToken ct)
        => Task.CompletedTask;

    public void ResetPlaybackDebounce(Guid userId, Guid episodeId)
    {
    }

    /// <summary>
    /// Walks forward from (currentSeason, currentEpisode + 1) for up
    /// to <paramref name="prefetch"/> slots, crossing season boundaries
    /// by re-querying TMDB for the next season. Stops at the end of the
    /// series (next season returns null or has zero episodes).
    /// </summary>
    private async Task<IReadOnlyList<(int Season, int Episode)>> ComputeNextUpAsync(
        int seriesTmdb, int currentSeason, int currentEpisode, int prefetch, CancellationToken ct)
    {
        var slots = new List<(int Season, int Episode)>(prefetch);
        var lang = _configProvider().DiscoveryLanguage;
        if (string.IsNullOrWhiteSpace(lang)) lang = null;

        var seasonDetails = await _tmdb.GetSeasonAsync(seriesTmdb, currentSeason, lang, ct).ConfigureAwait(false);
        if (seasonDetails is null)
        {
            return slots;
        }

        var maxEpisode = MaxEpisodeNumber(seasonDetails);
        var season = currentSeason;
        var ep = currentEpisode + 1;

        while (slots.Count < prefetch)
        {
            if (ep > maxEpisode)
            {
                season++;
                seasonDetails = await _tmdb.GetSeasonAsync(seriesTmdb, season, lang, ct).ConfigureAwait(false);
                if (seasonDetails is null || seasonDetails.Episodes.Count == 0)
                {
                    break; // end of series
                }

                maxEpisode = MaxEpisodeNumber(seasonDetails);
                ep = 1;
                if (maxEpisode <= 0)
                {
                    break;
                }
            }

            slots.Add((season, ep));
            ep++;
        }

        return slots;
    }

    private static int MaxEpisodeNumber(Clients.Models.TmdbSeasonDetails season)
    {
        var max = 0;
        foreach (var e in season.Episodes)
        {
            if (e.EpisodeNumber > max) max = e.EpisodeNumber;
        }

        return max;
    }
}
