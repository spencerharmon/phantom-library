using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Playback;
using MediaBrowser.Controller.Session;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PhantomLibrary.Api;

/// <summary>
/// p14-player-stats-overlay (ROI P14): live source + stream-health visibility in
/// the Jellyfin web/mobile player.
///
/// <list type="bullet">
///   <item><c>GET /Plugins/PhantomLibrary/playerstats.js</c> — the injected,
///   toggleable overlay shim (anonymous, like badges.js/shelves.js; also
///   bootstrapped by badges.js so existing index.html injections pick it up).</item>
///   <item><c>GET /Plugins/PhantomLibrary/PlayerStats/Current</c> — what the
///   overlay polls (2–5 s, only while the player is open). Resolves the CALLER's
///   own playback session (user + device claims) to its now-playing item, then
///   returns that item's materialised source + live gostream stats.</item>
///   <item><c>GET /Plugins/PhantomLibrary/PlayerStats/{itemId}</c> — per-item /
///   per-playback-session form. A non-admin may only read an item one of their
///   own sessions is currently playing (optionally pinned to
///   <c>playSessionId</c>/<c>mediaSourceId</c>); admins/API keys may read any.</item>
/// </list>
///
/// gostream is called server-side with the plugin's library token; nothing in
/// the response carries the token, the gostream URL, or stub/FUSE paths.
/// </summary>
[ApiController]
[Route("Plugins/PhantomLibrary")]
[Produces("application/json")]
public sealed class PhantomLibraryPlayerStatsController : ControllerBase
{
    internal const string ScriptResource = "Jellyfin.Plugin.PhantomLibrary.Configuration.phantomPlayerStats.js";

    // Jellyfin.Api's claim names (InternalClaimTypes / UserRoles); the plugin
    // does not reference Jellyfin.Api, so they are pinned here.
    private const string UserIdClaim = "Jellyfin-UserId";
    private const string DeviceIdClaim = "Jellyfin-DeviceId";
    private const string AdministratorRole = "Administrator";

    private readonly PlayerStatsService _stats;
    private readonly ISessionManager _sessions;

    /// <summary>Initializes a new instance of the <see cref="PhantomLibraryPlayerStatsController"/> class.</summary>
    public PhantomLibraryPlayerStatsController(PlayerStatsService stats, ISessionManager sessions)
    {
        _stats = stats ?? throw new ArgumentNullException(nameof(stats));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    /// <summary>Serves the player stats overlay shim.</summary>
    [HttpGet("playerstats.js")]
    [AllowAnonymous]
    [Produces("text/javascript")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult PlayerStatsScript()
    {
        var stream = typeof(Plugin).Assembly.GetManifestResourceStream(ScriptResource);
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-cache";
        return File(stream, "text/javascript");
    }

    /// <summary>Stats for whatever the caller's own session is playing right now.</summary>
    [HttpGet("PlayerStats/Current")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<PlayerStatsResponse>> Current(CancellationToken ct = default)
    {
        NoStore();
        if (!TryGetUserId(out var userId))
        {
            return Unauthorized();
        }

        var deviceId = Claim(DeviceIdClaim);
        var session = PlayingSessionsOf(userId)
            .OrderByDescending(s => string.Equals(s.DeviceId, deviceId, StringComparison.Ordinal))
            .ThenByDescending(s => s.LastActivityDate)
            .FirstOrDefault(s => string.IsNullOrEmpty(deviceId) || string.Equals(s.DeviceId, deviceId, StringComparison.Ordinal));
        var itemId = session is null ? Guid.Empty : NowPlayingId(session);
        if (session is null || itemId == Guid.Empty)
        {
            return Ok(PlayerStatsService.NotPlaying());
        }

        return Ok(await _stats.GetAsync(itemId, ContextOf(session, playSessionId: null), ct).ConfigureAwait(false));
    }

    /// <summary>Stats for one item (optionally one playback session of it).</summary>
    [HttpGet("PlayerStats/{itemId}")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PlayerStatsResponse>> ForItem(
        [FromRoute] Guid itemId,
        [FromQuery] string? playSessionId = null,
        [FromQuery] string? mediaSourceId = null,
        CancellationToken ct = default)
    {
        NoStore();
        if (!TryGetUserId(out var userId) && !IsAdmin())
        {
            return Unauthorized();
        }

        var deviceId = Claim(DeviceIdClaim);
        var session = PlayingSessionsOf(userId)
            .Where(s => NowPlayingId(s) == itemId)
            .Where(s => string.IsNullOrEmpty(mediaSourceId)
                || string.Equals(s.PlayState?.MediaSourceId, mediaSourceId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => string.Equals(s.DeviceId, deviceId, StringComparison.Ordinal))
            .ThenByDescending(s => s.LastActivityDate)
            .FirstOrDefault();
        if (session is null && !IsAdmin())
        {
            // Source/stream internals are visible to the user who is watching it,
            // not to every authenticated account.
            return StatusCode(StatusCodes.Status403Forbidden, PlayerStatsService.NotPlaying() with
            {
                ItemId = itemId.ToString("N", System.Globalization.CultureInfo.InvariantCulture),
            });
        }

        var playback = session is null
            ? new PlayerPlaybackContext(false, playSessionId, mediaSourceId, null, null)
            : ContextOf(session, playSessionId);
        var result = await _stats.GetAsync(itemId, playback, ct).ConfigureAwait(false);
        return result.Status == PlayerStatsStatus.ItemNotFound ? NotFound(result) : Ok(result);
    }

    private SessionInfo[] PlayingSessionsOf(Guid userId)
    {
        if (userId == Guid.Empty)
        {
            return Array.Empty<SessionInfo>();
        }

        return (_sessions.Sessions ?? Array.Empty<SessionInfo>())
            .Where(s => s is not null && s.UserId == userId && NowPlayingId(s) != Guid.Empty)
            .ToArray();
    }

    private static Guid NowPlayingId(SessionInfo s)
        => s.FullNowPlayingItem?.Id is { } full && full != Guid.Empty ? full : s.NowPlayingItem?.Id ?? Guid.Empty;

    private static PlayerPlaybackContext ContextOf(SessionInfo s, string? playSessionId)
        => new(
            true,
            playSessionId,
            s.PlayState?.MediaSourceId,
            s.PlayState?.IsPaused,
            s.PlayState?.PositionTicks);

    private void NoStore() => Response.Headers.CacheControl = "no-store";

    private string? Claim(string type)
        => User?.Claims.FirstOrDefault(c => string.Equals(c.Type, type, StringComparison.OrdinalIgnoreCase))?.Value;

    private bool TryGetUserId(out Guid userId)
    {
        userId = Guid.Empty;
        return Guid.TryParse(Claim(UserIdClaim), out userId) && userId != Guid.Empty;
    }

    private bool IsAdmin() => User?.IsInRole(AdministratorRole) ?? false;
}
