namespace Jellyfin.Plugin.PhantomLibrary.State;

/// <summary>
/// Versioned, immutable source snapshot in the existing plugin_meta namespace.
/// Written atomically with materialised_state, never from recommendation refreshes.
/// Path/time witnesses make a legacy writer's replacement fail closed.
/// </summary>
public sealed record MaterialisedSource(
    string Magnet, string InfoHash, long Size, int Seeders, string Indexer, string? Title);

internal sealed record MaterialisedSourceBinding(
    int Version, string StubPath, string FusePath, long MaterialisedAt, MaterialisedSource Source);
