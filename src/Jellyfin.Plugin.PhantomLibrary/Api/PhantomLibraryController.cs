using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
// TODO(stage-4.3): trim per plan §4.3 + 6.x
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PhantomLibrary.Api;

/// <summary>Wire DTO for POST /Plugins/PhantomLibrary/UserPrefs/{userId}.</summary>
public sealed class UserPrefsDto
{
    public bool ProtectFavourites { get; set; }
    public bool ShowPhantoms { get; set; }
    public bool AllowEager { get; set; }
}

/// <summary>
/// Admin-only REST surface for debug / manual operations against the
/// materialisation pipeline.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("Plugins/PhantomLibrary")]
[Produces("application/json")]
public sealed class PhantomLibraryController : ControllerBase
{
    private readonly IMaterialiser _materialiser;
    private readonly IMaterialisationQueue _queue;
    private readonly IGostreamClient _gostream;
    private readonly IApplicationPaths _paths;
    private readonly IUserManager _userManager;
    private readonly PhantomDb _db;

    public PhantomLibraryController(
        IMaterialiser materialiser,
        IMaterialisationQueue queue,
        IGostreamClient gostream,
        IApplicationPaths paths,
        IUserManager userManager,
        PhantomDb db)
    {
        _materialiser = materialiser;
        _queue = queue;
        _gostream = gostream;
        _paths = paths;
        _userManager = userManager;
        _db = db;
    }

    /// <summary>
    /// Serve the kebab-shim JS for browser injection. Public route
    /// (no auth) so the SPA can <script src="…"> it before login.
    /// Returns text/javascript so Firefox+nosniff doesn't reject it
    /// the way it does for application/javascript on the
    /// /web/ConfigurationPage route.
    /// </summary>
    [HttpGet("kebab.js")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult KebabScript()
    {
        var asm = typeof(Plugin).Assembly;
        var name = "Jellyfin.Plugin.PhantomLibrary.Configuration.phantomKebab.js";
        var stream = asm.GetManifestResourceStream(name);
        if (stream is null)
        {
            return NotFound();
        }
        return File(stream, "text/javascript");
    }

    /// <summary>Synchronously materialise an item and return its outcome.</summary>
    [HttpPost("Materialise/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Materialise(
        [FromRoute] Guid itemId,
        [FromQuery] MaterialiseTrigger trigger = MaterialiseTrigger.Manual,
        CancellationToken ct = default)
    {
        var result = await _materialiser.MaterialiseAsync(itemId, trigger, ct).ConfigureAwait(false);
        return result.Status switch
        {
            MaterialisationStatus.Success or MaterialisationStatus.Duplicate => Ok(result),
            MaterialisationStatus.Unavailable => UnprocessableEntity(result),
            _ => StatusCode(StatusCodes.Status500InternalServerError, result),
        };
    }

    /// <summary>Enqueue an item; returns 202.</summary>
    [HttpPost("Queue/{itemId}")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public IActionResult Queue(
        [FromRoute] Guid itemId,
        [FromQuery] MaterialiseTrigger trigger = MaterialiseTrigger.Manual)
    {
        _queue.EnqueueUser(itemId, trigger);
        return Accepted();
    }

    /// <summary>Returns queue + gostream-reachability status for the admin debug page.</summary>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Status(CancellationToken ct = default)
    {
        var reachable = false;
        try
        {
            reachable = await _gostream.ProbeAsync(ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            reachable = false;
        }

        return Ok(new
        {
            pendingUser = _queue.PendingUserCount,
            pendingEager = _queue.PendingEagerCount,
            dbPath = System.IO.Path.Combine(_paths.PluginConfigurationsPath, "PhantomLibrary", "phantom.db"),
            gostreamReachable = reachable,
        });
    }

    /// <summary>Returns per-user preference rows for all Jellyfin users.</summary>
    [HttpGet("UserPrefs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListUserPrefs(CancellationToken ct = default)
    {
        var stored = await _db.ListAllUserPrefsAsync(ct).ConfigureAwait(false);
        var dict = stored.ToDictionary(t => t.UserId, t => t.Prefs);
        var users = _userManager.GetUsers();
        var result = new List<object>();
        foreach (var u in users)
        {
            var prefs = dict.TryGetValue(u.Id, out var p) ? p : UserPrefsRow.Defaults;
            result.Add(new
            {
                userId = u.Id.ToString("N"),
                userName = u.Username,
                protectFavourites = prefs.ProtectFavourites,
                showPhantoms = prefs.ShowPhantoms,
                allowEager = prefs.AllowEager,
            });
        }

        return Ok(result);
    }

    /// <summary>Updates the per-user preference row for the given user.</summary>
    [HttpPost("UserPrefs/{userId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpsertUserPrefs(
        [FromRoute] Guid userId,
        [FromBody] UserPrefsDto body,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        var user = _userManager.GetUserById(userId);
        if (user is null) return NotFound();

        await _db.UpsertUserPrefsAsync(userId, new UserPrefsRow
        {
            ProtectFavourites = body.ProtectFavourites,
            ShowPhantoms = body.ShowPhantoms,
            AllowEager = body.AllowEager,
        }, ct).ConfigureAwait(false);
        return NoContent();
    }
}
