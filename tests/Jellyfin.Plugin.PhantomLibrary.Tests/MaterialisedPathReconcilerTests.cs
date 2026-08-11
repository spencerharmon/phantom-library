using System;
using System.IO;
using Jellyfin.Plugin.PhantomLibrary.Scheduled;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public sealed class MaterialisedPathReconcilerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "phantom-reconcile-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    [Theory]
    [InlineData("Avatar_The_Last_Airbender_S01E01_994f84bb.mkv", 1, 1)]
    [InlineData("Avatar_the_Last_Airbender_S01E08_fab58ce8.mkv", 1, 8)]
    [InlineData("South_Park_S28E03_3cc2eddb.mkv", 28, 3)]
    [InlineData("Some.Show.s02e10.1080p.mkv", 2, 10)]
    public void TryParseSeasonEpisode_ParsesToken(string fileName, int wantS, int wantE)
    {
        Assert.True(MaterialisedPathReconciler.TryParseSeasonEpisode(fileName, out var s, out var e));
        Assert.Equal(wantS, s);
        Assert.Equal(wantE, e);
    }

    [Theory]
    [InlineData("Scary_Movie_2026_1080p_1b67ca10.mkv")]
    [InlineData("no-episode-token.mkv")]
    [InlineData("")]
    public void TryParseSeasonEpisode_RejectsNonEpisode(string fileName)
    {
        Assert.False(MaterialisedPathReconciler.TryParseSeasonEpisode(fileName, out _, out _));
    }

    [Fact]
    public void FindEpisodeFile_HealsDirAndHashDrift()
    {
        // Current tree: series-name dir + a NEW per-episode hash (both differ from
        // the originally-recorded tt-id dir + old hash).
        var seasonDir = Path.Combine(_root, "Avatar_The_Last_Airbender (2024)", "Season.01");
        Directory.CreateDirectory(seasonDir);
        var target = Path.Combine(seasonDir, "Avatar_The_Last_Airbender_S01E01_994f84bb.mkv");
        File.WriteAllText(target, "x");
        File.WriteAllText(Path.Combine(seasonDir, "Avatar_The_Last_Airbender_S01E02_994f84bb.mkv"), "x");

        var found = MaterialisedPathReconciler.FindEpisodeFile(
            Path.Combine(_root, "Avatar_The_Last_Airbender (2024)"), 1, 1);

        Assert.Equal(target, found);
    }

    [Fact]
    public void FindEpisodeFile_ReturnsNullWhenEpisodeAbsent()
    {
        // Series dir present but the specific episode is genuinely gone (must NOT
        // fall back to a different episode) -> stays unresolved / unavailable.
        var seasonDir = Path.Combine(_root, "Avatar_The_Last_Airbender (2005)", "Season.02");
        Directory.CreateDirectory(seasonDir);
        File.WriteAllText(Path.Combine(seasonDir, "Avatar_The_Last_Airbender_S02E08_042077a1.mkv"), "x");

        var found = MaterialisedPathReconciler.FindEpisodeFile(
            Path.Combine(_root, "Avatar_The_Last_Airbender (2005)"), 2, 4);

        Assert.Null(found);
    }

    [Fact]
    public void FindEpisodeFile_MissingSeriesDir_ReturnsNull()
    {
        Assert.Null(MaterialisedPathReconciler.FindEpisodeFile(Path.Combine(_root, "nope"), 1, 1));
    }
}
