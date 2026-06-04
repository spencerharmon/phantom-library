using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Common.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PhantomLibrary.Api;

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

    public PhantomLibraryController(
        IMaterialiser materialiser,
        IMaterialisationQueue queue,
        IGostreamClient gostream,
        IApplicationPaths paths)
    {
        _materialiser = materialiser;
        _queue = queue;
        _gostream = gostream;
        _paths = paths;
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
}
