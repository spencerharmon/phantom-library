using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PhantomLibrary.Api;

/// <summary>
/// Calling-user (non-elevated) surface for the Phantom Library plugin: a user
/// reads and edits their OWN preferences and hides/unhides catalogue titles for
/// themselves.
///
/// <para>
/// This controller is deliberately split from the admin, elevation-gated
/// <see cref="PhantomLibraryController"/>: hiding a title and toggling one's own
/// prefs are ordinary-user actions, so the class carries a plain
/// <c>[Authorize]</c> (any authenticated user) exactly like
/// <see cref="PhantomLibraryBadgesController"/>. The acting user is resolved from
/// the <c>Jellyfin-UserId</c> claim Jellyfin stamps on every authenticated
/// request — never from a route/body parameter — so one user can never read or
/// mutate another user's state.
/// </para>
///
/// <para>
/// Hiding is title-level and movie/TV symmetric: <c>type</c> is <c>movie</c> or
/// <c>series</c>. A TV episode or season is hidden iff its parent series is, so
/// the client maps every phantom TV item (series/season/episode) to its series
/// TMDB id + <c>series</c>, and every phantom movie to its movie TMDB id +
/// <c>movie</c>.
/// </para>
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/PhantomLibrary")]
[Produces("application/json")]
public sealed class PhantomLibraryUserController : ControllerBase
{
    private readonly PhantomDb _db;

    public PhantomLibraryUserController(PhantomDb db)
    {
        _db = db;
    }

    /// <summary>Returns the calling user's Phantom preferences (defaults if unset).</summary>
    [HttpGet("User/Prefs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetMyPrefs(CancellationToken ct = default)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized();
        }

        var prefs = await _db.GetUserPrefsAsync(userId, ct).ConfigureAwait(false);
        return Ok(new
        {
            protectFavourites = prefs.ProtectFavourites,
            showPhantoms = prefs.ShowPhantoms,
            allowEager = prefs.AllowEager,
        });
    }

    /// <summary>Upserts the calling user's Phantom preferences.</summary>
    [HttpPost("User/Prefs")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SetMyPrefs(
        [FromBody] UserPrefsDto body,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized();
        }

        await _db.UpsertUserPrefsAsync(
            userId,
            new UserPrefs(body.ProtectFavourites, body.ShowPhantoms, body.AllowEager),
            ct).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Lists the titles the calling user has hidden.</summary>
    [HttpGet("User/Hidden")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListHidden(CancellationToken ct = default)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized();
        }

        var rows = await _db.ListHiddenItemsAsync(userId, ct).ConfigureAwait(false);
        return Ok(rows.Select(r => new
        {
            tmdbId = r.TmdbId,
            type = r.Type,
            hiddenAt = r.HiddenAt,
        }));
    }

    /// <summary>Reports whether the given title is hidden for the calling user.</summary>
    [HttpGet("User/Hidden/{type}/{tmdbId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetHiddenState(
        [FromRoute] string type,
        [FromRoute] int tmdbId,
        CancellationToken ct = default)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized();
        }

        if (!TryNormaliseType(type, out var canonical) || tmdbId <= 0)
        {
            return BadRequest(new { code = "invalid_item", message = "type must be 'movie' or 'series' and tmdbId a positive integer." });
        }

        var hidden = await _db.IsItemHiddenAsync(userId, tmdbId, canonical, ct).ConfigureAwait(false);
        return Ok(new { tmdbId, type = canonical, hidden });
    }

    /// <summary>Hides the given title for the calling user (idempotent).</summary>
    [HttpPost("User/Hidden/{type}/{tmdbId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Hide(
        [FromRoute] string type,
        [FromRoute] int tmdbId,
        CancellationToken ct = default)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized();
        }

        if (!TryNormaliseType(type, out var canonical) || tmdbId <= 0)
        {
            return BadRequest(new { code = "invalid_item", message = "type must be 'movie' or 'series' and tmdbId a positive integer." });
        }

        await _db.AddHiddenItemAsync(userId, tmdbId, canonical, ct).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Unhides the given title for the calling user (idempotent).</summary>
    [HttpDelete("User/Hidden/{type}/{tmdbId:int}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Unhide(
        [FromRoute] string type,
        [FromRoute] int tmdbId,
        CancellationToken ct = default)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized();
        }

        if (!TryNormaliseType(type, out var canonical) || tmdbId <= 0)
        {
            return BadRequest(new { code = "invalid_item", message = "type must be 'movie' or 'series' and tmdbId a positive integer." });
        }

        await _db.RemoveHiddenItemAsync(userId, tmdbId, canonical, ct).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Resolves the acting user from the <c>Jellyfin-UserId</c> claim Jellyfin
    /// stamps on authenticated requests. Mirrors
    /// <see cref="PhantomLibraryBadgesController"/> so both user-facing
    /// controllers key off the same identity.
    /// </summary>
    private bool TryGetCurrentUserId(out Guid userId)
    {
        userId = Guid.Empty;
        var claim = User?.Claims
            .FirstOrDefault(c => string.Equals(c.Type, "Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        return !string.IsNullOrWhiteSpace(claim) && Guid.TryParse(claim, out userId);
    }

    /// <summary>
    /// Accepts <c>movie</c>/<c>series</c> case-insensitively and emits the
    /// canonical lowercase token the <c>user_hidden_items.type</c> CHECK
    /// constraint stores; rejects anything else so a bad client fails with 400
    /// rather than a backend 500.
    /// </summary>
    private static bool TryNormaliseType(string? type, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(type))
        {
            return false;
        }

        var trimmed = type.Trim();
        if (string.Equals(trimmed, "movie", StringComparison.OrdinalIgnoreCase))
        {
            canonical = "movie";
            return true;
        }

        if (string.Equals(trimmed, "series", StringComparison.OrdinalIgnoreCase))
        {
            canonical = "series";
            return true;
        }

        return false;
    }
}
