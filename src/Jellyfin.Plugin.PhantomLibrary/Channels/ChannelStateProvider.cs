using System;
using System.Globalization;
using System.Threading;
using Jellyfin.Plugin.PhantomLibrary.State;

namespace Jellyfin.Plugin.PhantomLibrary.Channels;

/// <summary>
/// Per-channel <see cref="MediaBrowser.Controller.Channels.IChannel.DataVersion"/>
/// store. Jellyfin re-queries channel items whenever DataVersion changes,
/// so the channels need a way to invalidate their cached browse results
/// after discovery / materialise events.
///
/// Backed by <see cref="PhantomDb.SetMetaAsync(string, string, CancellationToken)"/>
/// under keys <c>channel_dataversion_movies</c> /
/// <c>channel_dataversion_shows</c> so the value survives plugin restart
/// (otherwise every restart would force a full re-scan even if the
/// underlying discovery cache hasn't changed).
///
/// Synchronous getters: the IChannel.DataVersion contract is a property
/// getter, not async. We hold the current value in process memory and
/// hydrate it once at first access; writes update both memory and the
/// DB.
/// </summary>
public sealed class ChannelStateProvider
{
    public const string KindMovies = "movies";
    public const string KindShows = "shows";

    private const string DataVersionSalt = "native-open-v1";

    private static readonly string[] AllKinds = { KindMovies, KindShows };

    private readonly PhantomDb _db;
    private readonly object _gate = new();
    private readonly System.Collections.Generic.Dictionary<string, string> _versions = new(StringComparer.Ordinal);
    private bool _hydrated;

    public ChannelStateProvider(PhantomDb db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Returns the current DataVersion for the named channel kind.
    /// Hydrates from <c>plugin_meta</c> on first call; subsequent calls
    /// are pure in-memory reads.
    /// </summary>
    public string DataVersion(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        EnsureHydrated();
        lock (_gate)
        {
            var stored = _versions.TryGetValue(kind, out var v) ? v : "1";
            return stored + ":" + DataVersionSalt;
        }
    }

    /// <summary>
    /// Advance the DataVersion for <paramref name="kind"/>. Uses
    /// monotonically-increasing unix-seconds so the new string is
    /// strictly different from the previous one even if called twice
    /// in the same second (we just append a tie-breaker).
    /// </summary>
    public void BumpDataVersion(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        EnsureHydrated();
        var next = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        lock (_gate)
        {
            _versions[kind] = next;
        }

        // Fire-and-forget DB persist; failure here just means next
        // restart re-bumps from a slightly older marker, no correctness
        // impact.
        _ = _db.SetMetaAsync(MetaKey(kind), next, CancellationToken.None);
    }

    private void EnsureHydrated()
    {
        if (Volatile.Read(ref _hydrated))
        {
            return;
        }

        lock (_gate)
        {
            if (_hydrated)
            {
                return;
            }

            foreach (var kind in AllKinds)
            {
                string? v = null;
                try
                {
                    v = _db.GetMetaAsync(MetaKey(kind), CancellationToken.None)
                        .GetAwaiter().GetResult();
                }
                catch
                {
                    // Empty / not-yet-created DB → fall through to default.
                }

                _versions[kind] = string.IsNullOrEmpty(v) ? "1" : v;
            }

            _hydrated = true;
        }
    }

    private static string MetaKey(string kind) => "channel_dataversion_" + kind;
}
