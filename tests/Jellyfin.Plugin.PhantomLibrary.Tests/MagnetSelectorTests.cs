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
    public async Task EpisodeProbe_PrioritisesExactEpisodeOverSeasonPacks()
    {
        var exact = MakeCandidate("Avatar The Last Airbender S02E01 The Avatar State 1080p FLAC 2 0 AVC REMUX", 5, 24);
        var pack = MakeCandidate("Avatar: The Last Airbender - S01 to S03 - 1080p - Bluray AAC5.1 - X264-Rapta", 5, 562);
        var ix = new Mock<IIndexerClient>(MockBehavior.Strict);
        ix.SetupGet(i => i.IsEnabled).Returns(true);
        ix.SetupGet(i => i.Name).Returns("ix");
        ix.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { pack, exact });

        var cfg = TestConfig();
        cfg.QualityPreset = QualityPreset.ResolutionSeeders;
        cfg.PreferredResolution = "1080p";
        cfg.ResolutionFallbackOrder = "1080p,720p,480p,unknown";
        var sel = new MagnetSelector(
            new[] { ix.Object },
            new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance),
            NullLogger<MagnetSelector>.Instance,
            () => cfg);

        var ranked = await sel.SelectRankedAsync(246, "tt0417299", "episode", 2, 1, "Avatar The Last Airbender", 2005, CancellationToken.None);

        Assert.Equal(exact.Magnet, ranked[0].Magnet);
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
    public async Task OnlyTorrentioEnabled_NoImdb_ReturnsNoCapableIndexer()
    {
        // Torrentio abstains (IndexerNotApplicableException) when no IMDB id is
        // available. With no other capable indexer, this must be NoCapableIndexer,
        // not IndeterminateTransient (which would churn) nor DefinitiveUnavailable
        // (nothing actually ran).
        var torrentio = new Mock<IIndexerClient>(MockBehavior.Strict);
        torrentio.SetupGet(i => i.IsEnabled).Returns(true);
        torrentio.SetupGet(i => i.Name).Returns("Torrentio");
        torrentio.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IndexerNotApplicableException("Torrentio requires an IMDB id"));

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { torrentio.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        var probe = await sel.ProbeAsync(1, null, "movie", null, null, "Movie", 2020, CancellationToken.None);

        Assert.Equal(MagnetProbeOutcome.NoCapableIndexer, probe.Outcome);
        Assert.Empty(probe.Candidates);
        Assert.Equal("no_capable_indexer", probe.ErrorKind);
    }

    [Fact]
    public async Task ProwlarrEnabled_NoImdb_TorrentioAbstains_ReturnsAvailable()
    {
        // Prowlarr is title-based and does not need an IMDB id. Torrentio abstains,
        // but Prowlarr returns a candidate, so the probe is Available.
        var prowlarr = new Mock<IIndexerClient>(MockBehavior.Strict);
        prowlarr.SetupGet(i => i.IsEnabled).Returns(true);
        prowlarr.SetupGet(i => i.Name).Returns("Prowlarr");
        prowlarr.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeCandidate("Movie 1080p", 5, 30) });

        var torrentio = new Mock<IIndexerClient>(MockBehavior.Strict);
        torrentio.SetupGet(i => i.IsEnabled).Returns(true);
        torrentio.SetupGet(i => i.Name).Returns("Torrentio");
        torrentio.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IndexerNotApplicableException("Torrentio requires an IMDB id"));

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { prowlarr.Object, torrentio.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        var probe = await sel.ProbeAsync(1, null, "movie", null, null, "Movie", 2020, CancellationToken.None);

        Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
        Assert.Single(probe.Candidates);
    }

    [Fact]
    public void HasCapableIndexer_TorrentioOnly_NoImdb_ReturnsFalse()
    {
        // p6-prefilter-unavailable: mirrors ProbeAsync's abstention logic so the
        // availability-sweep claim path can pre-classify without a probe call.
        var torrentio = new Mock<IIndexerClient>(MockBehavior.Strict);
        torrentio.SetupGet(i => i.IsEnabled).Returns(true);
        torrentio.SetupGet(i => i.RequiresImdb).Returns(true);

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { torrentio.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        Assert.False(sel.HasCapableIndexer(null));
    }

    [Fact]
    public void HasCapableIndexer_TorrentioOnly_WithImdb_ReturnsTrue()
    {
        var torrentio = new Mock<IIndexerClient>(MockBehavior.Strict);
        torrentio.SetupGet(i => i.IsEnabled).Returns(true);
        torrentio.SetupGet(i => i.RequiresImdb).Returns(true);

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { torrentio.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        Assert.True(sel.HasCapableIndexer("tt1234567"));
    }

    [Fact]
    public void HasCapableIndexer_ProwlarrEnabled_NoImdb_ReturnsTrue()
    {
        // p6-prowlarr-indexer-wiring: Prowlarr is title-based, so once it is part
        // of the capability set a no-IMDB title must NOT be pre-classified as
        // "no capable indexer" — that would wrongly deep-defer a title Prowlarr
        // could still probe.
        var prowlarr = new Mock<IIndexerClient>(MockBehavior.Strict);
        prowlarr.SetupGet(i => i.IsEnabled).Returns(true);
        prowlarr.SetupGet(i => i.RequiresImdb).Returns(false);

        var torrentio = new Mock<IIndexerClient>(MockBehavior.Strict);
        torrentio.SetupGet(i => i.IsEnabled).Returns(true);
        torrentio.SetupGet(i => i.RequiresImdb).Returns(true);

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { prowlarr.Object, torrentio.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        Assert.True(sel.HasCapableIndexer(null));
    }

    [Fact]
    public void HasCapableIndexer_NoEnabledIndexers_ReturnsTrue()
    {
        // No enabled indexers is a configuration gap, not a per-title fact — it
        // must stay on the ordinary transient retry cadence, not deep-defer.
        var disabled = new Mock<IIndexerClient>(MockBehavior.Strict);
        disabled.SetupGet(i => i.IsEnabled).Returns(false);

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { disabled.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        Assert.True(sel.HasCapableIndexer(null));
    }

    [Fact]
    public async Task BothCapable_Movie_ProwlarrHigherQuality_ProwlarrCandidateWins()
    {
        // p6-prowlarr-indexer-wiring "ranking case": when BOTH Torrentio and Prowlarr are
        // capable (an IMDB id is present so Torrentio does NOT abstain) and both return
        // candidates, the better RELEASE wins regardless of which indexer produced it — a
        // configured Prowlarr must not be starved by always preferring Torrentio, nor must a
        // higher-quality Torrentio release lose to a lesser Prowlarr one.
        var prowlarr = new Mock<IIndexerClient>(MockBehavior.Strict);
        prowlarr.SetupGet(i => i.IsEnabled).Returns(true);
        prowlarr.SetupGet(i => i.Name).Returns("Prowlarr");
        prowlarr.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeCandidate("Movie 1080p Prowlarr", 8, 50) });

        var torrentio = new Mock<IIndexerClient>(MockBehavior.Strict);
        torrentio.SetupGet(i => i.IsEnabled).Returns(true);
        torrentio.SetupGet(i => i.Name).Returns("Torrentio");
        torrentio.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeCandidate("Movie 480p Torrentio", 1, 5) });

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { torrentio.Object, prowlarr.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        var probe = await sel.ProbeAsync(1, "tt1234567", "movie", null, null, "Movie", 2020, CancellationToken.None);

        Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
        Assert.NotEmpty(probe.Candidates);
        Assert.Contains("Prowlarr", probe.Candidates[0].Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BothCapable_Episode_TorrentioHigherQuality_TorrentioCandidateWins()
    {
        // Episode-parity counterpart: with an IMDB id present (so Torrentio is capable too),
        // a higher-quality Torrentio release beats a lower-quality Prowlarr one — proving
        // Torrentio is not starved either once Prowlarr is wired in.
        var prowlarr = new Mock<IIndexerClient>(MockBehavior.Strict);
        prowlarr.SetupGet(i => i.IsEnabled).Returns(true);
        prowlarr.SetupGet(i => i.Name).Returns("Prowlarr");
        prowlarr.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeCandidate("Show S01E01 480p Prowlarr", 1, 5) });

        var torrentio = new Mock<IIndexerClient>(MockBehavior.Strict);
        torrentio.SetupGet(i => i.IsEnabled).Returns(true);
        torrentio.SetupGet(i => i.Name).Returns("Torrentio");
        torrentio.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeCandidate("Show S01E01 1080p Torrentio", 8, 50) });

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { prowlarr.Object, torrentio.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        var probe = await sel.ProbeAsync(99, "tt9999999", "episode", 1, 1, "Show", 2020, CancellationToken.None);

        Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
        Assert.NotEmpty(probe.Candidates);
        Assert.Contains("Torrentio", probe.Candidates[0].Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TorrentioAbstains_ProwlarrEmpty_ReturnsDefinitiveUnavailable()
    {
        // Prowlarr actually ran and returned nothing; Torrentio abstained. Since a
        // capable indexer produced a genuine empty result, this is definitive.
        var prowlarr = new Mock<IIndexerClient>(MockBehavior.Strict);
        prowlarr.SetupGet(i => i.IsEnabled).Returns(true);
        prowlarr.SetupGet(i => i.Name).Returns("Prowlarr");
        prowlarr.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<IndexerCandidate>());

        var torrentio = new Mock<IIndexerClient>(MockBehavior.Strict);
        torrentio.SetupGet(i => i.IsEnabled).Returns(true);
        torrentio.SetupGet(i => i.Name).Returns("Torrentio");
        torrentio.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IndexerNotApplicableException("Torrentio requires an IMDB id"));

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { prowlarr.Object, torrentio.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        var probe = await sel.ProbeAsync(1, null, "movie", null, null, "Movie", 2020, CancellationToken.None);

        Assert.Equal(MagnetProbeOutcome.DefinitiveUnavailable, probe.Outcome);
    }

    [Fact]
    public async Task OneRealTransient_OtherAbstains_NoCandidates_ReturnsIndeterminateTransient()
    {
        // A real transient failure dominates an abstention: retrying may help.
        var prowlarr = new Mock<IIndexerClient>(MockBehavior.Strict);
        prowlarr.SetupGet(i => i.IsEnabled).Returns(true);
        prowlarr.SetupGet(i => i.Name).Returns("Prowlarr");
        prowlarr.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IndexerTransientException("timeout"));

        var torrentio = new Mock<IIndexerClient>(MockBehavior.Strict);
        torrentio.SetupGet(i => i.IsEnabled).Returns(true);
        torrentio.SetupGet(i => i.Name).Returns("Torrentio");
        torrentio.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IndexerNotApplicableException("Torrentio requires an IMDB id"));

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { prowlarr.Object, torrentio.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        var probe = await sel.ProbeAsync(1, null, "movie", null, null, "Movie", 2020, CancellationToken.None);

        Assert.Equal(MagnetProbeOutcome.IndeterminateTransient, probe.Outcome);
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

    // --- p6-decouple-oracle-magnetcache: availability oracle (Torrentio-only) ---
    // ROI Priority 6, revised architecture item 1 (2026-08-26). ProbeAvailabilityAsync
    // is the hot per-item availability-sweep path and must invoke ONLY an indexer with
    // IsAvailabilityOracle=true (Torrentio); Prowlarr's heavy fan-out must never run
    // in this loop even when it is enabled.

    [Fact]
    public async Task ProbeAvailabilityAsync_Movie_WithImdb_CallsTorrentioOnly_NotProwlarr()
    {
        var torrentio = new Mock<IIndexerClient>(MockBehavior.Strict);
        torrentio.SetupGet(i => i.IsEnabled).Returns(true);
        torrentio.SetupGet(i => i.Name).Returns("Torrentio");
        torrentio.SetupGet(i => i.IsAvailabilityOracle).Returns(true);
        torrentio.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeCandidate("Movie 1080p Torrentio", 5, 40) });

        var prowlarr = new Mock<IIndexerClient>(MockBehavior.Strict);
        prowlarr.SetupGet(i => i.IsEnabled).Returns(true);
        prowlarr.SetupGet(i => i.Name).Returns("Prowlarr");
        prowlarr.SetupGet(i => i.IsAvailabilityOracle).Returns(false);
        // Deliberately NO SearchAsync setup on Prowlarr (Strict mock): the availability
        // oracle must never call it. Any invocation throws a MockException, failing
        // the test — the assertion IS the call-count check.

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { torrentio.Object, prowlarr.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        var probe = await sel.ProbeAvailabilityAsync(1, "tt1234567", "movie", null, null, "Movie", 2020, CancellationToken.None);

        Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
        Assert.Single(probe.Candidates);
        torrentio.Verify(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        prowlarr.Verify(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProbeAvailabilityAsync_Episode_WithImdb_CallsTorrentioOnly_NotProwlarr()
    {
        // Episode parity counterpart of the movie assertion above.
        var torrentio = new Mock<IIndexerClient>(MockBehavior.Strict);
        torrentio.SetupGet(i => i.IsEnabled).Returns(true);
        torrentio.SetupGet(i => i.Name).Returns("Torrentio");
        torrentio.SetupGet(i => i.IsAvailabilityOracle).Returns(true);
        torrentio.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeCandidate("Show S01E01 1080p Torrentio", 5, 40) });

        var prowlarr = new Mock<IIndexerClient>(MockBehavior.Strict);
        prowlarr.SetupGet(i => i.IsEnabled).Returns(true);
        prowlarr.SetupGet(i => i.Name).Returns("Prowlarr");
        prowlarr.SetupGet(i => i.IsAvailabilityOracle).Returns(false);
        // No SearchAsync setup: any call fails the (Strict) mock.

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { torrentio.Object, prowlarr.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        var probe = await sel.ProbeAvailabilityAsync(99, "tt9999999", "episode", 1, 1, "Show", 2020, CancellationToken.None);

        Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
        torrentio.Verify(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()), Times.Once);
        prowlarr.Verify(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProbeAvailabilityAsync_NoImdb_TorrentioAbstains_ProwlarrFindsNothing_FallsBackToNoCapableIndexer()
    {
        // availability-probe-reconcile-001 item 1: No-IMDB item, Torrentio (the
        // only availability-oracle indexer) abstains via
        // IndexerNotApplicableException. Unlike the OLD behaviour (Prowlarr
        // never invoked), the sweep now attempts a Prowlarr RECONCILE — but
        // here Prowlarr finds nothing either, so the final outcome is still
        // the same no-capable-indexer deep-defer as before.
        var torrentio = new Mock<IIndexerClient>(MockBehavior.Strict);
        torrentio.SetupGet(i => i.IsEnabled).Returns(true);
        torrentio.SetupGet(i => i.Name).Returns("Torrentio");
        torrentio.SetupGet(i => i.IsAvailabilityOracle).Returns(true);
        torrentio.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IndexerNotApplicableException("Torrentio requires an IMDB id"));

        var prowlarr = new Mock<IIndexerClient>(MockBehavior.Strict);
        prowlarr.SetupGet(i => i.IsEnabled).Returns(true);
        prowlarr.SetupGet(i => i.Name).Returns("Prowlarr");
        prowlarr.SetupGet(i => i.IsAvailabilityOracle).Returns(false);
        prowlarr.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<IndexerCandidate>)Array.Empty<IndexerCandidate>());

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { torrentio.Object, prowlarr.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        var probe = await sel.ProbeAvailabilityAsync(1, null, "movie", null, null, "Movie", 2020, CancellationToken.None);

        Assert.Equal(MagnetProbeOutcome.NoCapableIndexer, probe.Outcome);
        prowlarr.Verify(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProbeAvailabilityAsync_NoImdb_TorrentioAbstains_ProwlarrHighConfidence_ReconcilesToAvailable()
    {
        // availability-probe-reconcile-001 item 1, positive case: Torrentio
        // abstains but Prowlarr holds a magnet clearing the same
        // MinSeeders/size bar the oracle path enforces — the reconcile must
        // reach a DEFINITIVE available verdict rather than surfacing
        // NoCapableIndexer/availability_abstain.
        var torrentio = new Mock<IIndexerClient>(MockBehavior.Strict);
        torrentio.SetupGet(i => i.IsEnabled).Returns(true);
        torrentio.SetupGet(i => i.Name).Returns("Torrentio");
        torrentio.SetupGet(i => i.IsAvailabilityOracle).Returns(true);
        torrentio.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IndexerNotApplicableException("Torrentio requires an IMDB id"));

        var prowlarr = new Mock<IIndexerClient>(MockBehavior.Strict);
        prowlarr.SetupGet(i => i.IsEnabled).Returns(true);
        prowlarr.SetupGet(i => i.Name).Returns("Prowlarr");
        prowlarr.SetupGet(i => i.IsAvailabilityOracle).Returns(false);
        prowlarr.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<IndexerCandidate>)new[]
            {
                new IndexerCandidate
                {
                    Title = "Movie 2020 1080p",
                    Magnet = "magnet:?xt=urn:btih:" + new string('a', 40),
                    InfoHash = new string('a', 40),
                    Size = 5L * 1024 * 1024 * 1024,
                    Seeders = 40,
                    IndexerName = "Prowlarr",
                },
            });

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { torrentio.Object, prowlarr.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        var probe = await sel.ProbeAvailabilityAsync(1, null, "movie", null, null, "Movie", 2020, CancellationToken.None);

        Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
        Assert.NotEmpty(probe.Candidates);
        prowlarr.Verify(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProbeAvailabilityAsync_ImdbBearing_TorrentioAvailable_NeverInvokesProwlarrReconcile()
    {
        // Parity guard: when the oracle itself resolves definitively, the
        // reconcile fan-out must never fire.
        var torrentio = new Mock<IIndexerClient>(MockBehavior.Strict);
        torrentio.SetupGet(i => i.IsEnabled).Returns(true);
        torrentio.SetupGet(i => i.Name).Returns("Torrentio");
        torrentio.SetupGet(i => i.IsAvailabilityOracle).Returns(true);
        torrentio.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<IndexerCandidate>)new[]
            {
                new IndexerCandidate
                {
                    Title = "Movie 2020 1080p",
                    Magnet = "magnet:?xt=urn:btih:" + new string('b', 40),
                    InfoHash = new string('b', 40),
                    Size = 5L * 1024 * 1024 * 1024,
                    Seeders = 40,
                    IndexerName = "Torrentio",
                },
            });

        var prowlarr = new Mock<IIndexerClient>(MockBehavior.Strict);
        prowlarr.SetupGet(i => i.IsEnabled).Returns(true);
        prowlarr.SetupGet(i => i.Name).Returns("Prowlarr");
        prowlarr.SetupGet(i => i.IsAvailabilityOracle).Returns(false);
        // No SearchAsync setup: any call fails the (Strict) mock.

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { torrentio.Object, prowlarr.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        var probe = await sel.ProbeAvailabilityAsync(1, "tt0000001", "movie", null, null, "Movie", 2020, CancellationToken.None);

        Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
        prowlarr.Verify(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public void HasCapableAvailabilityIndexer_ProwlarrEnabledOnly_NoTorrentio_NoImdb_ReturnsTrue()
    {
        // A Prowlarr-only configuration has ZERO availability-oracle-eligible
        // indexers enabled — the same "config gap, not a per-title fact" shape as
        // HasCapableIndexer_NoEnabledIndexers_ReturnsTrue, just scoped to the
        // availability-oracle set. It must stay on the ordinary transient retry
        // cadence (true = "capable"), not deep-defer, so it recovers promptly once
        // Torrentio is actually configured.
        var prowlarr = new Mock<IIndexerClient>(MockBehavior.Strict);
        prowlarr.SetupGet(i => i.IsEnabled).Returns(true);
        prowlarr.SetupGet(i => i.RequiresImdb).Returns(false);
        prowlarr.SetupGet(i => i.IsAvailabilityOracle).Returns(false);

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { prowlarr.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        Assert.True(sel.HasCapableAvailabilityIndexer(null));
        Assert.True(sel.HasCapableIndexer(null));
    }

    [Fact]
    public void HasCapableAvailabilityIndexer_TorrentioAndProwlarrEnabled_NoImdb_ReturnsTrueViaReconcile()
    {
        // Torrentio (the only availability-oracle-eligible indexer) requires an
        // IMDB id and none is present. Unlike the pre-reconcile behaviour,
        // Prowlarr being enabled (a non-oracle, non-imdb-requiring indexer)
        // now MUST count here too (availability-probe-reconcile-001 item 1):
        // the sweep will reconcile the abstained oracle verdict against
        // Prowlarr rather than deep-deferring purely on the oracle's abstention.
        var torrentio = new Mock<IIndexerClient>(MockBehavior.Strict);
        torrentio.SetupGet(i => i.IsEnabled).Returns(true);
        torrentio.SetupGet(i => i.RequiresImdb).Returns(true);
        torrentio.SetupGet(i => i.IsAvailabilityOracle).Returns(true);

        var prowlarr = new Mock<IIndexerClient>(MockBehavior.Strict);
        prowlarr.SetupGet(i => i.IsEnabled).Returns(true);
        prowlarr.SetupGet(i => i.RequiresImdb).Returns(false);
        prowlarr.SetupGet(i => i.IsAvailabilityOracle).Returns(false);

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { torrentio.Object, prowlarr.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        Assert.True(sel.HasCapableAvailabilityIndexer(null));
        Assert.True(sel.HasCapableIndexer(null));
    }

    [Fact]
    public void HasCapableAvailabilityIndexer_TorrentioOnly_NoImdb_ReturnsFalse()
    {
        // No reconcile-capable indexer at all (only the oracle, which requires
        // an imdb id that is absent) -> still correctly reports not capable.
        var torrentio = new Mock<IIndexerClient>(MockBehavior.Strict);
        torrentio.SetupGet(i => i.IsEnabled).Returns(true);
        torrentio.SetupGet(i => i.RequiresImdb).Returns(true);
        torrentio.SetupGet(i => i.IsAvailabilityOracle).Returns(true);

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { torrentio.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        Assert.False(sel.HasCapableAvailabilityIndexer(null));
    }

    [Fact]
    public void HasCapableAvailabilityIndexer_TorrentioEnabled_WithImdb_ReturnsTrue()
    {
        var torrentio = new Mock<IIndexerClient>(MockBehavior.Strict);
        torrentio.SetupGet(i => i.IsEnabled).Returns(true);
        torrentio.SetupGet(i => i.RequiresImdb).Returns(true);
        torrentio.SetupGet(i => i.IsAvailabilityOracle).Returns(true);

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { torrentio.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        Assert.True(sel.HasCapableAvailabilityIndexer("tt1234567"));
    }

    // ------------------------------------------------------------------
    // ttfb-parallel-indexer-probe: concurrent fan-out + per-indexer timeout.
    // ------------------------------------------------------------------

    [Fact]
    public async Task ProbeFanOut_RunsIndexersConcurrently_NotSequentially()
    {
        // Two indexers each block on a barrier until BOTH have entered
        // SearchAsync. A sequential foreach+await can never satisfy this
        // (the second indexer is not started until the first completes),
        // so completion of this probe proves concurrent fan-out. A short
        // guard timeout makes the test fail fast rather than hang if the
        // fan-out ever regresses to sequential.
        var entered = new CountdownEvent(2);
        var release = new TaskCompletionSource();

        IReadOnlyList<IndexerCandidate> Gate(string title)
        {
            entered.Signal();
            // Wait until both indexers are concurrently in-flight.
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            release.Task.Wait(TimeSpan.FromSeconds(10));
            return new[] { MakeCandidate(title, 5, 20) };
        }

        var ix1 = new Mock<IIndexerClient>(MockBehavior.Strict);
        ix1.SetupGet(i => i.IsEnabled).Returns(true);
        ix1.SetupGet(i => i.Name).Returns("ix1");
        ix1.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.Run(() => Gate("Movie A 1080p")));

        var ix2 = new Mock<IIndexerClient>(MockBehavior.Strict);
        ix2.SetupGet(i => i.IsEnabled).Returns(true);
        ix2.SetupGet(i => i.Name).Returns("ix2");
        ix2.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.Run(() => Gate("Movie B 1080p")));

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { ix1.Object, ix2.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        var probeTask = sel.ProbeAsync(1, "tt1", "movie", null, null, "Movie", 2020, CancellationToken.None);

        // Both indexers must be concurrently in-flight within the barrier
        // wait; only then release them.
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)), "both indexers were not concurrently in-flight — fan-out is sequential");
        release.SetResult();

        var completed = await Task.WhenAny(probeTask, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(probeTask, completed);
        var probe = await probeTask;
        Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
        Assert.Equal(2, probe.Candidates.Count);
    }

    [Fact]
    public async Task SlowIndexerTimesOut_FastIndexerResultStillReturned()
    {
        // A slow indexer that never returns within the per-indexer timeout
        // must be isolated (cancelled) without blocking or dropping the fast
        // indexer's real candidate.
        var cfg = TestConfig();
        cfg.IndexerProbeTimeoutSeconds = 5; // clamp floor; still fast in test

        var slow = new Mock<IIndexerClient>(MockBehavior.Strict);
        slow.SetupGet(i => i.IsEnabled).Returns(true);
        slow.SetupGet(i => i.Name).Returns("slow");
        slow.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .Returns(async (IndexerQuery _, CancellationToken t) =>
            {
                // Block until the linked per-indexer timeout cancels us.
                await Task.Delay(Timeout.Infinite, t).ConfigureAwait(false);
                return (IReadOnlyList<IndexerCandidate>)Array.Empty<IndexerCandidate>();
            });

        var fast = new Mock<IIndexerClient>(MockBehavior.Strict);
        fast.SetupGet(i => i.IsEnabled).Returns(true);
        fast.SetupGet(i => i.Name).Returns("fast");
        fast.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeCandidate("Movie 1080p", 5, 30) });

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { slow.Object, fast.Object }, scorer, NullLogger<MagnetSelector>.Instance, () => cfg);

        var probe = await sel.ProbeAsync(1, "tt1", "movie", null, null, "Movie", 2020, CancellationToken.None);

        // The fast indexer's candidate is returned; the slow indexer's timeout
        // did not block or drop it.
        Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
        Assert.Single(probe.Candidates);
        Assert.Equal(30, probe.Candidates[0].Seeders);
    }

    [Fact]
    public async Task CallerCancellation_PropagatesFromFanOut()
    {
        var ix = new Mock<IIndexerClient>(MockBehavior.Strict);
        ix.SetupGet(i => i.IsEnabled).Returns(true);
        ix.SetupGet(i => i.Name).Returns("ix");
        ix.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .Returns(async (IndexerQuery _, CancellationToken t) =>
            {
                await Task.Delay(Timeout.Infinite, t).ConfigureAwait(false);
                return (IReadOnlyList<IndexerCandidate>)Array.Empty<IndexerCandidate>();
            });

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { ix.Object }, scorer, NullLogger<MagnetSelector>.Instance, TestConfig);

        using var cts = new CancellationTokenSource();
        var probeTask = sel.ProbeAsync(1, "tt1", "movie", null, null, "Movie", 2020, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probeTask);
    }

    // ------------------------------------------------------------------
    // ttfb-fast-indexer-early-return: bounded early-return once a fast
    // indexer's candidates already satisfy the config floor.
    // ------------------------------------------------------------------

    [Fact]
    public async Task EarlyReturn_Enabled_ReturnsBeforeSlowIndexerDelayElapses()
    {
        var cfg = TestConfig();
        cfg.FastIndexerEarlyReturnEnabled = true;
        cfg.MinEarlyReturnCandidates = 1;
        cfg.EarlyReturnMinElapsedMs = 0;

        var slowReturned = new TaskCompletionSource();
        var slowDelay = TimeSpan.FromSeconds(5);

        var slow = new Mock<IIndexerClient>(MockBehavior.Strict);
        slow.SetupGet(i => i.IsEnabled).Returns(true);
        slow.SetupGet(i => i.Name).Returns("slow");
        slow.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .Returns(async (IndexerQuery _, CancellationToken t) =>
            {
                await Task.Delay(slowDelay, t).ConfigureAwait(false);
                var result = new[] { MakeCandidate("Slow Movie 1080p", 5, 40) };
                slowReturned.TrySetResult();
                return (IReadOnlyList<IndexerCandidate>)result;
            });

        var fast = new Mock<IIndexerClient>(MockBehavior.Strict);
        fast.SetupGet(i => i.IsEnabled).Returns(true);
        fast.SetupGet(i => i.Name).Returns("fast");
        fast.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeCandidate("Fast Movie 1080p", 5, 30) });

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { slow.Object, fast.Object }, scorer, NullLogger<MagnetSelector>.Instance, () => cfg);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var probe = await sel.ProbeAsync(1, "tt1", "movie", null, null, "Movie", 2020, CancellationToken.None);
        sw.Stop();

        // Returned well before the slow indexer's configured delay elapsed.
        Assert.True(sw.Elapsed < slowDelay, $"expected early return before {slowDelay}, took {sw.Elapsed}");
        Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
        Assert.Single(probe.Candidates);
        Assert.Equal(30, probe.Candidates[0].Seeders);

        // The slow indexer's result is still awaited/used internally in the
        // background — it is not discarded/cancelled — even though the
        // caller already got a response.
        var slowCompleted = await Task.WhenAny(slowReturned.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(slowReturned.Task, slowCompleted);
    }

    [Fact]
    public async Task EarlyReturn_Disabled_DefaultBehaviorUnchanged()
    {
        // Regression guard: with the toggle at its default (false), behavior
        // is byte-for-byte the same full-wait as before this feature existed
        // — the probe waits for BOTH indexers and aggregates both results.
        var cfg = TestConfig();
        Assert.False(cfg.FastIndexerEarlyReturnEnabled);

        var ix1 = new Mock<IIndexerClient>(MockBehavior.Strict);
        ix1.SetupGet(i => i.IsEnabled).Returns(true);
        ix1.SetupGet(i => i.Name).Returns("ix1");
        ix1.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeCandidate("Movie A 1080p", 5, 30) });

        var ix2 = new Mock<IIndexerClient>(MockBehavior.Strict);
        ix2.SetupGet(i => i.IsEnabled).Returns(true);
        ix2.SetupGet(i => i.Name).Returns("ix2");
        ix2.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeCandidate("Movie B 1080p", 5, 40) });

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { ix1.Object, ix2.Object }, scorer, NullLogger<MagnetSelector>.Instance, () => cfg);

        var probe = await sel.ProbeAsync(1, "tt1", "movie", null, null, "Movie", 2020, CancellationToken.None);

        Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
        Assert.Equal(2, probe.Candidates.Count);
    }

    [Fact]
    public async Task EarlyReturn_EnabledForEpisode_MoviesEpisodeParity()
    {
        // Same early-return behavior applies to the episode flow (movie/TV
        // parity — both share ProbeCoreAsync, no item-type special-casing).
        var cfg = TestConfig();
        cfg.FastIndexerEarlyReturnEnabled = true;
        cfg.MinEarlyReturnCandidates = 1;
        cfg.EarlyReturnMinElapsedMs = 0;
        var slowDelay = TimeSpan.FromSeconds(5);

        var slow = new Mock<IIndexerClient>(MockBehavior.Strict);
        slow.SetupGet(i => i.IsEnabled).Returns(true);
        slow.SetupGet(i => i.Name).Returns("slow");
        slow.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .Returns(async (IndexerQuery _, CancellationToken t) =>
            {
                await Task.Delay(slowDelay, t).ConfigureAwait(false);
                return (IReadOnlyList<IndexerCandidate>)new[] { MakeCandidate("Show S01E01", 2, 40) };
            });

        var fast = new Mock<IIndexerClient>(MockBehavior.Strict);
        fast.SetupGet(i => i.IsEnabled).Returns(true);
        fast.SetupGet(i => i.Name).Returns("fast");
        fast.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeCandidate("Show S01E01", 2, 30) });

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { slow.Object, fast.Object }, scorer, NullLogger<MagnetSelector>.Instance, () => cfg);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var probe = await sel.ProbeAsync(1, "tt1", "episode", 1, 1, "Show", 2020, CancellationToken.None);
        sw.Stop();

        Assert.True(sw.Elapsed < slowDelay, $"expected early return before {slowDelay}, took {sw.Elapsed}");
        Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
        Assert.Single(probe.Candidates);
        Assert.Equal(30, probe.Candidates[0].Seeders);
    }

    [Fact]
    public async Task EarlyReturn_MinElapsedFloorNotYetPassed_StillWaitsForSlowIndexer()
    {
        // Even with a fast indexer already satisfying MinEarlyReturnCandidates,
        // a non-zero EarlyReturnMinElapsedMs floor must be respected: the
        // probe must not return before that floor has elapsed, guarding
        // against a near-instant fast result triggering a premature early
        // return ahead of other indexers getting a fair chance to contribute.
        var cfg = TestConfig();
        cfg.FastIndexerEarlyReturnEnabled = true;
        cfg.MinEarlyReturnCandidates = 1;
        cfg.EarlyReturnMinElapsedMs = 300;

        var fast = new Mock<IIndexerClient>(MockBehavior.Strict);
        fast.SetupGet(i => i.IsEnabled).Returns(true);
        fast.SetupGet(i => i.Name).Returns("fast");
        fast.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { MakeCandidate("Movie A 1080p", 5, 30) });

        var slower = new Mock<IIndexerClient>(MockBehavior.Strict);
        slower.SetupGet(i => i.IsEnabled).Returns(true);
        slower.SetupGet(i => i.Name).Returns("slower");
        slower.Setup(i => i.SearchAsync(It.IsAny<IndexerQuery>(), It.IsAny<CancellationToken>()))
            .Returns(async (IndexerQuery _, CancellationToken t) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), t).ConfigureAwait(false);
                return (IReadOnlyList<IndexerCandidate>)new[] { MakeCandidate("Movie B 1080p", 5, 20) };
            });

        var scorer = new Materialisation.QualityScorer(NullLogger<Materialisation.QualityScorer>.Instance);
        var sel = new MagnetSelector(new[] { fast.Object, slower.Object }, scorer, NullLogger<MagnetSelector>.Instance, () => cfg);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var probe = await sel.ProbeAsync(1, "tt1", "movie", null, null, "Movie", 2020, CancellationToken.None);
        sw.Stop();

        // The elapsed floor (300ms) forced this probe to keep waiting past
        // the fast indexer's near-instant completion, so BOTH indexers'
        // candidates end up aggregated (the slower one finished at ~500ms,
        // itself past the 300ms floor, before the next WhenAny iteration
        // could re-check and early-return).
        Assert.True(sw.ElapsedMilliseconds >= 300, $"expected to honor the {cfg.EarlyReturnMinElapsedMs}ms floor, took {sw.Elapsed}");
        Assert.Equal(MagnetProbeOutcome.Available, probe.Outcome);
        Assert.Equal(2, probe.Candidates.Count);
    }
}
