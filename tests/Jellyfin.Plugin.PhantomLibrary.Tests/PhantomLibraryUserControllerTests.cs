using System;
using System.Collections;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Api;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// Covers the calling-user (non-elevated) surface: a user reads/edits their OWN
/// prefs and hides/unhides catalogue titles for themselves. Identity is the
/// <c>Jellyfin-UserId</c> claim; there is no route/body user parameter, so the
/// isolation tests prove one user can never touch another's state.
///
/// Movie/TV parity: every hidden-item operation is exercised for BOTH
/// <c>movie</c> and <c>series</c> (a TV episode/season is hidden via its parent
/// series TMDB id, mapped client-side).
/// </summary>
public class PhantomLibraryUserControllerTests : IDisposable
{
    private readonly string _dbPath;

    public PhantomLibraryUserControllerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "phantom-user-" + Guid.NewGuid().ToString("N") + ".db");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_dbPath)) File.Delete(_dbPath);
        }
        catch
        {
            // best-effort temp cleanup
        }

        GC.SuppressFinalize(this);
    }

    private async Task<PhantomDb> NewDbAsync()
    {
        var db = new PhantomDb(_dbPath);
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        return db;
    }

    private static PhantomLibraryUserController MakeController(PhantomDb db, Guid? userId, ChannelStateProvider? state = null)
    {
        var ctrl = new PhantomLibraryUserController(db, state ?? new ChannelStateProvider(db));

        // userId == null models an authenticated request that is somehow
        // missing the Jellyfin-UserId claim → every action must 401.
        var identity = userId is null
            ? new ClaimsIdentity()
            : new ClaimsIdentity(
                new[] { new Claim("Jellyfin-UserId", userId.Value.ToString()) }, "test");

        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
        return ctrl;
    }

    private static T Prop<T>(IActionResult result, string name)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        var prop = ok.Value!.GetType().GetProperty(name);
        Assert.NotNull(prop);
        return (T)prop!.GetValue(ok.Value)!;
    }

    // ---- authentication gate --------------------------------------------

    [Fact]
    public async Task EveryEndpoint_WithoutUserClaim_Returns401()
    {
        using var db = await NewDbAsync();
        var ctrl = MakeController(db, userId: null);

        Assert.IsType<UnauthorizedResult>(await ctrl.GetMyPrefs(CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(
            await ctrl.SetMyPrefs(new UserPrefsDto(), CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await ctrl.ListHidden(CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(
            await ctrl.GetHiddenState("movie", 550, CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await ctrl.Hide("movie", 550, CancellationToken.None));
        Assert.IsType<UnauthorizedResult>(await ctrl.Unhide("movie", 550, CancellationToken.None));
    }

    // ---- own preferences -------------------------------------------------

    [Fact]
    public async Task GetMyPrefs_Unset_ReturnsDefaults()
    {
        using var db = await NewDbAsync();
        var ctrl = MakeController(db, Guid.NewGuid());

        var res = await ctrl.GetMyPrefs(CancellationToken.None);

        Assert.True(Prop<bool>(res, "protectFavourites"));
        Assert.True(Prop<bool>(res, "showPhantoms"));
        Assert.True(Prop<bool>(res, "allowEager"));
    }

    [Fact]
    public async Task SetMyPrefs_ThenGet_RoundTripsAndPersistsForCallingUser()
    {
        using var db = await NewDbAsync();
        var userId = Guid.NewGuid();
        var ctrl = MakeController(db, userId);

        var set = await ctrl.SetMyPrefs(
            new UserPrefsDto { ProtectFavourites = false, ShowPhantoms = false, AllowEager = true },
            CancellationToken.None);
        Assert.IsType<NoContentResult>(set);

        var res = await ctrl.GetMyPrefs(CancellationToken.None);
        Assert.False(Prop<bool>(res, "protectFavourites"));
        Assert.False(Prop<bool>(res, "showPhantoms"));
        Assert.True(Prop<bool>(res, "allowEager"));

        // Persisted against the claim's user id, not a body/route parameter.
        var stored = await db.GetUserPrefsAsync(userId, CancellationToken.None);
        Assert.False(stored.ProtectFavourites);
        Assert.False(stored.ShowPhantoms);
        Assert.True(stored.AllowEager);
    }

    // ---- hide / unhide: movie -------------------------------------------

    [Fact]
    public async Task HideUnhide_Movie_FullLifecycleForCallingUser()
    {
        using var db = await NewDbAsync();
        var userId = Guid.NewGuid();
        var ctrl = MakeController(db, userId);

        Assert.False(Prop<bool>(await ctrl.GetHiddenState("movie", 550, CancellationToken.None), "hidden"));

        Assert.IsType<NoContentResult>(await ctrl.Hide("movie", 550, CancellationToken.None));
        Assert.True(Prop<bool>(await ctrl.GetHiddenState("movie", 550, CancellationToken.None), "hidden"));

        var list = await ctrl.ListHidden(CancellationToken.None);
        Assert.Contains(EnumerateHidden(list), r => r.tmdbId == 550 && r.type == "movie");

        // Idempotent hide.
        Assert.IsType<NoContentResult>(await ctrl.Hide("movie", 550, CancellationToken.None));

        Assert.IsType<NoContentResult>(await ctrl.Unhide("movie", 550, CancellationToken.None));
        Assert.False(Prop<bool>(await ctrl.GetHiddenState("movie", 550, CancellationToken.None), "hidden"));

        // Idempotent unhide.
        Assert.IsType<NoContentResult>(await ctrl.Unhide("movie", 550, CancellationToken.None));
    }

    // ---- hide / unhide: series (TV parity) ------------------------------

    [Fact]
    public async Task HideUnhide_Series_FullLifecycleForCallingUser()
    {
        using var db = await NewDbAsync();
        var userId = Guid.NewGuid();
        var ctrl = MakeController(db, userId);

        Assert.False(Prop<bool>(await ctrl.GetHiddenState("series", 1399, CancellationToken.None), "hidden"));

        Assert.IsType<NoContentResult>(await ctrl.Hide("series", 1399, CancellationToken.None));
        Assert.True(Prop<bool>(await ctrl.GetHiddenState("series", 1399, CancellationToken.None), "hidden"));

        var list = await ctrl.ListHidden(CancellationToken.None);
        Assert.Contains(EnumerateHidden(list), r => r.tmdbId == 1399 && r.type == "series");

        Assert.IsType<NoContentResult>(await ctrl.Unhide("series", 1399, CancellationToken.None));
        Assert.False(Prop<bool>(await ctrl.GetHiddenState("series", 1399, CancellationToken.None), "hidden"));
    }

    [Fact]
    public async Task Hide_MovieAndSeries_AreIndependentTypes()
    {
        using var db = await NewDbAsync();
        var userId = Guid.NewGuid();
        var ctrl = MakeController(db, userId);

        // Same TMDB number under the two different types must not collide.
        await ctrl.Hide("movie", 777, CancellationToken.None);

        Assert.True(Prop<bool>(await ctrl.GetHiddenState("movie", 777, CancellationToken.None), "hidden"));
        Assert.False(Prop<bool>(await ctrl.GetHiddenState("series", 777, CancellationToken.None), "hidden"));
    }

    // ---- channel cache invalidation (REQ-M14-PER-USER Surface 3) --------

    [Fact]
    public async Task Hide_Movie_BumpsMoviesDataVersionOnly()
    {
        using var db = await NewDbAsync();
        var state = new ChannelStateProvider(db);
        var ctrl = MakeController(db, Guid.NewGuid(), state);
        var moviesBefore = state.DataVersion(ChannelStateProvider.KindMovies);
        var showsBefore = state.DataVersion(ChannelStateProvider.KindShows);

        Assert.IsType<NoContentResult>(await ctrl.Hide("movie", 550, CancellationToken.None));

        Assert.NotEqual(moviesBefore, state.DataVersion(ChannelStateProvider.KindMovies));
        Assert.Equal(showsBefore, state.DataVersion(ChannelStateProvider.KindShows));
    }

    [Fact]
    public async Task Hide_Series_BumpsShowsDataVersionOnly()
    {
        using var db = await NewDbAsync();
        var state = new ChannelStateProvider(db);
        var ctrl = MakeController(db, Guid.NewGuid(), state);
        var moviesBefore = state.DataVersion(ChannelStateProvider.KindMovies);
        var showsBefore = state.DataVersion(ChannelStateProvider.KindShows);

        Assert.IsType<NoContentResult>(await ctrl.Hide("series", 1399, CancellationToken.None));

        Assert.Equal(moviesBefore, state.DataVersion(ChannelStateProvider.KindMovies));
        Assert.NotEqual(showsBefore, state.DataVersion(ChannelStateProvider.KindShows));
    }

    [Fact]
    public async Task Unhide_Movie_AlsoBumpsMoviesDataVersion()
    {
        using var db = await NewDbAsync();
        var state = new ChannelStateProvider(db);
        var ctrl = MakeController(db, Guid.NewGuid(), state);
        await ctrl.Hide("movie", 550, CancellationToken.None);
        var afterHide = state.DataVersion(ChannelStateProvider.KindMovies);
        await Task.Delay(5, CancellationToken.None); // BumpDataVersion's tie-breaker is unix-milliseconds.

        Assert.IsType<NoContentResult>(await ctrl.Unhide("movie", 550, CancellationToken.None));

        Assert.NotEqual(afterHide, state.DataVersion(ChannelStateProvider.KindMovies));
    }

    // ---- validation ------------------------------------------------------

    [Theory]
    [InlineData("episode")]
    [InlineData("season")]
    [InlineData("garbage")]
    [InlineData("")]
    public async Task BadType_Returns400_OnEveryTypedEndpoint(string type)
    {
        using var db = await NewDbAsync();
        var ctrl = MakeController(db, Guid.NewGuid());

        Assert.IsType<BadRequestObjectResult>(await ctrl.GetHiddenState(type, 5, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await ctrl.Hide(type, 5, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await ctrl.Unhide(type, 5, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task NonPositiveTmdbId_Returns400(int tmdbId)
    {
        using var db = await NewDbAsync();
        var ctrl = MakeController(db, Guid.NewGuid());

        Assert.IsType<BadRequestObjectResult>(await ctrl.GetHiddenState("movie", tmdbId, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await ctrl.Hide("series", tmdbId, CancellationToken.None));
        Assert.IsType<BadRequestObjectResult>(await ctrl.Unhide("movie", tmdbId, CancellationToken.None));
    }

    [Fact]
    public async Task TypeMatching_IsCaseInsensitive()
    {
        using var db = await NewDbAsync();
        var ctrl = MakeController(db, Guid.NewGuid());

        Assert.IsType<NoContentResult>(await ctrl.Hide("MOVIE", 42, CancellationToken.None));
        // Stored canonical is lowercase; a lowercase read sees it.
        Assert.True(Prop<bool>(await ctrl.GetHiddenState("movie", 42, CancellationToken.None), "hidden"));
    }

    // ---- per-user isolation ---------------------------------------------

    [Fact]
    public async Task HiddenState_IsIsolatedPerUser()
    {
        using var db = await NewDbAsync();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();

        await MakeController(db, alice).Hide("movie", 603, CancellationToken.None);
        await MakeController(db, alice).Hide("series", 1396, CancellationToken.None);

        // Bob sees neither of Alice's hides and has an empty list.
        var bobCtrl = MakeController(db, bob);
        Assert.False(Prop<bool>(await bobCtrl.GetHiddenState("movie", 603, CancellationToken.None), "hidden"));
        Assert.False(Prop<bool>(await bobCtrl.GetHiddenState("series", 1396, CancellationToken.None), "hidden"));

        var bobList = await bobCtrl.ListHidden(CancellationToken.None);
        Assert.Empty(EnumerateHidden(bobList));

        // Alice still sees both.
        var aliceList = await MakeController(db, alice).ListHidden(CancellationToken.None);
        Assert.Equal(2, CountHidden(aliceList));
    }

    // ---- helpers ---------------------------------------------------------

    private static IEnumerable<(int tmdbId, string type)> EnumerateHidden(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        Assert.NotNull(ok.Value);
        foreach (var row in (IEnumerable)ok.Value!)
        {
            var t = row.GetType();
            var tmdb = (int)t.GetProperty("tmdbId")!.GetValue(row)!;
            var type = (string)t.GetProperty("type")!.GetValue(row)!;
            yield return (tmdb, type);
        }
    }

    private static int CountHidden(IActionResult result)
    {
        var n = 0;
        foreach (var _ in EnumerateHidden(result))
        {
            n++;
        }

        return n;
    }
}
