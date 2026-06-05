using System;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Library;

/// <summary>
/// Resolves the synthetic library root Folder under which Phantom Library
/// creates Virtual items. Honours <see cref="PluginConfiguration.PhantomTargetLibraryId"/>
/// when set, otherwise walks the user root for the first Movies / TV folder.
/// Per-process cache; <see cref="Invalidate"/> clears it after config changes.
/// </summary>
public sealed class VirtualLibraryRoot
{
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<VirtualLibraryRoot> _logger;
    private readonly Func<PluginConfiguration> _configProvider;
    private readonly object _lock = new();

    private Folder? _moviesParent;
    private Folder? _seriesParent;
    private bool _moviesResolved;
    private bool _seriesResolved;

    public VirtualLibraryRoot(ILibraryManager libraryManager, ILogger<VirtualLibraryRoot> logger)
        : this(libraryManager, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    public VirtualLibraryRoot(
        ILibraryManager libraryManager,
        ILogger<VirtualLibraryRoot> logger,
        Func<PluginConfiguration> configProvider)
    {
        _libraryManager = libraryManager;
        _logger = logger;
        _configProvider = configProvider;
    }

    /// <summary>Resolves the parent Folder for Movies. Cached.</summary>
    public Folder? ResolveMoviesParent()
    {
        lock (_lock)
        {
            if (_moviesResolved) return _moviesParent;
            _moviesParent = Resolve(CollectionType.movies);
            _moviesResolved = true;
            return _moviesParent;
        }
    }

    /// <summary>Resolves the parent Folder for TV Shows. Cached.</summary>
    public Folder? ResolveSeriesParent()
    {
        lock (_lock)
        {
            if (_seriesResolved) return _seriesParent;
            _seriesParent = Resolve(CollectionType.tvshows);
            _seriesResolved = true;
            return _seriesParent;
        }
    }

    /// <summary>Clears the per-process cache; next resolve walks the tree again.</summary>
    public void Invalidate()
    {
        lock (_lock)
        {
            _moviesParent = null;
            _seriesParent = null;
            _moviesResolved = false;
            _seriesResolved = false;
        }
    }

    private Folder? Resolve(CollectionType desired)
    {
        // 0. M10 path: phantoms must be parented into the per-kind phantom
        //    physical Folder (a Folder under /var/lib/jellyfin/phantom-library/{movies,shows})
        //    so their TopParentId matches one of the bound CollectionFolder's
        //    PhysicalFolderIds and browse surfaces them. Falls through to the
        //    legacy v0.1 resolution if the binder has not run yet.
        var phantom = ResolvePhantomPhysicalFolder(desired);
        if (phantom is not null)
        {
            return phantom;
        }

        // 1. Operator-pinned library by GUID.
        var configuredId = _configProvider().PhantomTargetLibraryId;
        if (!string.IsNullOrWhiteSpace(configuredId)
            && Guid.TryParse(configuredId, out var pinnedId)
            && pinnedId != Guid.Empty)
        {
            var pinned = _libraryManager.GetItemById(pinnedId) as Folder;
            if (pinned is not null)
            {
                _logger.LogDebug("VirtualLibraryRoot using configured library {Id} ({Name}) for {Type}",
                    pinnedId, pinned.Name, desired);
                return pinned;
            }

            _logger.LogWarning(
                "PhantomTargetLibraryId={Id} did not resolve to a library Folder; falling back to auto-pick.",
                configuredId);
        }

        // 2. Auto-pick: walk user root for the first Folder of the desired CollectionType.
        Folder? root;
        try
        {
            root = _libraryManager.GetUserRootFolder();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GetUserRootFolder threw while resolving {Type} parent", desired);
            return null;
        }

        if (root is null) return null;

        foreach (var child in root.Children)
        {
            if (child is not Folder folder) continue;
            var ct = _libraryManager.GetContentType(folder);
            if (ct == desired)
            {
                _logger.LogDebug("VirtualLibraryRoot auto-picked {Name} ({Id}) for {Type}",
                    folder.Name, folder.Id, desired);
                return folder;
            }
        }

        // 3. Fall back to root with a warning.
        _logger.LogWarning(
            "No Jellyfin library with CollectionType={Type} found; falling back to user root folder. " +
            "Set PhantomTargetLibraryId to silence this warning.",
            desired);
        return root;
    }

    private Folder? ResolvePhantomPhysicalFolder(CollectionType desired)
    {
        try
        {
            var cfg = _configProvider();
            var rootCfg = cfg.PhantomStubRoot;
            if (string.IsNullOrWhiteSpace(rootCfg))
            {
                return null;
            }

            var subdir = desired == CollectionType.movies ? "movies"
                : desired == CollectionType.tvshows ? "shows"
                : null;
            if (subdir is null) return null;

            var phantomDir = System.IO.Path.Combine(rootCfg, subdir);
            var found = _libraryManager.FindByPath(phantomDir, isFolder: true);
            if (found is Folder f && !(f is MediaBrowser.Controller.Entities.CollectionFolder))
            {
                _logger.LogDebug(
                    "VirtualLibraryRoot using phantom physical folder {Path} ({Id}) for {Type}",
                    phantomDir, f.Id, desired);
                return f;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ResolvePhantomPhysicalFolder failed for {Type}", desired);
        }

        return null;
    }
}
