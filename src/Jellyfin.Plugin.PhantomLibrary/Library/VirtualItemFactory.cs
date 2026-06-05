using System;
using System.Globalization;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.PhantomLibrary.Library;

/// <summary>
/// Builds Jellyfin <see cref="Movie"/> / <see cref="Series"/> instances
/// populated from TMDB detail payloads. Pure construction — callers (M4 /
/// M6 work) own persistence via <c>ILibraryManager.CreateItem</c>.
/// </summary>
/// <remarks>
/// On Jellyfin 10.10 <c>LocationType</c> is a read-only computed property
/// derived from <c>Path</c>; leaving <c>Path</c> null is sufficient for
/// the item to surface as Virtual.
/// </remarks>
public static class VirtualItemFactory
{
    /// <summary>Builds an unpersisted <see cref="Movie"/> from TMDB details.</summary>
    public static Movie CreateVirtualMovie(TmdbMovieDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);

        var movie = new Movie
        {
            Name = details.Title ?? string.Empty,
            OriginalTitle = details.OriginalTitle ?? string.Empty,
            Overview = details.Overview ?? string.Empty,
            ProductionYear = ParseYear(details.ReleaseDate),
            PremiereDate = ParseDate(details.ReleaseDate),
            Genres = details.Genres,
            Tagline = details.Tagline ?? string.Empty,
            CommunityRating = details.VoteAverage.HasValue ? (float?)details.VoteAverage.Value : null,
        };

        if (details.Runtime > 0)
        {
            movie.RunTimeTicks = TimeSpan.FromMinutes(details.Runtime).Ticks;
        }

        movie.ProviderIds["Tmdb"] = details.Id.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(details.ImdbId))
        {
            movie.ProviderIds["Imdb"] = details.ImdbId!;
        }

        return movie;
    }

    /// <summary>Builds an unpersisted <see cref="Movie"/> from a TMDB search-surface hit (trending / similar / recommendations).</summary>
    public static Movie CreateVirtualMovieFromHit(TmdbSearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        var movie = new Movie
        {
            Name = hit.Title ?? string.Empty,
            OriginalTitle = hit.OriginalTitle ?? string.Empty,
            Overview = hit.Overview ?? string.Empty,
            ProductionYear = ParseYear(hit.ReleaseDate),
            PremiereDate = ParseDate(hit.ReleaseDate),
            Genres = TmdbGenres.ResolveMovieGenres(hit.GenreIds),
            CommunityRating = hit.VoteAverage.HasValue ? (float?)hit.VoteAverage.Value : null,
        };
        movie.ProviderIds["Tmdb"] = hit.Id.ToString(CultureInfo.InvariantCulture);
        return movie;
    }

    /// <summary>Builds an unpersisted <see cref="Series"/> from a TMDB search-surface hit (trending / similar / recommendations).</summary>
    public static Series CreateVirtualSeriesFromHit(TmdbSearchHit hit)
    {
        ArgumentNullException.ThrowIfNull(hit);
        var series = new Series
        {
            Name = hit.Title ?? string.Empty,
            OriginalTitle = hit.OriginalTitle ?? string.Empty,
            Overview = hit.Overview ?? string.Empty,
            ProductionYear = ParseYear(hit.ReleaseDate),
            PremiereDate = ParseDate(hit.ReleaseDate),
            Genres = TmdbGenres.ResolveSeriesGenres(hit.GenreIds),
            CommunityRating = hit.VoteAverage.HasValue ? (float?)hit.VoteAverage.Value : null,
        };
        series.ProviderIds["Tmdb"] = hit.Id.ToString(CultureInfo.InvariantCulture);
        return series;
    }

    /// <summary>Builds an unpersisted <see cref="Series"/> from TMDB details.</summary>
    public static Series CreateVirtualSeries(TmdbSeriesDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);

        var series = new Series
        {
            Name = details.Name,
            OriginalTitle = details.OriginalName ?? string.Empty,
            Overview = details.Overview ?? string.Empty,
            ProductionYear = ParseYear(details.FirstAirDate),
            PremiereDate = ParseDate(details.FirstAirDate),
            Genres = details.Genres,
            CommunityRating = details.VoteAverage.HasValue ? (float?)details.VoteAverage.Value : null,
            Status = ParseSeriesStatus(details.Status),
        };

        series.ProviderIds["Tmdb"] = details.Id.ToString(CultureInfo.InvariantCulture);
        if (!string.IsNullOrWhiteSpace(details.ImdbId))
        {
            series.ProviderIds["Imdb"] = details.ImdbId!;
        }

        return series;
    }

    private static int? ParseYear(string? date)
    {
        if (string.IsNullOrWhiteSpace(date) || date.Length < 4)
        {
            return null;
        }

        return int.TryParse(date.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : null;
    }

    private static DateTime? ParseDate(string? date)
    {
        if (string.IsNullOrWhiteSpace(date))
        {
            return null;
        }

        return DateTime.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt
            : null;
    }

    private static SeriesStatus? ParseSeriesStatus(string? status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return null;
        }

        return status.Trim().ToUpperInvariant() switch
        {
            "RETURNING SERIES" or "IN PRODUCTION" or "PILOT" or "PLANNED" => SeriesStatus.Continuing,
            "ENDED" or "CANCELED" or "CANCELLED" => SeriesStatus.Ended,
            _ => null,
        };
    }
}
