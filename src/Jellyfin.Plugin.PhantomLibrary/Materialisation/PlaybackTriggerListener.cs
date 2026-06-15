using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Subscribes to <see cref="ISessionManager.PlaybackStart"/>. When a
/// user begins playback of a phantom-channel item that has not yet
/// been materialised (still carries the <c>phantom</c> tag), enqueue
/// a fire-and-forget materialise via <see cref="IMaterialiser"/>'s
/// legacy Guid wrapper. The splash placeholder continues playing for
/// the user while the materialise runs in the background; once it
/// completes the channel re-emits the item with the real MediaSource
/// and the user's next play picks up the FUSE-backed file.
///
/// Playback is never blocked. If the materialise fails the splash
/// keeps playing and the user can retry via the kebab.
/// </summary>
public sealed class PlaybackTriggerListener : IHostedService
{
    private readonly ISessionManager _sessions;
    private readonly IMaterialiser _materialiser;
    private readonly ILogger<PlaybackTriggerListener> _logger;

    public PlaybackTriggerListener(
        ISessionManager sessions,
        IMaterialiser materialiser,
        ILogger<PlaybackTriggerListener> logger)
    {
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        _materialiser = materialiser ?? throw new ArgumentNullException(nameof(materialiser));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart += OnPlaybackStart;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= OnPlaybackStart;
        return Task.CompletedTask;
    }

    private void OnPlaybackStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            var item = e?.Item;
            if (item is null)
            {
                return;
            }

            if (item.SourceType != SourceType.Channel || !ChannelIds.IsPhantom(item.ChannelId))
            {
                return;
            }

            // Only fire when the item is still in phantom (splash)
            // state. Once materialised the tag is gone and there's
            // nothing to do.
            if (item.Tags is null
                || !Array.Exists(item.Tags, t => string.Equals(t, "phantom", StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            if (!ChannelItemId.TryParse(item.ExternalId, out _))
            {
                return;
            }

            _logger.LogInformation(
                "PlaybackStart on phantom item {External}; firing background materialise",
                item.ExternalId);

            // Fire-and-forget. Splash keeps playing while we work.
            _ = Task.Run(async () =>
            {
                try
                {
                    await _materialiser.MaterialiseAsync(
                        item.Id, MaterialiseTrigger.Play, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Background materialise for {External} threw",
                        item.ExternalId);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PlaybackTriggerListener handler threw; swallowing");
        }
    }
}
