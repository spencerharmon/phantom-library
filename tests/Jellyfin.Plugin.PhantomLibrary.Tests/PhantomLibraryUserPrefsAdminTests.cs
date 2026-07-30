using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.PhantomLibrary.Api;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// Covers the admin (elevation-gated) per-user preference endpoints on
/// <see cref="PhantomLibraryController"/> that back the restored
/// <c>userPrefsPage.html</c> sub-page: <c>GET UserPrefs</c> (one row per Jellyfin
/// user, defaults when unset) and <c>POST UserPrefs/{userId}</c> (upsert, 404 for
/// an unknown user).
///
/// <para>
/// Preferences are global per-user toggles, not per-title, so there is no
/// movie/TV distinction to exercise here — the movie/TV show-hide parity lives in
/// <see cref="PhantomLibraryUserControllerTests"/>.
/// </para>
/// </summary>
public class PhantomLibraryUserPrefsAdminTests : IDisposable
{
    private readonly string _dbPath;

    public PhantomLibraryUserPrefsAdminTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "phantom-adminprefs-" + Guid.NewGuid().ToString("N") + ".db");
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
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

    // The UserPrefs endpoints touch only _userManager and _db; the other
    // constructor dependencies are never dereferenced on these paths, so they
    // are passed as null to keep the fixture focused (mirrors the intent of the
    // fuller BuildController in PhantomLibrarySourceControllerTests without
    // dragging in the whole materialisation graph).
    private static PhantomLibraryController MakeController(IUserManager users, PhantomDb db)
        => new(
            materialiser: null!,
            queue: null!,
            gostream: null!,
            paths: null!,
            userManager: users,
            db: db,
            sourceManager: null!,
            recommendationIngestor: null!,
            libraryManager: null!);

    private static User MakeUser(string name)
        => new(name, "auth-provider", "reset-provider") { Id = Guid.NewGuid() };

    private static IList AsList(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsAssignableFrom<IList>(ok.Value);
    }

    private static T Row<T>(object row, string name)
    {
        var prop = row.GetType().GetProperty(name);
        Assert.NotNull(prop);
        return (T)prop!.GetValue(row)!;
    }

    private static void AssertRow(object row, string userName, bool protect, bool show, bool eager)
    {
        Assert.Equal(userName, Row<string>(row, "userName"));
        Assert.Equal(protect, Row<bool>(row, "protectFavourites"));
        Assert.Equal(show, Row<bool>(row, "showPhantoms"));
        Assert.Equal(eager, Row<bool>(row, "allowEager"));
    }

    [Fact]
    public async Task ListUserPrefs_ReturnsOneRowPerUser_DefaultsWhenUnset()
    {
        using var db = await NewDbAsync();
        var alice = MakeUser("alice");
        var bob = MakeUser("bob");
        var users = new Mock<IUserManager>(MockBehavior.Loose);
        users.Setup(u => u.GetUsers()).Returns(new[] { alice, bob });

        var ctrl = MakeController(users.Object, db);
        var rows = AsList(await ctrl.ListUserPrefs(CancellationToken.None));

        // ListUserPrefs iterates GetUsers() in order, so the rows are
        // deterministic: exactly one per user, each carrying all-on defaults
        // (UserPrefs.Defaults) because nothing was stored.
        Assert.Collection(
            rows.Cast<object>(),
            row => AssertRow(row, "alice", protect: true, show: true, eager: true),
            row => AssertRow(row, "bob", protect: true, show: true, eager: true));
    }

    [Fact]
    public async Task ListUserPrefs_ReflectsStoredNonDefaultPrefs()
    {
        using var db = await NewDbAsync();
        var alice = MakeUser("alice");
        await db.UpsertUserPrefsAsync(alice.Id, new UserPrefs(false, false, false), CancellationToken.None);

        var users = new Mock<IUserManager>(MockBehavior.Loose);
        users.Setup(u => u.GetUsers()).Returns(new[] { alice });

        var ctrl = MakeController(users.Object, db);
        var rows = AsList(await ctrl.ListUserPrefs(CancellationToken.None));

        var row = Assert.Single(rows.Cast<object>());
        Assert.Equal(alice.Id.ToString("N"), Row<string>(row, "userId"));
        Assert.False(Row<bool>(row, "protectFavourites"));
        Assert.False(Row<bool>(row, "showPhantoms"));
        Assert.False(Row<bool>(row, "allowEager"));
    }

    [Fact]
    public async Task UpsertUserPrefs_KnownUser_Persists204()
    {
        using var db = await NewDbAsync();
        var alice = MakeUser("alice");
        var users = new Mock<IUserManager>(MockBehavior.Loose);
        users.Setup(u => u.GetUserById(alice.Id)).Returns(alice);

        var ctrl = MakeController(users.Object, db);
        var res = await ctrl.UpsertUserPrefs(
            alice.Id,
            new UserPrefsDto { ProtectFavourites = false, ShowPhantoms = true, AllowEager = false },
            CancellationToken.None);

        Assert.IsType<NoContentResult>(res);

        var stored = await db.GetUserPrefsAsync(alice.Id, CancellationToken.None);
        Assert.False(stored.ProtectFavourites);
        Assert.True(stored.ShowPhantoms);
        Assert.False(stored.AllowEager);
    }

    [Fact]
    public async Task UpsertUserPrefs_UnknownUser_Returns404()
    {
        using var db = await NewDbAsync();
        var users = new Mock<IUserManager>(MockBehavior.Loose);
        users.Setup(u => u.GetUserById(It.IsAny<Guid>())).Returns((User?)null);

        var ctrl = MakeController(users.Object, db);
        var res = await ctrl.UpsertUserPrefs(
            Guid.NewGuid(),
            new UserPrefsDto { ProtectFavourites = true, ShowPhantoms = true, AllowEager = true },
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(res);
    }
}
