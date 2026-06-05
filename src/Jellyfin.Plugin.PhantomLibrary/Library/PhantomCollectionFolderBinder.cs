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
public interface IPhantomCollectionFolderBinder : IDisposable
{
    Task BindAsync(CancellationToken ct);

    /// <summary>
    /// Install an event-driven watchdog that re-applies the binding
    /// any time something else (e.g. FolderMetadataService's image
    /// provider) saves the CollectionFolder and drops our phantom
    /// path from PhysicalLocationsList/PhysicalFolderIds. Per PLAN
    /// §M10 §Jellyfin upstream issue this race is a Jellyfin bug;
    /// the watchdog is the plugin-side mitigation.
    /// </summary>
    void InstallWatchdog();
}

/// <inheritdoc />
public sealed class PhantomCollectionFolderBinder : IPhantomCollectionFolderBinder
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<PhantomCollectionFolderBinder> _logger;
    private readonly Func<PluginConfiguration> _configProvider;

    // Watchdog state. Populated on each BindOne call so the
    // ItemUpdated event handler can recognise which CF ↔ phantomDir
    // ↔ physFolderId triple to re-patch on overwrite.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (string phantomDir, Guid physFolderId)> _bindings = new();
    private bool _watchdogInstalled;
    // Guard against re-entrancy when our own UpdateItemAsync fires
    // ItemUpdated for the same CF.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> _selfPatching = new();

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

        // Re-apply the patch in a loop until the PERSISTED state in
        // the repository (not the in-memory cache) shows our phantom
        // path. The metadata-saver pipeline (FolderMetadataService,
        // BaseDynamicImageProvider) calls UpdateToRepositoryAsync
        // milliseconds after we save, with a stale snapshot of
        // PhysicalLocationsList/PhysicalFolderIds it captured before
        // our patch. The in-memory CollectionFolder object still has
        // our patch (Jellyfin caches by Id, mutates in-place), but
        // the repository row was overwritten. Verify against the
        // repository.
        var stableCount = 0;
        for (var attempt = 0; attempt < 30; attempt++)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            var persisted = RetrievePersistedCollectionFolder(cf.Id);
            var persistedHasPhantom = persisted is not null
                && (persisted.PhysicalLocationsList ?? Array.Empty<string>()).Contains(phantomDir, StringComparer.Ordinal)
                && (persisted.PhysicalFolderIds ?? Array.Empty<Guid>()).Contains(physFolder.Id);

            if (persistedHasPhantom)
            {
                stableCount++;
                if (stableCount >= 3)
                {
                    _logger.LogDebug(
                        "[PhantomBinder] {Lib} binding persisted to repository after {Attempts} attempts",
                        libName, attempt + 1);
                    break;
                }
                continue;
            }

            stableCount = 0;

            // Re-fetch in-memory CF (may have been replaced) and
            // re-apply the patch. Use the in-memory object so the
            // change is visible to any concurrent reader; the
            // UpdateItemAsync call will then re-serialise our patched
            // state to the repository.
            var check = (_libraryManager.GetItemById(cf.Id) as CollectionFolder) ?? freshCf;

            var preLoc = check.PhysicalLocationsList ?? Array.Empty<string>();
            var preFid = check.PhysicalFolderIds ?? Array.Empty<Guid>();
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
        }

        // Final persistence check; log loudly if the race still won.
        var finalPersisted = RetrievePersistedCollectionFolder(cf.Id);
        var finalHasPhantom = finalPersisted is not null
            && (finalPersisted.PhysicalLocationsList ?? Array.Empty<string>()).Contains(phantomDir, StringComparer.Ordinal)
            && (finalPersisted.PhysicalFolderIds ?? Array.Empty<Guid>()).Contains(physFolder.Id);

        if (finalHasPhantom)
        {
            _logger.LogInformation(
                "[PhantomBinder] Bound phantom dir {Dir} to {Lib} (persisted); PhysicalFolderIds now [{Ids}]",
                phantomDir, libName,
                string.Join(",", (finalPersisted!.PhysicalFolderIds ?? Array.Empty<Guid>()).Select(g => g.ToString("N"))));
        }
        else
        {
            _logger.LogWarning(
                "[PhantomBinder] FAILED to persist phantom dir {Dir} to {Lib} after 30 attempts; " +
                "will retry on next periodic re-bind. " +
                "This is the documented Jellyfin AddMediaPath/metadata-saver race; " +
                "upstream PR pending. In-memory state has the patch; " +
                "persisted state does not.",
                phantomDir, libName);
        }

        // Register this binding with the watchdog so any subsequent
        // out-of-band save of this CF that drops the phantom path
        // gets re-patched automatically.
        _bindings[cf.Id] = (phantomDir, physFolder.Id);
    }

    /// <inheritdoc />
    public void InstallWatchdog()
    {
        if (_watchdogInstalled) return;
        _watchdogInstalled = true;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _logger.LogDebug("[PhantomBinder] watchdog installed on ILibraryManager.ItemUpdated");
    }

    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (e?.Item is not CollectionFolder cf) return;
        if (!_bindings.TryGetValue(cf.Id, out var binding)) return;

        // Avoid self-trigger when our own UpdateItemAsync fires this event.
        if (_selfPatching.ContainsKey(cf.Id)) return;

        var loc = cf.PhysicalLocationsList ?? Array.Empty<string>();
        var fid = cf.PhysicalFolderIds ?? Array.Empty<Guid>();
        var hasPath = loc.Contains(binding.phantomDir, StringComparer.Ordinal);
        var hasFid = fid.Contains(binding.physFolderId);

        if (hasPath && hasFid) return;

        // Fire-and-forget re-patch. CancellationToken.None is
        // appropriate — we want this to run regardless of the
        // upstream save's context.
        _ = Task.Run(async () =>
        {
            try
            {
                _selfPatching[cf.Id] = 1;
                _logger.LogDebug(
                    "[PhantomBinder] watchdog: CF {Name} ({Id}) updated without phantom path; re-patching",
                    cf.Name, cf.Id);

                // Read fresh, mutate, save.
                var fresh = (_libraryManager.GetItemById(cf.Id) as CollectionFolder) ?? cf;
                var freshLoc = fresh.PhysicalLocationsList ?? Array.Empty<string>();
                var freshFid = fresh.PhysicalFolderIds ?? Array.Empty<Guid>();
                fresh.PhysicalLocationsList = freshLoc.Append(binding.phantomDir).Distinct(StringComparer.Ordinal).ToArray();
                fresh.PhysicalFolderIds = freshFid.Append(binding.physFolderId).Distinct().ToArray();
                await _libraryManager.UpdateItemAsync(fresh, fresh.GetParent(),
                    ItemUpdateType.MetadataEdit, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "[PhantomBinder] watchdog re-patch for {Id} threw", cf.Id);
            }
            finally
            {
                // Brief debounce window so back-to-back saves from
                // the metadata pipeline collapse to one re-patch.
                await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                _selfPatching.TryRemove(cf.Id, out _);
            }
        });
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_watchdogInstalled)
        {
            try { _libraryManager.ItemUpdated -= OnItemUpdated; } catch { /* swallow */ }
            _watchdogInstalled = false;
        }
    }

    private CollectionFolder? RetrievePersistedCollectionFolder(Guid id)
    {
        // RetrieveItem bypasses the in-memory cache and reads directly
        // from the SQLite repository. That is the only way to observe
        // whether the metadata-saver pipeline overwrote our patch.
        try
        {
            return _libraryManager.RetrieveItem(id) as CollectionFolder;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[PhantomBinder] RetrieveItem({Id}) threw", id);
            return null;
        }
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
