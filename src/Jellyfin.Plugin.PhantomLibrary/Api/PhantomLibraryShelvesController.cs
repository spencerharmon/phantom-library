using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
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
    /// <summary>Stable row key (e.g. <c>available_now</c>, <c>genre_action</c>).</summary>
    public required string Key { get; init; }

    /// <summary>Human title (e.g. "Available now").</summary>
    public required string Title { get; init; }

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

    // Display order for well-known rows; genre_* rows sort after these (alpha),
    // leaving_soon last. Unlisted keys fall between the two via a large default.
    private static readonly IReadOnlyDictionary<string, int> RowOrder = new Dictionary<string, int>(StringComparer.Ordinal)
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
    private readonly ILogger<PhantomLibraryShelvesController> _logger;

    /// <summary>Initializes a new instance of the <see cref="PhantomLibraryShelvesController"/> class.</summary>
    /// <param name="channels">All registered channels; the phantom movie/show channels are selected out.</param>
    /// <param name="libraryManager">Library manager for channel-item guid derivation + validation.</param>
    /// <param name="logger">Logger.</param>
    public PhantomLibraryShelvesController(
        IEnumerable<IChannel> channels,
        ILibraryManager libraryManager,
        ILogger<PhantomLibraryShelvesController> logger)
    {
        ArgumentNullException.ThrowIfNull(channels);
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
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

    /// <summary>Builds the curated Home-screen shelves (movies + shows, merged by row).</summary>
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

        // Merge movie + show rows sharing a key into one shelf (movies first,
        // then shows, preserving each channel's relevance order). The title comes
        // from whichever channel produced the row first.
        var merged = new Dictionary<string, (string Title, List<ShelfItem> Items)>(StringComparer.Ordinal);
        var keyOrder = new List<string>();

        void Absorb(IReadOnlyList<CuratedRow> rows)
        {
            foreach (var row in rows)
            {
                if (!merged.TryGetValue(row.Key, out var bucket))
                {
                    bucket = (row.Title, new List<ShelfItem>());
                    merged[row.Key] = bucket;
                    keyOrder.Add(row.Key);
                }

                foreach (var item in row.Items)
                {
                    var shelfItem = ToShelfItem(item);
                    if (shelfItem is not null)
                    {
                        bucket.Items.Add(shelfItem);
                    }
                }
            }
        }

        Absorb(movieRows);
        Absorb(showRows);

        var ordered = keyOrder
            .Where(k => merged[k].Items.Count > 0)
            .OrderBy(k => RowOrder.TryGetValue(k, out var o) ? o : 50)
            .ThenBy(k => k, StringComparer.Ordinal)
            .Select(k => new ShelfRow
            {
                Key = k,
                Title = merged[k].Title,
                Items = merged[k].Items,
            })
            .ToList();

        return Ok(new ShelvesResponse { Rows = ordered });
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
