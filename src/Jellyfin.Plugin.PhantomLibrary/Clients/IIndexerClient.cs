using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.PhantomLibrary.Clients;

/// <summary>Indexer source (Prowlarr, Torrentio, ...).</summary>
public interface IIndexerClient
{
    string Name { get; }
    bool IsEnabled { get; }
    Task<IReadOnlyList<IndexerCandidate>> SearchAsync(IndexerQuery query, CancellationToken ct);
}

public sealed record IndexerQuery
{
    public required string Type { get; init; } // "movie" | "episode"
    public string? Imdb { get; init; }
    public int? Tmdb { get; init; }
    public string? Title { get; init; }
    public int? Year { get; init; }
    public int? Season { get; init; }
    public int? Episode { get; init; }
    /// <summary>For episode queries: the parent Series' IMDB id, distinct from the per-episode IMDB.</summary>
    public string? SeriesImdb { get; init; }
}

public sealed record IndexerCandidate
{
    public required string Title { get; init; }
    public required string Magnet { get; init; }
    public required string InfoHash { get; init; }
    public required long Size { get; init; }
    public required int Seeders { get; init; }
    public int Leechers { get; init; }
    public string? Source { get; init; }
    public string? IndexerName { get; init; }
}

/// <summary>Authentication failure (401/403) from an indexer.</summary>
public sealed class IndexerAuthException : Exception
{
    public IndexerAuthException(string message) : base(message) { }
    public IndexerAuthException() { }
    public IndexerAuthException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Transient or indeterminate indexer failure; callers must not treat it as a definitive empty result.</summary>
public sealed class IndexerTransientException : Exception
{
    public IndexerTransientException() { }
    public IndexerTransientException(string message) : base(message) { }
    public IndexerTransientException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// This indexer cannot serve the given query as-is (an abstention) — e.g. Torrentio
/// queried without an IMDB id. This is NOT a failure and NOT transient: it must not
/// be retried as though it might succeed later with the same query, and it must not
/// be counted as an error when deciding a probe outcome.
/// </summary>
public sealed class IndexerNotApplicableException : Exception
{
    public IndexerNotApplicableException() { }
    public IndexerNotApplicableException(string message) : base(message) { }
    public IndexerNotApplicableException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Helpers for building magnet URIs and parsing info-hashes.</summary>
public static class MagnetUtils
{
    /// <summary>Default tracker list mirroring gostream's DefaultTrackers().</summary>
    public static readonly IReadOnlyList<string> DefaultTrackers = new[]
    {
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://tracker.openbittorrent.com:6969/announce",
    };

    /// <summary>Build a magnet URI from an info-hash, display name, and tracker list.</summary>
    public static string BuildMagnet(string infoHash, string? displayName, IReadOnlyList<string>? trackers = null)
    {
        if (string.IsNullOrWhiteSpace(infoHash))
        {
            throw new ArgumentException("infoHash required", nameof(infoHash));
        }

        var sb = new System.Text.StringBuilder();
        sb.Append("magnet:?xt=urn:btih:").Append(infoHash);
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            sb.Append("&dn=").Append(Uri.EscapeDataString(displayName));
        }

        foreach (var tr in trackers ?? DefaultTrackers)
        {
            sb.Append("&tr=").Append(Uri.EscapeDataString(tr));
        }

        return sb.ToString();
    }

    /// <summary>Extract the info-hash from a magnet URI's xt=urn:btih:HASH segment; null if absent.</summary>
    public static string? ExtractInfoHash(string? magnet)
    {
        if (string.IsNullOrWhiteSpace(magnet))
        {
            return null;
        }

        const string marker = "urn:btih:";
        var idx = magnet.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0)
        {
            return null;
        }

        var start = idx + marker.Length;
        var end = magnet.IndexOf('&', start);
        var hash = end < 0 ? magnet[start..] : magnet[start..end];
        hash = hash.Trim();
        return string.IsNullOrWhiteSpace(hash) ? null : hash;
    }
}
