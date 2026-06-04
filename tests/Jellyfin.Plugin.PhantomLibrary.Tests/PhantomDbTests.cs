using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.State;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class PhantomDbTests : IDisposable
{
    private readonly string _path;
    private readonly PhantomDb _db;

    public PhantomDbTests()
    {
        _path = Path.Combine(Path.GetTempPath(), "phantomdb_" + Guid.NewGuid().ToString("N") + ".db");
        _db = new PhantomDb(_path);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_path); File.Delete(_path + "-wal"); File.Delete(_path + "-shm"); } catch { }
    }

    [Fact]
    public async Task Magnet_RoundTrip()
    {
        var key = new MagnetCacheKey(123, "tt1", "movie", null, null, "GostreamDefault");
        var e = new MagnetCacheEntry
        {
            Magnet = "magnet:?xt=urn:btih:ABC",
            InfoHash = "ABC",
            Size = 999,
            Seeders = 42,
            Indexer = "Prowlarr",
            CachedAt = DateTimeOffset.UtcNow,
            Ttl = TimeSpan.FromDays(7),
            Source = "user",
        };
        await _db.PutCachedMagnetAsync(key, e, default);
        var got = await _db.GetCachedMagnetAsync(key, default);
        Assert.NotNull(got);
        Assert.Equal("ABC", got!.InfoHash);
        Assert.Equal(42, got.Seeders);
    }

    [Fact]
    public async Task Unavailable_Marker_Expires()
    {
        var k = new UnavailableKey(7, null, "movie", null, null);
        await _db.MarkUnavailableAsync(k, TimeSpan.FromSeconds(-1), default);
        // retry_after in the past ⇒ no longer "marked".
        Assert.False(await _db.IsMarkedUnavailableAsync(k, default));

        await _db.MarkUnavailableAsync(k, TimeSpan.FromHours(24), default);
        Assert.True(await _db.IsMarkedUnavailableAsync(k, default));
    }

    [Fact]
    public async Task Concurrent_Log_Writes_All_Succeed()
    {
        var tasks = new Task[10];
        for (var i = 0; i < tasks.Length; i++)
        {
            var idx = i;
            tasks[i] = Task.Run(() => _db.LogMaterialisationAsync(new MaterialisationLogEntry
            {
                Timestamp = DateTimeOffset.UtcNow,
                Trigger = "manual",
                DurationMs = idx,
                Outcome = "success",
            }, default));
        }

        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task PhantomItem_RoundTrip()
    {
        var id = Guid.NewGuid();
        await _db.UpsertPhantomItemAsync(id, new PhantomItemRow
        {
            TmdbId = 1,
            ImdbId = "tt1",
            Type = "movie",
            State = PhantomItemState.Virtual,
            FirstSeen = DateTimeOffset.UtcNow,
            LastTouched = DateTimeOffset.UtcNow,
        }, default);
        var r = await _db.GetPhantomItemAsync(id, default);
        Assert.NotNull(r);
        Assert.Equal(PhantomItemState.Virtual, r!.State);
        Assert.Equal("tt1", r.ImdbId);
    }
}
