using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Channels;

/// <summary>One entry in the gostream virtual filesystem walk.</summary>
/// <param name="Path">Absolute path on the gostream FUSE mount.</param>
/// <param name="TmdbId">TMDB id if the file was identified, else null.</param>
public sealed record GostreamFileEntry(string Path, int? TmdbId);

/// <summary>One series-shaped directory in the gostream tv tree.</summary>
public sealed record GostreamSeriesEntry(
    string DirectoryPath,
    int? TmdbId,
    IReadOnlyList<GostreamSeasonEntry> Seasons);

/// <summary>One season subdirectory of a <see cref="GostreamSeriesEntry"/>.</summary>
public sealed record GostreamSeasonEntry(
    int SeasonNumber,
    IReadOnlyList<GostreamFileEntry> Episodes);

/// <summary>
/// Walks the gostream FUSE-mounted virtual MKV tree to enumerate
/// "orphan" files — items that exist on the gostream side but that the
/// plugin did not put there (and therefore are not surfaced by phantom
/// discovery / materialised_state). The movies channel emits these as
/// raw-filename entries so the operator can still see and play them.
///
/// Authoritative source for the (tmdb, fuse_path) mapping is the
/// plugin's own <c>materialised_state</c> table. Anything else found on
/// the FUSE mount is treated as an orphan.
///
/// Per plan §3.2 / critic IMPORTANT 8: do NOT cross-check with gostream
/// to derive tmdb ids; that races a foreign source-of-truth. The plugin
/// owns the materialised_state mapping.
/// </summary>
public sealed class GostreamFilesystemEnumerator
{
    private static readonly string[] VideoExtensions = { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".webm" };

    private readonly PhantomDb _db;
    private readonly ILogger<GostreamFilesystemEnumerator> _logger;

    public GostreamFilesystemEnumerator(PhantomDb db, ILogger<GostreamFilesystemEnumerator> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    private string MoviesRoot => MoviesRootOverride ?? Plugin.Instance?.Configuration.GostreamMoviesRoot ?? "/var/gostream/gostream-mkv-virtual/movies";

    private string ShowsRoot => ShowsRootOverride ?? Plugin.Instance?.Configuration.GostreamShowsRoot ?? "/var/gostream/gostream-mkv-virtual/tv";

    /// <summary>Test-only override for the movies root path.</summary>
    internal string? MoviesRootOverride { get; set; }

    /// <summary>Test-only override for the shows root path.</summary>
    internal string? ShowsRootOverride { get; set; }

    /// <summary>
    /// Enumerate orphan movie files. A file is "orphan" iff its path is
    /// neither in <paramref name="knownTmdbs"/>'s materialised-state
    /// fuse_path set nor matches a discovery-tracked id (the caller may
    /// pass <paramref name="knownTmdbs"/> empty if it only wants the FS
    /// snapshot; orphan-vs-known classification then collapses to "any
    /// file the materialised_state table also knows about is excluded").
    /// </summary>
    /// <param name="knownTmdbs">TMDB ids the caller has already seen from
    /// other sources (discovery_cache). Currently unused at this layer
    /// because tmdb→fuse_path requires materialised_state, which we
    /// consult directly. Reserved for future caller-driven dedup.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<GostreamFileEntry>> EnumerateOrphanMoviesAsync(
        IReadOnlySet<int> knownTmdbs,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(knownTmdbs);
        var root = MoviesRoot;
        if (!Directory.Exists(root))
        {
            _logger.LogDebug("Gostream movies root {Root} does not exist; no orphans", root);
            return Array.Empty<GostreamFileEntry>();
        }

        // Build the "this is a materialised file, not an orphan" exclusion set.
        var materialisedFusePaths = (await _db.ListMaterialisedStateAsync("movie", ct).ConfigureAwait(false))
            .Select(r => GostreamPathResolver.ResolvePath(r.FusePath, root))
            .ToHashSet(StringComparer.Ordinal);

        var results = new List<GostreamFileEntry>();
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate {Root}", root);
            return Array.Empty<GostreamFileEntry>();
        }

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsVideoFile(path))
            {
                continue;
            }

            if (materialisedFusePaths.Contains(path))
            {
                continue;
            }

            // No reliable cheap way to extract tmdb id from gostream paths
            // without a side-channel; leave TmdbId null and let downstream
            // dedup (if any) decide. _ = knownTmdbs reserved for future use.
            _ = knownTmdbs;
            results.Add(new GostreamFileEntry(path, null));
        }

        return results;
    }

    /// <summary>
    /// Enumerate series-shaped directories under the gostream tv root.
    /// Layout assumed:
    /// <code>
    ///   &lt;ShowsRoot&gt;/&lt;series_dir&gt;/Season &lt;N&gt;/&lt;episode_file&gt;.mkv
    /// </code>
    /// Returned <see cref="GostreamSeriesEntry.TmdbId"/> is null because
    /// the tmdb id is not derivable from the path alone. Stage 5.1
    /// extends this to consult materialised_state for series-id resolution.
    /// </summary>
    public Task<IReadOnlyList<GostreamSeriesEntry>> EnumerateSeriesAsync(CancellationToken ct)
    {
        var root = ShowsRoot;
        if (!Directory.Exists(root))
        {
            _logger.LogDebug("Gostream shows root {Root} does not exist; no series", root);
            return Task.FromResult<IReadOnlyList<GostreamSeriesEntry>>(Array.Empty<GostreamSeriesEntry>());
        }

        var series = new List<GostreamSeriesEntry>();
        IEnumerable<string> seriesDirs;
        try
        {
            seriesDirs = Directory.EnumerateDirectories(root);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate series under {Root}", root);
            return Task.FromResult<IReadOnlyList<GostreamSeriesEntry>>(Array.Empty<GostreamSeriesEntry>());
        }

        foreach (var seriesDir in seriesDirs)
        {
            ct.ThrowIfCancellationRequested();
            var seasons = new List<GostreamSeasonEntry>();
            IEnumerable<string> seasonDirs;
            try
            {
                seasonDirs = Directory.EnumerateDirectories(seriesDir);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not enumerate seasons under {Dir}", seriesDir);
                continue;
            }

            foreach (var seasonDir in seasonDirs)
            {
                if (!TryParseSeasonNumber(Path.GetFileName(seasonDir), out var seasonNumber))
                {
                    continue;
                }

                var episodes = new List<GostreamFileEntry>();
                IEnumerable<string> epFiles;
                try
                {
                    epFiles = Directory.EnumerateFiles(seasonDir);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not enumerate episodes under {Dir}", seasonDir);
                    continue;
                }

                foreach (var ep in epFiles)
                {
                    if (!IsVideoFile(ep))
                    {
                        continue;
                    }

                    episodes.Add(new GostreamFileEntry(ep, null));
                }

                if (episodes.Count > 0)
                {
                    seasons.Add(new GostreamSeasonEntry(seasonNumber, episodes));
                }
            }

            series.Add(new GostreamSeriesEntry(seriesDir, null, seasons));
        }

        return Task.FromResult<IReadOnlyList<GostreamSeriesEntry>>(series);
    }

    /// <summary>
    /// Walk movies dir and return the first file whose
    /// <see cref="ChannelItemId.ForOrphanPath(string)"/> hash matches
    /// <paramref name="hash"/>, or null if none.
    /// </summary>
    public async Task<GostreamFileEntry?> LookupOrphanByHashAsync(string hash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        var root = MoviesRoot;
        if (!Directory.Exists(root))
        {
            return null;
        }

        // Build the same exclusion set as the orphan enumeration so we
        // don't return a path that's actually a materialised item.
        var materialisedFusePaths = (await _db.ListMaterialisedStateAsync("movie", ct).ConfigureAwait(false))
            .Select(r => GostreamPathResolver.ResolvePath(r.FusePath, root))
            .ToHashSet(StringComparer.Ordinal);

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
        }
        catch
        {
            return null;
        }

        foreach (var path in files)
        {
            ct.ThrowIfCancellationRequested();
            if (!IsVideoFile(path) || materialisedFusePaths.Contains(path))
            {
                continue;
            }

            var id = ChannelItemId.ForOrphanPath(path);
            if (string.Equals(id.OrphanHash, hash, StringComparison.Ordinal))
            {
                return new GostreamFileEntry(path, null);
            }
        }

        return null;
    }

    private static bool IsVideoFile(string path)
    {
        var ext = Path.GetExtension(path);
        if (string.IsNullOrEmpty(ext))
        {
            return false;
        }

        foreach (var v in VideoExtensions)
        {
            if (string.Equals(ext, v, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseSeasonNumber(string dirName, out int seasonNumber)
    {
        seasonNumber = 0;
        if (string.IsNullOrWhiteSpace(dirName))
        {
            return false;
        }

        // Common forms: "Season 1", "Season 01", "Season1", "S01", "season 1".
        var s = dirName.Trim();
        if (s.StartsWith("Season ", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(s.AsSpan("Season ".Length).Trim(), out seasonNumber);
        }

        if (s.StartsWith("Season", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(s.AsSpan("Season".Length).Trim(), out seasonNumber);
        }

        if (s.StartsWith('S') || s.StartsWith('s'))
        {
            if (s.Length > 1)
            {
                return int.TryParse(s.AsSpan(1).Trim(), out seasonNumber);
            }
        }

        return int.TryParse(s, out seasonNumber);
    }
}
