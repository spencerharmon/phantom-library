using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Jellyfin.Plugin.PhantomLibrary.Swarm;

/// <summary>
/// In-memory, per-title memo of the freshest DHT peer count the swarm-health code
/// last looked up (movies by TMDB id; series by TMDB id, aggregated as the best
/// episode), read by <see cref="SwarmHealthMetadataProvider"/> on every metadata
/// refresh so a replace-all refresh does not cost a database probe per item.
///
/// p05-baseitem-writeback: the memo is filled INCREMENTALLY from lookups the worker
/// already performs — each bounded keyset sweep batch and each measurement
/// write-through — never by a per-tick aggregate over the whole <c>swarm_health</c>
/// table. An entry records the lookup outcome (including "no fresh measurement")
/// and the time it was taken; readers treat an entry older than their staleness
/// bound as a miss and fall back to a bounded single-title lookup.
/// Memory is bounded by the number of Phantom titles.
/// </summary>
public static class SwarmHealthCache
{
    private static readonly ConcurrentDictionary<(bool Series, int TmdbId), Entry> Entries = new();

    /// <summary>Number of memoised titles (tests / diagnostics).</summary>
    public static int Count => Entries.Count;

    /// <summary>
    /// Records the outcome of one fresh-window lookup for <paramref name="tmdbIds"/>:
    /// ids present in <paramref name="peersByTmdb"/> are measured, the others have no
    /// fresh measurement (unmeasured).
    /// </summary>
    public static void Record(bool series, IEnumerable<int> tmdbIds, IReadOnlyDictionary<int, int> peersByTmdb, DateTimeOffset lookedUpAt)
    {
        ArgumentNullException.ThrowIfNull(tmdbIds);
        ArgumentNullException.ThrowIfNull(peersByTmdb);
        foreach (var tmdb in tmdbIds)
        {
            Entries[(series, tmdb)] = new Entry(peersByTmdb.TryGetValue(tmdb, out var p) ? p : null, lookedUpAt);
        }
    }

    /// <summary>
    /// The memoised lookup for a title when it was taken at or after
    /// <paramref name="notBefore"/>; false when absent or older (caller must look it up).
    /// <paramref name="peers"/> is null for a title known to have no fresh measurement.
    /// </summary>
    public static bool TryGet(bool series, int tmdbId, DateTimeOffset notBefore, out int? peers)
    {
        if (Entries.TryGetValue((series, tmdbId), out var e) && e.LookedUpAt >= notBefore)
        {
            peers = e.Peers;
            return true;
        }

        peers = null;
        return false;
    }

    /// <summary>Drops every memoised entry (swarm health disabled, or tests).</summary>
    public static void Clear() => Entries.Clear();

    private readonly record struct Entry(int? Peers, DateTimeOffset LookedUpAt);
}
