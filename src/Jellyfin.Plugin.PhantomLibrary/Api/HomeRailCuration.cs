using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.PhantomLibrary.Api;

/// <summary>
/// home-shelves-per-user-curation.
///
/// A user's derived viewing taste: normalized genre affinities plus the
/// movie/TV split of what they actually watch. Built cheaply from a bounded
/// recent-play-history query (never an O(catalogue) scan) — see
/// <c>PhantomLibraryShelvesController.BuildTasteProfileAsync</c>.
/// </summary>
/// <param name="GenreWeights">
/// Lower-cased genre name → affinity in [0,1] (1 = the user's most-watched
/// genre). Empty when there is no history.
/// </param>
/// <param name="MovieShare">Fraction of watched titles that are movies, in [0,1].</param>
/// <param name="TvShare">Fraction of watched titles that are TV, in [0,1].</param>
/// <param name="SampleCount">Number of played titles the profile was built from (0 = cold start).</param>
public sealed record TasteProfile(
    IReadOnlyDictionary<string, double> GenreWeights,
    double MovieShare,
    double TvShare,
    int SampleCount)
{
    /// <summary>A cold-start profile (no history): no genre bias, neutral 50/50 type split.</summary>
    public static TasteProfile Empty { get; } = new TasteProfile(
        new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
        0.5,
        0.5,
        0);
}

/// <summary>
/// One candidate Home-screen rail before per-user selection: a single
/// (category, media-type) pairing with its already-resolved navigable members.
/// </summary>
/// <param name="Key">Unique rail key (<c>{category}::{movie|tv}</c>).</param>
/// <param name="Category">Base curated-row key (e.g. <c>available_now</c>, <c>genre_action</c>).</param>
/// <param name="Title">Human category title (e.g. "Available now", "Action").</param>
/// <param name="MediaType">"Movie" or "Series" — the rail's single media type.</param>
/// <param name="Items">Ordered, navigable members.</param>
public sealed record RailCandidate(
    string Key,
    string Category,
    string Title,
    string MediaType,
    IReadOnlyList<ShelfItem> Items);

/// <summary>
/// Pure, testable per-user Home-rail curation. Given every candidate
/// (category × media-type) rail and the user's <see cref="TasteProfile"/>,
/// scores each rail by how well it matches what the user normally watches
/// (genre affinity × media-type share) and returns a bounded subset in a
/// stable display order. Cold-start users (no history) get a sensible default
/// subset instead of an empty or arbitrary one.
/// </summary>
public static class HomeRailSelector
{
    // Base desirability of the well-known "spotlight" categories, independent
    // of genre taste. Genre rails derive their base from the user's affinity
    // for that genre instead (see ScoreCategory).
    private static readonly Dictionary<string, double> SpotlightBase = new Dictionary<string, double>(StringComparer.Ordinal)
    {
        [Channels.CuratedRows.KeyAvailableNow] = 1.00,
        [Channels.CuratedRows.KeyForYou] = 0.95,
        [Channels.CuratedRows.KeyTrending] = 0.90,
        [Channels.CuratedRows.KeyNewReleases] = 0.85,
        [Channels.CuratedRows.KeyPopular] = 0.80,
        [Channels.CuratedRows.KeyLeavingSoon] = 0.45,
    };

    /// <summary>
    /// Select and order the Home rails for a user.
    /// </summary>
    /// <param name="all">Every candidate rail (both media types).</param>
    /// <param name="profile">The user's taste profile (<see cref="TasteProfile.Empty"/> for cold start).</param>
    /// <param name="maxRails">Hard cap on rails returned (clamped to ≥1).</param>
    /// <param name="displayOrder">Category → display rank (lower first); unknown categories sort after known.</param>
    /// <returns>The chosen rails in display order.</returns>
    public static IReadOnlyList<RailCandidate> Select(
        IReadOnlyList<RailCandidate> all,
        TasteProfile profile,
        int maxRails,
        IReadOnlyDictionary<string, int> displayOrder)
    {
        ArgumentNullException.ThrowIfNull(all);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(displayOrder);
        var cap = Math.Max(1, maxRails);

        if (all.Count == 0)
        {
            return Array.Empty<RailCandidate>();
        }

        // Score every rail, keeping its incoming index for a stable tie-break.
        var scored = all
            .Select((rail, index) => (rail, index, score: Score(rail, profile)))
            .OrderByDescending(t => t.score)
            .ThenBy(t => t.index)
            .ToList();

        List<(RailCandidate Rail, int Index)> kept = scored
            .Take(cap)
            .Select(t => (t.rail, t.index))
            .ToList();

        // Keep the filter toggle meaningful: if the user demonstrably watches a
        // media type (share ≥ 15%) but truncation dropped every rail of it,
        // swap the lowest-scoring kept rail for the best available rail of the
        // missing type. Cold-start (SampleCount 0) uses neutral 50/50 shares so
        // both types are guaranteed representation here too.
        if (profile.MovieShare >= 0.15)
        {
            EnsureTypePresence(kept, scored, "Movie");
        }

        if (profile.TvShare >= 0.15)
        {
            EnsureTypePresence(kept, scored, "Series");
        }

        // Final display order: by category rank, then movies before TV, then
        // title — so the two type variants of a category sit together.
        return kept
            .OrderBy(k => displayOrder.TryGetValue(k.Rail.Category, out var o) ? o : 50)
            .ThenBy(k => k.Rail.MediaType == "Movie" ? 0 : 1)
            .ThenBy(k => k.Rail.Title, StringComparer.OrdinalIgnoreCase)
            .Select(k => k.Rail)
            .ToList();
    }

    private static double Score(RailCandidate rail, TasteProfile profile)
    {
        double categoryBase;
        if (SpotlightBase.TryGetValue(rail.Category, out var spotlight))
        {
            categoryBase = spotlight;
        }
        else
        {
            // Genre rail: base is driven by the user's affinity for this genre.
            // Range 0.30 (never watched) .. 1.30 (top genre) so a strongly
            // matched genre can out-rank a low-value spotlight (e.g. leaving_soon).
            var weight = profile.GenreWeights.TryGetValue(rail.Title, out var w) ? w : 0d;
            categoryBase = 0.30 + weight;
        }

        // Media-type multiplier: favour the type the user watches more, with a
        // 0.35 floor so the non-preferred type is de-emphasised, never erased.
        var share = rail.MediaType == "Movie" ? profile.MovieShare : profile.TvShare;
        var typeMultiplier = 0.35 + (0.65 * share);

        return categoryBase * typeMultiplier;
    }

    private static void EnsureTypePresence(
        List<(RailCandidate Rail, int Index)> kept,
        List<(RailCandidate Rail, int Index, double Score)> scored,
        string mediaType)
    {
        if (kept.Any(k => k.Rail.MediaType == mediaType))
        {
            return;
        }

        var candidate = scored.FirstOrDefault(t => t.Rail.MediaType == mediaType);
        if (candidate.Rail is null)
        {
            return; // no rail of this type exists at all
        }

        // Evict the lowest-scoring kept rail (last, since kept is score-ordered)
        // and insert the missing type's best rail.
        if (kept.Count > 0)
        {
            kept.RemoveAt(kept.Count - 1);
        }

        kept.Add((candidate.Rail, candidate.Index));
    }
}
