using System.Collections.Generic;
using System.Threading;

namespace Jellyfin.Plugin.PhantomLibrary.Swarm;

/// <summary>
/// In-memory snapshot of the freshest DHT peer count per TMDB id (movies, and
/// series aggregated from their episodes), refreshed by <c>SwarmHealthWorker</c>
/// each tick and read by <see cref="SwarmHealthMetadataProvider"/> on every
/// metadata refresh. Lock-free: readers see one complete snapshot or the next.
/// </summary>
public static class SwarmHealthCache
{
    private static Snapshot _current = new(false, new Dictionary<int, int>(), new Dictionary<int, int>());

    /// <summary>Gets a value indicating whether swarm health is enabled and a snapshot has been loaded.</summary>
    public static bool Enabled => Volatile.Read(ref _current).Enabled;

    /// <summary>Replaces the snapshot.</summary>
    public static void Set(bool enabled, IReadOnlyDictionary<int, int> movies, IReadOnlyDictionary<int, int> series)
        => Volatile.Write(ref _current, new Snapshot(enabled, movies, series));

    /// <summary>Measured peers for a movie (<paramref name="series"/> false) or series TMDB id; null when unmeasured.</summary>
    public static int? Peers(int tmdbId, bool series)
    {
        var snap = Volatile.Read(ref _current);
        var map = series ? snap.Series : snap.Movies;
        return map.TryGetValue(tmdbId, out var p) ? p : null;
    }

    private sealed record Snapshot(bool Enabled, IReadOnlyDictionary<int, int> Movies, IReadOnlyDictionary<int, int> Series);
}
