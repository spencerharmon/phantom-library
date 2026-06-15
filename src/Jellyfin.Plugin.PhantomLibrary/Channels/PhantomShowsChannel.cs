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
/// "Phantom Shows" channel. Stage 2.4 skeleton: returns an empty
/// channel item list. Full hierarchical implementation (Series →
/// Season → Episode folder navigation) arrives in Stage 5.1.
///
/// Implements the same capability set as PhantomMoviesChannel; see
/// that class for the rationale.
/// </summary>
public sealed class PhantomShowsChannel
    : IChannel, IRequiresMediaInfoCallback, ISupportsLatestMedia, IChannelItemRefresh
{
    /// <inheritdoc />
    public string Name => ChannelIds.ShowsName;

    /// <inheritdoc />
    public string Description => "Phantom Library — TV discovery + on-demand materialise via gostream.";

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
            ContentTypes = new List<ChannelMediaContentType> { ChannelMediaContentType.Episode },
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
        // Stage 2.4 skeleton; Stage 5.1 wires this to the
        // series/season/episode lookups.
        return Task.FromResult<ChannelItemInfo>(null!);
    }
}
