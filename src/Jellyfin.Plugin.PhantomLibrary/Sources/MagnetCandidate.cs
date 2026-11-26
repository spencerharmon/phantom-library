namespace Jellyfin.Plugin.PhantomLibrary.Sources;

/// <summary>Chosen magnet candidate; cache-independent value shared by the selector and repository.</summary>
public sealed record MagnetCandidate(string Magnet, string InfoHash, long Size, int Seeders, string Indexer)
{
    public string? Title { get; init; }
}
