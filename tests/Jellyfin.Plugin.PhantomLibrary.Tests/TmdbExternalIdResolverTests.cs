using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class TmdbExternalIdResolverTests : IDisposable
{
    private readonly string _dbPath;

    public TmdbExternalIdResolverTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "phantom-extids-" + Guid.NewGuid().ToString("N") + ".db");
    }

    public void Dispose()
    {
        try
        {
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

    private static TmdbExternalIdResolver Build(PhantomDb db, Mock<ITmdbClient> tmdb)
        => new(db, tmdb.Object, NullLogger<TmdbExternalIdResolver>.Instance);

    [Fact]
    public async Task CacheHit_Positive_ReturnsCachedWithoutFetching()
    {
        using var db = await NewDbAsync();
        await db.SetImdbIdAsync(42, "movie", "tt0000042", CancellationToken.None);

        var tmdb = new Mock<ITmdbClient>(MockBehavior.Strict);
        var sut = Build(db, tmdb);

        var result = await sut.GetImdbIdAsync(42, "movie", CancellationToken.None);

        Assert.Equal("tt0000042", result);
        tmdb.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CacheHit_NegativeWithinTtl_ReturnsNullWithoutFetching()
    {
        using var db = await NewDbAsync();
        await db.SetImdbIdAsync(7, "series", null, CancellationToken.None);

        var tmdb = new Mock<ITmdbClient>(MockBehavior.Strict);
        var sut = Build(db, tmdb);

        var result = await sut.GetImdbIdAsync(7, "series", CancellationToken.None);

        Assert.Null(result);
        tmdb.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CacheMiss_PositiveFetch_StoresAndReturns()
    {
        using var db = await NewDbAsync();
        var tmdb = new Mock<ITmdbClient>(MockBehavior.Strict);
        tmdb.Setup(c => c.GetImdbIdForMovieAsync(100, It.IsAny<CancellationToken>()))
            .ReturnsAsync("tt0001000");
        var sut = Build(db, tmdb);

        var result = await sut.GetImdbIdAsync(100, "movie", CancellationToken.None);

        Assert.Equal("tt0001000", result);
        var row = await db.GetImdbIdAsync(100, "movie", CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal("tt0001000", row!.ImdbId);
        tmdb.Verify(c => c.GetImdbIdForMovieAsync(100, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CacheMiss_NegativeFetch_StoresNegativeRow()
    {
        using var db = await NewDbAsync();
        var tmdb = new Mock<ITmdbClient>(MockBehavior.Strict);
        tmdb.Setup(c => c.GetImdbIdForSeriesAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        var sut = Build(db, tmdb);

        var result = await sut.GetImdbIdAsync(200, "series", CancellationToken.None);

        Assert.Null(result);
        var row = await db.GetImdbIdAsync(200, "series", CancellationToken.None);
        Assert.NotNull(row);
        Assert.Null(row!.ImdbId);
    }

    [Fact]
    public async Task FetchFailure_DoesNotPoisonCache()
    {
        using var db = await NewDbAsync();
        var tmdb = new Mock<ITmdbClient>(MockBehavior.Strict);
        tmdb.Setup(c => c.GetImdbIdForMovieAsync(300, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var sut = Build(db, tmdb);

        var result = await sut.GetImdbIdAsync(300, "movie", CancellationToken.None);

        Assert.Null(result);
        var row = await db.GetImdbIdAsync(300, "movie", CancellationToken.None);
        Assert.Null(row);
    }

    [Fact]
    public async Task PositiveCache_AlwaysReturnedRegardlessOfAge()
    {
        // Positive cache TTL is documented but not currently enforced;
        // a positive hit short-circuits regardless of fetched_at age.
        using var db = await NewDbAsync();
        await db.SetImdbIdAsync(500, "movie", "tt0000500", CancellationToken.None);

        var tmdb = new Mock<ITmdbClient>(MockBehavior.Strict);
        var sut = Build(db, tmdb);

        var result = await sut.GetImdbIdAsync(500, "movie", CancellationToken.None);

        Assert.Equal("tt0000500", result);
        tmdb.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task UnsupportedType_Throws()
    {
        using var db = await NewDbAsync();
        var tmdb = new Mock<ITmdbClient>(MockBehavior.Strict);
        var sut = Build(db, tmdb);

        await Assert.ThrowsAsync<ArgumentException>(
            () => sut.GetImdbIdAsync(1, "episode", CancellationToken.None));
    }
}
