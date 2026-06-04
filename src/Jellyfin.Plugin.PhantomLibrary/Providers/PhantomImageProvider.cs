using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.PhantomLibrary.Providers;

/// <summary>
/// TMDB-backed <see cref="IRemoteImageProvider"/>. Supplies posters,
/// backdrops, and logos for movies, series, and episodes that carry a
/// TMDB provider id.
/// </summary>
public sealed class PhantomImageProvider : IRemoteImageProvider
{
    private const int MaxPerType = 10;
    private const string TmdbProviderId = "Tmdb";

    private readonly ITmdbClient _tmdb;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>Initializes a new instance of the <see cref="PhantomImageProvider"/> class.</summary>
    public PhantomImageProvider(ITmdbClient tmdbClient, IHttpClientFactory httpClientFactory)
    {
        _tmdb = tmdbClient ?? throw new ArgumentNullException(nameof(tmdbClient));
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    }

    /// <inheritdoc/>
    public string Name => "Phantom Library (TMDB)";

    /// <inheritdoc/>
    public bool Supports(BaseItem item) => item is Movie or Series or Episode;

    /// <inheritdoc/>
    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        => new[] { ImageType.Primary, ImageType.Backdrop, ImageType.Logo };

    /// <inheritdoc/>
    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);

        var tmdbIdRaw = item.GetProviderId(TmdbProviderId);
        if (string.IsNullOrWhiteSpace(tmdbIdRaw)
            || !int.TryParse(tmdbIdRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var tmdbId))
        {
            return Array.Empty<RemoteImageInfo>();
        }

        var config = await _tmdb.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        TmdbImages? images = item switch
        {
            Movie => await _tmdb.GetMovieImagesAsync(tmdbId, item.PreferredMetadataLanguage, cancellationToken).ConfigureAwait(false),
            Series => await _tmdb.GetSeriesImagesAsync(tmdbId, item.PreferredMetadataLanguage, cancellationToken).ConfigureAwait(false),
            Episode => null, // M8 handles per-episode stills; current item has no resolvable Tmdb episode id wired up yet
            _ => null,
        };

        if (images is null)
        {
            return Array.Empty<RemoteImageInfo>();
        }

        var posters = TopN(images.Posters)
            .Select(i => Build(config, i, ImageType.Primary, isPoster: true));
        var backdrops = TopN(images.Backdrops)
            .Select(i => Build(config, i, ImageType.Backdrop, isPoster: false));
        var logos = TopN(images.Logos)
            .Select(i => Build(config, i, ImageType.Logo, isPoster: true));

        return posters.Concat(backdrops).Concat(logos).ToList();
    }

    /// <inheritdoc/>
    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient();
        return client.GetAsync(new Uri(url), cancellationToken);
    }

    private static IEnumerable<TmdbImage> TopN(IEnumerable<TmdbImage> source)
        => source.Where(i => !string.IsNullOrWhiteSpace(i.FilePath))
            .OrderByDescending(i => i.VoteAverage)
            .ThenByDescending(i => i.VoteCount)
            .Take(MaxPerType);

    private static RemoteImageInfo Build(TmdbConfiguration config, TmdbImage img, ImageType type, bool isPoster)
    {
        var fullUrl = isPoster
            ? config.BuildPosterUrl(img.FilePath, "original")
            : config.BuildBackdropUrl(img.FilePath, "original");
        var thumbUrl = isPoster
            ? config.BuildPosterUrl(img.FilePath, "w300")
            : config.BuildBackdropUrl(img.FilePath, "w300");
        return new RemoteImageInfo
        {
            ProviderName = "Phantom Library (TMDB)",
            Url = fullUrl,
            ThumbnailUrl = thumbUrl,
            Type = type,
            Width = img.Width,
            Height = img.Height,
            CommunityRating = img.VoteAverage,
            VoteCount = img.VoteCount,
            Language = img.Iso6391,
        };
    }
}
