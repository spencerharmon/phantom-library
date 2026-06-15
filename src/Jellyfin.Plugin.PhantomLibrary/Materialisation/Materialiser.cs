using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Stage-2.1 stub. The legacy file-on-disk materialiser was deleted
/// with the rest of the phantom-stub architecture; the tuple-based
/// rewrite per plan §4.2 lands in Stage 4.2.
/// </summary>
public sealed class Materialiser : IMaterialiser
{
    private readonly ILogger<Materialiser> _logger;

    public Materialiser(
        ILibraryManager libraryManager,
        IGostreamClient gostream,
        PhantomDb db,
        ITmdbClient tmdb,
        ILogger<Materialiser> logger)
    {
        _ = libraryManager;
        _ = gostream;
        _ = db;
        _ = tmdb;
        _logger = logger;
    }

    public event EventHandler<MaterialisationLifecycleEvent>? LifecycleChanged;

    public Task<MaterialisationOutcome> MaterialiseAsync(
        Guid jellyfinItemId, MaterialiseTrigger trigger, CancellationToken ct)
    {
        _logger.LogWarning(
            "Materialiser invoked for {Id} ({Trigger}); stage-2.1 stub — rewritten in Stage 4.2",
            jellyfinItemId, trigger);
        // Touch the event so the compiler doesn't warn it's unused.
        LifecycleChanged?.Invoke(this, new MaterialisationLifecycleEvent(
            jellyfinItemId, MaterialisationLifecyclePhase.Finished, null));
        return Task.FromResult(new MaterialisationOutcome
        {
            Status = MaterialisationStatus.Error,
            Error = "stage-2.1 stub; rewritten in Stage 4.2",
        });
    }
}
