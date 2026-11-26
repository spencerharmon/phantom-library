using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.PhantomLibrary.Swarm;

/// <summary>
/// Keeps the DHT "Peers" score on phantom movie/series BaseItems across metadata
/// refreshes. Core's MetadataService replaces <c>CriticRating</c> with the remote
/// providers' value (null for phantoms) on a replace-all refresh, which wiped the
/// value the swarm-health sync wrote; a custom provider runs AFTER that merge on
/// every refresh, so the score is re-applied as part of the refresh itself.
/// Only touches items of the two Phantom channels; inert while swarm health is disabled.
/// </summary>
public sealed class SwarmHealthMetadataProvider
    : ICustomMetadataProvider<Movie>, ICustomMetadataProvider<Series>, IHasOrder
{
    /// <inheritdoc />
    public string Name => "Phantom Library swarm health";

    /// <inheritdoc />
    public int Order => 1000;

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Movie item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        => Task.FromResult(Apply(item, series: false));

    /// <inheritdoc />
    public Task<ItemUpdateType> FetchAsync(Series item, MetadataRefreshOptions options, CancellationToken cancellationToken)
        => Task.FromResult(Apply(item, series: true));

    /// <summary>Applies the cached score to a phantom channel item; returns whether it changed.</summary>
    internal static ItemUpdateType Apply(BaseItem item, bool series)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!SwarmHealthCache.Enabled
            || (item.ChannelId != ChannelIds.Movies && item.ChannelId != ChannelIds.Shows))
        {
            return ItemUpdateType.None;
        }

        int? peers = ChannelItemId.TryParse(item.ExternalId, out var id) && id.TmdbId is { } tmdb
            ? SwarmHealthCache.Peers(tmdb, series)
            : null;
        var target = SwarmHealthScore.ToCriticRating(peers);
        if (SwarmHealthScore.SameRating(item.CriticRating, target))
        {
            return ItemUpdateType.None;
        }

        item.CriticRating = target;
        return ItemUpdateType.MetadataEdit;
    }
}
