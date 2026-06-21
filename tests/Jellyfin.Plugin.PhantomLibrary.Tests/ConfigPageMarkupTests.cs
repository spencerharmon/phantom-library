using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class ConfigPageMarkupTests
{
    [Fact]
    public void EmbyCheckboxLabels_ContainSpan_ForJellyfinCheckboxInitializer()
    {
        var html = File.ReadAllText(FindRepoFile("src/Jellyfin.Plugin.PhantomLibrary/Configuration/configPage.html"));
        var labels = Regex.Matches(html, @"<label\b[^>]*>(?<content>.*?)</label>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var checkedLabels = 0;
        var invalid = new List<string>();

        foreach (Match label in labels)
        {
            var content = label.Groups["content"].Value;
            if (content.Contains("is=\"emby-checkbox\"", StringComparison.OrdinalIgnoreCase))
            {
                checkedLabels++;
                if (!Regex.IsMatch(content, @"<span\b", RegexOptions.IgnoreCase))
                {
                    invalid.Add(Regex.Replace(content, @"\s+", " ").Trim());
                }
            }
        }

        Assert.NotEqual(0, checkedLabels);
        Assert.True(invalid.Count == 0, "emby-checkbox attachedCallback calls parent label querySelector('span').classList; labels without spans break checkbox activation: " + string.Join(" | ", invalid));
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not find repository file", relativePath);
    }
}
