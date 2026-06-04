using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Playback;

/// <summary>
/// Hosted service that subscribes to materialisation lifecycle events
/// from <see cref="IMaterialiser"/> and <see cref="IMaterialisationQueue"/>
/// and stamps a status prefix onto the item's Overview so Jellyfin
/// clients render the materialisation state natively alongside the
/// opaque splash playback. Original Overview is round-tripped via
/// <c>phantom_items.original_overview</c>.
/// </summary>
public sealed class PhantomStatusDecorator : IHostedService
{
    internal const string MaterialisingPrefix = "[🟡 materialising…] ";
    internal const string ReadyPrefix = "[✅ Ready — press play] ";

    private readonly IMaterialiser _materialiser;
    private readonly IMaterialisationQueue _queue;
    private readonly ILibraryManager _libraryManager;
    private readonly PhantomDb _db;
    private readonly ILogger<PhantomStatusDecorator> _logger;

    public PhantomStatusDecorator(
        IMaterialiser materialiser,
        IMaterialisationQueue queue,
        ILibraryManager libraryManager,
        PhantomDb db,
        ILogger<PhantomStatusDecorator> logger)
    {
        _materialiser = materialiser;
        _queue = queue;
        _libraryManager = libraryManager;
        _db = db;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _materialiser.LifecycleChanged += OnLifecycleChanged;
        _queue.LifecycleChanged += OnLifecycleChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _materialiser.LifecycleChanged -= OnLifecycleChanged;
        _queue.LifecycleChanged -= OnLifecycleChanged;
        return Task.CompletedTask;
    }

    private void OnLifecycleChanged(object? sender, MaterialisationLifecycleEvent evt)
    {
        // Fire-and-forget; never let the event loop block on DB / item I/O.
        _ = HandleAsync(evt, CancellationToken.None);
    }

    internal async Task HandleAsync(MaterialisationLifecycleEvent evt, CancellationToken ct)
    {
        try
        {
            var item = _libraryManager.GetItemById(evt.ItemId);
            if (item is null)
            {
                return;
            }

            switch (evt.Phase)
            {
                case MaterialisationLifecyclePhase.Queued:
                case MaterialisationLifecyclePhase.Started:
                    await ApplyPrefixAsync(item, MaterialisingPrefix, ct).ConfigureAwait(false);
                    break;
                case MaterialisationLifecyclePhase.Finished:
                    var success = evt.Outcome is { Status: MaterialisationStatus.Success or MaterialisationStatus.Duplicate };
                    await RestoreAsync(item, success, ct).ConfigureAwait(false);
                    break;
                default:
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PhantomStatusDecorator failed to handle {Phase} for {Id}", evt.Phase, evt.ItemId);
        }
    }

    private async Task ApplyPrefixAsync(BaseItem item, string prefix, CancellationToken ct)
    {
        var current = item.Overview ?? string.Empty;
        var stripped = StripKnownPrefixes(current);

        // Persist the genuinely-original overview (only the first call wins).
        await _db.RememberOriginalOverviewAsync(item.Id, stripped, ct).ConfigureAwait(false);

        var next = prefix + stripped;
        if (string.Equals(next, current, StringComparison.Ordinal))
        {
            return;
        }

        item.Overview = next;
        await PersistAsync(item, ct).ConfigureAwait(false);
    }

    private async Task RestoreAsync(BaseItem item, bool success, CancellationToken ct)
    {
        var original = await _db.TakeOriginalOverviewAsync(item.Id, ct).ConfigureAwait(false);
        var baseline = original ?? StripKnownPrefixes(item.Overview ?? string.Empty);

        var next = success ? ReadyPrefix + baseline : baseline;
        if (string.Equals(next, item.Overview, StringComparison.Ordinal))
        {
            return;
        }

        item.Overview = next;
        await PersistAsync(item, ct).ConfigureAwait(false);
    }

    private async Task PersistAsync(BaseItem item, CancellationToken ct)
    {
        try
        {
            await _libraryManager.UpdateItemAsync(
                item,
                item.GetParent(),
                ItemUpdateType.MetadataEdit,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "UpdateItemAsync(Overview) failed for {Id}; mutation kept in-memory", item.Id);
        }
    }

    internal static string StripKnownPrefixes(string s)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            if (s.StartsWith(MaterialisingPrefix, StringComparison.Ordinal))
            {
                s = s[MaterialisingPrefix.Length..];
                changed = true;
            }

            if (s.StartsWith(ReadyPrefix, StringComparison.Ordinal))
            {
                s = s[ReadyPrefix.Length..];
                changed = true;
            }
        }

        return s;
    }
}
