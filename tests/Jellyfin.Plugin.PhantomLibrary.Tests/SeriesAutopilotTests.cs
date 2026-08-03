using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Clients.Models;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class SeriesAutopilotTests : IDisposable
{
    private readonly string _dbPath;
    private readonly PhantomDb _db;
    private readonly Mock<ITmdbClient> _tmdb;
    private readonly Mock<IMaterialiser> _materialiser;
    private readonly PluginConfiguration _cfg;
    private readonly SeriesAutopilot _sut;

    // Capture materialise invocations for assertions.
    private readonly List<(int Tmdb, string Type, int? Season, int? Episode, MaterialiseTrigger Trigger)> _calls = new();

    public SeriesAutopilotTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "phantom-autopilot-" + Guid.NewGuid().ToString("N") + ".db");
        _db = new PhantomDb(_dbPath);

        _tmdb = new Mock<ITmdbClient>(MockBehavior.Strict);
        _materialiser = new Mock<IMaterialiser>(MockBehavior.Loose);
        _materialiser
            .Setup(m => m.MaterialiseAsync(
                It.IsAny<int>(), It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<int?>(),
                It.IsAny<MaterialiseTrigger>(), It.IsAny<CancellationToken>()))
            .Callback<int, string, int?, int?, MaterialiseTrigger, CancellationToken>((tmdb, type, season, episode, trigger, _) =>
            {
                lock (_calls)
                {
                    _calls.Add((tmdb, type, season, episode, trigger));
                }
            })
            .ReturnsAsync(MaterialisationOutcome.Success("/fuse/x.mkv", "/stub/x.mkv"));

        _cfg = new PluginConfiguration
        {
            SeriesAutopilotEnabled = true,
            SeriesAutopilotPrefetchEpisodes = 2,
        };

        _sut = new SeriesAutopilot(
            _materialiser.Object, _db, _tmdb.Object,
            NullLogger<SeriesAutopilot>.Instance,
            () => _cfg);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private static Episode MakeChannelEpisode(int tmdb, int season, int episode, bool phantomTag = false, Guid? channelIdOverride = null)
    {
        var e = new Episode
        {
            ExternalId = ChannelItemId.ForEpisode(tmdb, season, episode).Encode(),
            ChannelId = channelIdOverride ?? ChannelIds.Shows,
        };
        if (phantomTag)
        {
            e.Tags = new[] { "phantom" };
        }

        return e;
    }

    private void SetupSeason(int seriesTmdb, int season, int episodeCount)
    {
        var details = new TmdbSeasonDetails
        {
            SeriesTmdbId = seriesTmdb,
            SeasonNumber = season,
            Episodes = Enumerable.Range(1, episodeCount).Select(n => new TmdbEpisodeSummary
            {
                Id = season * 1000 + n,
                EpisodeNumber = n,
                SeasonNumber = season,
                Name = $"E{n}",
            }).ToList(),
        };
        _tmdb.Setup(t => t.GetSeasonAsync(seriesTmdb, season, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(details);
    }

    private void SetupSeasonNull(int seriesTmdb, int season)
    {
        _tmdb.Setup(t => t.GetSeasonAsync(seriesTmdb, season, It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((TmdbSeasonDetails?)null);
    }

    [Fact]
    public async Task BelowThreshold_NoMaterialise()
    {
        SetupSeason(1399, 1, 10);
        var ep = MakeChannelEpisode(1399, 1, 1);
        await _sut.OnEpisodePlaybackProgressAsync(Guid.NewGuid(), ep, percentWatched: 50.0, CancellationToken.None);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task AtThreshold_PhantomTagged_NoMaterialise_SplashGuard()
    {
        // Even though the listener should have guarded, autopilot guards again.
        SetupSeason(1399, 1, 10);
        var ep = MakeChannelEpisode(1399, 1, 1, phantomTag: true);
        await _sut.OnEpisodePlaybackProgressAsync(Guid.NewGuid(), ep, 85.0, CancellationToken.None);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task NonPhantomChannel_NoMaterialise()
    {
        SetupSeason(1399, 1, 10);
        var ep = MakeChannelEpisode(1399, 1, 1, channelIdOverride: Guid.NewGuid());
        await _sut.OnEpisodePlaybackProgressAsync(Guid.NewGuid(), ep, 85.0, CancellationToken.None);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task Disabled_NoMaterialise()
    {
        _cfg.SeriesAutopilotEnabled = false;
        var ep = MakeChannelEpisode(1399, 1, 1);
        await _sut.OnEpisodePlaybackProgressAsync(Guid.NewGuid(), ep, 85.0, CancellationToken.None);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task PrefetchZero_NoMaterialise()
    {
        _cfg.SeriesAutopilotPrefetchEpisodes = 0;
        var ep = MakeChannelEpisode(1399, 1, 1);
        await _sut.OnEpisodePlaybackProgressAsync(Guid.NewGuid(), ep, 85.0, CancellationToken.None);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task HappyPath_PrefetchesNextNEpisodes()
    {
        SetupSeason(1399, 1, 10);
        _cfg.SeriesAutopilotPrefetchEpisodes = 2;
        var ep = MakeChannelEpisode(1399, 1, 3);

        await _sut.OnEpisodePlaybackProgressAsync(Guid.NewGuid(), ep, 90.0, CancellationToken.None);

        // Give the fire-and-forget tasks a moment to register on the Loose mock.
        await Task.Yield();

        Assert.Equal(2, _calls.Count);
        Assert.Contains(_calls, c => c is { Tmdb: 1399, Type: "episode", Season: 1, Episode: 4, Trigger: MaterialiseTrigger.Autopilot });
        Assert.Contains(_calls, c => c is { Tmdb: 1399, Type: "episode", Season: 1, Episode: 5, Trigger: MaterialiseTrigger.Autopilot });
    }

    [Fact]
    public async Task SkipsAlreadyMaterialisedEpisode()
    {
        SetupSeason(1399, 1, 10);
        _cfg.SeriesAutopilotPrefetchEpisodes = 2;
        // Episode 4 already materialised; expect only episode 5 to be prefetched.
        await _db.InsertMaterialisedStateAsync(1399, "episode", 1, 4, "/s4", "/f4", CancellationToken.None);

        var ep = MakeChannelEpisode(1399, 1, 3);
        await _sut.OnEpisodePlaybackProgressAsync(Guid.NewGuid(), ep, 90.0, CancellationToken.None);

        Assert.Single(_calls);
        Assert.Equal((1399, "episode", (int?)1, (int?)5, MaterialiseTrigger.Autopilot), _calls[0]);
    }

    [Fact]
    public async Task SkipsAlreadyInFlightEpisode()
    {
        SetupSeason(1399, 1, 10);
        _cfg.SeriesAutopilotPrefetchEpisodes = 2;
        await _db.UpsertMaterialiseInFlightAsync(1399, "episode", 1, 5, CancellationToken.None);

        var ep = MakeChannelEpisode(1399, 1, 3);
        await _sut.OnEpisodePlaybackProgressAsync(Guid.NewGuid(), ep, 90.0, CancellationToken.None);

        Assert.Single(_calls);
        Assert.Equal((1399, "episode", (int?)1, (int?)4, MaterialiseTrigger.Autopilot), _calls[0]);
    }

    [Fact]
    public async Task CrossesSeasonBoundary()
    {
        // Season 1 has 10 episodes; user finishes S01E10 → next two should
        // be S02E01 and S02E02.
        SetupSeason(1399, 1, 10);
        SetupSeason(1399, 2, 10);
        _cfg.SeriesAutopilotPrefetchEpisodes = 2;

        var ep = MakeChannelEpisode(1399, 1, 10);
        await _sut.OnEpisodePlaybackProgressAsync(Guid.NewGuid(), ep, 90.0, CancellationToken.None);

        Assert.Equal(2, _calls.Count);
        Assert.Contains(_calls, c => c is { Season: 2, Episode: 1 });
        Assert.Contains(_calls, c => c is { Season: 2, Episode: 2 });
    }

    [Fact]
    public async Task EndOfSeries_NoMoreSeasons_NoMaterialise()
    {
        // S03 is the last season; S03E10 + next season returns null.
        SetupSeason(1399, 3, 10);
        SetupSeasonNull(1399, 4);
        _cfg.SeriesAutopilotPrefetchEpisodes = 2;

        var ep = MakeChannelEpisode(1399, 3, 10);
        await _sut.OnEpisodePlaybackProgressAsync(Guid.NewGuid(), ep, 90.0, CancellationToken.None);

        Assert.Empty(_calls);
    }

    [Fact]
    public async Task MalformedExternalId_NoMaterialise()
    {
        var ep = new Episode
        {
            ExternalId = "garbage",
            ChannelId = ChannelIds.Shows,
        };
        await _sut.OnEpisodePlaybackProgressAsync(Guid.NewGuid(), ep, 90.0, CancellationToken.None);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task WrongKindExternalId_NoMaterialise()
    {
        // movie kind on the shows channel — defensive guard.
        var ep = new Episode
        {
            ExternalId = ChannelItemId.ForMovie(42).Encode(),
            ChannelId = ChannelIds.Shows,
        };
        await _sut.OnEpisodePlaybackProgressAsync(Guid.NewGuid(), ep, 90.0, CancellationToken.None);
        Assert.Empty(_calls);
    }

    [Fact]
    public async Task TmdbReturnsNull_NoMaterialise()
    {
        SetupSeasonNull(1399, 1);
        var ep = MakeChannelEpisode(1399, 1, 1);
        await _sut.OnEpisodePlaybackProgressAsync(Guid.NewGuid(), ep, 90.0, CancellationToken.None);
        Assert.Empty(_calls);
    }
}
