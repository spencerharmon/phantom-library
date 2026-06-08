using System;
using System.Globalization;
using System.IO;
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
    /// Year-aware overload. Emits the new Jellyfin-native
    /// <c>&lt;Title&gt; (&lt;Year&gt;) [tmdbid-&lt;id&gt;]</c> layout so
    /// Jellyfin's resolver derives a clean Name without scanner-driven
    /// underscore garbage. <paramref name="year"/> may be null when
    /// genuinely unknown (TMDB lacks it). Default implementation
    /// forwards to the no-year overload for back-compat with test
    /// doubles; production <see cref="PhantomStubManager"/> overrides.
    /// </summary>
    Task<string> CreateAsync(string title, int? year, int tmdbId, PhantomMediaKind kind, CancellationToken ct)
        => CreateAsync(title, tmdbId, kind, ct);

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

    /// <summary>Year-aware overload; produces the new layout filename.</summary>
    string DeriveFilename(string title, int? year, int tmdbId, PhantomMediaKind kind)
        => DeriveFilename(title, tmdbId, kind);

    /// <summary>
    /// Deterministic per-series stub layout (PLAN §M13). Returns the
    /// series directory, season directory, and the inner S01E01 symlink
    /// file path. Pure / testable; does not touch disk.
    /// </summary>
    (string SeriesDir, string SeasonDir, string EpisodeFile) DeriveSeriesStubPaths(string title, int tmdbId);

    /// <summary>Year-aware overload; produces the new series stub layout.</summary>
    (string SeriesDir, string SeasonDir, string EpisodeFile) DeriveSeriesStubPaths(string title, int? year, int tmdbId)
        => DeriveSeriesStubPaths(title, tmdbId);

    /// <summary>True once BootstrapAsync has completed successfully at least once.</summary>
    bool IsReady { get; }
}

/// <inheritdoc />
public sealed class PhantomStubManager : IPhantomStubManager
{
    internal const string MoviesSubdir = "movies";
    internal const string ShowsSubdir = "shows";

    /// <summary>
    /// Legacy filename sentinel used by the pre-spike stub layout. New
    /// stubs use <see cref="TmdbIdTokenPrefix"/> /
    /// <see cref="TmdbIdTokenSuffix"/>. This constant is retained for
    /// back-compat: the one-shot migration recognises legacy paths via
    /// it, and the delete-safety / heal-detection logic still accepts
    /// it via <see cref="PhantomPathUtilities.IsPhantomStubPath"/>.
    /// Do not use for newly-created stubs.
    /// </summary>
    internal const string Sentinel = "__phantom_tmdb";

    /// <summary>Opening literal of the Jellyfin-native tmdb path token.</summary>
    internal const string TmdbIdTokenPrefix = "[tmdbid-";

    /// <summary>Closing literal of the Jellyfin-native tmdb path token.</summary>
    internal const string TmdbIdTokenSuffix = "]";

    private static readonly Regex UnsafeChars = new("[^A-Za-z0-9_]", RegexOptions.Compiled);
    private static readonly Regex CollapseUnderscores = new("_+", RegexOptions.Compiled);
    // DisplaySanitize: replace filesystem-hostile chars with space, then collapse whitespace.
    private static readonly Regex DisplayUnsafeChars = new("[\\\\/\\[\\]:*?<>|\"\u0000]", RegexOptions.Compiled);
    private static readonly Regex CollapseWhitespace = new("\\s+", RegexOptions.Compiled);

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
    public Task<string> CreateAsync(string title, int tmdbId, PhantomMediaKind kind, CancellationToken ct)
        => CreateAsync(title, null, tmdbId, kind, ct);

    /// <inheritdoc />
    public async Task<string> CreateAsync(string title, int? year, int tmdbId, PhantomMediaKind kind, CancellationToken ct)
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
            var (seriesDir, seasonDir, episodeFile) = DeriveSeriesStubPaths(title, year, tmdbId);
            Directory.CreateDirectory(seasonDir);
            EnsureSplashSymlink(episodeFile);
            return seriesDir;
        }

        var root = ResolveRoot();
        var dir = Path.Combine(root, MoviesSubdir);
        Directory.CreateDirectory(dir);

        var filename = DeriveFilename(title, year, tmdbId, kind);
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
                // Replace only if its filename carries the legacy sentinel OR the
                // new [tmdbid-] token; never clobber non-phantom files.
                var filename = Path.GetFileName(fullPath);
                if (PhantomPathUtilities.IsPhantomStubPath(filename))
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
                if (PhantomPathUtilities.IsPhantomStubPath(leaf))
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
        => DeriveFilename(title, null, tmdbId, kind);

    /// <inheritdoc />
    public string DeriveFilename(string title, int? year, int tmdbId, PhantomMediaKind kind)
    {
        var stem = DeriveStem(title, year, tmdbId);
        var ext = _splashExt ?? "mp4";
        return stem + "." + ext;
    }

    /// <inheritdoc />
    public (string SeriesDir, string SeasonDir, string EpisodeFile) DeriveSeriesStubPaths(string title, int tmdbId)
        => DeriveSeriesStubPaths(title, null, tmdbId);

    /// <inheritdoc />
    public (string SeriesDir, string SeasonDir, string EpisodeFile) DeriveSeriesStubPaths(string title, int? year, int tmdbId)
    {
        var ext = _splashExt ?? "mp4";
        var dirStem = DeriveStem(title, year, tmdbId);
        // Episode filename intentionally omits the [tmdbid-] token: the
        // bracketed token belongs on the series directory; the episode
        // gets a clean <Title> (<Year>) S01E01.<ext> for Jellyfin's
        // tvshows resolver.
        var episodeStem = DeriveDisplayStem(title, year);
        var root = ResolveRoot();
        var seriesDir = Path.Combine(root, ShowsSubdir, dirStem);
        // PLAN §M13: Season 01 is hardcoded; phantom series expose a
        // single placeholder episode. Real episodes land under canonical
        // Season NN paths under the gostream physical folder once the
        // user plays the placeholder.
        var seasonDir = Path.Combine(seriesDir, "Season 01");
        var episodeFile = Path.Combine(seasonDir, episodeStem + " S01E01." + ext);
        return (seriesDir, seasonDir, episodeFile);
    }

    /// <summary>
    /// Filesystem-safe stem in the new <c>&lt;Title&gt; (&lt;Year&gt;)
    /// [tmdbid-&lt;id&gt;]</c> Jellyfin-native form. Year segment omitted
    /// when null. Internal: used by both the file and dir derivers.
    /// </summary>
    private static string DeriveStem(string title, int? year, int tmdbId)
    {
        var display = DeriveDisplayStem(title, year);
        return display + " " + TmdbIdTokenPrefix
            + tmdbId.ToString(CultureInfo.InvariantCulture) + TmdbIdTokenSuffix;
    }

    private static string DeriveDisplayStem(string title, int? year)
    {
        var safe = DisplaySanitize(title);
        if (string.IsNullOrEmpty(safe))
        {
            safe = "Untitled";
        }

        return year.HasValue
            ? safe + " (" + year.Value.ToString(CultureInfo.InvariantCulture) + ")"
            : safe;
    }

    /// <summary>
    /// Legacy underscore-only sanitiser. Retained for migration reverse-
    /// derivation and any back-compat call sites. Do NOT use for new
    /// stub names — use <see cref="DisplaySanitize"/> instead, which
    /// preserves spaces / parens / hyphens so Jellyfin's resolver
    /// derives a clean Name.
    /// </summary>
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

    /// <summary>
    /// Display-friendly sanitiser: keeps alphanumerics, spaces, parens,
    /// hyphens, dots, apostrophes, commas, ampersands. Strips genuinely
    /// filesystem-hostile chars (<c>/, \, [, ], :, ?, *, &lt;, &gt;,
    /// |, "</c> and NUL) by replacing them with a single space; collapses
    /// runs of whitespace; trims. Brackets are stripped because the
    /// caller appends a literal <c>[tmdbid-&lt;id&gt;]</c> token.
    /// </summary>
    internal static string DisplaySanitize(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        var replaced = DisplayUnsafeChars.Replace(title, " ");
        var collapsed = CollapseWhitespace.Replace(replaced, " ");
        return collapsed.Trim();
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
