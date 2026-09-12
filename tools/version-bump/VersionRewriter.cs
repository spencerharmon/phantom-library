using System.Text.RegularExpressions;

namespace Phantom.Tools.VersionBump;

/// <summary>
/// Pure, text-in / text-out transforms that rewrite the version on each of the
/// version surfaces. Kept side-effect-free (no file IO) so the bump logic is
/// fully unit-testable; the CLI layer (<see cref="Program"/>) reads/writes files
/// around these.
///
/// Each transform is idempotent for a given target version: applying it twice
/// with the same target yields byte-identical output, so a CI step that re-runs
/// on the same commit never double-bumps.
/// </summary>
public static partial class VersionRewriter
{
    [GeneratedRegex(@"^(?<pre>version:\s*"")(?<ver>[^""]*)(?<post>"".*)$", RegexOptions.Multiline)]
    private static partial Regex BuildYamlVersionRegex();

    [GeneratedRegex(@"<AssemblyVersion>\s*(?<ver>[^<]*?)\s*</AssemblyVersion>")]
    private static partial Regex AssemblyVersionRegex();

    [GeneratedRegex(@"<FileVersion>\s*(?<ver>[^<]*?)\s*</FileVersion>")]
    private static partial Regex FileVersionRegex();

    [GeneratedRegex(@"<Version>\s*(?<ver>[^<]*?)\s*</Version>")]
    private static partial Regex VersionRegex();

    /// <summary>Read the current <c>version: "A.B.C.D"</c> from a build.yaml string.</summary>
    public static PluginVersion ReadBuildYamlVersion(string buildYaml)
    {
        var m = BuildYamlVersionRegex().Match(buildYaml);
        if (!m.Success)
        {
            throw new FormatException("build.yaml has no `version: \"...\"` line");
        }

        return PluginVersion.Parse(m.Groups["ver"].Value);
    }

    /// <summary>Rewrite the build.yaml <c>version:</c> line to the four-part target.</summary>
    public static string SetBuildYamlVersion(string buildYaml, PluginVersion target)
    {
        var re = BuildYamlVersionRegex();
        if (!re.IsMatch(buildYaml))
        {
            throw new FormatException("build.yaml has no `version: \"...\"` line");
        }

        return re.Replace(
            buildYaml,
            m => m.Groups["pre"].Value + target.ToFourPart() + m.Groups["post"].Value,
            1);
    }

    /// <summary>
    /// Rewrite the csproj version surfaces in lockstep: AssemblyVersion and
    /// FileVersion to the four-part target, Version to the three-part target.
    /// </summary>
    public static string SetCsprojVersion(string csproj, PluginVersion target)
    {
        var four = target.ToFourPart();
        var three = target.ToThreePart();

        var text = csproj;
        text = ReplaceSingle(text, AssemblyVersionRegex(), $"<AssemblyVersion>{four}</AssemblyVersion>", "<AssemblyVersion>");
        text = ReplaceSingle(text, FileVersionRegex(), $"<FileVersion>{four}</FileVersion>", "<FileVersion>");
        text = ReplaceSingle(text, VersionRegex(), $"<Version>{three}</Version>", "<Version>");
        return text;
    }

    private static string ReplaceSingle(string text, Regex re, string replacement, string label)
    {
        if (!re.IsMatch(text))
        {
            throw new FormatException($"csproj has no `{label}...` element");
        }

        return re.Replace(text, replacement, 1);
    }
}
