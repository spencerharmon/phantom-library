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
/// Walks the gostream FUSE-mounted virtual MKV tree to enumerate external
/// files — playable items that exist on the gostream side but do not have a
/// Phantom-owned <c>materialised_state</c> row. The channels emit these as
/// ordinary playable gostream items so phantoms, gostream files, and other
/// media files can coexist without relying on Jellyfin's library scanner.
///
/// Authoritative source for Phantom-owned (tmdb, fuse_path) mappings is the
/// plugin's own <c>materialised_state</c> table. Files outside that mapping
/// are still first-class playable channel items; they are just not Phantom
/// state-machine items.
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

    private static readonly TimeSpan EnumerationTtl = TimeSpan.FromSeconds(30);
    private static readonly SemaphoreSlim MoviesGate = new(1, 1);
    private static readonly SemaphoreSlim SeriesGate = new(1, 1);
    private static IReadOnlyList<GostreamFileEntry>? _moviesCache;
    private static string? _moviesCacheVersion;
    private static DateTimeOffset _moviesCacheAt = DateTimeOffset.MinValue;
    private static IReadOnlyList<GostreamSeriesEntry>? _seriesCache;
    private static string? _seriesCacheVersion;
    private static DateTimeOffset _seriesCacheAt = DateTimeOffset.MinValue;

    // Memoization for the (expensive, recursive) filesystem-version walk. This value is a
    // component of each channel's IChannel.DataVersion (Jellyfin's channel-item cache key);
    // recomputing it per browse both cost a full FUSE-tree walk and made the cache key churn
    // continuously as content materialised, defeating the channel cache -- catastrophic on the
    // PostgreSQL backend where a cache miss forces a full folder re-sync (~13k queries/page).
    private static readonly TimeSpan FilesystemVersionTtl = TimeSpan.FromSeconds(30);
    private static readonly object FsVersionGate = new();
    private static string? _moviesFsVersion;
    private static DateTimeOffset _moviesFsVersionAt = DateTimeOffset.MinValue;
    private static string? _showsFsVersion;
    private static DateTimeOffset _showsFsVersionAt = DateTimeOffset.MinValue;

    /// <summary>Test hook: drop cached enumeration results for a deterministic cold walk.</summary>
    internal static void ResetForTests()
    {
        _moviesCache = null;
        _moviesCacheVersion = null;
        _moviesCacheAt = DateTimeOffset.MinValue;
        _seriesCache = null;
        _seriesCacheVersion = null;
        _seriesCacheAt = DateTimeOffset.MinValue;
        _moviesFsVersion = null;
        _moviesFsVersionAt = DateTimeOffset.MinValue;
        _showsFsVersion = null;
        _showsFsVersionAt = DateTimeOffset.MinValue;
    }

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

    public string MoviesVersion() => CachedFilesystemVersion(MoviesRoot, movies: true);

    public string ShowsVersion() => CachedFilesystemVersion(ShowsRoot, movies: false);

    /// <summary>
    /// Returns <see cref="FilesystemVersion"/> for <paramref name="root"/>, memoized for
    /// <see cref="FilesystemVersionTtl"/>. The raw computation is a full recursive walk of the
    /// FUSE-mounted tree and was previously run on every browse (it feeds the channel DataVersion
    /// cache key). Memoizing bounds the walk frequency and holds the key stable within the window;
    /// newly-materialised files become visible up to the TTL later. The walk itself runs outside
    /// the lock so a slow FUSE stat never serialises unrelated callers.
    /// </summary>
    private string CachedFilesystemVersion(string root, bool movies)
    {
        var now = DateTimeOffset.UtcNow;
        lock (FsVersionGate)
        {
            if (movies)
            {
                if (_moviesFsVersion is not null && now - _moviesFsVersionAt < FilesystemVersionTtl)
                {
                    return _moviesFsVersion;
                }
            }
            else if (_showsFsVersion is not null && now - _showsFsVersionAt < FilesystemVersionTtl)
            {
                return _showsFsVersion;
            }
        }

        var computed = FilesystemVersion(root);

        lock (FsVersionGate)
        {
            if (movies)
            {
                _moviesFsVersion = computed;
                _moviesFsVersionAt = now;
            }
            else
            {
                _showsFsVersion = computed;
                _showsFsVersionAt = now;
            }
        }

        return computed;
    }

    private static string FilesystemVersion(string root)
    {
        if (!Directory.Exists(root))
        {
            return "missing";
        }

        try
        {
            var count = 0;
            var maxTicks = Directory.GetLastWriteTimeUtc(root).Ticks;
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (!IsVideoFile(path))
                {
                    continue;
                }

                count++;
                var ticks = File.GetLastWriteTimeUtc(path).Ticks;
                if (ticks > maxTicks)
                {
                    maxTicks = ticks;
                }
            }

            return count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + maxTicks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return "error";
        }
    }

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
        var version = MoviesVersion();
        var cached = _moviesCache;
        if (cached is not null && _moviesCacheVersion == version && DateTimeOffset.UtcNow - _moviesCacheAt < EnumerationTtl)
        {
            return cached;
        }

        await MoviesGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = _moviesCache;
            if (cached is not null && _moviesCacheVersion == version && DateTimeOffset.UtcNow - _moviesCacheAt < EnumerationTtl)
            {
                return cached;
            }

            var fresh = await WalkOrphanMoviesAsync(knownTmdbs, ct).ConfigureAwait(false);
            _moviesCache = fresh;
            _moviesCacheVersion = version;
            _moviesCacheAt = DateTimeOffset.UtcNow;
            return fresh;
        }
        finally
        {
            MoviesGate.Release();
        }
    }

    private async Task<IReadOnlyList<GostreamFileEntry>> WalkOrphanMoviesAsync(
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
    public async Task<IReadOnlyList<GostreamSeriesEntry>> EnumerateSeriesAsync(CancellationToken ct)
    {
        var version = ShowsVersion();
        var cached = _seriesCache;
        if (cached is not null && _seriesCacheVersion == version && DateTimeOffset.UtcNow - _seriesCacheAt < EnumerationTtl)
        {
            return cached;
        }

        await SeriesGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = _seriesCache;
            if (cached is not null && _seriesCacheVersion == version && DateTimeOffset.UtcNow - _seriesCacheAt < EnumerationTtl)
            {
                return cached;
            }

            var fresh = await WalkSeriesAsync(ct).ConfigureAwait(false);
            _seriesCache = fresh;
            _seriesCacheVersion = version;
            _seriesCacheAt = DateTimeOffset.UtcNow;
            return fresh;
        }
        finally
        {
            SeriesGate.Release();
        }
    }

    private async Task<IReadOnlyList<GostreamSeriesEntry>> WalkSeriesAsync(CancellationToken ct)
    {
        var root = ShowsRoot;
        if (!Directory.Exists(root))
        {
            _logger.LogDebug("Gostream shows root {Root} does not exist; no series", root);
            return Array.Empty<GostreamSeriesEntry>();
        }

        var materialisedFusePaths = (await _db.ListMaterialisedStateAsync("episode", ct).ConfigureAwait(false))
            .Select(r => GostreamPathResolver.ResolvePath(r.FusePath, root))
            .ToHashSet(StringComparer.Ordinal);

        var series = new List<GostreamSeriesEntry>();
        IEnumerable<string> seriesDirs;
        try
        {
            seriesDirs = Directory.EnumerateDirectories(root);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate series under {Root}", root);
            return Array.Empty<GostreamSeriesEntry>();
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
                    if (!IsVideoFile(ep) || materialisedFusePaths.Contains(ep))
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

            if (seasons.Count > 0)
            {
                series.Add(new GostreamSeriesEntry(seriesDir, null, seasons));
            }
        }

        return series;
    }

    /// <summary>
    /// Walk the shows dir and return the first video file whose
    /// <see cref="ChannelItemId.ForOrphanPath(string)"/> hash matches
    /// <paramref name="hash"/>, or null if none.
    /// </summary>
    public async Task<GostreamFileEntry?> LookupOrphanEpisodeByHashAsync(string hash, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hash);
        var root = ShowsRoot;
        if (!Directory.Exists(root))
        {
            return null;
        }

        var materialisedFusePaths = (await _db.ListMaterialisedStateAsync("episode", ct).ConfigureAwait(false))
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

        // Common forms: "Season 1", "Season 01", "Season.01", "Season_01", "Season1", "S01", "season 1".
        var s = dirName.Trim().Replace('.', ' ').Replace('_', ' ');
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
