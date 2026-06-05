using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Library;

/// <summary>
/// Glue between TMDB and Jellyfin's Series/Season/Episode hierarchy.
/// Ensures Series, Season, and Episode rows exist in Jellyfin before
/// materialisation. Idempotent: existing rows are reused via the Tmdb
/// provider id (Series) or IndexNumber (Season/Episode).
/// </summary>
public interface ISeriesIngestor
{
    Task<Series> EnsureSeriesAsync(int seriesTmdbId, CancellationToken ct);
    Task<Episode> EnsureEpisodeAsync(int seriesTmdbId, int seasonNumber, int episodeNumber, CancellationToken ct);
    Task<int> EnsureSeasonAsync(int seriesTmdbId, int seasonNumber, CancellationToken ct);
}

/// <inheritdoc />
public sealed class SeriesIngestor : ISeriesIngestor
{
    private const string TmdbProvider = "Tmdb";

    private readonly ILibraryManager _libraryManager;
    private readonly ITmdbClient _tmdb;
    private readonly VirtualLibraryRoot _root;
    private readonly PhantomDb _db;
    private readonly IPhantomStubManager _stubs;
    private readonly ILogger<SeriesIngestor> _logger;

    public SeriesIngestor(
        ILibraryManager libraryManager,
        ITmdbClient tmdb,
        VirtualLibraryRoot root,
        PhantomDb db,
        IPhantomStubManager stubs,
        ILogger<SeriesIngestor> logger)
    {
        _libraryManager = libraryManager;
        _tmdb = tmdb;
        _root = root;
        _db = db;
        _stubs = stubs;
        _logger = logger;
    }

    public async Task<Series> EnsureSeriesAsync(int seriesTmdbId, CancellationToken ct)
    {
        var existing = FindExistingSeries(seriesTmdbId);
        if (existing is not null)
        {
            return existing;
        }

        var details = await _tmdb.GetSeriesAsync(seriesTmdbId, null, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"TMDB returned no series for id {seriesTmdbId.ToString(CultureInfo.InvariantCulture)}");

        var series = VirtualItemFactory.CreateVirtualSeries(details);
        series.Id = _libraryManager.GetNewItemId(
            $"phantom_series_{seriesTmdbId.ToString(CultureInfo.InvariantCulture)}", series.GetType());

        // Attach phantom stub + lock so the scanner cannot rename us. See PLAN §M10.
        // Episodes do NOT get stubs: the autopilot creates+materialises them in one
        // operation, so a phantom episode symlink would be born-and-die immediately.
        if (_stubs.IsReady)
        {
            try
            {
                var stubPath = await _stubs.CreateAsync(series.Name ?? string.Empty, seriesTmdbId, PhantomMediaKind.Series, ct).ConfigureAwait(false);
                series.Path = stubPath;
                series.IsLocked = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "SeriesIngestor stub create failed for tmdb={Tmdb}; series will be path-less Virtual",
                    seriesTmdbId);
            }
        }

        var parent = _root.ResolveSeriesParent() ?? _libraryManager.GetUserRootFolder();
        // See SuggestionsContributor: SetParent before CreateItem so ParentId is wired.
        if (parent is Folder pf) series.SetParent(pf);
        _libraryManager.CreateItem(series, parent);

        await _db.UpsertPhantomItemAsync(series.Id, new PhantomItemRow
        {
            TmdbId = seriesTmdbId,
            ImdbId = details.ImdbId,
            Type = "series",
            State = PhantomItemState.Virtual,
            FirstSeen = DateTimeOffset.UtcNow,
            LastTouched = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);

        _logger.LogDebug("SeriesIngestor created Series tmdb={Tmdb} ({Name})", seriesTmdbId, details.Name);
        return series;
    }

    public async Task<int> EnsureSeasonAsync(int seriesTmdbId, int seasonNumber, CancellationToken ct)
    {
        var seasonDetails = await _tmdb.GetSeasonAsync(seriesTmdbId, seasonNumber, null, ct).ConfigureAwait(false);
        if (seasonDetails is null)
        {
            return 0;
        }

        var seriesDetails = await _tmdb.GetSeriesAsync(seriesTmdbId, null, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"TMDB returned no series for id {seriesTmdbId.ToString(CultureInfo.InvariantCulture)}");

        var seriesItem = await EnsureSeriesAsync(seriesTmdbId, ct).ConfigureAwait(false);
        var seasonItem = EnsureSeasonItem(seriesItem, seasonNumber);

        var count = 0;
        foreach (var ep in seasonDetails.Episodes)
        {
            if (ep.EpisodeNumber <= 0) continue;
            await EnsureEpisodeFromDataAsync(seriesItem, seasonItem, seriesDetails, seasonDetails, ep, ct)
                .ConfigureAwait(false);
            count++;
        }

        return count;
    }

    public async Task<Episode> EnsureEpisodeAsync(int seriesTmdbId, int seasonNumber, int episodeNumber, CancellationToken ct)
    {
        var seriesItem = await EnsureSeriesAsync(seriesTmdbId, ct).ConfigureAwait(false);
        var seasonItem = EnsureSeasonItem(seriesItem, seasonNumber);

        var existing = FindEpisode(seasonItem, episodeNumber);
        if (existing is not null)
        {
            return existing;
        }

        var seriesDetails = await _tmdb.GetSeriesAsync(seriesTmdbId, null, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"TMDB returned no series for id {seriesTmdbId.ToString(CultureInfo.InvariantCulture)}");
        var seasonDetails = await _tmdb.GetSeasonAsync(seriesTmdbId, seasonNumber, null, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"TMDB returned no season {seasonNumber.ToString(CultureInfo.InvariantCulture)} for series {seriesTmdbId.ToString(CultureInfo.InvariantCulture)}");

        var summary = seasonDetails.Episodes.FirstOrDefault(e => e.EpisodeNumber == episodeNumber);
        if (summary is null)
        {
            throw new InvalidOperationException(
                $"TMDB season {seasonNumber.ToString(CultureInfo.InvariantCulture)} of series {seriesTmdbId.ToString(CultureInfo.InvariantCulture)} has no episode {episodeNumber.ToString(CultureInfo.InvariantCulture)}");
        }

        // Try to upgrade to TmdbEpisodeDetails for per-episode imdb_id.
        TmdbEpisodeSummary detailed = summary;
        try
        {
            var details = await _tmdb.GetEpisodeAsync(seriesTmdbId, seasonNumber, episodeNumber, null, ct).ConfigureAwait(false);
            if (details is not null) detailed = details;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetEpisode failed for s{S}e{E} tmdb={T}; falling back to summary", seasonNumber, episodeNumber, seriesTmdbId);
        }

        return await EnsureEpisodeFromDataAsync(seriesItem, seasonItem, seriesDetails, seasonDetails, detailed, ct)
            .ConfigureAwait(false);
    }

    private async Task<Episode> EnsureEpisodeFromDataAsync(
        Series seriesItem,
        Season seasonItem,
        TmdbSeriesDetails seriesDetails,
        TmdbSeasonDetails seasonDetails,
        TmdbEpisodeSummary episode,
        CancellationToken ct)
    {
        var existing = FindEpisode(seasonItem, episode.EpisodeNumber);
        if (existing is not null)
        {
            return existing;
        }

        var ep = VirtualItemFactory.CreateVirtualEpisode(seriesDetails, seasonDetails, episode);
        ep.Id = _libraryManager.GetNewItemId(
            $"phantom_episode_{seriesDetails.Id.ToString(CultureInfo.InvariantCulture)}_{seasonDetails.SeasonNumber.ToString(CultureInfo.InvariantCulture)}_{episode.EpisodeNumber.ToString(CultureInfo.InvariantCulture)}",
            ep.GetType());

        if (seasonItem is Folder sif) ep.SetParent(sif);
        _libraryManager.CreateItem(ep, seasonItem);

        await _db.UpsertPhantomItemAsync(ep.Id, new PhantomItemRow
        {
            TmdbId = episode.Id,
            ImdbId = (episode as TmdbEpisodeDetails)?.ImdbId,
            Type = "episode",
            State = PhantomItemState.Virtual,
            FirstSeen = DateTimeOffset.UtcNow,
            LastTouched = DateTimeOffset.UtcNow,
        }, ct).ConfigureAwait(false);

        return ep;
    }

    private Season EnsureSeasonItem(Series seriesItem, int seasonNumber)
    {
        var existing = FindSeason(seriesItem, seasonNumber);
        if (existing is not null)
        {
            return existing;
        }

        var season = new Season
        {
            ParentIndexNumber = seasonNumber,
            IndexNumber = seasonNumber,
            Name = $"Season {seasonNumber.ToString(CultureInfo.InvariantCulture)}",
            SeriesId = seriesItem.Id,
            SeriesName = seriesItem.Name,
        };

        season.Id = _libraryManager.GetNewItemId(
            $"phantom_season_{seriesItem.Id:N}_{seasonNumber.ToString(CultureInfo.InvariantCulture)}",
            season.GetType());

        if (seriesItem is Folder srf) season.SetParent(srf);
        _libraryManager.CreateItem(season, seriesItem);
        return season;
    }

    private Series? FindExistingSeries(int seriesTmdbId)
    {
        try
        {
            var q = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                HasAnyProviderId = new Dictionary<string, string>
                {
                    [TmdbProvider] = seriesTmdbId.ToString(CultureInfo.InvariantCulture),
                },
                Limit = 1,
            };
            var matches = _libraryManager.GetItemList(q);
            return matches.Count > 0 ? matches[0] as Series : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SeriesIngestor Series lookup failed for tmdb={Tmdb}", seriesTmdbId);
            return null;
        }
    }

    private Season? FindSeason(Series series, int seasonNumber)
    {
        try
        {
            foreach (var child in series.Children)
            {
                if (child is Season s && s.IndexNumber == seasonNumber)
                {
                    return s;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Season lookup failed for series {Id}", series.Id);
        }

        return null;
    }

    private Episode? FindEpisode(Season season, int episodeNumber)
    {
        try
        {
            foreach (var child in season.Children)
            {
                if (child is Episode e && e.IndexNumber == episodeNumber)
                {
                    return e;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Episode lookup failed for season {Id}", season.Id);
        }

        return null;
    }
}
