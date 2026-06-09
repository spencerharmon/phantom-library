using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PhantomLibrary.Api;

/// <summary>Wire DTO for POST /Plugins/PhantomLibrary/States.</summary>
public sealed class StatesRequestDto
{
    /// <summary>
    /// Jellyfin item GUIDs to look up. Accepts both 32-hex-no-dash
    /// ("N") and 8-4-4-4-12 ("D") forms; any other format is silently
    /// dropped.
    /// </summary>
    public IReadOnlyList<string>? Ids { get; set; }
}

/// <summary>
/// User-facing (non-elevated) endpoints for the Phantom Library plugin.
/// Kept in a separate controller from <see cref="PhantomLibraryController"/>
/// because that controller is gated behind <c>RequiresElevation</c> and
/// these endpoints must be reachable by ordinary logged-in users (the
/// badge overlay is rendered for everyone, not just admins). Uses the
/// bare <c>[Authorize]</c> attribute (no named policy) which resolves to
/// the host's <c>DefaultPolicy</c> — in Jellyfin 10.11 that is wired
/// to <c>DefaultAuthorizationRequirement</c> ("any logged-in user").
/// Naming the policy explicitly (e.g. <c>"DefaultAuthorization"</c>)
/// would fail at request time: Jellyfin only registers a fixed set of
/// named policies (RequiresElevation, FirstTimeSetupOr*, etc.) and an
/// unknown name causes the authorization middleware to throw before
/// <c>[AllowAnonymous]</c> can short-circuit, 500-ing even the
/// anonymous <c>badges.js</c> route.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/PhantomLibrary")]
[Produces("application/json")]
public sealed class PhantomLibraryBadgesController : ControllerBase
{
    private readonly PhantomDb _db;

    public PhantomLibraryBadgesController(PhantomDb db)
    {
        _db = db;
    }

    /// <summary>
    /// Serve the badge-overlay JS for browser injection. Public route
    /// (no auth) so the SPA can <c>&lt;script src="…"&gt;</c> load it
    /// before login completes. Mirrors the kebab.js serving pattern in
    /// <see cref="PhantomLibraryController.KebabScript"/>.
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

    /// <summary>
    /// Bulk lookup of phantom state for a set of Jellyfin item GUIDs.
    /// Response is a flat object keyed by the 32-hex-no-dash form of
    /// each input GUID that is present in the plugin's phantom_items
    /// table; GUIDs absent from the table (i.e. regular library items
    /// or gostream-direct items not registered as phantoms) are omitted.
    /// Values are the <see cref="PhantomItemState"/> enum name:
    /// "Phantom", "Virtual", "Materialised", or "Unavailable".
    /// </summary>
    [HttpPost("States")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> States(
        [FromBody] StatesRequestDto body,
        CancellationToken ct)
    {
        if (body is null || body.Ids is null)
        {
            return BadRequest(new { error = "ids required" });
        }

        // Parse + dedupe input GUIDs. Tolerate both "N" and "D" forms;
        // silently drop malformed strings rather than 400-ing the whole
        // request (the client may scoop up data-id attributes from
        // unrelated DOM nodes that happen to have non-GUID ids).
        var parsed = new HashSet<Guid>();
        foreach (var raw in body.Ids)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            if (Guid.TryParse(raw, out var g))
            {
                parsed.Add(g);
            }
        }

        var states = await _db.GetStatesAsync(parsed, ct).ConfigureAwait(false);

        // Emit as { "<guid32>": "<State>", ... } — the JS client keys
        // its cache by the 32-hex form, so we normalise here.
        var result = new Dictionary<string, string>(states.Count, StringComparer.Ordinal);
        foreach (var (guid, state) in states)
        {
            result[guid.ToString("N")] = state.ToString();
        }
        return Ok(result);
    }
}
