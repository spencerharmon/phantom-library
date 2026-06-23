using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PhantomLibrary.Api;

/// <summary>
/// Admin-only REST surface for debug / manual operations against the
/// materialisation pipeline.
/// </summary>
/// <remarks>
/// The per-user-preferences endpoints were removed in Stage 2.2; the
/// underlying <c>user_prefs</c> table is gone with the file-on-disk
/// architecture. Channel-arch may re-introduce per-user controls in a
/// later stage.
/// </remarks>
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
    private readonly PhantomSourceManager _sourceManager;
    private readonly ILibraryManager _libraryManager;

    public PhantomLibraryController(
        IMaterialiser materialiser,
        IMaterialisationQueue queue,
        IGostreamClient gostream,
        IApplicationPaths paths,
        IUserManager userManager,
        PhantomDb db,
        PhantomSourceManager sourceManager,
        ILibraryManager libraryManager)
    {
        _materialiser = materialiser;
        _queue = queue;
        _gostream = gostream;
        _paths = paths;
        _userManager = userManager;
        _db = db;
        _sourceManager = sourceManager;
        _libraryManager = libraryManager;
    }

    /// <summary>
    /// Serve the kebab-shim JS for browser injection. Public route
    /// (no auth) so the SPA can <script src="…"> it before login.
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

    [HttpGet("Items/ResolveExternalId/{itemId}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult ResolveExternalId([FromRoute] Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null || string.IsNullOrWhiteSpace(item.ExternalId))
        {
            return NotFound(new { code = "not_found" });
        }

        return Ok(new { externalId = item.ExternalId });
    }

    [HttpGet("Items/{externalId}/Sources")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Sources([FromRoute] string externalId, CancellationToken ct = default)
    {
        var response = await _sourceManager.GetSourcesAsync(externalId, ct).ConfigureAwait(false);
        return response is null ? NotFound(new { code = "not_found" }) : Ok(response);
    }

    [HttpPost("Items/{externalId}/Sources/RejectCurrent")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> RejectCurrent([FromRoute] string externalId, CancellationToken ct = default)
    {
        var result = await _sourceManager.RejectCurrentAsync(externalId, ct).ConfigureAwait(false);
        return ToActionResult(result);
    }

    [HttpPost("Items/{externalId}/Sources/MaterialiseCandidate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> MaterialiseCandidate(
        [FromRoute] string externalId,
        [FromBody] PhantomMaterialiseCandidateRequest? request,
        CancellationToken ct = default)
    {
        var result = await _sourceManager.MaterialiseCandidateAsync(externalId, request, ct).ConfigureAwait(false);
        return ToActionResult(result);
    }

    /// <summary>Returns queue + gostream-reachability status for the admin debug page.</summary>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Status(CancellationToken ct = default)
    {
        _ = _userManager;
        _ = _db;
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

    private ObjectResult ToActionResult(PhantomSourceOperationResult result)
        => result.Status switch
        {
            PhantomSourceOperationStatus.Success => Ok(result),
            PhantomSourceOperationStatus.NotFound or PhantomSourceOperationStatus.CandidateNotFound => NotFound(result),
            PhantomSourceOperationStatus.NoCurrent or PhantomSourceOperationStatus.InFlight => Conflict(result),
            PhantomSourceOperationStatus.NoAlternate => UnprocessableEntity(result),
            _ => StatusCode(StatusCodes.Status500InternalServerError, result),
        };
}
