using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;
using Movie = MediaBrowser.Controller.Entities.Movies.Movie;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PhantomLibrary.Api;

/// <summary>
/// Request payload for the bulk-state lookup endpoint.
/// </summary>
public sealed class PhantomLibraryStatesRequest
{
    public System.Collections.Generic.List<string>? Ids { get; set; }
}

/// <summary>
/// User-facing (non-elevated) endpoints for the Phantom Library plugin.
///
/// <list type="bullet">
///   <item><c>GET /Plugins/PhantomLibrary/badges.js</c> serves the
///   browser-injected badge-overlay JS (anonymous so the SPA can load
///   it before login).</item>
///   <item><c>POST /Plugins/PhantomLibrary/States</c> resolves a batch
///   of channel-bound BaseItem ids into their current badge state
///   ("Phantom", "Materialising", "Materialised"). Items that aren't
///   phantom-channel-bound are omitted from the response.</item>
/// </list>
///
/// Plan §4.3.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/PhantomLibrary")]
[Produces("application/json")]
public sealed class PhantomLibraryBadgesController : ControllerBase
{
    private readonly ILibraryManager _libraryManager;
    private readonly PhantomDb _db;

    public const string StatePhantom = "Phantom";
    public const string StateMaterialising = "Materialising";
    public const string StateMaterialised = "Materialised";

    public PhantomLibraryBadgesController(ILibraryManager libraryManager, PhantomDb db)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>
    /// Resolve a batch of BaseItem ids to badge states.
    /// </summary>
    [HttpPost("States")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> States([FromBody] PhantomLibraryStatesRequest? request, CancellationToken ct)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (request?.Ids is null || request.Ids.Count == 0)
        {
            return Ok(result);
        }

        foreach (var raw in request.Ids)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(raw) || !Guid.TryParse(raw, out var guid))
            {
                continue;
            }

            var item = _libraryManager.GetItemById(guid);
            if (item is null)
            {
                var matches = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    ItemIds = new[] { guid },
                    SourceTypes = new[] { SourceType.Channel },
                });
                item = matches.Count > 0 ? matches[0] : null;
            }

            if (item is null)
            {
                var phantomItems = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    ChannelIds = new[] { ChannelIds.Movies, ChannelIds.Shows },
                    SourceTypes = new[] { SourceType.Channel },
                });
                foreach (var candidate in phantomItems)
                {
                    if (candidate.Id == guid)
                    {
                        item = candidate;
                        break;
                    }
                }
            }
            ChannelItemId parsed;
            if (item is not null && ChannelIds.IsPhantom(item.ChannelId) && ChannelItemId.TryParse(item.ExternalId, out parsed))
            {
                // resolved through Jellyfin library manager
            }
            else
            {
                var computed = await TryResolveByComputedChannelIdAsync(guid, ct).ConfigureAwait(false);
                if (computed is null)
                {
                    continue;
                }

                parsed = computed;
            }

            // Only movie/episode kinds carry materialise state. Series /
            // season folders are navigation containers; omit them so the
            // browser badge overlay never stamps a badge onto a series or
            // season thumbnail.
            int? tmdbId = parsed.TmdbId;
            string? type = parsed.Kind switch
            {
                ChannelItemId.KindMovie => "movie",
                ChannelItemId.KindEpisode => "episode",
                _ => null,
            };

            if (tmdbId is null || type is null)
            {
                continue;
            }

            var (sSentinel, eSentinel) = ChannelItemId.ToSentinels(parsed.Season, parsed.Episode);
            string state;
            if (await _db.GetMaterialisedStateAsync(tmdbId.Value, type, sSentinel, eSentinel, ct).ConfigureAwait(false) is not null)
            {
                state = StateMaterialised;
            }
            else if (IsRealGostreamChannelItem(item))
            {
                state = StateMaterialised;
            }
            else if (await _db.IsMaterialiseInFlightAsync(tmdbId.Value, type, sSentinel, eSentinel, ct).ConfigureAwait(false))
            {
                state = StateMaterialising;
            }
            else
            {
                state = StatePhantom;
            }

            result[raw] = state;
        }

        return Ok(result);
    }

    private static bool IsRealGostreamChannelItem(BaseItem? item)
    {
        if (item is null)
        {
            return false;
        }

        if (item.Tags.Contains("phantom", StringComparer.OrdinalIgnoreCase)
            || item.Tags.Contains("orphan", StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = item.Path ?? string.Empty;
        return path.Contains("/gostream", StringComparison.OrdinalIgnoreCase)
            && (path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ChannelItemId?> TryResolveByComputedChannelIdAsync(Guid requestedId, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in await _db.ListDiscoveryCacheAsync("movie", ct).ConfigureAwait(false))
        {
            var id = ChannelItemId.ForMovie(row.TmdbId);
            if (seen.Add(id.Encode()) && ComputeMovieGuid(id) == requestedId)
            {
                return id;
            }
        }

        foreach (var row in await _db.ListMaterialisedStateAsync("movie", ct).ConfigureAwait(false))
        {
            var id = ChannelItemId.ForMovie(row.TmdbId);
            if (seen.Add(id.Encode()) && ComputeMovieGuid(id) == requestedId)
            {
                return id;
            }
        }

        foreach (var row in await _db.ListMaterialisedStateAsync("episode", ct).ConfigureAwait(false))
        {
            var id = ChannelItemId.ForEpisode(row.TmdbId, row.Season, row.Episode);
            if (seen.Add(id.Encode()) && ComputeEpisodeGuid(id) == requestedId)
            {
                return id;
            }
        }

        return null;
    }

    private Guid ComputeMovieGuid(ChannelItemId id)
        => _libraryManager.GetNewItemId(id.Encode() + ChannelIds.MoviesName + "16", typeof(Movie));

    private Guid ComputeEpisodeGuid(ChannelItemId id)
        => _libraryManager.GetNewItemId(id.Encode() + ChannelIds.ShowsName + "16", typeof(Episode));

    /// <summary>
    /// Serve the badge-overlay JS for browser injection. Public route
    /// (no auth) so the SPA can <c>&lt;script src="\u2026"&gt;</c> load it
    /// before login completes.
    /// </summary>
    [HttpGet("badges.js")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult BadgesScript()
    {
        var asm = typeof(Plugin).Assembly;
        var name = "Jellyfin.Plugin.PhantomLibrary.Configuration.phantomBadges.js";
        var stream = asm.GetManifestResourceStream(name);
        if (stream is null)
        {
            return NotFound();
        }

        return File(stream, "text/javascript");
    }
}
