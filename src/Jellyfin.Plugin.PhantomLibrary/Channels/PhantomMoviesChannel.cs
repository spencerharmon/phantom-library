using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Channels;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.Plugin.PhantomLibrary.Channels;

/// <summary>
/// "Phantom Movies" channel. Stage 2.4 skeleton: returns an empty
/// channel item list. Full implementation arrives in Stage 3.3
/// (discovery + materialised + orphan-gostream union).
///
/// Implements:
///   IChannel                       — required base contract
///   IRequiresMediaInfoCallback     — channel emits MediaSources at
///                                    browse time AND can answer a
///                                    per-id callback on play
///   ISupportsLatestMedia           — surfaces a "Latest in Phantom
///                                    Movies" Home row
///   IChannelItemRefresh            — opt-in for the patched
///                                    IChannelItemRefreshManager so
///                                    materialise-on-demand can
///                                    refresh a single item by
///                                    external id without paging.
/// </summary>
public sealed class PhantomMoviesChannel
    : IChannel, IRequiresMediaInfoCallback, ISupportsLatestMedia, IChannelItemRefresh
{
    /// <inheritdoc />
    public string Name => ChannelIds.MoviesName;

    /// <inheritdoc />
    public string Description => "Phantom Library — movie discovery + on-demand materialise via gostream.";

    /// <inheritdoc />
    public string DataVersion => "1";

    /// <inheritdoc />
    public string HomePageUrl => string.Empty;

    /// <inheritdoc />
    public ChannelParentalRating ParentalRating => ChannelParentalRating.GeneralAudience;

    /// <inheritdoc />
    public InternalChannelFeatures GetChannelFeatures()
    {
        return new InternalChannelFeatures
        {
            ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Movie },
            MediaTypes = new List<ChannelMediaType> { ChannelMediaType.Video },
        };
    }

    /// <inheritdoc />
    public bool IsEnabledFor(string userId) => true;

    /// <inheritdoc />
    public Task<ChannelItemResult> GetChannelItems(InternalChannelItemQuery query, CancellationToken cancellationToken)
    {
        return Task.FromResult(new ChannelItemResult
        {
            Items = Array.Empty<ChannelItemInfo>(),
            TotalRecordCount = 0,
        });
    }

    /// <inheritdoc />
    public Task<IEnumerable<MediaSourceInfo>> GetChannelItemMediaInfo(string id, CancellationToken cancellationToken)
    {
        return Task.FromResult<IEnumerable<MediaSourceInfo>>(Array.Empty<MediaSourceInfo>());
    }

    /// <inheritdoc />
    public Task<IEnumerable<ChannelItemInfo>> GetLatestMedia(ChannelLatestMediaSearch request, CancellationToken cancellationToken)
    {
        return Task.FromResult<IEnumerable<ChannelItemInfo>>(Array.Empty<ChannelItemInfo>());
    }

    /// <inheritdoc />
    public Task<DynamicImageResponse> GetChannelImage(ImageType type, CancellationToken cancellationToken)
    {
        return Task.FromResult(new DynamicImageResponse { HasImage = false });
    }

    /// <inheritdoc />
    public IEnumerable<ImageType> GetSupportedChannelImages() => Array.Empty<ImageType>();

    /// <inheritdoc />
    public Task<ChannelItemInfo> GetChannelItemAsync(string channelItemExternalId, CancellationToken cancellationToken)
    {
        // Stage 2.4 skeleton — channel emits no items yet so any
        // refresh request is for an unknown id. Stage 3.3 wires this
        // to the discovery + materialised state lookups.
        return Task.FromResult<ChannelItemInfo>(null!);
    }
}
