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
