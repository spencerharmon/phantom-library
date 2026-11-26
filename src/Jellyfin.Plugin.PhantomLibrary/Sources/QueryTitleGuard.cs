using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.State;

namespace Jellyfin.Plugin.PhantomLibrary.Sources;

/// <summary>
/// Degenerate-query guard (ROI P13, p13-degenerate-query-guard). An indexer
/// title term that is empty, whitespace-only or punctuation/symbol-only
/// (e.g. the production <c>Term: [. 2026]</c> — a "." title plus the year)
/// burns indexer budget and can never match. This guard normalises a title
/// and classifies it; <see cref="MagnetSelector"/> applies it to EVERY
/// indexer query (interactive and background, movie and episode) before any
/// outbound request is built.
/// </summary>
public static class QueryTitleGuard
{
    /// <summary>
    /// Normalise a title for use as an indexer search term: trims, collapses
    /// internal whitespace runs (incl. tabs/newlines/NBSP) to a single space,
    /// and drops control/format characters (zero-width joiners etc.). Returns
    /// <c>null</c> when the result carries no letter or digit at all, i.e. is
    /// DEGENERATE and must never be sent to an indexer. Never invents text:
    /// a usable result is always a substring-preserving cleanup of the input.
    /// </summary>
    public static string? Normalize(string? title)
    {
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        var sb = new StringBuilder(title.Length);
        var pendingSpace = false;
        var hasLetterOrDigit = false;
        foreach (var rune in title.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }

            var cat = Rune.GetUnicodeCategory(rune);
            if (cat is System.Globalization.UnicodeCategory.Control
                or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.Surrogate
                or System.Globalization.UnicodeCategory.OtherNotAssigned)
            {
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(rune.ToString());
            if (Rune.IsLetterOrDigit(rune))
            {
                hasLetterOrDigit = true;
            }
        }

        return hasLetterOrDigit ? sb.ToString() : null;
    }

    /// <summary>True when <paramref name="title"/> would be a degenerate indexer term.</summary>
    public static bool IsDegenerate(string? title) => Normalize(title) is null;
}

/// <summary>Where a repaired indexer title term came from.</summary>
public enum QueryTitleSource
{
    /// <summary>The caller's own title was usable (after normalisation).</summary>
    Primary,

    /// <summary>Repaired from the TMDB record's original title.</summary>
    OriginalTitle,

    /// <summary>Repaired from a TMDB alternative / translated title.</summary>
    AlternateTitle,
}

/// <summary>A non-degenerate repaired title and its provenance.</summary>
public sealed record QueryTitleRepair(string Title, QueryTitleSource Source);

/// <summary>
/// Resolves a usable search title for an item whose primary title is
/// degenerate, strictly from the TMDB record (never fabricated).
/// </summary>
public interface IQueryTitleRepairSource
{
    /// <summary>
    /// Return the first non-degenerate title in the order original title →
    /// alternative/translated titles, or <c>null</c> when TMDB has none.
    /// <paramref name="type"/> is the indexer query type (<c>movie</c> |
    /// <c>episode</c>); episodes repair from their parent series record.
    /// </summary>
    Task<QueryTitleRepair?> RepairAsync(int tmdbId, string type, CancellationToken ct);
}

/// <summary>
/// Production <see cref="IQueryTitleRepairSource"/>: the stored
/// <c>tmdb_metadata.original_title</c> first (no network), then TMDB's
/// <c>alternative_titles</c> + <c>translations</c> for the movie / series.
/// </summary>
public sealed class TmdbQueryTitleRepairSource : IQueryTitleRepairSource
{
    private readonly PhantomDb _db;
    private readonly ITmdbClient _tmdb;

    public TmdbQueryTitleRepairSource(PhantomDb db, ITmdbClient tmdb)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _tmdb = tmdb ?? throw new ArgumentNullException(nameof(tmdb));
    }

    public async Task<QueryTitleRepair?> RepairAsync(int tmdbId, string type, CancellationToken ct)
    {
        var isMovie = string.Equals(type, "movie", StringComparison.OrdinalIgnoreCase);
        var meta = await _db.GetTmdbMetadataAsync(tmdbId, isMovie ? "movie" : "series", ct).ConfigureAwait(false);
        var original = QueryTitleGuard.Normalize(meta?.OriginalTitle);
        if (original is not null)
        {
            return new QueryTitleRepair(original, QueryTitleSource.OriginalTitle);
        }

        IReadOnlyList<string> variants = isMovie
            ? await _tmdb.GetMovieTitleVariantsAsync(tmdbId, ct).ConfigureAwait(false)
            : await _tmdb.GetSeriesTitleVariantsAsync(tmdbId, ct).ConfigureAwait(false);
        foreach (var variant in variants)
        {
            var normalised = QueryTitleGuard.Normalize(variant);
            if (normalised is not null)
            {
                return new QueryTitleRepair(normalised, QueryTitleSource.AlternateTitle);
            }
        }

        return null;
    }
}
