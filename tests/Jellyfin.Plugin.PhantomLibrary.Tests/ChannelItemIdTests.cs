using Jellyfin.Plugin.PhantomLibrary.Channels;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class ChannelItemIdTests
{
    // ---- round-trips ----

    [Fact]
    public void Movie_EncodeDecode_Roundtrips()
    {
        var id = ChannelItemId.ForMovie(42);
        Assert.Equal("movie_42", id.Encode());
        var parsed = ChannelItemId.Parse("movie_42");
        Assert.Equal("movie", parsed.Kind);
        Assert.Equal(42, parsed.TmdbId);
        Assert.Null(parsed.Season);
        Assert.Null(parsed.Episode);
        Assert.Null(parsed.OrphanHash);
    }

    [Fact]
    public void Series_EncodeDecode_Roundtrips()
    {
        var id = ChannelItemId.ForSeries(1399);
        Assert.Equal("series_1399", id.Encode());
        var parsed = ChannelItemId.Parse("series_1399");
        Assert.Equal("series", parsed.Kind);
        Assert.Equal(1399, parsed.TmdbId);
    }

    [Fact]
    public void Season_EncodeDecode_Roundtrips()
    {
        var id = ChannelItemId.ForSeason(1399, 3);
        Assert.Equal("season_1399_s03", id.Encode());
        var parsed = ChannelItemId.Parse("season_1399_s03");
        Assert.Equal("season", parsed.Kind);
        Assert.Equal(1399, parsed.TmdbId);
        Assert.Equal(3, parsed.Season);
        Assert.Null(parsed.Episode);
    }

    [Fact]
    public void Season_LargeNumber_RoundtripsUnpadded()
    {
        var id = ChannelItemId.ForSeason(1399, 100);
        Assert.Equal("season_1399_s100", id.Encode());
        var parsed = ChannelItemId.Parse("season_1399_s100");
        Assert.Equal(100, parsed.Season);
    }

    [Fact]
    public void Episode_EncodeDecode_Roundtrips()
    {
        var id = ChannelItemId.ForEpisode(1399, 1, 5);
        Assert.Equal("episode_1399_s01e05", id.Encode());
        var parsed = ChannelItemId.Parse("episode_1399_s01e05");
        Assert.Equal("episode", parsed.Kind);
        Assert.Equal(1399, parsed.TmdbId);
        Assert.Equal(1, parsed.Season);
        Assert.Equal(5, parsed.Episode);
    }

    [Fact]
    public void Episode_LargeNumbers_RoundtripsUnpadded()
    {
        var id = ChannelItemId.ForEpisode(1399, 12, 100);
        Assert.Equal("episode_1399_s12e100", id.Encode());
        var parsed = ChannelItemId.Parse("episode_1399_s12e100");
        Assert.Equal(12, parsed.Season);
        Assert.Equal(100, parsed.Episode);
    }

    [Fact]
    public void Orphan_EncodeDecode_Roundtrips()
    {
        var id = ChannelItemId.ForOrphanPath("/var/gostream/foo.mkv");
        var encoded = id.Encode();
        Assert.StartsWith("orphan_", encoded);
        Assert.Equal(7 + 16, encoded.Length);
        var parsed = ChannelItemId.Parse(encoded);
        Assert.Equal("orphan", parsed.Kind);
        Assert.Equal(id.OrphanHash, parsed.OrphanHash);
    }

    // ---- orphan stability ----

    [Fact]
    public void Orphan_SamePath_ProducesSameHash()
    {
        var a = ChannelItemId.ForOrphanPath("/var/gostream/foo.mkv");
        var b = ChannelItemId.ForOrphanPath("/var/gostream/foo.mkv");
        Assert.Equal(a.OrphanHash, b.OrphanHash);
    }

    [Fact]
    public void Orphan_DifferentPaths_ProduceDifferentHashes()
    {
        var a = ChannelItemId.ForOrphanPath("/var/gostream/foo.mkv");
        var b = ChannelItemId.ForOrphanPath("/var/gostream/bar.mkv");
        Assert.NotEqual(a.OrphanHash, b.OrphanHash);
    }

    [Fact]
    public void Orphan_RenamedPath_ProducesDifferentHash()
    {
        // Documented behaviour: rename = new id; UserData on the old
        // id orphans. See ChannelItemId XML doc for rationale.
        var a = ChannelItemId.ForOrphanPath("/var/gostream/movies/foo.mkv");
        var b = ChannelItemId.ForOrphanPath("/var/gostream/movies/foo (1).mkv");
        Assert.NotEqual(a.OrphanHash, b.OrphanHash);
    }

    // ---- rejections ----

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("movie_")]
    [InlineData("movie_abc")]
    [InlineData("season_42")]              // missing _s<NN>
    [InlineData("season_42_s")]            // missing season number
    [InlineData("episode_42_s01")]         // missing e<NN>
    [InlineData("episode_42_s01e")]        // empty episode
    [InlineData("orphan_")]                // empty hash
    [InlineData("orphan_xyz")]             // non-hex
    public void Parse_Invalid_ReturnsFalse(string input)
    {
        Assert.False(ChannelItemId.TryParse(input, out _));
    }

    [Fact]
    public void Parse_Null_ReturnsFalse()
    {
        Assert.False(ChannelItemId.TryParse(null, out _));
    }

    [Fact]
    public void Parse_Garbage_Throws()
    {
        Assert.Throws<System.FormatException>(() => ChannelItemId.Parse("not_a_real_id"));
    }

    // ---- sentinel conversion ----

    [Fact]
    public void Sentinels_NullSeasonEpisode_BecomesMinusOne()
    {
        var (s, e) = ChannelItemId.ToSentinels(null, null);
        Assert.Equal(-1, s);
        Assert.Equal(-1, e);
    }

    [Fact]
    public void Sentinels_RealSeasonEpisode_PassesThrough()
    {
        var (s, e) = ChannelItemId.ToSentinels(3, 7);
        Assert.Equal(3, s);
        Assert.Equal(7, e);
    }

    [Fact]
    public void Sentinels_MinusOne_BecomesNull()
    {
        var (s, e) = ChannelItemId.FromSentinels(-1, -1);
        Assert.Null(s);
        Assert.Null(e);
    }

    [Fact]
    public void Sentinels_RealValues_FromSentinels_PassesThrough()
    {
        var (s, e) = ChannelItemId.FromSentinels(3, 7);
        Assert.Equal(3, s);
        Assert.Equal(7, e);
    }

    [Fact]
    public void Sentinels_Symmetry_NullRoundtrips()
    {
        var (s1, e1) = ChannelItemId.ToSentinels(null, null);
        var (s2, e2) = ChannelItemId.FromSentinels(s1, e1);
        Assert.Null(s2);
        Assert.Null(e2);
    }

    [Fact]
    public void Sentinels_Symmetry_RealValuesRoundtrip()
    {
        var (s1, e1) = ChannelItemId.ToSentinels(5, 12);
        var (s2, e2) = ChannelItemId.FromSentinels(s1, e1);
        Assert.Equal(5, s2);
        Assert.Equal(12, e2);
    }

    // ---- critical: id is stable across materialise state ----

    [Fact]
    public void ForMovie_Id_DoesNotDependOnMaterialiseState()
    {
        // Per plan §2.3 BLOCKER 1 fix: the id for tmdb=42 is the same
        // string regardless of whether tmdb=42 is in materialised_state.
        // This test documents that contract.
        Assert.Equal("movie_42", ChannelItemId.ForMovie(42).Encode());
    }

    [Fact]
    public void ForEpisode_Id_DoesNotDependOnMaterialiseState()
    {
        Assert.Equal("episode_1399_s01e01", ChannelItemId.ForEpisode(1399, 1, 1).Encode());
    }
}
