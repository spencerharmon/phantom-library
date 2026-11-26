using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using MediaBrowser.Controller.Channels;

namespace Jellyfin.Plugin.PhantomLibrary.Swarm;

/// <summary>How the BaseItem score sweep treats a persisted channel item (p05-baseitem-writeback).</summary>
public enum ScoredTitleKind
{
    /// <summary>Not a movie/series title (season, episode, unknown): never written.</summary>
    NotScored,

    /// <summary>A TMDB-backed movie/series title: scored from its fresh measurement.</summary>
    Measurable,

    /// <summary>A gostream-only title without a TMDB id: always the unmeasured score.</summary>
    Orphan,
}

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

    /// <summary>ExternalId prefix of a gostream-only (no TMDB match) series folder in the Shows channel.</summary>
    public const string OrphanSeriesPrefix = "orphanseries_";

    /// <summary>
    /// p05-baseitem-writeback: which persisted channel BaseItem carries a Peers score.
    /// Movie (<c>movie_&lt;tmdb&gt;</c>) and series (<c>series_&lt;tmdb&gt;</c>) titles are
    /// <see cref="ScoredTitleKind.Measurable"/> by TMDB id; orphan movies
    /// (<c>orphan_&lt;hash&gt;</c>) and orphan series folders (<c>orphanseries_&lt;hash&gt;</c>)
    /// have no TMDB id and therefore no candidate to measure — they are
    /// <see cref="ScoredTitleKind.Orphan"/> and always score <see cref="UnmeasuredScore"/>.
    /// Seasons, episodes and anything unparseable are <see cref="ScoredTitleKind.NotScored"/>
    /// and must be left untouched.
    /// </summary>
    public static (ScoredTitleKind Kind, int TmdbId) Classify(string? externalId)
    {
        if (string.IsNullOrEmpty(externalId))
        {
            return (ScoredTitleKind.NotScored, 0);
        }

        if (externalId.StartsWith(OrphanSeriesPrefix, StringComparison.Ordinal))
        {
            return (ScoredTitleKind.Orphan, 0);
        }

        if (!ChannelItemId.TryParse(externalId, out var id))
        {
            return (ScoredTitleKind.NotScored, 0);
        }

        return id.Kind switch
        {
            ChannelItemId.KindMovie or ChannelItemId.KindSeries when id.TmdbId is { } tmdb => (ScoredTitleKind.Measurable, tmdb),
            ChannelItemId.KindOrphan => (ScoredTitleKind.Orphan, 0),
            _ => (ScoredTitleKind.NotScored, 0),
        };
    }

    /// <summary>
    /// Target CriticRating for a persisted channel row, or null when the row is not a
    /// scored title (left untouched). <paramref name="peersByTmdb"/> holds the best fresh
    /// measurement per TMDB id (series already aggregated over their episodes).
    /// </summary>
    public static float? TargetFor(string? externalId, IReadOnlyDictionary<int, int> peersByTmdb)
    {
        ArgumentNullException.ThrowIfNull(peersByTmdb);
        var (kind, tmdb) = Classify(externalId);
        return kind switch
        {
            ScoredTitleKind.Measurable => ToCriticRating(peersByTmdb.TryGetValue(tmdb, out var p) ? p : null),
            ScoredTitleKind.Orphan => UnmeasuredScore,
            _ => null,
        };
    }

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
