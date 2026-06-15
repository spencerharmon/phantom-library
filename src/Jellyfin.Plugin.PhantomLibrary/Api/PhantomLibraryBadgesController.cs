using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PhantomLibrary.Api;

/// <summary>
/// User-facing (non-elevated) endpoints for the Phantom Library plugin.
/// Currently only serves the badge-overlay JS asset; the
/// <c>POST /States</c> bulk-state lookup was removed in Stage 2.2 along
/// with the file-on-disk <c>phantom_items</c> table. Channel-arch badge
/// state will be re-introduced in Stage 4.3, sourced from
/// <c>materialised_state</c> + <c>materialise_in_flight</c>.
/// </summary>
[ApiController]
[Authorize]
[Route("Plugins/PhantomLibrary")]
[Produces("application/json")]
public sealed class PhantomLibraryBadgesController : ControllerBase
{
    /// <summary>
    /// Serve the badge-overlay JS for browser injection. Public route
    /// (no auth) so the SPA can <c>&lt;script src="…"&gt;</c> load it
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
