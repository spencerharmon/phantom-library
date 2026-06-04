using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Subscribes to <see cref="ILibraryManager.ItemAdded"/>; for newly added
/// Virtual movies/series with a TMDB id, queues a PreResolve pass that
/// caches the magnet without calling gostream.
/// </summary>
public sealed class EagerResolver : IHostedService
{
    private readonly ILibraryManager _libraryManager;
    private readonly IMaterialisationQueue _queue;
    private readonly ILogger<EagerResolver> _logger;
    private readonly System.Func<PluginConfiguration> _configProvider;

    public EagerResolver(ILibraryManager libraryManager, IMaterialisationQueue queue, ILogger<EagerResolver> logger)
        : this(libraryManager, queue, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    public EagerResolver(
        ILibraryManager libraryManager,
        IMaterialisationQueue queue,
        ILogger<EagerResolver> logger,
        System.Func<PluginConfiguration> configProvider)
    {
        _libraryManager = libraryManager;
        _queue = queue;
        _logger = logger;
        _configProvider = configProvider;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded += OnItemAdded;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemAdded -= OnItemAdded;
        return Task.CompletedTask;
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        if (!_configProvider().EagerResolveEnabled) return;
        var item = e?.Item;
        if (item is null) return;
        if (item is not Movie && item is not Series) return;
        if (!string.IsNullOrWhiteSpace(item.Path)) return;
        if (item.ProviderIds is null || !item.ProviderIds.ContainsKey("Tmdb")) return;

        _logger.LogDebug("EagerResolve enqueue {Id} ({Name})", item.Id, item.Name);
        _queue.EnqueueEager(item.Id);
    }
}
