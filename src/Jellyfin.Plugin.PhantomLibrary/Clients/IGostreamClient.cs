using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.PhantomLibrary.Clients;

/// <summary>
/// Talks to the gostream <c>POST /api/library/add</c> + <c>/api/library/remove</c>
/// endpoints introduced by the phantom-library/api-add patch.
/// </summary>
public interface IGostreamClient
{
    /// <summary>Registers a torrent with gostream and returns the resulting FUSE/stub paths.</summary>
    Task<GostreamAddResult> AddAsync(GostreamAddRequest request, CancellationToken ct);

    /// <summary>Removes a previously registered stub. 404 is swallowed; other failures throw.</summary>
    Task RemoveAsync(string stubPath, CancellationToken ct);

    /// <summary>Probes endpoint reachability. Used for the legacy-fallback decision in PLAN.</summary>
    Task<bool> ProbeAsync(CancellationToken ct);

    /// <summary>
    /// Best-effort: asks the gostream Vault Mode endpoint to prestage the
    /// given stub at the supplied priority. Throws on 5xx / connection
    /// errors; callers wrap and log.
    /// </summary>
    Task PrestageAsync(string stubPath, int priority, CancellationToken ct);

    /// <summary>
    /// True if the gostream server exposes the Vault Mode prestage /
    /// prestage-status endpoints. Probes once and caches the answer for
    /// the process lifetime.
    /// </summary>
    Task<bool> IsVaultModePresentAsync(CancellationToken ct);

    /// <summary>
    /// Best-effort: clears the Vault Mode persistence marker for the given
    /// stub. 404 is swallowed (idempotent); 5xx throws.
    /// </summary>
    Task UnprestageAsync(string stubPath, CancellationToken ct);
}

public sealed record GostreamAddRequest
{
    public required string Type { get; init; }
    public string? Imdb { get; init; }
    public int? Tmdb { get; init; }
    public required string Title { get; init; }
    public int? Year { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public string? SeriesImdb { get; init; }
    public required string Magnet { get; init; }
    public string? MinQuality { get; init; }
}

public sealed record GostreamAddResult
{
    public required string StubPath { get; init; }
    public required string FusePath { get; init; }
    public required string Hash { get; init; }
    public required long Size { get; init; }
    public bool AlreadyExisted { get; init; }
}
