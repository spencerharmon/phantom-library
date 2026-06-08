using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Library;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// Test double: behaves as if Bootstrap always succeeded, returns
/// predictable synthetic paths, never touches the filesystem.
/// </summary>
internal sealed class NullPhantomStubManager : IPhantomStubManager
{
    public bool IsReady { get; set; } = false; // off by default to keep existing tests path-less
    public System.Collections.Generic.List<string> Deleted { get; } = new();
    public System.Collections.Generic.List<(string Title, int Tmdb, PhantomMediaKind Kind)> Created { get; } = new();

    public Task BootstrapAsync(CancellationToken ct)
    {
        IsReady = true;
        return Task.CompletedTask;
    }

    public Task<string> CreateAsync(string title, int tmdbId, PhantomMediaKind kind, CancellationToken ct)
    {
        Created.Add((title, tmdbId, kind));
        if (kind == PhantomMediaKind.Series)
        {
            // PLAN §M13: series stub is a per-series directory.
            var (seriesDir, _, _) = DeriveSeriesStubPaths(title, tmdbId);
            return Task.FromResult(seriesDir);
        }
        return Task.FromResult($"/tmp/phantom-test/movies/{DeriveFilename(title, tmdbId, kind)}");
    }

    public Task DeleteAsync(string symlinkPath, CancellationToken ct)
    {
        Deleted.Add(symlinkPath);
        return Task.CompletedTask;
    }

    public string DeriveFilename(string title, int tmdbId, PhantomMediaKind kind)
        => $"{(string.IsNullOrWhiteSpace(title) ? "untitled" : title)}__phantom_tmdb{tmdbId}.mp4";

    public (string SeriesDir, string SeasonDir, string EpisodeFile) DeriveSeriesStubPaths(string title, int tmdbId)
    {
        var safe = string.IsNullOrWhiteSpace(title) ? "untitled" : title;
        var stem = $"{safe}__phantom_tmdb{tmdbId}";
        var seriesDir = $"/tmp/phantom-test/shows/{stem}";
        var seasonDir = $"{seriesDir}/Season 01";
        var episodeFile = $"{seasonDir}/{stem} S01E01.mp4";
        return (seriesDir, seasonDir, episodeFile);
    }
}
