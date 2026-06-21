using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class MagnetSelectorTests
{
    private static IndexerCandidate MakeCandidate(string title, long sizeGb, int seeders)
        => new()
        {
            Title = title,
            Magnet = "magnet:?xt=urn:btih:" + Guid.NewGuid().ToString("N"),
            InfoHash = Guid.NewGuid().ToString("N"),
            Size = sizeGb * 1024L * 1024L * 1024L,
            Seeders = seeders,
            IndexerName = "test",
        };

    private static PluginConfiguration TestConfig() => new()
    {
        MinSeeders = 1,
        MinSizeGb1080p = 1,
        MinSizeGb4K = 1,
    };

    [Fact]
    public async Task AggregatesFromAllIndexers_PicksScorerWinner()
    {
        var ix1 = new Mock<IIndexerClient>(MockBehavior.Strict);
        ix1.SetupGet(i => i.IsEnabled).Returns(true);
        ix1.SetupGet(i => i.Name).Returns("ix1");
        ix1.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeCandidate("Movie 1080p", 5, 50) });

        var ix2 = new Mock<IIndexerClient>(MockBehavior.Strict);
        ix2.SetupGet(i => i.IsEnabled).Returns(true);
        ix2.SetupGet(i => i.Name).Returns("ix2");
        ix2.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeCandidate("Movie 2160p HDR", 25, 100) });

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var cfg = TestConfig();
        cfg.QualityPreset = QualityPreset.GostreamDefault;
        var sel = new MagnetSelector(
            new[] { ix1.Object, ix2.Object },
            scorer,
            NullLogger<MagnetSelector>.Instance,
            () => cfg);

        var picked = await sel.SelectAsync(42, "tt0000042", "movie", null, null, "Movie", 2020, CancellationToken.None);

        Assert.NotNull(picked);
        // The 4K candidate (size 25GB, seeders 100) outranks the 1080p
        // candidate (5GB, 50 seeders) under GostreamDefault scoring.
        Assert.True(picked!.Size >= 20L * 1024 * 1024 * 1024);
        Assert.Equal(100, picked.Seeders);
    }

    [Fact]
    public async Task NoCandidates_ReturnsNull()
    {
        var ix = new Mock<IIndexerClient>(MockBehavior.Strict);
        ix.SetupGet(i => i.IsEnabled).Returns(true);
        ix.SetupGet(i => i.Name).Returns("ix");
        ix.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IndexerCandidate>());

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var cfg = TestConfig();
        var sel = new MagnetSelector(new[] { ix.Object }, scorer, NullLogger<MagnetSelector>.Instance, () => cfg);

        var picked = await sel.SelectAsync(1, null, "movie", null, null, "Nothing", 1999, CancellationToken.None);
        Assert.Null(picked);
    }

    [Fact]
    public async Task SkipsDisabledIndexers()
    {
        var disabled = new Mock<IIndexerClient>(MockBehavior.Strict);
        disabled.SetupGet(i => i.IsEnabled).Returns(false);
        disabled.SetupGet(i => i.Name).Returns("disabled");

        var enabled = new Mock<IIndexerClient>(MockBehavior.Strict);
        enabled.SetupGet(i => i.IsEnabled).Returns(true);
        enabled.SetupGet(i => i.Name).Returns("enabled");
        enabled.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeCandidate("Movie 1080p", 5, 10) });

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var cfg = TestConfig();
        var sel = new MagnetSelector(
            new[] { disabled.Object, enabled.Object },
            scorer,
            NullLogger<MagnetSelector>.Instance,
            () => cfg);

        var picked = await sel.SelectAsync(1, null, "movie", null, null, "Movie", 2020, CancellationToken.None);
        Assert.NotNull(picked);
        disabled.Verify(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task IndexerThrows_SwallowedAndOtherIndexersStillScored()
    {
        var bad = new Mock<IIndexerClient>(MockBehavior.Strict);
        bad.SetupGet(i => i.IsEnabled).Returns(true);
        bad.SetupGet(i => i.Name).Returns("bad");
        bad.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("boom"));

        var good = new Mock<IIndexerClient>(MockBehavior.Strict);
        good.SetupGet(i => i.IsEnabled).Returns(true);
        good.SetupGet(i => i.Name).Returns("good");
        good.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeCandidate("Movie 1080p", 5, 10) });

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var cfg = TestConfig();
        var sel = new MagnetSelector(
            new[] { bad.Object, good.Object },
            scorer,
            NullLogger<MagnetSelector>.Instance,
            () => cfg);

        var picked = await sel.SelectAsync(1, null, "movie", null, null, "Movie", 2020, CancellationToken.None);
        Assert.NotNull(picked);
    }

    [Fact]
    public async Task AllEnabledIndexersTransient_ReturnsIndeterminateTransient()
    {
        var ix1 = new Mock<IIndexerClient>(MockBehavior.Strict);
        ix1.SetupGet(i => i.IsEnabled).Returns(true);
        ix1.SetupGet(i => i.Name).Returns("ix1");
        ix1.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IndexerTransientException("timeout"));

        var ix2 = new Mock<IIndexerClient>(MockBehavior.Strict);
        ix2.SetupGet(i => i.IsEnabled).Returns(true);
        ix2.SetupGet(i => i.Name).Returns("ix2");
        ix2.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IndexerTransientException("bad gateway"));

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { ix1.Object, ix2.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        var probe = await sel.ProbeAsync(1, "tt1", "movie", null, null, "Movie", 2020, CancellationToken.None);

        Assert.Equal(MagnetProbeOutcome.IndeterminateTransient, probe.Outcome);
    }

    [Fact]
    public async Task OneEmptyOneTransientNoCandidates_ReturnsIndeterminateTransient()
    {
        var empty = new Mock<IIndexerClient>(MockBehavior.Strict);
        empty.SetupGet(i => i.IsEnabled).Returns(true);
        empty.SetupGet(i => i.Name).Returns("empty");
        empty.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IndexerCandidate>());

        var timeout = new Mock<IIndexerClient>(MockBehavior.Strict);
        timeout.SetupGet(i => i.IsEnabled).Returns(true);
        timeout.SetupGet(i => i.Name).Returns("timeout");
        timeout.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IndexerTransientException("timeout"));

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { empty.Object, timeout.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        var probe = await sel.ProbeAsync(1, "tt1", "movie", null, null, "Movie", 2020, CancellationToken.None);

        Assert.Equal(MagnetProbeOutcome.IndeterminateTransient, probe.Outcome);
    }

    [Fact]
    public async Task AllEnabledIndexersEmpty_ReturnsDefinitiveUnavailable()
    {
        var ix1 = new Mock<IIndexerClient>(MockBehavior.Strict);
        ix1.SetupGet(i => i.IsEnabled).Returns(true);
        ix1.SetupGet(i => i.Name).Returns("ix1");
        ix1.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IndexerCandidate>());

        var ix2 = new Mock<IIndexerClient>(MockBehavior.Strict);
        ix2.SetupGet(i => i.IsEnabled).Returns(true);
        ix2.SetupGet(i => i.Name).Returns("ix2");
        ix2.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IndexerCandidate>());

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { ix1.Object, ix2.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        var probe = await sel.ProbeAsync(1, "tt1", "movie", null, null, "Movie", 2020, CancellationToken.None);

        Assert.Equal(MagnetProbeOutcome.DefinitiveUnavailable, probe.Outcome);
    }

    [Fact]
    public async Task OneCandidateOneTransient_UsesCandidate()
    {
        var candidate = new Mock<IIndexerClient>(MockBehavior.Strict);
        candidate.SetupGet(i => i.IsEnabled).Returns(true);
        candidate.SetupGet(i => i.Name).Returns("candidate");
        candidate.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeCandidate("Movie 1080p", 5, 10) });

        var timeout = new Mock<IIndexerClient>(MockBehavior.Strict);
        timeout.SetupGet(i => i.IsEnabled).Returns(true);
        timeout.SetupGet(i => i.Name).Returns("timeout");
        timeout.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IndexerTransientException("timeout"));

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { candidate.Object, timeout.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        var probe = await sel.ProbeAsync(1, "tt1", "movie", null, null, "Movie", 2020, CancellationToken.None);

        Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
        Assert.Single(probe.Candidates);
    }

    [Fact]
    public async Task EpisodeQuery_PassesSeriesImdb()
    {
        IndexerQuery? captured = null;
        var ix = new Mock<IIndexerClient>(MockBehavior.Strict);
        ix.SetupGet(i => i.IsEnabled).Returns(true);
        ix.SetupGet(i => i.Name).Returns("ix");
        ix.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .Callback<IndexerQuery, CancellationToken>((q, _) => captured = q)
            .ReturnsAsync(new[] { MakeCandidate("Show S01E01 1080p", 5, 10) });

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var cfg = TestConfig();
        var sel = new MagnetSelector(new[] { ix.Object }, scorer, NullLogger<MagnetSelector>.Instance, () => cfg);

        var picked = await sel.SelectAsync(99, "tt9999", "episode", 1, 1, "Show", 2020, CancellationToken.None);
        Assert.NotNull(picked);
        Assert.NotNull(captured);
        Assert.Equal("episode", captured!.Type);
        Assert.Equal("tt9999", captured.SeriesImdb);
        Assert.Null(captured.Imdb);
        Assert.Equal(1, captured.Season);
        Assert.Equal(1, captured.Episode);
    }
}
