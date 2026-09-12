using Phantom.Tools.VersionBump;
using Xunit;

namespace PhantomVersionBump.Tests;

public class PluginVersionTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3, 0)]
    [InlineData("1.2.3.4", 1, 2, 3, 4)]
    [InlineData("0.5.5.0", 0, 5, 5, 0)]
    public void Parse_ReadsPartsCorrectly(string text, int major, int minor, int patch, int revision)
    {
        var v = PluginVersion.Parse(text);
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
        Assert.Equal(revision, v.Revision);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4.5")]
    [InlineData("a.b.c")]
    [InlineData("-1.2.3")]
    public void Parse_RejectsInvalidInput(string text)
    {
        Assert.ThrowsAny<Exception>(() => PluginVersion.Parse(text));
    }

    [Fact]
    public void ToFourPart_And_ToThreePart_FormatCorrectly()
    {
        var v = new PluginVersion(1, 2, 3, 4);
        Assert.Equal("1.2.3.4", v.ToFourPart());
        Assert.Equal("1.2.3", v.ToThreePart());
    }

    [Fact]
    public void BumpRevision_IncrementsOnlyRevision()
    {
        var v = new PluginVersion(1, 2, 3, 4);
        var bumped = v.BumpRevision();
        Assert.Equal(new PluginVersion(1, 2, 3, 5), bumped);
    }

    [Theory]
    [InlineData("1.0.0.0", "1.0.0.1", true)]
    [InlineData("1.0.0.1", "1.0.0.0", false)]
    [InlineData("1.0.0.0", "1.0.0.0", false)]
    [InlineData("1.0.0.0", "1.1.0.0", true)]
    [InlineData("1.9.0.0", "2.0.0.0", true)]
    public void Comparison_IsPartWiseNumeric(string aText, string bText, bool aLessThanB)
    {
        var a = PluginVersion.Parse(aText);
        var b = PluginVersion.Parse(bText);
        Assert.Equal(aLessThanB, a < b);
        Assert.Equal(!aLessThanB && a != b, a > b);
    }

    [Fact]
    public void Equality_ComparesAllParts()
    {
        var a = PluginVersion.Parse("1.2.3.4");
        var b = new PluginVersion(1, 2, 3, 4);
        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
    }

    [Fact]
    public void Constructor_RejectsNegativeParts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PluginVersion(-1, 0, 0, 0));
    }
}
