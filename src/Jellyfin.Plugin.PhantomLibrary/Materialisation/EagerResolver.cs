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
    private readonly IEagerHintSink? _hintSink;

    public EagerResolver(ILibraryManager libraryManager, IMaterialisationQueue queue, ILogger<EagerResolver> logger, IEagerHintSink hintSink)
        : this(libraryManager, queue, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration(), hintSink)
    {
    }

    public EagerResolver(
        ILibraryManager libraryManager,
        IMaterialisationQueue queue,
        ILogger<EagerResolver> logger,
        System.Func<PluginConfiguration> configProvider,
        IEagerHintSink? hintSink = null)
    {
        _libraryManager = libraryManager;
        _queue = queue;
        _logger = logger;
        _configProvider = configProvider;
        _hintSink = hintSink;
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
        // Only Movies are eager-resolvable. Series are containers; the autopilot
        // drives episode-level pre-resolution per playback context (M8). Enqueueing
        // a Series here just spams 'Series-level materialisation not supported' logs.
        if (item is not Movie) return;
        // Virtual items carry a null/empty Path; that is the case we want
        // to eager-resolve. Skip materialised items (Path set).
        if (!string.IsNullOrWhiteSpace(item.Path)) return;
        if (item.ProviderIds is null || !item.ProviderIds.ContainsKey("Tmdb")) return;

        var hint = _hintSink?.ConsumeHint(item.Id) ?? EagerHint.None;
        _logger.LogDebug("EagerResolve enqueue {Id} ({Name}) hint={Hint}", item.Id, item.Name, hint);
        if (hint == EagerHint.SimilarToFavourite || hint == EagerHint.UserRecommendation)
        {
            _queue.EnqueueUser(item.Id, MaterialiseTrigger.PreResolve);
        }
        else
        {
            _queue.EnqueueEager(item.Id);
        }
    }
}
