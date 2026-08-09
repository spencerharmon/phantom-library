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

    private const string DataVersionSalt = "external-tv-metadata-v3";

    // Coalescing window: rapid successive availability transitions (the probe worker fires many
    // per minute across a multi-million-row catalogue) advance the published DataVersion at most
    // once per window instead of on every change. This bounds how often Jellyfin invalidates its
    // channel-item disk cache (keyed on DataVersion) -- on the PostgreSQL backend a cache miss
    // forces a full ~13k-query re-sync of the whole channel folder per page, so unbounded
    // invalidation is the difference between a sub-second cached browse and a 17-60s one. The window
    // must exceed the worst-case cold re-sync time (the Movies channel measured ~47s) or
    // back-to-back browses each land after the window and re-publish, never benefiting from the
    // cache. Trade-off: newly-changed content becomes visible up to this window later.
    private static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(60);

    private static readonly string[] AllKinds = { KindMovies, KindShows };

    private readonly PhantomDb _db;
    private readonly object _gate = new();
    private readonly System.Collections.Generic.Dictionary<string, string> _versions = new(StringComparer.Ordinal);
    private readonly System.Collections.Generic.Dictionary<string, DateTimeOffset> _lastPublishedAt = new(StringComparer.Ordinal);
    private readonly System.Collections.Generic.Dictionary<string, bool> _pendingBump = new(StringComparer.Ordinal);
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
            MaybePublishLocked(kind, DateTimeOffset.UtcNow);
            var stored = _versions.TryGetValue(kind, out var v) ? v : "1";
            return stored + ":" + DataVersionSalt;
        }
    }

    /// <summary>
    /// Advance the DataVersion for <paramref name="kind"/>. Uses
    /// monotonically-increasing unix-millis so the new string is
    /// strictly different from the previous one even if called twice
    /// in the same second.
    ///
    /// The advance is COALESCED: a bump only marks the kind dirty; the
    /// published version actually changes at most once per
    /// <see cref="CoalesceWindow"/> (see <see cref="MaybePublishLocked"/>).
    /// A read (<see cref="DataVersion"/>) also flushes a due pending bump,
    /// so the version still advances promptly on the next browse after the
    /// window elapses even if no further bump arrives.
    /// </summary>
    public void BumpDataVersion(string kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        EnsureHydrated();
        lock (_gate)
        {
            _pendingBump[kind] = true;
            MaybePublishLocked(kind, DateTimeOffset.UtcNow);
        }
    }

    /// <summary>
    /// Publishes a pending DataVersion advance for <paramref name="kind"/> only when at least
    /// <see cref="CoalesceWindow"/> has elapsed since the previous publish. Must be called while
    /// holding <see cref="_gate"/>. The first pending bump after process start publishes
    /// immediately (last-published defaults to <see cref="DateTimeOffset.MinValue"/>), then
    /// subsequent bumps within a window are coalesced into a single advance.
    /// </summary>
    private void MaybePublishLocked(string kind, DateTimeOffset now)
    {
        if (!_pendingBump.TryGetValue(kind, out var pending) || !pending)
        {
            return;
        }

        var last = _lastPublishedAt.TryGetValue(kind, out var t) ? t : DateTimeOffset.MinValue;
        if (now - last < CoalesceWindow)
        {
            return;
        }

        var next = now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
        _versions[kind] = next;
        _lastPublishedAt[kind] = now;
        _pendingBump[kind] = false;

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
