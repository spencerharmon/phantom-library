using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Library;
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
/// Wire DTO for <c>POST /Plugins/PhantomLibrary/UserPrefs/{userId}</c> — the
/// admin per-user-preferences sub-page (userPrefsPage.html) posts one of these
/// per user row it edits.
/// </summary>
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
/// <remarks>
/// The admin per-user-preferences endpoints (<c>GET UserPrefs</c> /
/// <c>POST UserPrefs/{userId}</c>) back the userPrefsPage.html sub-page and
/// were re-introduced for the m14 per-user show/hide surface on the
/// channel-arch <c>user_prefs</c> table (schema v12). The calling-user
/// (non-elevated) show/hide + own-prefs endpoints live on
/// <see cref="PhantomLibraryUserController"/>, which is not elevation-gated.
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
    private readonly IFavouriteRecommendationIngestor _recommendationIngestor;

    public PhantomLibraryController(
        IMaterialiser materialiser,
        IMaterialisationQueue queue,
        IGostreamClient gostream,
        IApplicationPaths paths,
        IUserManager userManager,
        PhantomDb db,
        PhantomSourceManager sourceManager,
        IFavouriteRecommendationIngestor recommendationIngestor)
    {
        _materialiser = materialiser;
        _queue = queue;
        _gostream = gostream;
        _paths = paths;
        _userManager = userManager;
        _db = db;
        _sourceManager = sourceManager;
        _recommendationIngestor = recommendationIngestor;
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

    /// <summary>
    /// Manually trigger favourite-style TMDB similar/recommendations
    /// ingestion for a seed title. Mirrors the automatic
    /// UserDataSavedListener path (a user favouriting the title) and is the
    /// admin/rig hook for REQ-M14-RECOMMENDATIONS. New movie rows enqueue
    /// availability probing; new series rows enqueue expansion.
    /// </summary>
    [HttpPost("Recommendations/Ingest")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> IngestRecommendations(
        [FromQuery] int tmdbId,
        [FromQuery] string type,
        CancellationToken ct = default)
    {
        if (tmdbId <= 0)
        {
            return BadRequest(new { code = "invalid_tmdb_id", message = "tmdbId must be a positive integer." });
        }

#pragma warning disable CA1308 // Canonical hidden-item tokens are lowercase ('movie'/'series'), not identifiers used for round-trip display.
        var normalised = type?.Trim().ToLowerInvariant();
#pragma warning restore CA1308
        if (normalised != "movie" && normalised != "series")
        {
            return BadRequest(new { code = "invalid_type", message = "type must be 'movie' or 'series'." });
        }

        var result = await _recommendationIngestor.IngestForFavouriteAsync(tmdbId, normalised, ct).ConfigureAwait(false);
        return Ok(result);
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

    /// <summary>
    /// Returns one preference row per Jellyfin user for the admin sub-page.
    /// Users with no stored row report <see cref="UserPrefs.Defaults"/>.
    /// </summary>
    [HttpGet("UserPrefs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListUserPrefs(CancellationToken ct = default)
    {
        var result = new List<object>();
        foreach (var user in _userManager.GetUsers())
        {
            var prefs = await _db.GetUserPrefsAsync(user.Id, ct).ConfigureAwait(false);
            result.Add(new
            {
                userId = user.Id.ToString("N"),
                userName = user.Username,
                protectFavourites = prefs.ProtectFavourites,
                showPhantoms = prefs.ShowPhantoms,
                allowEager = prefs.AllowEager,
            });
        }

        return Ok(result);
    }

    /// <summary>Upserts the preference row for the given user (admin sub-page).</summary>
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
        if (user is null)
        {
            return NotFound();
        }

        await _db.UpsertUserPrefsAsync(
            userId,
            new UserPrefs(body.ProtectFavourites, body.ShowPhantoms, body.AllowEager),
            ct).ConfigureAwait(false);
        return NoContent();
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
