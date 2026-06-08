using Jellyfin.Plugin.PhantomLibrary.Library;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests.Library;

public class PhantomPathUtilitiesTests
{
    // ── IsPhantomStubPath ──────────────────────────────────────────────

    [Theory]
    [InlineData("/var/lib/jellyfin/phantom-library/movies/The_Boys__phantom_tmdb1234.mp4")]
    [InlineData("/var/lib/jellyfin/phantom-library/shows/Severance__phantom_tmdb95396")]
    [InlineData("/var/lib/jellyfin/phantom-library/shows/Severance__phantom_tmdb95396/Season 01/Severance__phantom_tmdb95396 S01E01.mp4")]
    [InlineData("Some_Title__phantom_tmdb777")]
    public void LegacyForms_AreRecognised(string p)
        => Assert.True(PhantomPathUtilities.IsPhantomStubPath(p));

    [Theory]
    [InlineData("/var/lib/jellyfin/phantom-library/movies/The Boys (2019) [tmdbid-1234].mp4")]
    [InlineData("/var/lib/jellyfin/phantom-library/shows/Severance (2022) [tmdbid-95396]")]
    [InlineData("/var/lib/jellyfin/phantom-library/shows/Severance (2022) [tmdbid-95396]/Season 01/Severance (2022) S01E01.mp4")]
    [InlineData("Some Title [tmdbid-777]")]
    public void NewForms_AreRecognised(string p)
        => Assert.True(PhantomPathUtilities.IsPhantomStubPath(p));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/var/lib/jellyfin/media/movies/Real Movie (2020).mkv")]
    [InlineData("Inception (2010)")]
    [InlineData("Bracket name [imdbid-tt12345].mkv")] // not tmdbid
    public void NonPhantom_ReturnsFalse(string? p)
        => Assert.False(PhantomPathUtilities.IsPhantomStubPath(p));

    [Fact]
    public void MixedSegments_BothFormsRecognised()
    {
        // Legacy series dir holding a new-format episode (post-spike interop case).
        var p = "/var/lib/jellyfin/phantom-library/shows/Old__phantom_tmdb555/Season 01/New (2024) [tmdbid-555] S01E01.mp4";
        Assert.True(PhantomPathUtilities.IsPhantomStubPath(p));
    }

    // ── TryParseTmdbId ─────────────────────────────────────────────────

    [Theory]
    [InlineData("/movies/The Boys__phantom_tmdb1234.mp4", 1234)]
    [InlineData("/shows/Severance (2022) [tmdbid-95396]/Season 01/x.mp4", 95396)]
    [InlineData("Some Title [tmdbid-777]", 777)]
    [InlineData("Some_Title__phantom_tmdb777", 777)]
    public void TryParseTmdbId_ExtractsId(string p, int expected)
        => Assert.Equal(expected, PhantomPathUtilities.TryParseTmdbId(p));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/movies/Plain File.mkv")]
    public void TryParseTmdbId_ReturnsNullForNonPhantom(string? p)
        => Assert.Null(PhantomPathUtilities.TryParseTmdbId(p));

    [Fact]
    public void TryParseTmdbId_PrefersNewTokenWhenBothPresent()
    {
        // Theoretical mixed path: new token wins. (No real on-disk path
        // would carry both, but the deterministic-precedence guarantee
        // matters for the migration which reads both layouts.)
        var p = "/shows/Old__phantom_tmdb111/Season 01/New [tmdbid-222] S01E01.mp4";
        Assert.Equal(222, PhantomPathUtilities.TryParseTmdbId(p));
    }

    [Fact]
    public void IsLegacyStubPath_OnlyTrueForLegacy()
    {
        Assert.True(PhantomPathUtilities.IsLegacyStubPath("/m/X__phantom_tmdb1.mp4"));
        Assert.False(PhantomPathUtilities.IsLegacyStubPath("/m/X [tmdbid-1].mp4"));
        Assert.False(PhantomPathUtilities.IsLegacyStubPath(null));
        Assert.False(PhantomPathUtilities.IsLegacyStubPath(""));
    }
}
