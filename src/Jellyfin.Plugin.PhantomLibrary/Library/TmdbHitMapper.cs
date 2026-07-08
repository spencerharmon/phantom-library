using System;
using System.Globalization;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using Jellyfin.Plugin.PhantomLibrary.State;

namespace Jellyfin.Plugin.PhantomLibrary.Library;

/// <summary>
/// Maps a lightweight <see cref="TmdbSearchHit"/> (as returned by
/// trending / discover / similar / recommendations list endpoints) into a
/// <see cref="TmdbMetadataRow"/> suitable for the append-only catalogue.
///
/// This is deliberately the "list-shape" mapping: it does not fetch
/// per-item details (runtime, genres, certifications). Those are warmed
/// lazily elsewhere. Both <see cref="Scheduled.DiscoveryRefreshTask"/> and
/// the favourite-recommendation ingestor share this so the catalogue rows
/// they write are byte-for-byte equivalent regardless of which surface
/// discovered the title.
/// </summary>
public static class TmdbHitMapper
{
    /// <summary>
    /// Builds a catalogue metadata row from a TMDB list hit. <paramref name="type"/>
    /// must be <c>"movie"</c> or <c>"series"</c>. Title falls back to the
    /// original-language title when the localised title is blank; callers
    /// should drop rows whose resulting <see cref="TmdbMetadataRow.Title"/>
    /// is still blank (TMDB occasionally returns neither).
    /// </summary>
    public static TmdbMetadataRow MapSearchHitToMetadata(TmdbSearchHit hit, string type)
    {
        ArgumentNullException.ThrowIfNull(hit);
        var title = !string.IsNullOrWhiteSpace(hit.Title) ? hit.Title! : (hit.OriginalTitle ?? string.Empty);
        return new TmdbMetadataRow(
            TmdbId: hit.Id,
            Type: type,
            Title: title,
            Year: ParseYear(hit.ReleaseDate),
            Overview: hit.Overview,
            PosterUrl: BuildImageUrl(hit.PosterPath),
            BackdropUrl: BuildImageUrl(hit.BackdropPath),
            Genres: null,
            OfficialRating: null,
            CommunityRating: hit.VoteAverage,
            OriginalTitle: hit.OriginalTitle,
            FetchedAt: DateTimeOffset.UtcNow);
    }

    /// <summary>Extracts the four-digit year prefix from a TMDB date string (yyyy-MM-dd).</summary>
    public static int? ParseYear(string? releaseDate)
    {
        if (string.IsNullOrWhiteSpace(releaseDate) || releaseDate.Length < 4)
        {
            return null;
        }

        if (int.TryParse(releaseDate.AsSpan(0, 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y))
        {
            return y;
        }

        return null;
    }

    /// <summary>
    /// Builds a TMDB CDN poster/backdrop URL from a relative path. <c>w500</c>
    /// is the standard size used by the Jellyfin TMDB metadata provider; the
    /// URL is stable in practice so we bypass /configuration.
    /// </summary>
    public static string? BuildImageUrl(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var prefixed = path.StartsWith('/') ? path : "/" + path;
        return "https://image.tmdb.org/t/p/w500" + prefixed;
    }
}
