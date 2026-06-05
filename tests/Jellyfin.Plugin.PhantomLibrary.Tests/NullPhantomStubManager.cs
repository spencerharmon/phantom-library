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
        return Task.FromResult($"/tmp/phantom-test/{(kind == PhantomMediaKind.Movie ? "movies" : "shows")}/{DeriveFilename(title, tmdbId, kind)}");
    }

    public Task DeleteAsync(string symlinkPath, CancellationToken ct)
    {
        Deleted.Add(symlinkPath);
        return Task.CompletedTask;
    }

    public string DeriveFilename(string title, int tmdbId, PhantomMediaKind kind)
        => $"{(string.IsNullOrWhiteSpace(title) ? "untitled" : title)}__phantom_tmdb{tmdbId}.mp4";
}
