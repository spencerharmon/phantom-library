using System.Collections.Generic;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class QualityScorerTests
{
    private static QualityScorer New() => new(NullLogger<QualityScorer>.Instance);

    private static IndexerCandidate C(string title, long size, int seeders) => new()
    {
        Title = title,
        Magnet = "magnet:?xt=urn:btih:" + title.GetHashCode().ToString("X"),
        InfoHash = title.GetHashCode().ToString("X"),
        Size = size,
        Seeders = seeders,
    };

    private const long GB = 1024L * 1024 * 1024;

    [Fact]
    public void Gostream_Prefers_4K_DV_Over_4K_HDR()
    {
        var cands = new List<IndexerCandidate>
        {
            C("Movie 2160p HDR x265", 30 * GB, 100),
            C("Movie 2160p Dolby Vision x265", 30 * GB, 100),
        };
        var b = New().PickBest(cands, QualityPreset.GostreamDefault, 5, 4, 20);
        Assert.Contains("Dolby Vision", b!.Title);
    }

    [Fact]
    public void Gostream_Prefers_4K_Over_1080p()
    {
        var cands = new List<IndexerCandidate>
        {
            C("Movie 1080p", 5 * GB, 1000),
            C("Movie 2160p", 30 * GB, 50),
        };
        var b = New().PickBest(cands, QualityPreset.GostreamDefault, 5, 4, 20);
        Assert.Contains("2160p", b!.Title);
    }

    [Fact]
    public void Min_Seeders_Filter_Drops_Below_Floor()
    {
        var cands = new List<IndexerCandidate>
        {
            C("X 1080p", 5 * GB, 1),
        };
        Assert.Null(New().PickBest(cands, QualityPreset.GostreamDefault, 5, 4, 20));
    }

    [Fact]
    public void Min_Size_Filter_Drops_Below_Floor()
    {
        var cands = new List<IndexerCandidate>
        {
            C("X 1080p", 1 * GB, 100),
        };
        Assert.Null(New().PickBest(cands, QualityPreset.GostreamDefault, 5, 4, 20));
    }

    [Fact]
    public void Untagged_SD_HDRip_Below_1080p_Floor_Is_Rejected()
    {
        // Steven Universe Movie regression: Torrentio's only candidate
        // was "Steven.Universe.The.Movie.2019.HDRip.XviD.AC3-EVO" at
        // ~1.09 GB, no 1080p/4K tag. Without the SD-band guard, the
        // scorer passes it through and gostream rejects it with
        // 422 no_valid_files, surfacing as a misleading materialise
        // failure to the operator.
        var cands = new List<IndexerCandidate>
        {
            C("Steven.Universe.The.Movie.2019.HDRip.XviD.AC3-EVO", 1170378588L, 31),
        };
        Assert.Null(New().PickBest(cands, QualityPreset.GostreamDefault, 5, 4, 20));
    }

    [Fact]
    public void Untagged_Large_Release_Passes_When_Above_Floor()
    {
        // A release with no resolution tag but above the 1080p min size
        // (e.g. a generic BluRay rip) must still be accepted — gostream's
        // size band will accept it too.
        var cands = new List<IndexerCandidate>
        {
            C("Movie.2019.BluRay.x264", 8 * GB, 100),
        };
        Assert.NotNull(New().PickBest(cands, QualityPreset.GostreamDefault, 5, 4, 20));
    }

    [Fact]
    public void BiggestMostSeeded_Picks_Biggest_Then_Seeders()
    {
        var cands = new List<IndexerCandidate>
        {
            C("A 1080p", 5 * GB, 1000),
            C("B 2160p", 50 * GB, 50),
            C("C 2160p", 50 * GB, 80),
        };
        var b = New().PickBest(cands, QualityPreset.BiggestMostSeeded, 5, 4, 20);
        Assert.Equal("C 2160p", b!.Title);
    }

    [Fact]
    public void ResolutionSeeders_Prefers_1080p_Over_4K_ByDefault()
    {
        var cands = new List<IndexerCandidate>
        {
            C("Movie 2160p DV Atmos", 30 * GB, 50),
            C("Movie 1080p", 5 * GB, 50),
        };

        var b = New().PickBest(cands, QualityPreset.ResolutionSeeders, 5, 4, 20,
            "1080p,720p,480p,2160p,4k,unknown", 3, "1080p");

        Assert.Equal("Movie 1080p", b!.Title);
    }

    [Fact]
    public void ResolutionSeeders_SeederWeight_Can_Outrank_Within_FallbackPolicy()
    {
        var cands = new List<IndexerCandidate>
        {
            C("Movie 1080p", 5 * GB, 10),
            C("Movie 720p", 5 * GB, 500),
        };

        var b = New().PickBest(cands, QualityPreset.ResolutionSeeders, 5, 0, 20,
            "1080p,720p,480p,2160p,4k,unknown", 3, "1080p");

        Assert.Equal("Movie 720p", b!.Title);
    }

    [Fact]
    public void ResolutionSeeders_Allows_Lower_Resolution_When_Only_Candidate()
    {
        var cands = new List<IndexerCandidate>
        {
            C("Movie 720p", 2 * GB, 50),
        };

        var b = New().PickBest(cands, QualityPreset.ResolutionSeeders, 5, 4, 20,
            "1080p,720p,480p,2160p,4k,unknown", 3, "1080p");

        Assert.Equal("Movie 720p", b!.Title);
    }

    [Fact]
    public void ResolutionSeeders_PreferredResolution_Moves_Preferred_To_Front()
    {
        var cands = new List<IndexerCandidate>
        {
            C("Movie 1080p", 5 * GB, 50),
            C("Movie 720p", 5 * GB, 50),
        };

        var b = New().PickBest(cands, QualityPreset.ResolutionSeeders, 5, 0, 20,
            "1080p,720p,480p,2160p,4k,unknown", 3, "720p");

        Assert.Equal("Movie 720p", b!.Title);
    }

    [Fact]
    public void Custom_Falls_Back_To_Default()
    {
        var cands = new List<IndexerCandidate>
        {
            C("X 1080p", 5 * GB, 50),
        };
        var b = New().PickBest(cands, QualityPreset.Custom, 5, 4, 20);
        Assert.NotNull(b);
    }
}
