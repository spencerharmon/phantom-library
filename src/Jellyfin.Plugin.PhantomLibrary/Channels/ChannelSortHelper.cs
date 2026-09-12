using System;
using System.Collections.Generic;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Model.Channels;

namespace Jellyfin.Plugin.PhantomLibrary.Channels;

/// <summary>
/// p10-relevance-sort: applies an explicit Jellyfin channel sort request on
/// top of the browse channels' default blended-relevance ordering
/// (<see cref="Jellyfin.Plugin.PhantomLibrary.State.PhantomDb.ListVisibleMovieRowsAsync(System.Threading.CancellationToken)"/>
/// / <see cref="Jellyfin.Plugin.PhantomLibrary.State.PhantomDb.ListVisibleSeriesRowsAsync(int,System.Threading.CancellationToken)"/>).
///
/// Jellyfin's native channel-browse sort control is constrained to the
/// fixed <see cref="ChannelItemSortField"/> enum (Name, CommunityRating,
/// PremiereDate, DateCreated, Runtime, PlayCount, CommunityPlayCount) — a
/// channel cannot add its own field labels, only choose which of these it
/// declares support for (<see cref="InternalChannelFeatures.DefaultSortFields"/>)
/// and honor when requested. The ROI's five named options map onto it as:
///
///   "Most available"     -> no explicit SortBy (the default order already
///                            ranks materialised/available first).
///   "Newest"              -> <see cref="ChannelItemSortField.PremiereDate"/>.
///   "Recently added"      -> <see cref="ChannelItemSortField.DateCreated"/>
///                            (mapped to <c>tmdb_metadata.fetched_at</c> —
///                            the plugin's own "added to catalogue" instant).
///   "Trending/Popular"    -> <see cref="ChannelItemSortField.CommunityRating"/>,
///                            a proxy: the plugin does not persist TMDB's
///                            actual `popularity` field (see the p10-
///                            relevance-sort change doc's Notes for why that
///                            capture is out of scope for this pass).
///   "Recently played"     -> NOT offered. Jellyfin only knows PlayCount /
///                            CommunityPlayCount from a real BaseItem's own
///                            persisted UserData, which does not exist until
///                            a phantom is materialised — this channel layer
///                            has no such signal to sort unmaterialised
///                            phantoms by, so requesting either field is a
///                            no-op (falls through to the default order).
/// </summary>
internal static class ChannelSortHelper
{
    /// <summary>
    /// The sort fields this plugin's channels can actually honor, for
    /// <see cref="InternalChannelFeatures.DefaultSortFields"/>.
    /// </summary>
    public static readonly IReadOnlyList<ChannelItemSortField> SupportedSortFields = new[]
    {
        ChannelItemSortField.PremiereDate,
        ChannelItemSortField.DateCreated,
        ChannelItemSortField.CommunityRating,
        ChannelItemSortField.Name,
    };

    /// <summary>
    /// Re-sorts <paramref name="items"/> in place per <paramref name="sortBy"/>
    /// / <paramref name="descending"/>. A null <paramref name="sortBy"/>, or
    /// one of the unsupported fields (<see cref="ChannelItemSortField.Runtime"/>,
    /// <see cref="ChannelItemSortField.PlayCount"/>,
    /// <see cref="ChannelItemSortField.CommunityPlayCount"/>), is a no-op —
    /// the list keeps its caller-supplied default blended order.
    /// </summary>
    public static void ApplyExplicitSort(List<ChannelItemInfo> items, ChannelItemSortField? sortBy, bool descending)
    {
        ArgumentNullException.ThrowIfNull(items);

        Comparison<ChannelItemInfo>? cmp = sortBy switch
        {
            ChannelItemSortField.Name => (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            ChannelItemSortField.CommunityRating => (a, b) => Nullable.Compare(a.CommunityRating, b.CommunityRating),
            ChannelItemSortField.PremiereDate => (a, b) => Nullable.Compare(a.PremiereDate, b.PremiereDate),
            ChannelItemSortField.DateCreated => (a, b) => Nullable.Compare(a.DateCreated, b.DateCreated),
            _ => null,
        };

        if (cmp is null)
        {
            return;
        }

        var effective = descending ? new Comparison<ChannelItemInfo>((a, b) => cmp(b, a)) : cmp;
        items.Sort(effective);
    }
}
