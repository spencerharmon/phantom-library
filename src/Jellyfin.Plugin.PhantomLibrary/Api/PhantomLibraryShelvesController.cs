using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;
using Movie = MediaBrowser.Controller.Entities.Movies.Movie;
using Series = MediaBrowser.Controller.Entities.TV.Series;

namespace Jellyfin.Plugin.PhantomLibrary.Api;

/// <summary>A single item in a Home-screen shelf.</summary>
public sealed class ShelfItem
{
    /// <summary>The navigable Jellyfin item guid (32-hex, no dashes) — the client opens <c>#/details?id=&lt;Id&gt;</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Display name.</summary>
    public required string Name { get; init; }

    /// <summary>Poster image URL (may be null/empty; the shim renders a placeholder).</summary>
    public string? ImageUrl { get; init; }

    /// <summary>"Movie" | "Series" — for a11y / analytics only.</summary>
    public required string Type { get; init; }
}

/// <summary>A titled Home-screen shelf (curated row) with its ordered members.</summary>
public sealed class ShelfRow
{
    /// <summary>Unique row key (<c>{category}::{movie|tv}</c>, e.g. <c>genre_action::movie</c>).</summary>
    public required string Key { get; init; }

    /// <summary>Base curated category key (e.g. <c>available_now</c>, <c>genre_action</c>) — shared by the Movie and TV variants.</summary>
    public required string Category { get; init; }

    /// <summary>Human category title (e.g. "Available now", "Action").</summary>
    public required string Title { get; init; }

    /// <summary>"Movie" or "Series" — the single media type of every member; drives the Home Movies/TV filter toggle.</summary>
    public required string MediaType { get; init; }

    /// <summary>Ordered, navigable members.</summary>
    public required IReadOnlyList<ShelfItem> Items { get; init; }
}

/// <summary>The <c>/Plugins/PhantomLibrary/Shelves</c> payload.</summary>
public sealed class ShelvesResponse
{
    /// <summary>Ordered shelves for the Home screen.</summary>
    public required IReadOnlyList<ShelfRow> Rows { get; init; }
}

/// <summary>
/// home-shelves-web-shim.
///
/// Serves the Netflix-style Home-screen shelves for phantom-library. Two
/// endpoints, mirroring the established kebab.js/badges.js web-shim pattern
/// (Jellyfin 10.11.x exposes CustomCss but no CustomJs, so the fork image injects
/// plugin-served scripts into jellyfin-web/index.html):
///
///   GET /Plugins/PhantomLibrary/shelves.js  — the injected browser shim (no-auth,
///        served as text/javascript from the embedded resource).
///   GET /Plugins/PhantomLibrary/Shelves     — the row data (authenticated): the
///        SAME tested <see cref="CuratedRows"/> categorisation the retired folder
///        view used, over the bounded flat movie + top-level-series lists, with
///        each member resolved to its navigable Jellyfin item guid.
///
/// Guid resolution reuses the derivation verified against 10.11.9's ChannelManager
/// (<c>GetNewItemId(externalId + channelName + "16", typeof(T))</c>; see
/// <see cref="Channels.ChannelIds"/> / PhantomLibraryBadgesController). Every guid
/// is validated with <see cref="ILibraryManager.GetItemById(Guid)"/>; a member
/// whose BaseItem core has not (yet) wrapped is dropped rather than emitted with a
/// non-navigable id — never a placeholder.
/// </summary>
[ApiController]
[Route("Plugins/PhantomLibrary")]
[Produces("application/json")]
public sealed class PhantomLibraryShelvesController : ControllerBase
{
    private const string ShelvesScriptResource = "Jellyfin.Plugin.PhantomLibrary.Configuration.phantomShelves.js";

    // Hard cap on the recent-play-history sample the per-user taste profile is
    // built from — keeps the profile query O(recent), never O(catalogue).
    private const int PlayHistorySampleCap = 250;

    // Display order for well-known rows; genre_* rows sort after these (alpha),
    // leaving_soon last. Unlisted keys fall between the two via a large default.
    private static readonly Dictionary<string, int> RowOrder = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        [CuratedRows.KeyAvailableNow] = 0,
        [CuratedRows.KeyNewReleases] = 1,
        [CuratedRows.KeyTrending] = 2,
        [CuratedRows.KeyPopular] = 3,
        [CuratedRows.KeyForYou] = 4,
        [CuratedRows.KeyLeavingSoon] = 100,
    };

    private readonly PhantomMoviesChannel? _movies;
    private readonly PhantomShowsChannel? _shows;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILogger<PhantomLibraryShelvesController> _logger;

    /// <summary>Initializes a new instance of the <see cref="PhantomLibraryShelvesController"/> class.</summary>
    /// <param name="channels">All registered channels; the phantom movie/show channels are selected out.</param>
    /// <param name="libraryManager">Library manager for channel-item guid derivation + validation.</param>
    /// <param name="userManager">User manager for resolving the acting user's watch history.</param>
    /// <param name="logger">Logger.</param>
    public PhantomLibraryShelvesController(
        IEnumerable<IChannel> channels,
        ILibraryManager libraryManager,
        IUserManager userManager,
        ILogger<PhantomLibraryShelvesController> logger)
    {
        ArgumentNullException.ThrowIfNull(channels);
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        var list = channels as IReadOnlyList<IChannel> ?? channels.ToList();
        _movies = list.OfType<PhantomMoviesChannel>().FirstOrDefault();
        _shows = list.OfType<PhantomShowsChannel>().FirstOrDefault();
    }

    /// <summary>Serves the injected Home-shelves browser shim.</summary>
    /// <returns>The <c>phantomShelves.js</c> script.</returns>
    [HttpGet("shelves.js")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetShelvesScript()
    {
        var stream = typeof(PhantomLibraryShelvesController).Assembly.GetManifestResourceStream(ShelvesScriptResource);
        if (stream is null)
        {
            _logger.LogError("phantomShelves.js embedded resource {Resource} not found", ShelvesScriptResource);
            return NotFound();
        }

        return File(stream, "text/javascript");
    }

    /// <summary>Builds the curated, per-user Home-screen shelves (separate Movie and TV rails).</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The ordered shelves.</returns>
    [HttpGet("Shelves")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<ShelvesResponse>> GetShelves(CancellationToken ct)
    {
        // Per-user visibility (subtracts the user's hidden set); Guid.Empty gives
        // the server-wide visible catalogue, which is the correct fallback when
        // the claim is absent rather than an error.
        _ = TryGetCurrentUserId(out var userId);

        var movieRows = _movies is not null
            ? await _movies.BuildShelfRowsAsync(userId, ct).ConfigureAwait(false)
            : (IReadOnlyList<CuratedRow>)Array.Empty<CuratedRow>();
        var showRows = _shows is not null
            ? await _shows.BuildShelfRowsAsync(userId, ct).ConfigureAwait(false)
            : (IReadOnlyList<CuratedRow>)Array.Empty<CuratedRow>();

        // home-shelves-split-tv-movie: do NOT merge the two channels' rows. Each
        // (category, media-type) pairing becomes its own rail so a category like
        // "Available now" or "Action" yields a separate Movie rail and TV rail.
        var rails = new List<RailCandidate>();

        void Collect(IReadOnlyList<CuratedRow> rows, string mediaType, string typeSlug)
        {
            foreach (var row in rows)
            {
                var items = new List<ShelfItem>();
                foreach (var item in row.Items)
                {
                    var shelfItem = ToShelfItem(item);
                    if (shelfItem is not null)
                    {
                        items.Add(shelfItem);
                    }
                }

                if (items.Count == 0)
                {
                    continue;
                }

                rails.Add(new RailCandidate(
                    Key: row.Key + "::" + typeSlug,
                    Category: row.Key,
                    Title: row.Title,
                    MediaType: mediaType,
                    Items: items));
            }
        }

        Collect(movieRows, "Movie", "movie");
        Collect(showRows, "Series", "tv");

        // home-shelves-per-user-curation: rank + trim the candidate rails to the
        // subset that best matches what this user actually watches (genre
        // affinity × movie/TV share), bounded by config. Cold-start users get a
        // sensible default subset from the same selector.
        var maxRails = Math.Max(1, Plugin.Instance?.Configuration.CuratedHomeMaxRails ?? 14);
        var profile = BuildTasteProfile(userId, ct);
        var selected = HomeRailSelector.Select(rails, profile, maxRails, RowOrder);

        var ordered = selected
            .Select(r => new ShelfRow
            {
                Key = r.Key,
                Category = r.Category,
                Title = r.Title,
                MediaType = r.MediaType,
                Items = r.Items,
            })
            .ToList();

        return Ok(new ShelvesResponse { Rows = ordered });
    }

    /// <summary>
    /// Build the acting user's <see cref="TasteProfile"/> from a BOUNDED recent
    /// play-history query (movies + episodes, capped by <see cref="PlayHistorySampleCap"/>,
    /// most-recently-played first) — never an O(catalogue) scan. Movie genres come
    /// from the item; episode genres are resolved from the parent series (bounded,
    /// cached distinct lookups). Genre weights decay with recency and are
    /// normalized so the top genre = 1.0. Guid.Empty / an unknown user / no
    /// history all yield <see cref="TasteProfile.Empty"/> (cold start).
    /// </summary>
    private TasteProfile BuildTasteProfile(Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty)
        {
            return TasteProfile.Empty;
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return TasteProfile.Empty;
        }

        IReadOnlyList<BaseItem> played;
        try
        {
            played = _libraryManager.GetItemList(new InternalItemsQuery(user)
            {
                IsPlayed = true,
                IncludeItemTypes = new[] { BaseItemKind.Movie, BaseItemKind.Episode },
                Recursive = true,
                Limit = PlayHistorySampleCap,
                OrderBy = new[] { (ItemSortBy.DatePlayed, SortOrder.Descending) },
                EnableTotalRecordCount = false,
            }) ?? (IReadOnlyList<BaseItem>)Array.Empty<BaseItem>();
        }
        catch (Exception ex)
        {
            // A history-query failure must never break the Home screen: fall back
            // to the cold-start default subset rather than erroring the endpoint.
            _logger.LogWarning(ex, "taste-profile play-history query failed for user {UserId}; using cold-start default", userId);
            return TasteProfile.Empty;
        }

        var genreRaw = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var seriesGenreCache = new Dictionary<Guid, string[]>();
        int movieCount = 0;
        int tvCount = 0;
        var n = played.Count;

        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            var item = played[i];

            // Recency decay: most-recent play weighs 1.0, oldest in the sample 0.4.
            var recency = 1.0 - (0.6 * i / Math.Max(1, n - 1));

            string[] genres;
            if (item is Episode episode)
            {
                tvCount++;
                genres = ResolveSeriesGenres(episode, seriesGenreCache);
            }
            else
            {
                movieCount++;
                genres = item.Genres ?? Array.Empty<string>();
            }

            foreach (var genre in genres)
            {
                if (string.IsNullOrWhiteSpace(genre))
                {
                    continue;
                }

                genreRaw[genre] = genreRaw.TryGetValue(genre, out var w) ? w + recency : recency;
            }
        }

        var total = movieCount + tvCount;
        if (total == 0)
        {
            return TasteProfile.Empty;
        }

        var maxWeight = genreRaw.Count > 0 ? genreRaw.Values.Max() : 0d;
        var normalized = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (maxWeight > 0)
        {
            foreach (var kvp in genreRaw)
            {
                normalized[kvp.Key] = kvp.Value / maxWeight;
            }
        }

        var movieShare = (double)movieCount / total;
        return new TasteProfile(normalized, movieShare, 1d - movieShare, total);
    }

    /// <summary>
    /// Resolve an episode's genres from its parent series (episodes rarely carry
    /// their own genres). Bounded + cached by series id so a long play history
    /// costs at most one lookup per distinct watched series.
    /// </summary>
    private string[] ResolveSeriesGenres(Episode episode, Dictionary<Guid, string[]> cache)
    {
        var ownGenres = episode.Genres;
        if (ownGenres is { Length: > 0 })
        {
            return ownGenres;
        }

        var seriesId = episode.SeriesId;
        if (seriesId == Guid.Empty)
        {
            return Array.Empty<string>();
        }

        if (cache.TryGetValue(seriesId, out var cached))
        {
            return cached;
        }

        var genres = _libraryManager.GetItemById(seriesId)?.Genres ?? Array.Empty<string>();
        cache[seriesId] = genres;
        return genres;
    }

    /// <summary>
    /// Map a curated-row channel item to a navigable shelf item, or null when its
    /// Jellyfin guid does not resolve (core has not wrapped it yet / not a leaf).
    /// </summary>
    private ShelfItem? ToShelfItem(ChannelItemInfo item)
    {
        if (item is null || !ChannelItemId.TryParse(item.Id, out var parsed))
        {
            return null;
        }

        Guid guid;
        string type;
        switch (parsed.Kind)
        {
            case ChannelItemId.KindMovie:
                guid = _libraryManager.GetNewItemId(parsed.Encode() + ChannelIds.MoviesName + "16", typeof(Movie));
                type = "Movie";
                break;
            case ChannelItemId.KindSeries:
                guid = _libraryManager.GetNewItemId(parsed.Encode() + ChannelIds.ShowsName + "16", typeof(Series));
                type = "Series";
                break;
            case ChannelItemId.KindEpisode:
                guid = _libraryManager.GetNewItemId(parsed.Encode() + ChannelIds.ShowsName + "16", typeof(Episode));
                type = "Episode";
                break;
            default:
                // season / orphan folders are not shelf cards.
                return null;
        }

        // Only emit ids core has actually wrapped into a BaseItem, so the client's
        // #/details?id=<guid> navigation always resolves. Dropping the rest is
        // preferable to shipping a non-navigable placeholder.
        if (_libraryManager.GetItemById(guid) is null)
        {
            return null;
        }

        return new ShelfItem
        {
            Id = guid.ToString("N", CultureInfo.InvariantCulture),
            Name = item.Name ?? string.Empty,
            ImageUrl = item.ImageUrl,
            Type = type,
        };
    }

    /// <summary>Resolves the acting user from the <c>Jellyfin-UserId</c> claim.</summary>
    private bool TryGetCurrentUserId(out Guid userId)
    {
        userId = Guid.Empty;
        var claim = User?.Claims
            .FirstOrDefault(c => string.Equals(c.Type, "Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        return !string.IsNullOrWhiteSpace(claim) && Guid.TryParse(claim, out userId);
    }
}
