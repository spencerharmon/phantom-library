using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using MediaBrowser.Controller.Channels;

namespace Jellyfin.Plugin.PhantomLibrary.Swarm;

/// <summary>
/// Pure swarm-health ranking rules (ROI Priority 0.5).
///
/// The Jellyfin web list view sorts the persisted channel BaseItems itself and
/// offers no plugin-defined sort field, so the measured swarm is carried in the
/// BaseItem's <c>CriticRating</c> (unused for phantoms: TMDB only supplies a
/// community rating) and the plugin's web script relabels that sort option
/// "Peers (DHT)". Encoding, chosen so a DESCENDING sort (Postgres sorts NULLs
/// first on DESC) reads: live swarms by size, then unmeasured, then dead:
///   measured, &gt;=1 peer   -&gt; min(peers, 100)
///   unmeasured             -&gt; <see cref="UnmeasuredScore"/> (0.5)
///   measured, 0 peers      -&gt; 0
/// </summary>
public static class SwarmHealthScore
{
    /// <summary>CriticRating written for a phantom with no fresh measurement.</summary>
    public const float UnmeasuredScore = 0.5f;

    /// <summary>Cap: the CriticRating scale is 0..100.</summary>
    public const int MaxScore = 100;

    /// <summary>Score for a fresh measurement (null = unmeasured).</summary>
    public static float ToCriticRating(int? dhtPeers)
        => dhtPeers switch
        {
            null => UnmeasuredScore,
            <= 0 => 0f,
            _ => Math.Min(dhtPeers.Value, MaxScore),
        };

    /// <summary>True when the stored rating already equals the target (float-tolerant).</summary>
    public static bool SameRating(float? current, float target)
        => current is { } c && Math.Abs(c - target) < 0.01f;

    /// <summary>
    /// "Healthiest swarms" shelf: the given items (already pruned/visible) that
    /// have a live measured swarm, largest first, capped at <paramref name="rowSize"/>.
    /// Ties keep the input (relevance) order.
    /// </summary>
    public static IReadOnlyList<ChannelItemInfo> HealthiestRow(
        IReadOnlyList<ChannelItemInfo> items,
        IReadOnlyDictionary<int, int> peersByTmdb,
        int rowSize)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(peersByTmdb);
        return items
            .Select((item, index) => (item, index, peers: PeersFor(item, peersByTmdb)))
            .Where(x => x.peers > 0)
            .OrderByDescending(x => x.peers)
            .ThenBy(x => x.index)
            .Take(Math.Max(1, rowSize))
            .Select(x => x.item)
            .ToList();
    }

    /// <summary>Measured peers for a movie/series channel item, or -1 when unmeasured/not a phantom title.</summary>
    public static int PeersFor(ChannelItemInfo item, IReadOnlyDictionary<int, int> peersByTmdb)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(peersByTmdb);
        if (!ChannelItemId.TryParse(item.Id, out var id)
            || id.TmdbId is not { } tmdb
            || (id.Kind != ChannelItemId.KindMovie && id.Kind != ChannelItemId.KindSeries))
        {
            return -1;
        }

        return peersByTmdb.TryGetValue(tmdb, out var peers) ? peers : -1;
    }
}
