using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Playback;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Library;

/// <summary>Phantom stub kinds.</summary>
public enum PhantomMediaKind
{
    /// <summary>A movie stub. Materialises into <c>movies/</c>.</summary>
    Movie,

    /// <summary>A TV series stub. Materialises into <c>shows/</c>.</summary>
    Series,
}

/// <summary>
/// Owns the on-disk symlink farm under
/// <see cref="PluginConfiguration.PhantomStubRoot"/>. Each phantom
/// BaseItem gets a uniquely named symlink whose target is the shared
/// extracted splash file; this gives Jellyfin's scanner per-item Paths
/// (required for browse visibility) without burning disk.
/// </summary>
public interface IPhantomStubManager
{
    /// <summary>Verifies root dirs exist + are writable; extracts splash. Throws on operator-fixable failure.</summary>
    Task BootstrapAsync(CancellationToken ct);

    /// <summary>
    /// Creates (or reuses) the on-disk stub for one phantom and returns its
    /// absolute path. For <see cref="PhantomMediaKind.Movie"/> this is the
    /// loose-file symlink path under <c>movies/</c>. For
    /// <see cref="PhantomMediaKind.Series"/> this is the **per-series
    /// directory** under <c>shows/</c> (which contains
    /// <c>Season 01/&lt;stem&gt; S01E01.&lt;ext&gt;</c>); callers persist that
    /// directory as the Series BaseItem's Path. See PLAN §M13.
    /// </summary>
    Task<string> CreateAsync(string title, int tmdbId, PhantomMediaKind kind, CancellationToken ct);

    /// <summary>
    /// Deletes the stub. Idempotent; swallows not-found. For series stubs
    /// (directories under <c>shows/</c> carrying the <c>__phantom_tmdb</c>
    /// sentinel in the leaf name) the whole tree is removed recursively.
    /// Refuses to recursively delete any directory that does NOT carry the
    /// sentinel.
    /// </summary>
    Task DeleteAsync(string symlinkPath, CancellationToken ct);

    /// <summary>Deterministic filename for a phantom. Pure / testable.</summary>
    string DeriveFilename(string title, int tmdbId, PhantomMediaKind kind);

    /// <summary>
    /// Deterministic per-series stub layout (PLAN §M13). Returns the
    /// series directory, season directory, and the inner S01E01 symlink
    /// file path. Pure / testable; does not touch disk.
    /// </summary>
    (string SeriesDir, string SeasonDir, string EpisodeFile) DeriveSeriesStubPaths(string title, int tmdbId);

    /// <summary>True once BootstrapAsync has completed successfully at least once.</summary>
    bool IsReady { get; }
}

/// <inheritdoc />
public sealed class PhantomStubManager : IPhantomStubManager
{
    internal const string MoviesSubdir = "movies";
    internal const string ShowsSubdir = "shows";
    internal const string Sentinel = "__phantom_tmdb";

    private static readonly Regex UnsafeChars = new("[^A-Za-z0-9_]", RegexOptions.Compiled);
    private static readonly Regex CollapseUnderscores = new("_+", RegexOptions.Compiled);

    private readonly IApplicationPaths _paths;
    private readonly ILogger<PhantomStubManager> _logger;
    private readonly Func<PluginConfiguration> _configProvider;
    private readonly SemaphoreSlim _bootstrapGate = new(1, 1);

    private string? _splashPath;
    private string? _splashExt;
    private string? _rootForCachedSplash;

    public PhantomStubManager(
        IApplicationPaths paths,
        ILogger<PhantomStubManager> logger)
        : this(paths, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    public PhantomStubManager(
        IApplicationPaths paths,
        ILogger<PhantomStubManager> logger,
        Func<PluginConfiguration> configProvider)
    {
        _paths = paths;
        _logger = logger;
        _configProvider = configProvider;
    }

    /// <inheritdoc />
    public bool IsReady { get; private set; }

    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308",
        Justification = "Filename extensions are canonically lowercase on disk.")]
    public async Task BootstrapAsync(CancellationToken ct)
    {
        await _bootstrapGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var root = ResolveRoot();
            var movies = Path.Combine(root, MoviesSubdir);
            var shows = Path.Combine(root, ShowsSubdir);

            EnsureWritable(root);
            EnsureWritable(movies);
            EnsureWritable(shows);

            var splash = await SplashStream.GetLocalPathAsync(_paths, ct).ConfigureAwait(false);
            _splashPath = splash;
            _splashExt = (Path.GetExtension(splash) ?? string.Empty)
                .TrimStart('.')
                .ToLowerInvariant();
            if (string.IsNullOrEmpty(_splashExt))
            {
                _splashExt = "mp4";
            }
            _rootForCachedSplash = root;

            // Drop a non-media sentinel inside each subdir so Jellyfin's
            // empty-folder skip in Folder.IsLibraryFolderAccessible does
            // not cull our newly-bound physical folders before any
            // phantoms have been created. Dotfile prefix keeps it out
            // of any user-facing browse / scan results.
            EnsureSentinel(movies);
            EnsureSentinel(shows);

            IsReady = true;
            _logger.LogInformation(
                "[PhantomStubManager] bootstrap OK: root={Root} splash={Splash}",
                root, splash);
        }
        finally
        {
            _bootstrapGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string> CreateAsync(string title, int tmdbId, PhantomMediaKind kind, CancellationToken ct)
    {
        if (!IsReady || _splashPath is null)
        {
            // Defensive: try one bootstrap if caller forgot. If still not ready, surface
            // a clear error rather than silently creating stubs that point nowhere.
            await BootstrapAsync(ct).ConfigureAwait(false);
            if (!IsReady || _splashPath is null)
            {
                throw new InvalidOperationException(
                    "PhantomStubManager not initialised; BootstrapAsync did not complete.");
            }
        }

        if (kind == PhantomMediaKind.Series)
        {
            var (seriesDir, seasonDir, episodeFile) = DeriveSeriesStubPaths(title, tmdbId);
            Directory.CreateDirectory(seasonDir);
            EnsureSplashSymlink(episodeFile);
            return seriesDir;
        }

        var root = ResolveRoot();
        var dir = Path.Combine(root, MoviesSubdir);
        Directory.CreateDirectory(dir);

        var filename = DeriveFilename(title, tmdbId, kind);
        var full = Path.Combine(dir, filename);
        EnsureSplashSymlink(full);
        return full;
    }

    private void EnsureSplashSymlink(string fullPath)
    {
        // Idempotent: existing symlink that already points at our splash is fine.
        try
        {
            var existingInfo = new FileInfo(fullPath);
            if (existingInfo.Exists)
            {
                var target = existingInfo.LinkTarget;
                if (string.Equals(target, _splashPath, StringComparison.Ordinal)
                    || string.Equals(target, _splashPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                // Existing entry points elsewhere (or is a real file we did not create).
                // Replace only if its filename carries our sentinel; never clobber non-phantom files.
                var filename = Path.GetFileName(fullPath);
                if (filename.Contains(Sentinel, StringComparison.Ordinal))
                {
                    File.Delete(fullPath);
                }
                else
                {
                    throw new IOException($"Refusing to overwrite non-phantom file at {fullPath}");
                }
            }
        }
        catch (FileNotFoundException) { /* race: created+deleted; fall through to create */ }
        catch (DirectoryNotFoundException) { /* unlikely after CreateDirectory; fall through */ }

        File.CreateSymbolicLink(fullPath, _splashPath!);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string symlinkPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(symlinkPath))
        {
            return Task.CompletedTask;
        }

        try
        {
            // Series stubs are directories. Recursively remove only if the
            // leaf carries our sentinel — refuse to nuke arbitrary dirs
            // that happen to have been passed in.
            if (Directory.Exists(symlinkPath) && !IsReparseSymlink(symlinkPath))
            {
                var leaf = Path.GetFileName(symlinkPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (leaf.Contains(Sentinel, StringComparison.Ordinal))
                {
                    Directory.Delete(symlinkPath, recursive: true);
                }
                else
                {
                    _logger.LogWarning(
                        "[PhantomStubManager] refusing to recursively delete directory without phantom sentinel: {Path}",
                        symlinkPath);
                }
            }
            else
            {
                File.Delete(symlinkPath);
            }
        }
        catch (FileNotFoundException)
        {
            // Already gone — fine.
        }
        catch (DirectoryNotFoundException)
        {
            // Parent vanished — fine.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PhantomStubManager] delete failed for {Path}", symlinkPath);
        }

        return Task.CompletedTask;
    }

    private static bool IsReparseSymlink(string path)
    {
        try
        {
            var di = new DirectoryInfo(path);
            return di.Exists && (di.Attributes & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint;
        }
        catch
        {
            return false;
        }
    }

    /// <inheritdoc />
    public string DeriveFilename(string title, int tmdbId, PhantomMediaKind kind)
    {
        var safe = Sanitize(title);
        if (string.IsNullOrEmpty(safe))
        {
            safe = "untitled";
        }

        var ext = _splashExt ?? "mp4";
        var sb = new StringBuilder(safe.Length + 32);
        sb.Append(safe).Append(Sentinel).Append(tmdbId.ToString(CultureInfo.InvariantCulture)).Append('.').Append(ext);
        return sb.ToString();
    }

    /// <inheritdoc />
    public (string SeriesDir, string SeasonDir, string EpisodeFile) DeriveSeriesStubPaths(string title, int tmdbId)
    {
        var safe = Sanitize(title);
        if (string.IsNullOrEmpty(safe))
        {
            safe = "untitled";
        }

        var ext = _splashExt ?? "mp4";
        var stem = safe + Sentinel + tmdbId.ToString(CultureInfo.InvariantCulture);
        var root = ResolveRoot();
        var seriesDir = Path.Combine(root, ShowsSubdir, stem);
        // PLAN §M13: Season 01 is hardcoded; phantom series expose a
        // single placeholder episode. Real episodes land under canonical
        // Season NN paths under the gostream physical folder once the
        // user plays the placeholder.
        var seasonDir = Path.Combine(seriesDir, "Season 01");
        var episodeFile = Path.Combine(seasonDir, stem + " S01E01." + ext);
        return (seriesDir, seasonDir, episodeFile);
    }

    private static string Sanitize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var replaced = UnsafeChars.Replace(title, "_");
        var collapsed = CollapseUnderscores.Replace(replaced, "_");
        return collapsed.Trim('_');
    }

    private string ResolveRoot()
    {
        var root = _configProvider().PhantomStubRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = "/var/lib/jellyfin/phantom-library";
        }
        return root;
    }

    private static void EnsureSentinel(string dir)
    {
        var sentinel = Path.Combine(dir, ".phantom-library-keep");
        try
        {
            if (!File.Exists(sentinel))
            {
                File.WriteAllText(sentinel, "Phantom Library sentinel; do not delete. See PLAN §M10.\n");
            }
        }
        catch
        {
            // Best-effort; the EnsureWritable probe above already proved
            // the dir is writable, so a failure here is transient.
        }
    }

    private static void EnsureWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Phantom stub root '{dir}' could not be created. Operator must: " +
                $"sudo mkdir -p /var/lib/jellyfin/phantom-library/{{movies,shows}} && " +
                $"sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/phantom-library",
                ex);
        }

        var probe = Path.Combine(dir, ".phantom-write-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Phantom stub root '{dir}' is not writable by the Jellyfin process. Operator must: " +
                $"sudo mkdir -p /var/lib/jellyfin/phantom-library/{{movies,shows}} && " +
                $"sudo chown -R jellyfin:jellyfin /var/lib/jellyfin/phantom-library",
                ex);
        }
    }
}
