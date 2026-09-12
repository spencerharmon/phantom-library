using Phantom.Tools.VersionBump;
using Xunit;

namespace PhantomVersionBump.Tests;

public class VersionRewriterTests
{
    private const string SampleBuildYaml =
        "name: \"Phantom Library\"\n" +
        "guid: \"11111111-1111-1111-1111-111111111111\"\n" +
        "version: \"0.5.5.0\"\n" +
        "targetAbi: \"10.11.0.0\"\n" +
        "framework: \"net9.0\"\n";

    private const string SampleCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
        "  <PropertyGroup>\n" +
        "    <AssemblyVersion>0.5.4.0</AssemblyVersion>\n" +
        "    <FileVersion>0.5.4.0</FileVersion>\n" +
        "    <Version>0.5.4</Version>\n" +
        "  </PropertyGroup>\n" +
        "</Project>\n";

    [Fact]
    public void ReadBuildYamlVersion_ParsesVersionLine()
    {
        var v = VersionRewriter.ReadBuildYamlVersion(SampleBuildYaml);
        Assert.Equal(new PluginVersion(0, 5, 5, 0), v);
    }

    [Fact]
    public void ReadBuildYamlVersion_ThrowsWhenMissing()
    {
        Assert.Throws<FormatException>(() => VersionRewriter.ReadBuildYamlVersion("name: \"x\"\n"));
    }

    [Fact]
    public void SetBuildYamlVersion_OnlyChangesVersionLine()
    {
        var target = new PluginVersion(0, 5, 5, 1);
        var updated = VersionRewriter.SetBuildYamlVersion(SampleBuildYaml, target);

        Assert.Contains("version: \"0.5.5.1\"", updated);
        Assert.Contains("targetAbi: \"10.11.0.0\"", updated);
        Assert.Contains("name: \"Phantom Library\"", updated);
    }

    [Fact]
    public void SetBuildYamlVersion_IsIdempotentForSameTarget()
    {
        var target = new PluginVersion(0, 5, 5, 1);
        var once = VersionRewriter.SetBuildYamlVersion(SampleBuildYaml, target);
        var twice = VersionRewriter.SetBuildYamlVersion(once, target);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void SetCsprojVersion_UpdatesAllThreeSurfacesInLockstep()
    {
        var target = new PluginVersion(0, 5, 5, 1);
        var updated = VersionRewriter.SetCsprojVersion(SampleCsproj, target);

        Assert.Contains("<AssemblyVersion>0.5.5.1</AssemblyVersion>", updated);
        Assert.Contains("<FileVersion>0.5.5.1</FileVersion>", updated);
        Assert.Contains("<Version>0.5.5</Version>", updated);
    }

    [Fact]
    public void SetCsprojVersion_IsIdempotentForSameTarget()
    {
        var target = new PluginVersion(0, 5, 5, 1);
        var once = VersionRewriter.SetCsprojVersion(SampleCsproj, target);
        var twice = VersionRewriter.SetCsprojVersion(once, target);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void SetCsprojVersion_ThrowsWhenSurfaceMissing()
    {
        var target = new PluginVersion(1, 0, 0, 0);
        Assert.Throws<FormatException>(() => VersionRewriter.SetCsprojVersion("<Project></Project>", target));
    }
}
