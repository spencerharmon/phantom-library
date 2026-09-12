using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// p10-netflix-style-rows regression coverage for the pure row-categorisation
/// logic (<see cref="CuratedRows"/>). These are the FAILS-without / PASSES-with
/// tests for the feature's core: a flat, pruned, relevance-ordered browse list
/// becomes multiple bounded curated rows, and each row's FolderId round-trips.
/// </summary>
public class CuratedRowsTests
{
    private static readonly DateTime Now = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    private static ChannelItemInfo Leaf(
        string id,
        bool phantom = false,
        double? rating = null,
        DateTime? dateCreated = null,
        params string[] genres)
    {
        var item = new ChannelItemInfo
        {
            Id = id,
            Name = id,
            Type = ChannelItemType.Media,
            ContentType = ChannelMediaContentType.Movie,
            MediaType = ChannelMediaType.Video,
            DateCreated = dateCreated,
            CommunityRating = rating is { } r ? (float)r : null,
            Tags = phantom ? new List<string> { "phantom" } : new List<string>(),
        };
        if (genres.Length > 0)
        {
            item.Genres = genres.ToList();
        }

        return item;
    }

    private static CuratedRowConfig Config(int rowSize = 40, int genreMin = 3)
        => new(rowSize, genreMin, Now);

    private static IReadOnlyList<RowCandidate> Classify(IEnumerable<ChannelItemInfo> items)
        => items.Select(i => CuratedRows.Classify(i)).ToList();

    [Fact]
    public void FolderId_RoundTrips_ForScope()
    {
        var id = CuratedRows.BuildRowFolderId("movies", CuratedRows.KeyPopular);
        Assert.True(CuratedRows.TryParseRowFolderId(id, "movies", out var key));
        Assert.Equal(CuratedRows.KeyPopular, key);

        // Wrong scope must not match.
        Assert.False(CuratedRows.TryParseRowFolderId(id, "shows", out _));
        // A normal channel item id is not a row folder.
        Assert.False(CuratedRows.TryParseRowFolderId("movie_42", "movies", out _));
        Assert.False(CuratedRows.TryParseRowFolderId(null, "movies", out _));
    }

    [Fact]
    public void Build_EmptyInput_YieldsNoRows()
    {
        var rows = CuratedRows.Build(Array.Empty<RowCandidate>(), Config());
        Assert.Empty(rows);
    }

    [Fact]
    public void AvailableNow_ContainsOnlyHighConfidenceTitles()
    {
        var candidates = Classify(new[]
        {
            Leaf("movie_1"),                 // available (materialised/high-confidence)
            Leaf("movie_2", phantom: true),  // phantom → not available
            Leaf("movie_3"),                 // available
        });

        var rows = CuratedRows.Build(candidates, Config());
        var available = CuratedRows.Find(rows, CuratedRows.KeyAvailableNow);

        Assert.NotNull(available);
        Assert.Equal(new[] { "movie_1", "movie_3" }, available!.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void Popular_OrdersByCommunityRating_TieKeepsIncomingOrder()
    {
        var candidates = Classify(new[]
        {
            Leaf("movie_a", rating: 5.0), // incoming index 0
            Leaf("movie_b", rating: 9.0), // highest
            Leaf("movie_c", rating: 5.0), // tie with a → keeps after a
        });

        var rows = CuratedRows.Build(candidates, Config());
        var popular = CuratedRows.Find(rows, CuratedRows.KeyPopular);

        Assert.NotNull(popular);
        Assert.Equal(new[] { "movie_b", "movie_a", "movie_c" }, popular!.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void NewReleases_OrdersByDateCreatedDescending()
    {
        var candidates = Classify(new[]
        {
            Leaf("movie_old", dateCreated: Now.AddDays(-100)),
            Leaf("movie_new", dateCreated: Now.AddDays(-1)),
            Leaf("movie_mid", dateCreated: Now.AddDays(-10)),
        });

        var rows = CuratedRows.Build(candidates, Config());
        var newReleases = CuratedRows.Find(rows, CuratedRows.KeyNewReleases);

        Assert.NotNull(newReleases);
        Assert.Equal(new[] { "movie_new", "movie_mid", "movie_old" }, newReleases!.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void Trending_OnlyRecentTitles_RankedByRating()
    {
        var candidates = Classify(new[]
        {
            Leaf("recent_hi", rating: 8.0, dateCreated: Now.AddDays(-5)),
            Leaf("recent_lo", rating: 3.0, dateCreated: Now.AddDays(-5)),
            Leaf("old_hi", rating: 9.9, dateCreated: Now.AddDays(-90)), // too old to trend
        });

        var rows = CuratedRows.Build(candidates, Config());
        var trending = CuratedRows.Find(rows, CuratedRows.KeyTrending);

        Assert.NotNull(trending);
        Assert.Equal(new[] { "recent_hi", "recent_lo" }, trending!.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void GenreRows_OnlyWhenAboveMinItems_LargestFirst()
    {
        var candidates = Classify(new[]
        {
            Leaf("a", genres: "Action"),
            Leaf("b", genres: "Action"),
            Leaf("c", genres: "Action"),   // Action now has 3 (>= min 3)
            Leaf("d", genres: "Comedy"),
            Leaf("e", genres: "Comedy"),   // Comedy has 2 (< min 3) → folded away
        });

        var rows = CuratedRows.Build(candidates, Config(genreMin: 3));
        var genreRows = rows.Where(r => r.Key.StartsWith(CuratedRows.GenreKeyPrefix, StringComparison.Ordinal)).ToList();

        Assert.Single(genreRows);
        Assert.Equal("Action", genreRows[0].Title);
        Assert.Equal(CuratedRows.GenreKeyPrefix + "action", genreRows[0].Key);
    }

    [Fact]
    public void ForYou_FallsBackToPopularity_WhenNoAffinity()
    {
        var candidates = Classify(new[]
        {
            Leaf("low", rating: 2.0),
            Leaf("high", rating: 9.0),
        });

        var rows = CuratedRows.Build(candidates, Config());
        var forYou = CuratedRows.Find(rows, CuratedRows.KeyForYou);

        Assert.NotNull(forYou);
        // Fallback = global popularity order (high rated first).
        Assert.Equal("high", forYou!.Items[0].Id);
    }

    [Fact]
    public void ForYou_UsesAffinity_WhenAvailable()
    {
        var lowPopHighAffinity = CuratedRows.Classify(Leaf("pick", rating: 1.0), affinity: 5.0);
        var highPopNoAffinity = CuratedRows.Classify(Leaf("skip", rating: 9.0));
        var rows = CuratedRows.Build(new[] { highPopNoAffinity, lowPopHighAffinity }, Config());

        var forYou = CuratedRows.Find(rows, CuratedRows.KeyForYou);
        Assert.NotNull(forYou);
        // Affinity present → affinity wins over popularity, and only affinity-
        // scored items populate the row.
        Assert.Equal(new[] { "pick" }, forYou!.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void LeavingSoon_ContainsEvictionPendingTitles()
    {
        var normal = CuratedRows.Classify(Leaf("keep"));
        var leaving = CuratedRows.Classify(Leaf("go"), leavingSoon: true);
        var rows = CuratedRows.Build(new[] { normal, leaving }, Config());

        var leavingRow = CuratedRows.Find(rows, CuratedRows.KeyLeavingSoon);
        Assert.NotNull(leavingRow);
        Assert.Equal(new[] { "go" }, leavingRow!.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void RowSize_CapsEveryRow_BoundedByConfig()
    {
        var many = Enumerable.Range(0, 50)
            .Select(i => Leaf("m" + i.ToString(System.Globalization.CultureInfo.InvariantCulture), rating: i, dateCreated: Now.AddDays(-i)))
            .ToList();

        var rows = CuratedRows.Build(Classify(many), Config(rowSize: 10));

        foreach (var row in rows)
        {
            Assert.True(row.Items.Count <= 10, $"row {row.Key} exceeded the size cap: {row.Items.Count}");
        }
    }

    [Fact]
    public void ToFolderItems_AreBrowsableFolders_WithParseableIds()
    {
        var candidates = Classify(new[] { Leaf("movie_1", rating: 5.0) });
        var rows = CuratedRows.Build(candidates, Config());
        var folders = CuratedRows.ToFolderItems(rows, "movies");

        Assert.NotEmpty(folders);
        foreach (var folder in folders)
        {
            Assert.Equal(ChannelItemType.Folder, folder.Type);
            Assert.Equal(ChannelFolderType.Container, folder.FolderType);
            Assert.True(CuratedRows.TryParseRowFolderId(folder.Id, "movies", out _));
        }
    }
}
