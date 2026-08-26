using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Diagnostics;
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
    private readonly IUserManager _userManager;
    private readonly Func<PluginConfiguration> _configProvider;

    public const string StatePhantom = "Phantom";
    public const string StateMaterialising = "Materialising";
    public const string StateMaterialised = "Materialised";
    public const string StateUnavailable = "Unavailable";

    public PhantomLibraryBadgesController(ILibraryManager libraryManager, PhantomDb db, IUserManager userManager)
        : this(libraryManager, db, userManager, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal PhantomLibraryBadgesController(
        ILibraryManager libraryManager,
        PhantomDb db,
        IUserManager userManager,
        Func<PluginConfiguration> configProvider)
    {
        _libraryManager = libraryManager ?? throw new ArgumentNullException(nameof(libraryManager));
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
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

        var visibility = _configProvider().PhantomBadgeVisibility;
        if (visibility == PhantomBadgeVisibility.Off
            || (visibility == PhantomBadgeVisibility.HideForNonAdmins && !IsRequestAdmin()))
        {
            return Ok(result);
        }

        var requests = new List<(string Raw, Guid Guid)>();
        foreach (var raw in request.Ids)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(raw) && Guid.TryParse(raw, out var guid))
            {
                requests.Add((raw, guid));
            }
        }

        if (requests.Count == 0)
        {
            return Ok(result);
        }

        // P5 baseline: the badge-state batch re-resolve is the plugin-owned
        // cost of a list-view sort/filter change (phantomBadges.js re-polls the
        // freshly visible tile set on every reorder/filter).
        using var flowScope = PhantomFlowMetrics.Time(PhantomFlowMetrics.FlowSortFilter);
        flowScope.ItemCount = requests.Count;

        var resolved = new Dictionary<Guid, (BaseItem? Item, ChannelItemId Parsed)>();
        var unresolved = new HashSet<Guid>(requests.Select(r => r.Guid));

        // Guids that resolve to a concrete, non-channel library BaseItem.
        // These are genuine library content (regular movies/episodes in
        // Continue Watching, Next Up, etc.) and are definitively NOT virtual
        // phantom cards, so they must never enter the computed-channel-id
        // fallback below. Without this, every Home-screen badge poll — which
        // batches real library card ids — forced a full phantom-catalog scan
        // (hundreds of thousands of MD5 id computations), keeping the web
        // loading indicator lit and Continue Watching slow.
        var realLibraryGuids = new HashSet<Guid>();

        foreach (var guid in unresolved.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            var item = _libraryManager.GetItemById(guid);
            if (TryParsePhantomItem(item, out var parsed))
            {
                resolved[guid] = (item, parsed);
                unresolved.Remove(guid);
            }
            else if (item is not null && item.SourceType != SourceType.Channel)
            {
                realLibraryGuids.Add(guid);
            }
        }

        if (unresolved.Count > 0)
        {
            var matches = _libraryManager.GetItemList(new InternalItemsQuery
            {
                ItemIds = unresolved.ToArray(),
                SourceTypes = new[] { SourceType.Channel },
            });

            foreach (var item in matches ?? Array.Empty<BaseItem>())
            {
                ct.ThrowIfCancellationRequested();
                if (!unresolved.Contains(item.Id) || !TryParsePhantomItem(item, out var parsed))
                {
                    continue;
                }

                resolved[item.Id] = (item, parsed);
                unresolved.Remove(item.Id);
            }
        }

        Dictionary<Guid, ChannelItemId>? computedIds = null;

        foreach (var (raw, guid) in requests)
        {
            ct.ThrowIfCancellationRequested();

            BaseItem? item = null;
            ChannelItemId parsed;
            if (resolved.TryGetValue(guid, out var hit))
            {
                item = hit.Item;
                parsed = hit.Parsed;
            }
            else
            {
                if (realLibraryGuids.Contains(guid))
                {
                    // Concrete non-channel library item; not a phantom card.
                    continue;
                }

                computedIds ??= await GetComputedChannelIdMapAsync(ct).ConfigureAwait(false);
                if (!computedIds.TryGetValue(guid, out var computed))
                {
                    continue;
                }

                parsed = computed;
            }

            if (IsExternalGostreamChannelItem(item))
            {
                continue;
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
            else if ((await _db.GetAvailabilityItemAsync(tmdbId.Value, type, sSentinel, eSentinel, ct).ConfigureAwait(false))?.Status == "unavailable")
            {
                state = StateUnavailable;
            }
            else
            {
                state = StatePhantom;
            }

            result[raw] = state;
        }

        return Ok(result);
    }

    private bool IsRequestAdmin()
    {
        var userIdClaim = User?.Claims.FirstOrDefault(c => string.Equals(c.Type, "Jellyfin-UserId", StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
        {
            return false;
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return false;
        }

        var permissionsProperty = user.GetType().GetProperty("Permissions", BindingFlags.Instance | BindingFlags.Public);
        if (permissionsProperty?.GetValue(user) is not System.Collections.IEnumerable permissions)
        {
            return false;
        }

        foreach (var permission in permissions)
        {
            if (permission is null)
            {
                continue;
            }

            var kind = permission.GetType().GetProperty("Kind", BindingFlags.Instance | BindingFlags.Public)?.GetValue(permission)?.ToString();
            var value = permission.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public)?.GetValue(permission);
            if (string.Equals(kind, "IsAdministrator", StringComparison.OrdinalIgnoreCase)
                && value is bool isAdmin
                && isAdmin)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsExternalGostreamChannelItem(BaseItem? item)
    {
        return item is not null
            && item.Tags.Contains("external", StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsRealGostreamChannelItem(BaseItem? item)
    {
        if (item is null)
        {
            return false;
        }

        if (item.Tags.Contains("phantom", StringComparer.OrdinalIgnoreCase)
            || item.Tags.Contains("external", StringComparer.OrdinalIgnoreCase)
            || item.Tags.Contains("orphan", StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        var path = item.Path ?? string.Empty;
        var cfg = Plugin.Instance?.Configuration;
        var movieRoot = cfg?.GostreamMoviesRoot ?? "/var/gostream/gostream-mkv-virtual/movies";
        var showRoot = cfg?.GostreamShowsRoot ?? "/var/gostream/gostream-mkv-virtual/tv";
        return (path.StartsWith(movieRoot.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(showRoot.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            && (path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".m4v", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase));
    }

    private static bool TryParsePhantomItem(BaseItem? item, out ChannelItemId parsed)
    {
        if (item is not null
            && ChannelIds.IsPhantom(item.ChannelId)
            && ChannelItemId.TryParse(item.ExternalId, out parsed))
        {
            return true;
        }

        parsed = null!;
        return false;
    }

    // The computed-channel-id map enumerates the entire visible phantom
    // catalogue (every visible movie + every display episode of every visible
    // series) and MD5-hashes each into its deterministic BaseItem guid. On the
    // operator's data this is ~540k rows / hashes. It only changes when the
    // catalogue's membership changes (refresh tasks, materialise), not on
    // per-item state transitions (which are computed live from the DB on every
    // request), so it is safe to cache across requests for a short TTL. The
    // semaphore collapses concurrent badge polls onto a single rebuild instead
    // of letting each request kick off its own full-catalogue scan.
    private static readonly TimeSpan ComputedMapTtl = TimeSpan.FromSeconds(60);
    private static readonly SemaphoreSlim ComputedMapGate = new(1, 1);
    private static Dictionary<Guid, ChannelItemId>? _computedMapCache;
    private static DateTimeOffset _computedMapBuiltAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Test hook: drops the cached computed-channel-id map so a unit test can
    /// force a cold rebuild deterministically. Not used in production.
    /// </summary>
    internal static void ResetComputedChannelIdMapCacheForTests()
    {
        _computedMapCache = null;
        _computedMapBuiltAt = DateTimeOffset.MinValue;
    }

    private async Task<Dictionary<Guid, ChannelItemId>> GetComputedChannelIdMapAsync(CancellationToken ct)
    {
        var cached = _computedMapCache;
        if (cached is not null && DateTimeOffset.UtcNow - _computedMapBuiltAt < ComputedMapTtl)
        {
            return cached;
        }

        await ComputedMapGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            cached = _computedMapCache;
            if (cached is not null && DateTimeOffset.UtcNow - _computedMapBuiltAt < ComputedMapTtl)
            {
                return cached;
            }

            var fresh = await BuildComputedChannelIdMapAsync(ct).ConfigureAwait(false);
            _computedMapCache = fresh;
            _computedMapBuiltAt = DateTimeOffset.UtcNow;
            return fresh;
        }
        finally
        {
            ComputedMapGate.Release();
        }
    }

    /// <summary>
    /// p6-search-list-surface-split: the computed-id fallback map must cover
    /// every movie/episode the search/BaseItem path can surface, not only
    /// what the browse LIST shows — otherwise an unavailable/unknown phantom
    /// reached via global search (or a list-hidden series' own season-detail
    /// grid) would resolve to no badge state at all here. Movies use the
    /// full unfiltered catalogue; episodes use the flat (series-visibility-
    /// agnostic) available/materialised id set — <c>ListVisibleEpisodeIdsAsync</c>
    /// deliberately does not gate on <c>SeriesMinAvailableEpisodes</c>, unlike
    /// the browse LIST's per-series <c>ListDisplayEpisodeIdsForVisibleSeriesAsync</c>.
    /// </summary>
    private async Task<Dictionary<Guid, ChannelItemId>> BuildComputedChannelIdMapAsync(CancellationToken ct)
    {
        var result = new Dictionary<Guid, ChannelItemId>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var row in await _db.ListAllMovieRowsAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var id = ChannelItemId.ForMovie(row.Metadata.TmdbId);
            if (seen.Add(id.Encode()))
            {
                result.TryAdd(ComputeMovieGuid(id), id);
            }
        }

        foreach (var row in await _db.ListMaterialisedStateAsync("movie", ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var id = ChannelItemId.ForMovie(row.TmdbId);
            if (seen.Add(id.Encode()))
            {
                result.TryAdd(ComputeMovieGuid(id), id);
            }
        }

        foreach (var row in await _db.ListVisibleEpisodeIdsAsync(ct).ConfigureAwait(false))
        {
            ct.ThrowIfCancellationRequested();
            var id = ChannelItemId.ForEpisode(row.SeriesTmdbId, row.Season, row.Episode);
            if (seen.Add(id.Encode()))
            {
                result.TryAdd(ComputeEpisodeGuid(id), id);
            }
        }

        return result;
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
