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

    /// <summary>Validates a candidate without writing a stub or materialised library entry.</summary>
    Task<GostreamValidateResult> ValidateAsync(GostreamValidateRequest request, CancellationToken ct);

    /// <summary>Releases/de-prioritises a validation lease for a losing candidate.</summary>
    Task ReleaseValidationAsync(GostreamValidationReleaseRequest request, CancellationToken ct);

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

    /// <summary>
    /// p14-player-stats-overlay: <c>GET /api/library/stats?hash=&lt;infohash&gt;</c>
    /// (gostream:phantom-player-stats-endpoint). Returns the live per-torrent
    /// stream statistics, or <c>null</c> when gostream answers 404
    /// <c>torrent_not_found</c> (hash not loaded in the engine). Authenticated
    /// with the server-side library token; the token never leaves the plugin.
    /// Throws <see cref="GostreamBadRequestException"/> on 4xx (auth/invalid
    /// hash) and <see cref="GostreamServerException"/> on 5xx.
    /// </summary>
    Task<GostreamTorrentStats?> GetTorrentStatsAsync(string infoHash, CancellationToken ct);
}

/// <summary>
/// gostream <c>/api/library/stats</c> response. Every measurement is nullable:
/// gostream reports <c>null</c> for "not yet computable", never a synthetic 0.
/// </summary>
public sealed record GostreamTorrentStats
{
    public required string Hash { get; init; }
    public int? Peers { get; init; }
    public int? Seeders { get; init; }
    public double? DownloadRateBytesPerSec { get; init; }
    public long? BytesCompleted { get; init; }
    public long? Size { get; init; }
    public long? ReadOffset { get; init; }
    public long? BufferedAheadBytes { get; init; }
    public double? BufferedAheadSeconds { get; init; }
    public string? StreamState { get; init; }
    public System.DateTimeOffset? LastReadAt { get; init; }
    public System.DateTimeOffset? TorrentSampledAt { get; init; }
    public System.DateTimeOffset? StreamSampledAt { get; init; }
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
    public string[]? AllowedVideoContainers { get; init; }
    public int? SelectedFileId { get; init; }
    public string? SelectedFilePath { get; init; }
    public string[]? RequiredAudioLanguages { get; init; }
    public string? PreferredAudioLanguage { get; init; }
    public string? ValidationSessionId { get; init; }
}

public sealed record GostreamAddResult
{
    public required string StubPath { get; init; }
    public required string FusePath { get; init; }
    public required string Hash { get; init; }
    public required long Size { get; init; }
    public bool AlreadyExisted { get; init; }
}

public sealed record GostreamValidateRequest
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
    public string[] RequiredAudioLanguages { get; init; } = System.Array.Empty<string>();
    public string? PreferredAudioLanguage { get; init; }
    public string[]? AllowedVideoContainers { get; init; }
    public required string ValidationSessionId { get; init; }
}

public sealed record GostreamSelectedFile
{
    public int? Id { get; init; }
    public string? Path { get; init; }
    public long? Size { get; init; }
    public string? Container { get; init; }
}

public sealed record GostreamAudioTrack
{
    public int StreamIndex { get; init; }
    public string? Language { get; init; }
    public string? Title { get; init; }
    public string? Codec { get; init; }
    public int? Channels { get; init; }
}

public sealed record GostreamValidateResult
{
    public required string Status { get; init; }
    public string? Reason { get; init; }
    public string? Hash { get; init; }
    public GostreamSelectedFile? SelectedFile { get; init; }
    public System.Collections.Generic.IReadOnlyList<GostreamAudioTrack> AudioTracks { get; init; } = System.Array.Empty<GostreamAudioTrack>();
    public int? SelectedAudioIndex { get; init; }
    public string? SelectedAudioLanguage { get; init; }
    public string? ValidationSessionId { get; init; }
    public System.DateTimeOffset? ValidationLeaseExpiresAt { get; init; }
}

public sealed record GostreamValidationReleaseRequest
{
    public required string ValidationSessionId { get; init; }
    public string? Hash { get; init; }
}
