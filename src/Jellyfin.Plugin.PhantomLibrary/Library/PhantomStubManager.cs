using System;
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

    /// <summary>Creates (or reuses) the symlink for one phantom. Returns absolute path.</summary>
    Task<string> CreateAsync(string title, int tmdbId, PhantomMediaKind kind, CancellationToken ct);

    /// <summary>Deletes the symlink. Idempotent; swallows not-found.</summary>
    Task DeleteAsync(string symlinkPath, CancellationToken ct);

    /// <summary>Deterministic filename for a phantom. Pure / testable.</summary>
    string DeriveFilename(string title, int tmdbId, PhantomMediaKind kind);

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

        var root = ResolveRoot();
        var subdir = kind == PhantomMediaKind.Movie ? MoviesSubdir : ShowsSubdir;
        var dir = Path.Combine(root, subdir);
        Directory.CreateDirectory(dir);

        var filename = DeriveFilename(title, tmdbId, kind);
        var full = Path.Combine(dir, filename);

        // Idempotent: existing symlink that already points at our splash is fine.
        try
        {
            var existingInfo = new FileInfo(full);
            if (existingInfo.Exists)
            {
                var target = existingInfo.LinkTarget;
                if (string.Equals(target, _splashPath, StringComparison.Ordinal)
                    || string.Equals(target, _splashPath, StringComparison.OrdinalIgnoreCase))
                {
                    return full;
                }

                // Existing entry points elsewhere (or is a real file we did not create).
                // Replace only if it carries our sentinel; never clobber non-phantom files.
                if (filename.Contains(Sentinel, StringComparison.Ordinal))
                {
                    File.Delete(full);
                }
                else
                {
                    throw new IOException($"Refusing to overwrite non-phantom file at {full}");
                }
            }
        }
        catch (FileNotFoundException) { /* race: created+deleted; fall through to create */ }
        catch (DirectoryNotFoundException) { /* unlikely after CreateDirectory; fall through */ }

        File.CreateSymbolicLink(full, _splashPath);
        return full;
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
            File.Delete(symlinkPath);
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
        sb.Append(safe).Append(Sentinel).Append(tmdbId).Append('.').Append(ext);
        return sb.ToString();
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
