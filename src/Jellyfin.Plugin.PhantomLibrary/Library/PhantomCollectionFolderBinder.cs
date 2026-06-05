using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Library;

/// <summary>
/// Binds the plugin-owned phantom stub directories to the operator's
/// gostream movie / show CollectionFolders so phantoms appear in the
/// same library as materialised items. Idempotent; works around the
/// Jellyfin AddMediaPath/refresh asymmetry documented in PLAN §M10.
/// </summary>
public interface IPhantomCollectionFolderBinder
{
    Task BindAsync(CancellationToken ct);
}

/// <inheritdoc />
public sealed class PhantomCollectionFolderBinder : IPhantomCollectionFolderBinder
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PhantomCollectionFolderBinder> _logger;
    private readonly Func<PluginConfiguration> _configProvider;

    public PhantomCollectionFolderBinder(
        ILibraryManager libraryManager,
        ILogger<PhantomCollectionFolderBinder> logger)
        : this(libraryManager, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    public PhantomCollectionFolderBinder(
        ILibraryManager libraryManager,
        ILogger<PhantomCollectionFolderBinder> logger,
        Func<PluginConfiguration> configProvider)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _configProvider = configProvider;
    }

    /// <inheritdoc />
    public async Task BindAsync(CancellationToken ct)
    {
        var cfg = _configProvider();
        var root = string.IsNullOrWhiteSpace(cfg.PhantomStubRoot)
            ? "/var/lib/jellyfin/phantom-library"
            : cfg.PhantomStubRoot;

        var moviesLib = string.IsNullOrWhiteSpace(cfg.PhantomMoviesLibraryName)
            ? "gostream-movies" : cfg.PhantomMoviesLibraryName;
        var showsLib = string.IsNullOrWhiteSpace(cfg.PhantomShowsLibraryName)
            ? "gostream-shows" : cfg.PhantomShowsLibraryName;

        await BindOneAsync(moviesLib, Path.Combine(root, PhantomStubManager.MoviesSubdir), ct).ConfigureAwait(false);
        await BindOneAsync(showsLib, Path.Combine(root, PhantomStubManager.ShowsSubdir), ct).ConfigureAwait(false);
    }

    private async Task BindOneAsync(string libName, string phantomDir, CancellationToken ct)
    {
        var cf = FindCollectionFolder(libName);
        if (cf is null)
        {
            _logger.LogWarning(
                "[PhantomBinder] configured phantom library '{Lib}' not found; skipping. " +
                "Operator may need to create the library in Jellyfin first or update the plugin config.",
                libName);
            return;
        }

        if (cf.PhysicalLocationsList is not null
            && cf.PhysicalLocationsList.Any(p => string.Equals(p, phantomDir, StringComparison.Ordinal)))
        {
            _logger.LogDebug("[PhantomBinder] phantom dir {Dir} already bound to {Lib}", phantomDir, libName);
            return;
        }

        try
        {
            _libraryManager.AddMediaPath(libName, new MediaPathInfo(phantomDir));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[PhantomBinder] AddMediaPath('{Lib}', '{Dir}') failed; continuing with workaround",
                libName, phantomDir);
        }

        await ValidateTopLibraryFoldersAsync(ct).ConfigureAwait(false);

        // Wait for the post-ValidateTopLibraryFolders metadata-saver
        // pipeline (image providers, dynamic image fetchers, etc.) to
        // complete. Without this slack window concurrent saves race
        // our patch and the persisted CollectionFolder reverts to the
        // pre-bind snapshot. Verified live: at ~10s the pipeline has
        // settled and our patch sticks.
        try { await Task.Delay(TimeSpan.FromSeconds(15), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        // Re-fetch the CollectionFolder (it may have been replaced by validation).
        var freshCf = (_libraryManager.GetItemById(cf.Id) as CollectionFolder) ?? cf;

        // Find the new physical Folder child whose Path matches our dir.
        var physFolder = FindPhysicalFolder(phantomDir);
        if (physFolder is null)
        {
            _logger.LogWarning(
                "[PhantomBinder] physical folder for phantom dir '{Dir}' not created after validation; skipping bind",
                phantomDir);
            return;
        }

        var locations = freshCf.PhysicalLocationsList ?? Array.Empty<string>();
        var folderIds = freshCf.PhysicalFolderIds ?? Array.Empty<Guid>();

        var hasLocation = locations.Any(p => string.Equals(p, phantomDir, StringComparison.Ordinal));
        var hasFolderId = folderIds.Contains(physFolder.Id);

        if (hasLocation && hasFolderId)
        {
            _logger.LogInformation(
                "[PhantomBinder] {Lib} already correctly bound to {Dir} after AddMediaPath (upstream fix may have landed)",
                libName, phantomDir);
            return;
        }

        if (!hasLocation)
        {
            freshCf.PhysicalLocationsList = locations.Concat(new[] { phantomDir }).ToArray();
        }

        if (!hasFolderId)
        {
            freshCf.PhysicalFolderIds = folderIds.Concat(new[] { physFolder.Id }).ToArray();
        }

        try
        {
            await _libraryManager.UpdateItemAsync(
                freshCf,
                freshCf.GetParent(),
                ItemUpdateType.MetadataEdit,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[PhantomBinder] UpdateItemAsync failed for {Lib}; binding may not persist until next restart",
                libName);
            return;
        }

        // Re-apply the patch in a loop. Concurrent racy metadata-saver
        // writes can revert our patch; the loop outlasts them. Stop
        // early when the in-memory state matches what we wrote AND has
        // survived two consecutive attempts unchanged.
        var stableCount = 0;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var check = _libraryManager.GetItemById(cf.Id) as CollectionFolder;
            if (check is null) break;

            var preLoc = check.PhysicalLocationsList ?? Array.Empty<string>();
            var preFid = check.PhysicalFolderIds ?? Array.Empty<Guid>();
            var preHadPhantom = preLoc.Contains(phantomDir, StringComparer.Ordinal)
                && preFid.Contains(physFolder.Id);

            check.PhysicalLocationsList = preLoc.Append(phantomDir).Distinct(StringComparer.Ordinal).ToArray();
            check.PhysicalFolderIds = preFid.Append(physFolder.Id).Distinct().ToArray();

            try
            {
                await _libraryManager.UpdateItemAsync(check, check.GetParent(),
                    ItemUpdateType.MetadataEdit, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[PhantomBinder] re-patch attempt {N} for {Lib} threw", attempt + 1, libName);
            }
            freshCf = check;

            if (preHadPhantom)
            {
                stableCount++;
                if (stableCount >= 3)
                {
                    _logger.LogDebug(
                        "[PhantomBinder] {Lib} binding stable after {Attempts} attempts",
                        libName, attempt + 1);
                    break;
                }
            }
            else
            {
                stableCount = 0;
            }
        }

        _logger.LogInformation(
            "[PhantomBinder] Bound phantom dir {Dir} to {Lib}; PhysicalFolderIds now [{Ids}]",
            phantomDir, libName,
            string.Join(",", (freshCf.PhysicalFolderIds ?? Array.Empty<Guid>()).Select(g => g.ToString("N"))));
    }

    private CollectionFolder? FindCollectionFolder(string name)
    {
        try
        {
            var root = _libraryManager.GetUserRootFolder();
            if (root is null) return null;
            foreach (var child in root.Children)
            {
                if (child is CollectionFolder cf
                    && string.Equals(cf.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return cf;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[PhantomBinder] CollectionFolder lookup failed for {Name}", name);
        }

        return null;
    }

    private Folder? FindPhysicalFolder(string phantomDir)
    {
        try
        {
            // The physical folder is registered as a Folder under the root,
            // not necessarily a Children of the user root folder. Use the
            // path index (FindByPath) which goes via the library DB.
            var found = _libraryManager.FindByPath(phantomDir, isFolder: true);
            if (found is Folder f) return f;

            // Fallback: walk root children (covers the common case where
            // ValidateTopLibraryFolders has registered the physical folder
            // as a direct child of the user root).
            var root = _libraryManager.GetUserRootFolder();
            if (root is null) return null;
            foreach (var child in root.Children)
            {
                if (child is Folder folder
                    && !(child is CollectionFolder)
                    && string.Equals(folder.Path, phantomDir, StringComparison.Ordinal))
                {
                    return folder;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[PhantomBinder] physical folder lookup failed for {Dir}", phantomDir);
        }

        return null;
    }

    private async Task ValidateTopLibraryFoldersAsync(CancellationToken ct)
    {
        // The two-arg overload (CancellationToken, bool) lives on the concrete
        // LibraryManager only; ILibraryManager exposes only the one-arg form.
        var t = _libraryManager.GetType();
        var method = t.GetMethod(
            "ValidateTopLibraryFolders",
            BindingFlags.Public | BindingFlags.Instance,
            new[] { typeof(CancellationToken), typeof(bool) });

        if (method is not null)
        {
            try
            {
                var result = method.Invoke(_libraryManager, new object[] { ct, false });
                if (result is Task task)
                {
                    await task.ConfigureAwait(false);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[PhantomBinder] reflective ValidateTopLibraryFolders(ct, bool) failed");
            }
        }

        try
        {
            await _libraryManager.ValidateTopLibraryFolders(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[PhantomBinder] ValidateTopLibraryFolders threw");
        }
    }
}
