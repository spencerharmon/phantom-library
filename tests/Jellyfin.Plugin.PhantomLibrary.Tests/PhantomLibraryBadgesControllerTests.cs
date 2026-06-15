using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Api;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class PhantomLibraryBadgesControllerTests : IDisposable
{
    private readonly string _dbPath;

    public PhantomLibraryBadgesControllerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "phantom-badges-" + Guid.NewGuid().ToString("N") + ".db");

        // Video.SourceType consults a static recordings manager; stub it.
        var rec = new Mock<IRecordingsManager>(MockBehavior.Loose);
        rec.Setup(r => r.GetActiveRecordingInfo(It.IsAny<string>())).Returns((ActiveRecordingInfo?)null);
        Video.RecordingsManager = rec.Object;
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
            // best-effort
        }
    }

    private async Task<PhantomDb> NewDbAsync()
    {
        var db = new PhantomDb(_dbPath);
        await db.SetMetaAsync("__init__", "1", CancellationToken.None);
        return db;
    }

    private static Movie MakePhantomMovie(Guid id, int tmdb)
        => new()
        {
            Id = id,
            Name = "Test",
            ExternalId = "movie_" + tmdb,
            ChannelId = ChannelIds.Movies,
        };

    private static Dictionary<string, string> Cast(IActionResult r)
    {
        var ok = Assert.IsType<OkObjectResult>(r);
        return Assert.IsType<Dictionary<string, string>>(ok.Value);
    }

    [Fact]
    public async Task NoIds_ReturnsEmptyDict()
    {
        using var db = await NewDbAsync();
        var lib = new Mock<ILibraryManager>(MockBehavior.Loose);
        var ctrl = new PhantomLibraryBadgesController(lib.Object, db);
        var res = await ctrl.States(new PhantomLibraryStatesRequest(), CancellationToken.None);
        Assert.Empty(Cast(res));
    }

    [Fact]
    public async Task NonChannelItem_OmittedFromResponse()
    {
        using var db = await NewDbAsync();
        var id = Guid.NewGuid();
        var nonChannel = new Movie { Id = id, Name = "regular" }; // ChannelId stays empty → SourceType = Library
        var lib = new Mock<ILibraryManager>(MockBehavior.Loose);
        lib.Setup(l => l.GetItemById(id)).Returns(nonChannel);

        var ctrl = new PhantomLibraryBadgesController(lib.Object, db);
        var res = await ctrl.States(new PhantomLibraryStatesRequest { Ids = new() { id.ToString() } }, CancellationToken.None);
        Assert.Empty(Cast(res));
    }

    [Fact]
    public async Task PhantomMovie_NoState_NoInFlight_ReturnsPhantom()
    {
        using var db = await NewDbAsync();
        var id = Guid.NewGuid();
        var item = MakePhantomMovie(id, 1);
        var lib = new Mock<ILibraryManager>(MockBehavior.Loose);
        lib.Setup(l => l.GetItemById(id)).Returns(item);

        var ctrl = new PhantomLibraryBadgesController(lib.Object, db);
        var res = await ctrl.States(new PhantomLibraryStatesRequest { Ids = new() { id.ToString() } }, CancellationToken.None);
        var dict = Cast(res);
        Assert.Equal("Phantom", dict[id.ToString()]);
    }

    [Fact]
    public async Task PhantomMovie_InFlightRowPresent_ReturnsMaterialising()
    {
        using var db = await NewDbAsync();
        var id = Guid.NewGuid();
        var item = MakePhantomMovie(id, 2);
        var lib = new Mock<ILibraryManager>(MockBehavior.Loose);
        lib.Setup(l => l.GetItemById(id)).Returns(item);
        await db.UpsertMaterialiseInFlightAsync(2, "movie", -1, -1, CancellationToken.None);

        var ctrl = new PhantomLibraryBadgesController(lib.Object, db);
        var res = await ctrl.States(new PhantomLibraryStatesRequest { Ids = new() { id.ToString() } }, CancellationToken.None);
        Assert.Equal("Materialising", Cast(res)[id.ToString()]);
    }

    [Fact]
    public async Task PhantomMovie_MaterialisedStatePresent_ReturnsMaterialised()
    {
        using var db = await NewDbAsync();
        var id = Guid.NewGuid();
        var item = MakePhantomMovie(id, 3);
        var lib = new Mock<ILibraryManager>(MockBehavior.Loose);
        lib.Setup(l => l.GetItemById(id)).Returns(item);
        await db.InsertMaterialisedStateAsync(3, "movie", -1, -1, "/stub", "/fuse", CancellationToken.None);

        var ctrl = new PhantomLibraryBadgesController(lib.Object, db);
        var res = await ctrl.States(new PhantomLibraryStatesRequest { Ids = new() { id.ToString() } }, CancellationToken.None);
        Assert.Equal("Materialised", Cast(res)[id.ToString()]);
    }

    [Fact]
    public async Task Precedence_MaterialisedWinsOverInFlight()
    {
        using var db = await NewDbAsync();
        var id = Guid.NewGuid();
        var item = MakePhantomMovie(id, 4);
        var lib = new Mock<ILibraryManager>(MockBehavior.Loose);
        lib.Setup(l => l.GetItemById(id)).Returns(item);
        await db.InsertMaterialisedStateAsync(4, "movie", -1, -1, "/stub", "/fuse", CancellationToken.None);
        await db.UpsertMaterialiseInFlightAsync(4, "movie", -1, -1, CancellationToken.None);

        var ctrl = new PhantomLibraryBadgesController(lib.Object, db);
        var res = await ctrl.States(new PhantomLibraryStatesRequest { Ids = new() { id.ToString() } }, CancellationToken.None);
        Assert.Equal("Materialised", Cast(res)[id.ToString()]);
    }

    [Fact]
    public async Task UnparseableExternalId_Omitted()
    {
        using var db = await NewDbAsync();
        var id = Guid.NewGuid();
        var item = new Movie { Id = id, ExternalId = "garbage", ChannelId = ChannelIds.Movies, Name = "x" };
        var lib = new Mock<ILibraryManager>(MockBehavior.Loose);
        lib.Setup(l => l.GetItemById(id)).Returns(item);

        var ctrl = new PhantomLibraryBadgesController(lib.Object, db);
        var res = await ctrl.States(new PhantomLibraryStatesRequest { Ids = new() { id.ToString() } }, CancellationToken.None);
        Assert.Empty(Cast(res));
    }

    [Fact]
    public async Task MalformedGuid_Ignored()
    {
        using var db = await NewDbAsync();
        var lib = new Mock<ILibraryManager>(MockBehavior.Loose);
        var ctrl = new PhantomLibraryBadgesController(lib.Object, db);
        var res = await ctrl.States(new PhantomLibraryStatesRequest { Ids = new() { "not-a-guid", "" } }, CancellationToken.None);
        Assert.Empty(Cast(res));
    }
}
