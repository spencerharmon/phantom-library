using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Swarm;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// The custom metadata provider re-applies the Peers score after core's replace-all
/// refresh merge (which NULLs CriticRating), with the SAME policy as the score sweep,
/// served from the incrementally-filled cache or a bounded single-title lookup.
/// </summary>
[Collection(SwarmHealthMetricsCollection.Name)] // shares the process-wide SwarmHealthCache
public sealed class SwarmHealthProviderTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private readonly List<(string Type, IReadOnlyCollection<int> Ids, DateTimeOffset Fresh)> _lookups = new();
    private readonly Dictionary<(string, int), int> _db = new();

    public SwarmHealthProviderTests() => SwarmHealthCache.Clear();

    public void Dispose() => SwarmHealthCache.Clear();

    [Fact]
    public async Task RestoresScoreWipedByRefresh_FromCache_WithTheSweepPolicy_NoLookup()
    {
        SwarmHealthCache.Record(false, new[] { 10, 11, 12 }, new Dictionary<int, int> { [10] = 42, [11] = 0 }, Now);
        SwarmHealthCache.Record(true, new[] { 20 }, new Dictionary<int, int> { [20] = 250 }, Now);
        var provider = NewProvider();
        var live = new Movie { ChannelId = ChannelIds.Movies, ExternalId = "movie_10", CriticRating = null };
        var dead = new Movie { ChannelId = ChannelIds.Movies, ExternalId = "movie_11" };
        var unmeasured = new Movie { ChannelId = ChannelIds.Movies, ExternalId = "movie_12" };
        var series = new Series { ChannelId = ChannelIds.Shows, ExternalId = "series_20" };

        Assert.Equal(ItemUpdateType.MetadataEdit, await provider.ApplyAsync(live, default));
        await provider.ApplyAsync(dead, default);
        await provider.ApplyAsync(unmeasured, default);
        await provider.ApplyAsync(series, default);

        Assert.Empty(_lookups); // every title was memoised by the sweep: no DB access at all
        Assert.Equal(SwarmHealthScore.TargetFor("movie_10", new Dictionary<int, int> { [10] = 42 }), live.CriticRating);
        Assert.Equal(0f, dead.CriticRating);
        Assert.Equal(SwarmHealthScore.UnmeasuredScore, unmeasured.CriticRating);
        Assert.Equal(100f, series.CriticRating); // capped like the sweep
        Assert.True(dead.CriticRating < unmeasured.CriticRating && unmeasured.CriticRating < live.CriticRating);
        Assert.Equal(ItemUpdateType.None, await provider.ApplyAsync(live, default)); // idempotent
    }

    [Fact]
    public async Task CacheMissOrStale_DoesOneBoundedSingleTitleLookup_AndMemoisesIt()
    {
        _db[("movie", 30)] = 7;
        _db[("episode", 40)] = 0;
        SwarmHealthCache.Record(false, new[] { 31 }, new Dictionary<int, int> { [31] = 99 }, Now.AddHours(-30)); // older than recheck (24h)
        var provider = NewProvider();
        var miss = new Movie { ChannelId = ChannelIds.Movies, ExternalId = "movie_30" };
        var stale = new Movie { ChannelId = ChannelIds.Movies, ExternalId = "movie_31" };
        var series = new Series { ChannelId = ChannelIds.Shows, ExternalId = "series_40" };

        await provider.ApplyAsync(miss, default);
        await provider.ApplyAsync(stale, default);
        await provider.ApplyAsync(series, default);

        Assert.Equal(7f, miss.CriticRating);
        Assert.Equal(SwarmHealthScore.UnmeasuredScore, stale.CriticRating); // re-looked-up: no fresh measurement now
        Assert.Equal(0f, series.CriticRating); // dead series (episode aggregate type)
        Assert.Equal(3, _lookups.Count);
        Assert.All(_lookups, l => Assert.Single(l.Ids)); // bounded: one title per lookup, never an aggregate
        Assert.Equal(("episode", 40), (_lookups[2].Type, Assert.Single(_lookups[2].Ids)));
        Assert.All(_lookups, l => Assert.Equal(Now.AddDays(-7), l.Fresh)); // same fresh window as the sweep

        // Memoised: the next refresh of the same title needs no lookup.
        miss.CriticRating = null;
        await provider.ApplyAsync(miss, default);
        Assert.Equal(7f, miss.CriticRating);
        Assert.Equal(3, _lookups.Count);
    }

    [Fact]
    public async Task OrphanMoviesAndOrphanSeriesFolders_GetUnmeasured_WithoutLookup()
    {
        var provider = NewProvider();
        var orphan = new Movie { ChannelId = ChannelIds.Movies, ExternalId = "orphan_0123456789abcdef" };
        var orphanSeries = new Folder { ChannelId = ChannelIds.Shows, ExternalId = "orphanseries_0123456789abcdef" };

        Assert.Equal(ItemUpdateType.MetadataEdit, await provider.ApplyAsync(orphan, default));
        Assert.Equal(ItemUpdateType.MetadataEdit, await provider.ApplyAsync(orphanSeries, default));

        Assert.Equal(SwarmHealthScore.TargetFor(orphan.ExternalId, new Dictionary<int, int>()), orphan.CriticRating);
        Assert.Equal(SwarmHealthScore.UnmeasuredScore, orphanSeries.CriticRating);
        Assert.Empty(_lookups);
    }

    [Fact]
    public async Task LeavesNonPhantom_SeasonsEpisodes_ForeignChannels_AndDisabledStateAlone()
    {
        _db[("movie", 10)] = 50;
        var provider = NewProvider();
        var library = new Movie { ChannelId = Guid.Empty, ExternalId = "movie_10", CriticRating = 88f };
        var foreign = new Movie { ChannelId = Guid.NewGuid(), ExternalId = "movie_10", CriticRating = 33f };
        var season = new Season { ChannelId = ChannelIds.Shows, ExternalId = "season_20_s01" };
        var episode = new Episode { ChannelId = ChannelIds.Shows, ExternalId = "episode_20_s01e01" };
        var seriesTypedFolderInMovies = new Folder { ChannelId = ChannelIds.Movies, ExternalId = "orphanseries_00" };

        foreach (var item in new BaseItem[] { library, foreign, season, episode, seriesTypedFolderInMovies })
        {
            Assert.Equal(ItemUpdateType.None, await provider.ApplyAsync(item, default));
        }

        Assert.Equal(88f, library.CriticRating);
        Assert.Equal(33f, foreign.CriticRating);
        Assert.Null(season.CriticRating);
        Assert.Null(episode.CriticRating);
        Assert.Null(seriesTypedFolderInMovies.CriticRating);

        var disabled = NewProvider(serviceUrl: string.Empty);
        var phantom = new Movie { ChannelId = ChannelIds.Movies, ExternalId = "movie_10" };
        Assert.Equal(ItemUpdateType.None, await disabled.ApplyAsync(phantom, default));
        Assert.Null(phantom.CriticRating);
        Assert.Empty(_lookups);
    }

    [Fact]
    public async Task LookupFailure_NeverFailsTheCoreRefresh()
    {
        var provider = new SwarmHealthMetadataProvider(
            (_, _, _, _) => throw new InvalidOperationException("db down"),
            Config,
            () => Now,
            NullLogger.Instance);
        var movie = new Movie { ChannelId = ChannelIds.Movies, ExternalId = "movie_5", CriticRating = 12f };

        Assert.Equal(ItemUpdateType.None, await provider.ApplyAsync(movie, default));
        Assert.Equal(12f, movie.CriticRating);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelling = new SwarmHealthMetadataProvider(
            (_, _, _, ct) => Task.FromCanceled<IReadOnlyDictionary<int, int>>(ct),
            Config,
            () => Now,
            NullLogger.Instance);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelling.ApplyAsync(movie, cts.Token));
    }

    private SwarmHealthMetadataProvider NewProvider(string serviceUrl = "http://swarm-health.test:8095")
        => new(
            (type, ids, fresh, _) =>
            {
                _lookups.Add((type, ids, fresh));
                var map = new Dictionary<int, int>();
                foreach (var id in ids)
                {
                    if (_db.TryGetValue((type, id), out var p))
                    {
                        map[id] = p;
                    }
                }

                return Task.FromResult<IReadOnlyDictionary<int, int>>(map);
            },
            () =>
            {
                var c = Config();
                c.SwarmHealthServiceUrl = serviceUrl;
                return c;
            },
            () => Now,
            NullLogger.Instance);

    private static PluginConfiguration Config() => SwarmHealthTests.Config();
}
