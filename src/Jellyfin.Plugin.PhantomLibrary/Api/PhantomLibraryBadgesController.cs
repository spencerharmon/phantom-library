using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
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
            if (item is null
                || item.SourceType != SourceType.Channel
                || !ChannelIds.IsPhantom(item.ChannelId))
            {
                continue;
            }

            if (!ChannelItemId.TryParse(item.ExternalId, out var parsed))
            {
                continue;
            }

            // Only movie/episode kinds carry materialise state. Series /
            // season folders are always "Phantom" (the folder itself
            // isn't materialised; its episodes are).
            int? tmdbId = parsed.TmdbId;
            string? type = parsed.Kind switch
            {
                ChannelItemId.KindMovie => "movie",
                ChannelItemId.KindEpisode => "episode",
                _ => null,
            };

            if (tmdbId is null || type is null)
            {
                // Series/season/orphan/unknown — surface as Phantom so
                // the UI's badge-applier doesn't error, even though the
                // badge is mostly meaningless on these.
                result[raw] = StatePhantom;
                continue;
            }

            var (sSentinel, eSentinel) = ChannelItemId.ToSentinels(parsed.Season, parsed.Episode);
            string state;
            if (await _db.GetMaterialisedStateAsync(tmdbId.Value, type, sSentinel, eSentinel, ct).ConfigureAwait(false) is not null)
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
