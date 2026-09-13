using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.PhantomLibrary.Api;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// home-shelves-per-user-curation coverage for the pure per-user rail selector
/// (<see cref="HomeRailSelector"/>): separate Movie/TV rails, ranking by the
/// user's genre affinity + movie/TV share, the rail cap, cold-start defaults,
/// and the both-types-present guarantee that keeps the filter toggle meaningful.
/// </summary>
public class HomeRailSelectorTests
{
    private static readonly IReadOnlyDictionary<string, int> DisplayOrder = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [CuratedRows.KeyAvailableNow] = 0,
        [CuratedRows.KeyNewReleases] = 1,
        [CuratedRows.KeyTrending] = 2,
        [CuratedRows.KeyPopular] = 3,
        [CuratedRows.KeyForYou] = 4,
        [CuratedRows.KeyLeavingSoon] = 100,
    };

    private static ShelfItem Item(string id)
        => new ShelfItem { Id = id, Name = id, Type = "Movie" };

    private static RailCandidate Rail(string category, string title, string mediaType)
    {
        var slug = mediaType == "Movie" ? "movie" : "tv";
        return new RailCandidate(
            category + "::" + slug,
            category,
            title,
            mediaType,
            new List<ShelfItem> { Item(category + "-1"), Item(category + "-2") });
    }

    // Both a Movie and a TV rail for every well-known category + two genres.
    private static List<RailCandidate> FullCandidateSet()
    {
        var cats = new (string Key, string Title)[]
        {
            (CuratedRows.KeyAvailableNow, "Available now"),
            (CuratedRows.KeyNewReleases, "New releases"),
            (CuratedRows.KeyTrending, "Trending this week"),
            (CuratedRows.KeyPopular, "Popular on Phantom"),
            (CuratedRows.KeyForYou, "Recommended for you"),
            (CuratedRows.GenreKeyPrefix + "action", "Action"),
            (CuratedRows.GenreKeyPrefix + "romance", "Romance"),
        };
        var rails = new List<RailCandidate>();
        foreach (var (key, title) in cats)
        {
            rails.Add(Rail(key, title, "Movie"));
            rails.Add(Rail(key, title, "Series"));
        }

        return rails;
    }

    [Fact]
    public void ColdStart_ReturnsBoundedSubset_WithBothTypes()
    {
        var rails = FullCandidateSet();
        var selected = HomeRailSelector.Select(rails, TasteProfile.Empty, 8, DisplayOrder);

        Assert.Equal(8, selected.Count);
        Assert.Contains(selected, r => r.MediaType == "Movie");
        Assert.Contains(selected, r => r.MediaType == "Series");
        // Available now (highest spotlight) survives for the cold-start user.
        Assert.Contains(selected, r => r.Category == CuratedRows.KeyAvailableNow);
    }

    [Fact]
    public void RailCap_IsRespected()
    {
        var rails = FullCandidateSet(); // 14 candidates
        var selected = HomeRailSelector.Select(rails, TasteProfile.Empty, 5, DisplayOrder);
        Assert.Equal(5, selected.Count);
    }

    [Fact]
    public void GenreAffinity_PromotesWatchedGenreOverUnwatched()
    {
        var rails = FullCandidateSet();
        // User strongly watches Action, never Romance; balanced type split.
        var profile = new TasteProfile(
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { ["action"] = 1.0 },
            0.5,
            0.5,
            40);

        // Tight cap forces genre competition: Action must make the cut, Romance must not.
        var selected = HomeRailSelector.Select(rails, profile, 6, DisplayOrder);
        Assert.Contains(selected, r => r.Category == CuratedRows.GenreKeyPrefix + "action");
        Assert.DoesNotContain(selected, r => r.Category == CuratedRows.GenreKeyPrefix + "romance");
    }

    [Fact]
    public void MovieDominantUser_StillKeepsAtLeastOneTvRail()
    {
        var rails = FullCandidateSet();
        // 85% movies, 15% TV — TV share hits the presence floor exactly.
        var profile = new TasteProfile(
            new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase),
            0.85,
            0.15,
            40);

        var selected = HomeRailSelector.Select(rails, profile, 6, DisplayOrder);
        Assert.Contains(selected, r => r.MediaType == "Movie");
        Assert.Contains(selected, r => r.MediaType == "Series");
        // Movie-dominant taste should still skew the majority to movies.
        Assert.True(selected.Count(r => r.MediaType == "Movie") >= selected.Count(r => r.MediaType == "Series"));
    }

    [Fact]
    public void DisplayOrder_GroupsCategoryVariants_MoviesBeforeTv()
    {
        var rails = FullCandidateSet();
        var selected = HomeRailSelector.Select(rails, TasteProfile.Empty, 14, DisplayOrder);

        // available_now is the first category; its Movie variant precedes its TV variant.
        var movieIdx = selected.ToList().FindIndex(r => r.Key == CuratedRows.KeyAvailableNow + "::movie");
        var tvIdx = selected.ToList().FindIndex(r => r.Key == CuratedRows.KeyAvailableNow + "::tv");
        Assert.True(movieIdx >= 0 && tvIdx >= 0);
        Assert.True(movieIdx < tvIdx);
    }

    [Fact]
    public void EmptyInput_YieldsEmpty()
    {
        var selected = HomeRailSelector.Select(Array.Empty<RailCandidate>(), TasteProfile.Empty, 10, DisplayOrder);
        Assert.Empty(selected);
    }
}
