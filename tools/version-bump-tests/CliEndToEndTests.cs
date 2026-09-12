using Phantom.Tools.VersionBump;
using Xunit;

namespace PhantomVersionBump.Tests;

public class CliEndToEndTests : IDisposable
{
    private readonly string _repo;

    private const string SampleBuildYaml =
        "name: \"Phantom Library\"\n" +
        "version: \"0.5.5.0\"\n" +
        "targetAbi: \"10.11.0.0\"\n";

    private const string SampleCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
        "  <PropertyGroup>\n" +
        "    <AssemblyVersion>0.5.4.0</AssemblyVersion>\n" +
        "    <FileVersion>0.5.4.0</FileVersion>\n" +
        "    <Version>0.5.4</Version>\n" +
        "  </PropertyGroup>\n" +
        "</Project>\n";

    public CliEndToEndTests()
    {
        _repo = Directory.CreateTempSubdirectory("phantom-version-bump-tests-").FullName;
        Directory.CreateDirectory(Path.Combine(_repo, "src", "Jellyfin.Plugin.PhantomLibrary"));
        File.WriteAllText(Path.Combine(_repo, "build.yaml"), SampleBuildYaml);
        File.WriteAllText(
            Path.Combine(_repo, "src", "Jellyfin.Plugin.PhantomLibrary", "Jellyfin.Plugin.PhantomLibrary.csproj"),
            SampleCsproj);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_repo, recursive: true);
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    private string BuildYamlPath => Path.Combine(_repo, "build.yaml");

    private string CsprojPath =>
        Path.Combine(_repo, "src", "Jellyfin.Plugin.PhantomLibrary", "Jellyfin.Plugin.PhantomLibrary.csproj");

    [Fact]
    public void Bump_IncrementsRevision_AndKeepsLockstep()
    {
        var exit = Program.Main(new[] { "--repo", _repo, "--bump", "--quiet" });
        Assert.Equal(0, exit);

        var buildYaml = File.ReadAllText(BuildYamlPath);
        var csproj = File.ReadAllText(CsprojPath);

        Assert.Contains("version: \"0.5.5.1\"", buildYaml);
        Assert.Contains("<AssemblyVersion>0.5.5.1</AssemblyVersion>", csproj);
        Assert.Contains("<FileVersion>0.5.5.1</FileVersion>", csproj);
        Assert.Contains("<Version>0.5.5</Version>", csproj);
    }

    [Fact]
    public void Set_ExplicitTarget_WritesTarget()
    {
        var exit = Program.Main(new[] { "--repo", _repo, "--set", "1.0.0.0", "--quiet" });
        Assert.Equal(0, exit);

        var buildYaml = File.ReadAllText(BuildYamlPath);
        Assert.Contains("version: \"1.0.0.0\"", buildYaml);
    }

    [Fact]
    public void Set_NonMonotonicTarget_Fails()
    {
        var exit = Program.Main(new[] { "--repo", _repo, "--set", "0.0.0.0", "--quiet" });
        Assert.Equal(2, exit);

        // Nothing should have been written.
        var buildYaml = File.ReadAllText(BuildYamlPath);
        Assert.Contains("version: \"0.5.5.0\"", buildYaml);
    }

    [Fact]
    public void ReRunningForSameCommit_WithSetIdempotent_DoesNotDoubleBump()
    {
        var first = Program.Main(new[] { "--repo", _repo, "--set", "2.0.0.0", "--quiet" });
        var second = Program.Main(new[] { "--repo", _repo, "--set", "2.0.0.0", "--quiet" });

        Assert.Equal(0, first);
        Assert.Equal(0, second);

        var buildYaml = File.ReadAllText(BuildYamlPath);
        Assert.Contains("version: \"2.0.0.0\"", buildYaml);
    }

    [Fact]
    public void Check_ReturnsZero_WhenAlreadyInLockstep()
    {
        var bumpExit = Program.Main(new[] { "--repo", _repo, "--set", "3.0.0.0", "--quiet" });
        Assert.Equal(0, bumpExit);

        var checkExit = Program.Main(new[] { "--repo", _repo, "--set", "3.0.0.0", "--check", "--quiet" });
        Assert.Equal(0, checkExit);
    }

    [Fact]
    public void Check_ReturnsThree_WhenMismatched()
    {
        var checkExit = Program.Main(new[] { "--repo", _repo, "--bump", "--check", "--quiet" });
        Assert.Equal(3, checkExit);

        // --check must not write.
        var buildYaml = File.ReadAllText(BuildYamlPath);
        Assert.Contains("version: \"0.5.5.0\"", buildYaml);
    }

    [Fact]
    public void MissingBuildYaml_FailsCleanly()
    {
        var emptyRepo = Directory.CreateTempSubdirectory("phantom-version-bump-empty-").FullName;
        try
        {
            var exit = Program.Main(new[] { "--repo", emptyRepo, "--bump", "--quiet" });
            Assert.Equal(2, exit);
        }
        finally
        {
            Directory.Delete(emptyRepo, recursive: true);
        }
    }

    [Fact]
    public void UnknownArgument_ReturnsUsageError()
    {
        var exit = Program.Main(new[] { "--repo", _repo, "--bogus" });
        Assert.Equal(2, exit);
    }
}
