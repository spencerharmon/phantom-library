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
        Assert.Null(await _db.IsMarkedUnavailableAsync(k, default));

        await _db.MarkUnavailableAsync(k, TimeSpan.FromHours(24), default);
        Assert.NotNull(await _db.IsMarkedUnavailableAsync(k, default));
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
    public async Task GetStates_BulkLookup_OnlyReturnsKnownGuids()
    {
        var phantom = Guid.NewGuid();
        var materialised = Guid.NewGuid();
        var unavailable = Guid.NewGuid();
        var unknown = Guid.NewGuid(); // never inserted; must be absent from result

        var now = DateTimeOffset.UtcNow;
        await _db.UpsertPhantomItemAsync(phantom, new PhantomItemRow
        {
            TmdbId = 10, Type = "movie", State = PhantomItemState.Phantom,
            FirstSeen = now, LastTouched = now,
        }, default);
        await _db.UpsertPhantomItemAsync(materialised, new PhantomItemRow
        {
            TmdbId = 11, Type = "movie", State = PhantomItemState.Materialised,
            FirstSeen = now, LastTouched = now,
        }, default);
        await _db.UpsertPhantomItemAsync(unavailable, new PhantomItemRow
        {
            TmdbId = 12, Type = "movie", State = PhantomItemState.Unavailable,
            FirstSeen = now, LastTouched = now,
        }, default);

        var states = await _db.GetStatesAsync(
            new[] { phantom, materialised, unavailable, unknown }, default);

        Assert.Equal(3, states.Count);
        Assert.Equal(PhantomItemState.Phantom, states[phantom]);
        Assert.Equal(PhantomItemState.Materialised, states[materialised]);
        Assert.Equal(PhantomItemState.Unavailable, states[unavailable]);
        Assert.False(states.ContainsKey(unknown));
    }

    [Fact]
    public async Task GetStates_Empty_ReturnsEmpty()
    {
        var states = await _db.GetStatesAsync(Array.Empty<Guid>(), default);
        Assert.Empty(states);
    }

    [Fact]
    public async Task GetStates_BatchesAcrossSmallBatchSize()
    {
        // Insert 7 rows; force batch size of 3 to exercise multi-batch path.
        var ids = new Guid[7];
        for (var i = 0; i < ids.Length; i++)
        {
            ids[i] = Guid.NewGuid();
            await _db.UpsertPhantomItemAsync(ids[i], new PhantomItemRow
            {
                TmdbId = 1000 + i, Type = "movie", State = PhantomItemState.Phantom,
                FirstSeen = DateTimeOffset.UtcNow, LastTouched = DateTimeOffset.UtcNow,
            }, default);
        }
        var states = await _db.GetStatesAsync(ids, default, batchSize: 3);
        Assert.Equal(ids.Length, states.Count);
        foreach (var id in ids)
        {
            Assert.Equal(PhantomItemState.Phantom, states[id]);
        }
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

    [Fact]
    public async Task OriginalOverview_RememberThenTake_RoundTrips()
    {
        var id = Guid.NewGuid();
        var remembered = await _db.RememberOriginalOverviewAsync(id, "original text", default);
        Assert.Equal("original text", remembered);

        // Re-remember must NOT clobber the first stored value.
        var second = await _db.RememberOriginalOverviewAsync(id, "[materialising…] original text", default);
        Assert.Equal("original text", second);

        var taken = await _db.TakeOriginalOverviewAsync(id, default);
        Assert.Equal("original text", taken);

        // After Take, the column is cleared.
        var takenAgain = await _db.TakeOriginalOverviewAsync(id, default);
        Assert.Null(takenAgain);
    }

    [Fact]
    public async Task TmdbCache_RoundTrip_AndExpiry()
    {
        await _db.PutTmdbCacheAsync("trending/movie", "hash1", "en-US", "[{\"id\":1}]", TimeSpan.FromHours(1), default);
        var hit = await _db.GetTmdbCacheAsync("trending/movie", "hash1", "en-US", default);
        Assert.Equal("[{\"id\":1}]", hit);

        Assert.Null(await _db.GetTmdbCacheAsync("trending/movie", "otherhash", "en-US", default));

        await _db.PutTmdbCacheAsync("trending/tv", "hash2", "en-US", "[]", TimeSpan.FromSeconds(-10), default);
        Assert.Null(await _db.GetTmdbCacheAsync("trending/tv", "hash2", "en-US", default));
    }

    [Fact]
    public async Task PurgeExpiredTmdbCache_RemovesOnlyExpired()
    {
        await _db.PutTmdbCacheAsync("fresh", "h", "en", "[]", TimeSpan.FromHours(1), default);
        await _db.PutTmdbCacheAsync("stale", "h", "en", "[]", TimeSpan.FromSeconds(-1), default);
        await _db.PutTmdbCacheAsync("alsoStale", "h", "en", "[]", TimeSpan.FromSeconds(-1), default);

        var n = await _db.PurgeExpiredTmdbCacheAsync(default);
        Assert.Equal(2, n);

        Assert.NotNull(await _db.GetTmdbCacheAsync("fresh", "h", "en", default));
        Assert.Null(await _db.GetTmdbCacheAsync("stale", "h", "en", default));
    }

    [Fact]
    public async Task V2_To_V3_Migration_Preserves_Data_And_Adds_TmdbCache()
    {
        var p = Path.Combine(Path.GetTempPath(), "phantommig3_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                       new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                       {
                           DataSource = p,
                           Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                       }.ToString()))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"CREATE TABLE phantom_items (
                        item_guid TEXT PRIMARY KEY,
                        tmdb_id INTEGER,
                        imdb_id TEXT,
                        type TEXT NOT NULL,
                        state TEXT NOT NULL,
                        first_seen INTEGER NOT NULL,
                        last_touched INTEGER NOT NULL,
                        eviction_protected INTEGER NOT NULL DEFAULT 0,
                        original_overview TEXT
                    );";
                    cmd.ExecuteNonQuery();
                }

                var id = Guid.NewGuid();
                using (var ins = conn.CreateCommand())
                {
                    ins.CommandText = @"INSERT INTO phantom_items
                        (item_guid, tmdb_id, imdb_id, type, state, first_seen, last_touched, eviction_protected, original_overview)
                        VALUES ($g, 7, 'tt7', 'movie', 'Virtual', 1000, 2000, 0, 'preserved');";
                    ins.Parameters.AddWithValue("$g", id.ToString("N"));
                    ins.ExecuteNonQuery();
                }

                using (var sv = conn.CreateCommand())
                {
                    sv.CommandText = "PRAGMA user_version = 2;";
                    sv.ExecuteNonQuery();
                }

                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }

            using (var db = new PhantomDb(p))
            {
                await db.PutTmdbCacheAsync("x", "h", "en", "[]", TimeSpan.FromHours(1), default);
                Assert.Equal("[]", await db.GetTmdbCacheAsync("x", "h", "en", default));
            }

            using var verify = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = p,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
                }.ToString());
            verify.Open();
            using (var vc = verify.CreateCommand())
            {
                vc.CommandText = "PRAGMA user_version;";
                Assert.Equal(5L, Convert.ToInt64(vc.ExecuteScalar(),
                    System.Globalization.CultureInfo.InvariantCulture));
            }

            using (var sel = verify.CreateCommand())
            {
                sel.CommandText = "SELECT imdb_id, original_overview FROM phantom_items LIMIT 1;";
                using var r = sel.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal("tt7", r.GetString(0));
                Assert.Equal("preserved", r.GetString(1));
            }

            using (var t = verify.CreateCommand())
            {
                t.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='tmdb_cache';";
                Assert.NotNull(t.ExecuteScalar());
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(p); File.Delete(p + "-wal"); File.Delete(p + "-shm"); } catch { }
        }
    }

    [Fact]
    public async Task V1_To_V2_Migration_Preserves_Data_And_Adds_Column()
    {
        var p = Path.Combine(Path.GetTempPath(), "phantommig_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            // Build a v1 database by hand: schema matches the original
            // SchemaV1Sql; user_version = 1; no original_overview column.
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                       new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                       {
                           DataSource = p,
                           Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                       }.ToString()))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"CREATE TABLE phantom_items (
                        item_guid TEXT PRIMARY KEY,
                        tmdb_id INTEGER,
                        imdb_id TEXT,
                        type TEXT NOT NULL,
                        state TEXT NOT NULL,
                        first_seen INTEGER NOT NULL,
                        last_touched INTEGER NOT NULL,
                        eviction_protected INTEGER NOT NULL DEFAULT 0
                    );";
                    cmd.ExecuteNonQuery();
                }

                var id = Guid.NewGuid();
                using (var ins = conn.CreateCommand())
                {
                    ins.CommandText = @"INSERT INTO phantom_items
                        (item_guid, tmdb_id, imdb_id, type, state, first_seen, last_touched, eviction_protected)
                        VALUES ($g, 42, 'tt42', 'movie', 'Virtual', 1000, 2000, 0);";
                    ins.Parameters.AddWithValue("$g", id.ToString("N"));
                    ins.ExecuteNonQuery();
                }

                using (var sv = conn.CreateCommand())
                {
                    sv.CommandText = "PRAGMA user_version = 1;";
                    sv.ExecuteNonQuery();
                }

                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }

            // Open through PhantomDb — should auto-migrate to current version.
            using var db = new PhantomDb(p);
            // Touch the DB so EnsureSchema runs.
            var entry = await db.GetPhantomItemAsync(Guid.NewGuid(), default);
            Assert.Null(entry); // missing row, but migration must not have thrown

            // Verify migration: user_version=current and column exists.
            using var verify = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = p,
                    Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
                }.ToString());
            verify.Open();
            using (var vc = verify.CreateCommand())
            {
                vc.CommandText = "PRAGMA user_version;";
                Assert.Equal(5L, Convert.ToInt64(vc.ExecuteScalar(),
                    System.Globalization.CultureInfo.InvariantCulture));
            }

            using (var cols = verify.CreateCommand())
            {
                cols.CommandText = "PRAGMA table_info(phantom_items);";
                using var r = cols.ExecuteReader();
                var found = false;
                while (r.Read())
                {
                    if (string.Equals(r.GetString(1), "original_overview", StringComparison.Ordinal))
                    {
                        found = true;
                        break;
                    }
                }

                Assert.True(found, "original_overview column not added by migration");
            }

            // Existing row preserved with imdb='tt42'.
            using (var sel = verify.CreateCommand())
            {
                sel.CommandText = "SELECT imdb_id, original_overview FROM phantom_items LIMIT 1;";
                using var r = sel.ExecuteReader();
                Assert.True(r.Read());
                Assert.Equal("tt42", r.GetString(0));
                Assert.True(r.IsDBNull(1));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(p); File.Delete(p + "-wal"); File.Delete(p + "-shm"); } catch { }
        }
    }
}

public class PhantomDbM7Tests : IDisposable
{
    private readonly string _path;
    private readonly PhantomDb _db;

    public PhantomDbM7Tests()
    {
        _path = Path.Combine(Path.GetTempPath(), "phantomdbm7_" + Guid.NewGuid().ToString("N") + ".db");
        _db = new PhantomDb(_path);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_path); File.Delete(_path + "-wal"); File.Delete(_path + "-shm"); } catch { }
    }

    [Fact]
    public async Task UserPrefs_RoundTrip_And_Defaults()
    {
        var u = Guid.NewGuid();
        var def = await _db.GetUserPrefsAsync(u, default);
        Assert.True(def.ProtectFavourites);
        Assert.True(def.ShowPhantoms);
        Assert.True(def.AllowEager);

        await _db.UpsertUserPrefsAsync(u, new UserPrefsRow { ProtectFavourites = false, ShowPhantoms = false, AllowEager = false }, default);
        var got = await _db.GetUserPrefsAsync(u, default);
        Assert.False(got.ProtectFavourites);

        var all = await _db.ListAllUserPrefsAsync(default);
        Assert.Single(all);
        Assert.Equal(u, all[0].UserId);
    }

    [Fact]
    public async Task ListItemsByState_Filters_Correctly()
    {
        var v = Guid.NewGuid();
        var m = Guid.NewGuid();
        await _db.UpsertPhantomItemAsync(v, new PhantomItemRow
        {
            Type = "movie", State = PhantomItemState.Virtual,
            FirstSeen = DateTimeOffset.UtcNow, LastTouched = DateTimeOffset.UtcNow,
        }, default);
        await _db.UpsertPhantomItemAsync(m, new PhantomItemRow
        {
            Type = "movie", State = PhantomItemState.Materialised,
            FirstSeen = DateTimeOffset.UtcNow, LastTouched = DateTimeOffset.UtcNow,
            StubPath = "/s", FusePath = "/f", MaterialisedAt = DateTimeOffset.UtcNow,
        }, default);

        var virt = await _db.ListItemsByStateAsync("Virtual", default);
        var mat = await _db.ListItemsByStateAsync("Materialised", default);
        Assert.Single(virt);
        Assert.Equal(v, virt[0].ItemGuid);
        Assert.Single(mat);
        Assert.Equal("/s", mat[0].StubPath);
        Assert.Equal("/f", mat[0].FusePath);
        Assert.NotNull(mat[0].MaterialisedAt);
    }

    [Fact]
    public async Task PurgeExpiredPhantoms_DeletesOlderThanRetention()
    {
        var old = Guid.NewGuid();
        var fresh = Guid.NewGuid();
        await _db.UpsertPhantomItemAsync(old, new PhantomItemRow
        {
            Type = "movie", State = PhantomItemState.Phantom,
            FirstSeen = DateTimeOffset.UtcNow.AddDays(-30),
            LastTouched = DateTimeOffset.UtcNow.AddDays(-30),
        }, default);
        await _db.UpsertPhantomItemAsync(fresh, new PhantomItemRow
        {
            Type = "movie", State = PhantomItemState.Phantom,
            FirstSeen = DateTimeOffset.UtcNow.AddHours(-1),
            LastTouched = DateTimeOffset.UtcNow,
        }, default);

        var n = await _db.PurgeExpiredPhantomsAsync(TimeSpan.FromDays(7), default);
        Assert.Equal(1, n);
        Assert.Null(await _db.GetPhantomItemAsync(old, default));
        Assert.NotNull(await _db.GetPhantomItemAsync(fresh, default));
    }

    [Fact]
    public async Task PurgeExpiredUnavailable_RemovesExpired()
    {
        var k = new UnavailableKey(101, null, "movie", null, null);
        var fresh = new UnavailableKey(102, null, "movie", null, null);
        await _db.MarkUnavailableAsync(k, TimeSpan.FromSeconds(-1), default);
        await _db.MarkUnavailableAsync(fresh, TimeSpan.FromHours(1), default);

        var n = await _db.PurgeExpiredUnavailableMarkersAsync(default);
        Assert.Equal(1, n);
    }

    [Fact]
    public async Task V3_To_V4_Migration_Adds_StubFuseMat_Columns()
    {
        var p = Path.Combine(Path.GetTempPath(), "phantommig4_" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var conn = new Microsoft.Data.Sqlite.SqliteConnection(
                       new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                       {
                           DataSource = p,
                           Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWriteCreate,
                       }.ToString()))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"CREATE TABLE phantom_items (
                        item_guid TEXT PRIMARY KEY,
                        tmdb_id INTEGER,
                        imdb_id TEXT,
                        type TEXT NOT NULL,
                        state TEXT NOT NULL,
                        first_seen INTEGER NOT NULL,
                        last_touched INTEGER NOT NULL,
                        eviction_protected INTEGER NOT NULL DEFAULT 0,
                        original_overview TEXT
                    );
                    INSERT INTO phantom_items (item_guid, type, state, first_seen, last_touched)
                    VALUES ('keep', 'movie', 'Virtual', 1000, 2000);";
                    cmd.ExecuteNonQuery();
                }
                using (var sv = conn.CreateCommand())
                {
                    sv.CommandText = "PRAGMA user_version = 3;";
                    sv.ExecuteNonQuery();
                }
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            }

            using (var db = new PhantomDb(p))
            {
                _ = await db.GetPhantomItemAsync(Guid.NewGuid(), default);
            }

            using var verify = new Microsoft.Data.Sqlite.SqliteConnection(
                new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
                {
                    DataSource = p, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadWrite,
                }.ToString());
            verify.Open();
            using (var vc = verify.CreateCommand())
            {
                vc.CommandText = "PRAGMA user_version;";
                Assert.Equal(5L, Convert.ToInt64(vc.ExecuteScalar(),
                    System.Globalization.CultureInfo.InvariantCulture));
            }
            var cols = new System.Collections.Generic.HashSet<string>();
            using (var ti = verify.CreateCommand())
            {
                ti.CommandText = "PRAGMA table_info(phantom_items);";
                using var rd = ti.ExecuteReader();
                while (rd.Read()) cols.Add(rd.GetString(1));
            }
            Assert.Contains("stub_path", cols);
            Assert.Contains("fuse_path", cols);
            Assert.Contains("materialised_at", cols);

            using (var sel = verify.CreateCommand())
            {
                sel.CommandText = "SELECT COUNT(*) FROM phantom_items WHERE item_guid='keep';";
                Assert.Equal(1L, Convert.ToInt64(sel.ExecuteScalar(),
                    System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { File.Delete(p); File.Delete(p + "-wal"); File.Delete(p + "-shm"); } catch { }
        }
    }
}
