using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;

namespace Jellyfin.Plugin.PhantomLibrary.Channels;

/// <summary>
/// A leaf title's classification inputs, extracted once from the built
/// <see cref="ChannelItemInfo"/> so curated-row derivation needs no DB round-trip.
/// </summary>
/// <param name="Item">The already-built channel item (row member as-is).</param>
/// <param name="Available">
/// True iff the title is materialised / high-confidence (no <c>phantom</c> tag) —
/// the "Available now" signal.
/// </param>
/// <param name="DateCreated">Catalogue-entry time (fetched_at) — the recency signal.</param>
/// <param name="CommunityRating">TMDB vote average proxy for popularity.</param>
/// <param name="Genres">TMDB genres (may be empty).</param>
/// <param name="LeavingSoon">
/// True iff the title is materialised and eviction-pending — the "Leaving soon" signal.
/// </param>
/// <param name="Affinity">
/// Optional per-user affinity score (favourite/played adjacency); higher = more
/// relevant. Null when cheap personalisation is unavailable, in which case the
/// "Because you played…" row falls back to global popularity.
/// </param>
public readonly record struct RowCandidate(
    ChannelItemInfo Item,
    bool Available,
    DateTime? DateCreated,
    float? CommunityRating,
    IReadOnlyList<string> Genres,
    bool LeavingSoon,
    double? Affinity);

/// <summary>Tuning knobs for curated-row construction (from PluginConfiguration).</summary>
/// <param name="RowSize">Hard per-row cap (≥1).</param>
/// <param name="GenreRowMinItems">Minimum members for a genre to earn a row (≥1).</param>
/// <param name="NowUtc">Clock for recency/newness windows (injectable for tests).</param>
public readonly record struct CuratedRowConfig(int RowSize, int GenreRowMinItems, DateTime NowUtc);

/// <summary>
/// A materialised curated row: folder-facing metadata plus the (already
/// relevance-ordered, capped) member items to emit when the folder opens.
/// </summary>
public sealed class CuratedRow
{
    public required string Key { get; init; }

    public required string Title { get; init; }

    public required IReadOnlyList<ChannelItemInfo> Items { get; init; }
}

/// <summary>
/// p10-netflix-style-rows.
///
/// Turns the single flat, already-<b>pruned</b> (p10-prune-nonplayable-browse)
/// and <b>relevance-ordered</b> (p10-relevance-sort) browse list into multiple
/// curated rows (categories) — Available now, Popular on Phantom, New releases,
/// Trending this week, Because you played…, genre rows, Leaving soon — the way a
/// streaming Home screen presents its catalogue.
///
/// Design constraint (ROI item 3): each row is a <b>bounded, cheap</b> selection
/// respecting the O(recent)/O(row-size) rule that forced removal of
/// <c>ISupportsLatestMedia</c> — never an O(catalogue) enumeration on Home load.
/// This builder never touches the database: it categorises the <i>flat list the
/// channel already built</i> (itself the bounded browse-visible set, so pruning
/// is inherited by construction — an item absent from the flat list can appear in
/// no row), keeps each row's members in the flat list's default relevance order
/// (so per-row order also inherits p10-relevance-sort), and caps every row at
/// <see cref="CuratedRowConfig.RowSize"/>. Rows surface in Jellyfin as top-level
/// <see cref="ChannelItemType.Folder"/> category folders — the cheapest surface
/// that renders as browsable rows — each identified by a
/// <c>__row_&lt;channel&gt;_&lt;key&gt;__</c> FolderId the channel parses to
/// re-emit that row's members.
/// </summary>
public static class CuratedRows
{
    /// <summary>The FolderId prefix bracketing a curated-row sentinel.</summary>
    public const string RowIdPrefix = "__row_";

    /// <summary>The FolderId suffix bracketing a curated-row sentinel.</summary>
    public const string RowIdSuffix = "__";

    // Stable per-row keys (also the FolderId segment).
    public const string KeyAvailableNow = "available_now";
    public const string KeyPopular = "popular";
    public const string KeyNewReleases = "new_releases";
    public const string KeyTrending = "trending";
    public const string KeyForYou = "for_you";
    public const string KeyLeavingSoon = "leaving_soon";
    public const string GenreKeyPrefix = "genre_";

    /// <summary>Build the FolderId for a row within a channel scope ("movies"/"shows").</summary>
    public static string BuildRowFolderId(string channelScope, string rowKey)
        => RowIdPrefix + channelScope + "_" + rowKey + RowIdSuffix;

    /// <summary>
    /// True and yields <paramref name="rowKey"/> iff <paramref name="folderId"/>
    /// is a curated-row sentinel for <paramref name="channelScope"/>.
    /// </summary>
    public static bool TryParseRowFolderId(string? folderId, string channelScope, out string rowKey)
    {
        rowKey = string.Empty;
        if (string.IsNullOrEmpty(folderId))
        {
            return false;
        }

        var expectedPrefix = RowIdPrefix + channelScope + "_";
        if (!folderId.StartsWith(expectedPrefix, StringComparison.Ordinal)
            || !folderId.EndsWith(RowIdSuffix, StringComparison.Ordinal)
            || folderId.Length <= expectedPrefix.Length + RowIdSuffix.Length)
        {
            return false;
        }

        rowKey = folderId.Substring(
            expectedPrefix.Length,
            folderId.Length - expectedPrefix.Length - RowIdSuffix.Length);
        return rowKey.Length > 0;
    }

    /// <summary>
    /// Classify a built channel leaf/folder item into a <see cref="RowCandidate"/>.
    /// </summary>
    public static RowCandidate Classify(
        ChannelItemInfo item,
        bool leavingSoon = false,
        double? affinity = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        var available = item.Tags is null || !item.Tags.Contains("phantom");
        return new RowCandidate(
            item,
            available,
            item.DateCreated,
            item.CommunityRating,
            item.Genres ?? (IReadOnlyList<string>)Array.Empty<string>(),
            leavingSoon,
            affinity);
    }

    /// <summary>
    /// Derive the ordered set of curated rows from the flat candidate list. The
    /// input MUST already be in the channel's default (materialised/available-
    /// first, then relevance_score, then recency) order — every row preserves
    /// that relative order and simply selects + caps, so no row does its own
    /// O(catalogue) sort. Rows with no members are dropped; genre rows below the
    /// min-items threshold are folded away.
    /// </summary>
    public static IReadOnlyList<CuratedRow> Build(IReadOnlyList<RowCandidate> candidates, CuratedRowConfig config)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var rowSize = Math.Max(1, config.RowSize);
        var genreMin = Math.Max(1, config.GenreRowMinItems);
        var newReleaseCutoff = config.NowUtc.AddDays(-30);
        var rows = new List<CuratedRow>();

        // 1. Available now — materialised / high-confidence titles, in the
        //    incoming relevance order (materialised already sort first).
        var availableNow = candidates.Where(c => c.Available).Select(c => c.Item).Take(rowSize).ToList();
        AddRow(rows, KeyAvailableNow, "Available now", availableNow);

        // 2. Popular on Phantom — highest community rating (TMDB popularity
        //    proxy). Ties keep the incoming (relevance) order.
        var popular = OrderByScoreStable(candidates, c => c.CommunityRating ?? 0f)
            .Select(c => c.Item).Take(rowSize).ToList();
        AddRow(rows, KeyPopular, "Popular on Phantom", popular);

        // 3. New releases — most-recently catalogued titles (fetched_at DESC),
        //    the O(recent) recency signal.
        var newReleases = OrderByScoreStable(candidates, c => (double)(c.DateCreated ?? DateTime.MinValue).Ticks)
            .Select(c => c.Item).Take(rowSize).ToList();
        AddRow(rows, KeyNewReleases, "New releases", newReleases);

        // 4. Trending this week — a cheap trending proxy: entered within the
        //    last 30 days AND rates well. No O(catalogue) TMDB-trending scan.
        var trending = candidates
            .Where(c => (c.DateCreated ?? DateTime.MinValue) >= newReleaseCutoff)
            .OrderByDescending(c => c.CommunityRating ?? 0f)
            .Take(rowSize)
            .Select(c => c.Item)
            .ToList();
        AddRow(rows, KeyTrending, "Trending this week", trending);

        // 5. Because you played… / More like your favourites — per-user affinity
        //    when available, else a global-popularity fallback (ROI: "falling
        //    back to global popularity when cheap personalisation is
        //    unavailable").
        var affinityScored = candidates.Where(c => c.Affinity is > 0).ToList();
        List<ChannelItemInfo> forYou;
        string forYouTitle;
        if (affinityScored.Count > 0)
        {
            forYou = OrderByScoreStable(affinityScored, c => c.Affinity ?? 0d)
                .Select(c => c.Item).Take(rowSize).ToList();
            forYouTitle = "More like your favourites";
        }
        else
        {
            forYou = popular;
            forYouTitle = "Recommended for you";
        }

        AddRow(rows, KeyForYou, forYouTitle, forYou);

        // 6. Genre rows — one row per genre that clears the min-items bar, in
        //    descending member-count order. Each keeps incoming relevance order.
        var byGenre = new Dictionary<string, List<ChannelItemInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
        {
            foreach (var g in c.Genres)
            {
                if (string.IsNullOrWhiteSpace(g))
                {
                    continue;
                }

                if (!byGenre.TryGetValue(g, out var list))
                {
                    list = new List<ChannelItemInfo>();
                    byGenre[g] = list;
                }

                if (list.Count < rowSize)
                {
                    list.Add(c.Item);
                }
            }
        }

        foreach (var kvp in byGenre
            .Where(kvp => kvp.Value.Count >= genreMin)
            .OrderByDescending(kvp => kvp.Value.Count)
            .ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
        {
            AddRow(rows, GenreKeyPrefix + SlugifyGenre(kvp.Key), kvp.Key, kvp.Value);
        }

        // 7. Leaving soon — eviction-pending materialised titles.
        var leavingSoon = candidates.Where(c => c.LeavingSoon).Select(c => c.Item).Take(rowSize).ToList();
        AddRow(rows, KeyLeavingSoon, "Leaving soon", leavingSoon);

        return rows;
    }

    /// <summary>Look up a single row's members by key (folder-open path).</summary>
    public static CuratedRow? Find(IReadOnlyList<CuratedRow> rows, string rowKey)
        => rows.FirstOrDefault(r => string.Equals(r.Key, rowKey, StringComparison.Ordinal));

    /// <summary>
    /// Render the row set as top-level browsable folder items for a channel
    /// scope. Empty when there are no rows (caller then falls back to the flat
    /// list). Each folder's Id is the row's parseable sentinel FolderId.
    /// </summary>
    public static IReadOnlyList<ChannelItemInfo> ToFolderItems(
        IReadOnlyList<CuratedRow> rows,
        string channelScope)
    {
        var folders = new List<ChannelItemInfo>(rows.Count);
        foreach (var row in rows)
        {
            folders.Add(new ChannelItemInfo
            {
                Id = BuildRowFolderId(channelScope, row.Key),
                Name = row.Title,
                Type = ChannelItemType.Folder,
                FolderType = ChannelFolderType.Container,
            });
        }

        return folders;
    }

    private static void AddRow(List<CuratedRow> rows, string key, string title, List<ChannelItemInfo> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        rows.Add(new CuratedRow { Key = key, Title = title, Items = items });
    }

    /// <summary>
    /// Stable descending sort by <paramref name="score"/> that preserves the
    /// incoming (relevance) order for ties — so a row never re-derives an
    /// independent global ranking, it only re-prioritises the already-ordered
    /// bounded set.
    /// </summary>
    private static IEnumerable<RowCandidate> OrderByScoreStable(
        IReadOnlyList<RowCandidate> candidates,
        Func<RowCandidate, double> score)
        => candidates
            .Select((c, i) => (c, i))
            .OrderByDescending(t => score(t.c))
            .ThenBy(t => t.i)
            .Select(t => t.c);

    private static string SlugifyGenre(string genre)
    {
        var chars = genre
            .ToUpperInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '_')
            .ToArray();
        var slug = new string(chars).Trim('_');
        while (slug.Contains("__", StringComparison.Ordinal))
        {
            slug = slug.Replace("__", "_", StringComparison.Ordinal);
        }

        return slug.Length == 0
            ? Math.Abs(genre.GetHashCode(StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture)
            : slug;
    }
}
