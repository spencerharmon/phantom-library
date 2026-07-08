using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// Unit coverage for <see cref="VaultManager"/> — the bridge from the phantom
/// favourite/eviction lifecycle to gostream's Vault Mode prestage/unprestage
/// endpoints. Uses a real <see cref="PhantomDb"/> (temp sqlite) so the stub-path
/// resolution against <c>materialised_state</c> is exercised end-to-end, and a
/// mocked <see cref="IGostreamClient"/> so the gostream calls are asserted
/// without HTTP. Movie and episode paths are covered at parity.
/// </summary>
public sealed class VaultManagerTests : IDisposable
{
    private readonly string _dbPath;

    public VaultManagerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "phantom-vault-" + Guid.NewGuid().ToString("N") + ".db");
    }

    public void Dispose()
    {
        try
        {
            SqliteConnection.ClearAllPools();
            if (File.Exists(_dbPath))
            {
                File.Delete(_dbPath);
            }
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

    private static (VaultManager sut, Mock<IGostreamClient> gostream) BuildSut(
        PhantomDb db,
        PluginConfiguration? cfg = null,
        bool vaultPresent = true)
    {
        var gostream = new Mock<IGostreamClient>(MockBehavior.Loose);
        gostream.Setup(g => g.IsVaultModePresentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(vaultPresent);
        gostream.Setup(g => g.PrestageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        gostream.Setup(g => g.UnprestageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        cfg ??= new PluginConfiguration { VaultModeEnabled = true, VaultPrestagePriority = 50 };
        var sut = new VaultManager(db, gostream.Object, NullLogger<VaultManager>.Instance, () => cfg);
        return (sut, gostream);
    }

    // ---- prestage: happy path (movie + episode parity) ----

    [Fact]
    public async Task Prestage_MaterialisedMovie_CallsGostreamPrestageWithStubAndPriority()
    {
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(42, "movie", -1, -1, "/stub/m.mkv", "/fuse/m.mkv", CancellationToken.None);
        var (sut, gostream) = BuildSut(db, new PluginConfiguration { VaultModeEnabled = true, VaultPrestagePriority = 77 });

        await sut.PrestageAsync(42, "movie", null, null, CancellationToken.None);

        gostream.Verify(g => g.PrestageAsync("/stub/m.mkv", 77, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Prestage_MaterialisedEpisode_CallsGostreamPrestageWithStubAndPriority()
    {
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(1399, "episode", 1, 2, "/stub/ep.mkv", "/fuse/ep.mkv", CancellationToken.None);
        var (sut, gostream) = BuildSut(db, new PluginConfiguration { VaultModeEnabled = true, VaultPrestagePriority = 50 });

        await sut.PrestageAsync(1399, "episode", 1, 2, CancellationToken.None);

        gostream.Verify(g => g.PrestageAsync("/stub/ep.mkv", 50, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- prestage: gates ----

    [Fact]
    public async Task Prestage_Disabled_NoGostreamContactAtAll()
    {
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(42, "movie", -1, -1, "/stub/m.mkv", "/fuse/m.mkv", CancellationToken.None);
        var (sut, gostream) = BuildSut(db, new PluginConfiguration { VaultModeEnabled = false, VaultPrestagePriority = 50 });

        await sut.PrestageAsync(42, "movie", null, null, CancellationToken.None);

        gostream.Verify(g => g.IsVaultModePresentAsync(It.IsAny<CancellationToken>()), Times.Never);
        gostream.Verify(g => g.PrestageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Prestage_VaultAbsent_NoPrestageCall()
    {
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(42, "movie", -1, -1, "/stub/m.mkv", "/fuse/m.mkv", CancellationToken.None);
        var (sut, gostream) = BuildSut(db, vaultPresent: false);

        await sut.PrestageAsync(42, "movie", null, null, CancellationToken.None);

        gostream.Verify(g => g.PrestageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Prestage_NotMaterialised_NoPrestageCall()
    {
        using var db = await NewDbAsync();
        // No materialised_state row inserted.
        var (sut, gostream) = BuildSut(db);

        await sut.PrestageAsync(999, "movie", null, null, CancellationToken.None);

        gostream.Verify(g => g.PrestageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Prestage_NegativePriority_ClampedToZero()
    {
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(42, "movie", -1, -1, "/stub/m.mkv", "/fuse/m.mkv", CancellationToken.None);
        var (sut, gostream) = BuildSut(db, new PluginConfiguration { VaultModeEnabled = true, VaultPrestagePriority = -5 });

        await sut.PrestageAsync(42, "movie", null, null, CancellationToken.None);

        gostream.Verify(g => g.PrestageAsync("/stub/m.mkv", 0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Prestage_GostreamThrows_Swallowed()
    {
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(42, "movie", -1, -1, "/stub/m.mkv", "/fuse/m.mkv", CancellationToken.None);
        var (sut, gostream) = BuildSut(db);
        gostream.Setup(g => g.PrestageAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GostreamServerException(500, "boom"));

        // Must not throw.
        await sut.PrestageAsync(42, "movie", null, null, CancellationToken.None);

        gostream.Verify(g => g.PrestageAsync("/stub/m.mkv", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- unprestage by tuple (movie + episode parity) ----

    [Fact]
    public async Task Unprestage_MaterialisedMovie_CallsGostreamUnprestageWithStub()
    {
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(42, "movie", -1, -1, "/stub/m.mkv", "/fuse/m.mkv", CancellationToken.None);
        var (sut, gostream) = BuildSut(db);

        await sut.UnprestageAsync(42, "movie", null, null, CancellationToken.None);

        gostream.Verify(g => g.UnprestageAsync("/stub/m.mkv", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Unprestage_MaterialisedEpisode_CallsGostreamUnprestageWithStub()
    {
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(1399, "episode", 1, 2, "/stub/ep.mkv", "/fuse/ep.mkv", CancellationToken.None);
        var (sut, gostream) = BuildSut(db);

        await sut.UnprestageAsync(1399, "episode", 1, 2, CancellationToken.None);

        gostream.Verify(g => g.UnprestageAsync("/stub/ep.mkv", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Unprestage_IgnoresDisabledFlag_StillReleasesFootprint()
    {
        // Asymmetry: unprestage is NOT gated on VaultModeEnabled, so turning
        // Vault Mode off still drains prestaged footprint.
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(42, "movie", -1, -1, "/stub/m.mkv", "/fuse/m.mkv", CancellationToken.None);
        var (sut, gostream) = BuildSut(db, new PluginConfiguration { VaultModeEnabled = false, VaultPrestagePriority = 50 });

        await sut.UnprestageAsync(42, "movie", null, null, CancellationToken.None);

        gostream.Verify(g => g.UnprestageAsync("/stub/m.mkv", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Unprestage_VaultAbsent_NoUnprestageCall()
    {
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(42, "movie", -1, -1, "/stub/m.mkv", "/fuse/m.mkv", CancellationToken.None);
        var (sut, gostream) = BuildSut(db, vaultPresent: false);

        await sut.UnprestageAsync(42, "movie", null, null, CancellationToken.None);

        gostream.Verify(g => g.UnprestageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Unprestage_NotMaterialised_NoUnprestageCall()
    {
        using var db = await NewDbAsync();
        var (sut, gostream) = BuildSut(db);

        await sut.UnprestageAsync(999, "movie", null, null, CancellationToken.None);

        gostream.Verify(g => g.UnprestageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Unprestage_GostreamThrows_Swallowed()
    {
        using var db = await NewDbAsync();
        await db.InsertMaterialisedStateAsync(42, "movie", -1, -1, "/stub/m.mkv", "/fuse/m.mkv", CancellationToken.None);
        var (sut, gostream) = BuildSut(db);
        gostream.Setup(g => g.UnprestageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new GostreamServerException(500, "boom"));

        await sut.UnprestageAsync(42, "movie", null, null, CancellationToken.None);

        gostream.Verify(g => g.UnprestageAsync("/stub/m.mkv", It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---- unprestage by stub path (eviction-sweeper entry point) ----

    [Fact]
    public async Task UnprestageStub_Present_CallsGostream()
    {
        using var db = await NewDbAsync();
        var (sut, gostream) = BuildSut(db);

        await sut.UnprestageStubAsync("/stub/direct.mkv", CancellationToken.None);

        gostream.Verify(g => g.UnprestageAsync("/stub/direct.mkv", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnprestageStub_IgnoresDisabledFlag_StillReleases()
    {
        using var db = await NewDbAsync();
        var (sut, gostream) = BuildSut(db, new PluginConfiguration { VaultModeEnabled = false });

        await sut.UnprestageStubAsync("/stub/direct.mkv", CancellationToken.None);

        gostream.Verify(g => g.UnprestageAsync("/stub/direct.mkv", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UnprestageStub_VaultAbsent_NoCall()
    {
        using var db = await NewDbAsync();
        var (sut, gostream) = BuildSut(db, vaultPresent: false);

        await sut.UnprestageStubAsync("/stub/direct.mkv", CancellationToken.None);

        gostream.Verify(g => g.UnprestageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task UnprestageStub_EmptyStub_NoCall(string stub)
    {
        using var db = await NewDbAsync();
        var (sut, gostream) = BuildSut(db);

        await sut.UnprestageStubAsync(stub, CancellationToken.None);

        gostream.Verify(g => g.IsVaultModePresentAsync(It.IsAny<CancellationToken>()), Times.Never);
        gostream.Verify(g => g.UnprestageAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
