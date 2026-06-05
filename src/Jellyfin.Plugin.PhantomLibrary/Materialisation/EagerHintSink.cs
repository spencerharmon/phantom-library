using System;
using System.Collections.Concurrent;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Reason hint attached by SuggestionsContributor when it creates a
/// Virtual item, so EagerResolver can pick the right queue lane.
/// </summary>
public enum EagerHint
{
    /// <summary>Unknown — default eager lane.</summary>
    None = 0,

    /// <summary>From TMDB Trending refresh — baseline eager lane.</summary>
    Trending,

    /// <summary>Surfaced as "Similar to X" where X is a user favourite — user lane.</summary>
    SimilarToFavourite,

    /// <summary>From a per-user recommendation fan-out — user lane.</summary>
    UserRecommendation,
}

/// <summary>
/// Bridge between item producers (SuggestionsContributor) and EagerResolver.
/// Producers register a hint keyed by the Jellyfin item id BEFORE the item
/// is inserted via ILibraryManager.CreateItem; EagerResolver consumes the
/// hint inside its ItemAdded handler.
/// </summary>
public interface IEagerHintSink
{
    /// <summary>Register a hint for the given Jellyfin item id.</summary>
    void RegisterHint(Guid jellyfinItemId, EagerHint hint);

    /// <summary>Consume (and remove) a hint, returning <see cref="EagerHint.None"/> if absent.</summary>
    EagerHint ConsumeHint(Guid jellyfinItemId);
}

/// <summary>Process-wide in-memory hint registry.</summary>
public sealed class EagerHintSink : IEagerHintSink
{
    private readonly ConcurrentDictionary<Guid, EagerHint> _hints = new();

    public void RegisterHint(Guid jellyfinItemId, EagerHint hint)
    {
        if (jellyfinItemId == Guid.Empty || hint == EagerHint.None) return;
        _hints[jellyfinItemId] = hint;
    }

    public EagerHint ConsumeHint(Guid jellyfinItemId)
    {
        return _hints.TryRemove(jellyfinItemId, out var h) ? h : EagerHint.None;
    }
}
