using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class PhantomKebabScriptTests
{
    // Regex-driven mobile assertions read the shipped CSS the shim injects. Executable
    // mobile-viewport DOM/API evidence (the shim run against a phone-sized DOM, with the
    // materialise/reject tap flow) lives in tools/rig-scenarios/phantom-kebab-mobile-dom.mjs
    // (run via tools/rig-scenarios/38-mobile-source-dom.sh); it is exercised by
    // MobileDomEvidence_HarnessPasses below when node is available.

    [Fact]
    public void SourceControls_FetchByStableExternalId_NotJellyfinGuid()
    {
        var js = ReadScript();

        Assert.Contains("getPlayablePhantomItem", js);
        Assert.Contains("item.ExternalId", js);
        Assert.Contains("fetchSources(ctx.externalId)", js);
        Assert.Contains("Plugins/PhantomLibrary/Items/", js);
        Assert.Contains("/Sources", js);
        Assert.Contains("encodeURIComponent(externalId)", js);
    }

    [Fact]
    public void SourceControls_RenderDetailsSectionAndTouchSizedControls()
    {
        var js = ReadScript();

        Assert.Contains("Phantom Source", js);
        Assert.Contains("phantom-source-section", js);
        Assert.Contains("phantom-source-candidates", js);
        Assert.Contains("Materialise selected source", js);
        Assert.Contains("Reject current source", js);
        Assert.Contains("min-height:44px", js);
        Assert.Contains("@media (max-width: 600px)", js);
        Assert.Contains("touch-action:manipulation", js);
    }

    [Fact]
    public void SourceControls_MobileMediaQuery_StacksAndFillsWidthWithoutOverflowOrIosZoom()
    {
        var js = ReadScript();
        var media = MobileMediaBlock(js);

        // Controls stack vertically and fill the width on a phone viewport.
        Assert.Matches(new Regex(@"\.phantom-source-row\{[^}]*display:block"), media);
        Assert.Matches(new Regex(@"\.phantom-source-select\{[^}]*width:100%"), media);
        Assert.Matches(new Regex(@"\.phantom-source-button\{[^}]*width:100%"), media);
        // Drop the desktop min-width so the <select> never overflows a narrow phone.
        Assert.Matches(new Regex(@"\.phantom-source-select\{[^}]*min-width:0"), media);
        // Pin the <select> font to 16px so iOS Safari does not auto-zoom on focus.
        Assert.Matches(new Regex(@"\.phantom-source-select\{[^}]*font-size:16px"), media);
    }

    [Fact]
    public void ActionSheetInjectedEntries_AreTouchSized()
    {
        var js = ReadScript();

        // The kebab (...) action sheet is the primary mobile affordance; injected
        // entries must be >=44px touch targets with no double-tap-zoom delay.
        Assert.Contains("button.style.minHeight = '44px'", js);
        Assert.Contains("button.style.touchAction = 'manipulation'", js);
    }

    [Fact]
    public void SourceControls_GateToPhantomMoviesAndEpisodesOnly()
    {
        var js = ReadScript();

        Assert.Contains("/^movie_\\d+$/.test(externalId)", js);
        Assert.Contains("/^episode_\\d+_s\\d+e\\d+$/.test(externalId)", js);
        Assert.Contains("item.Type !== 'Movie' && item.Type !== 'Episode'", js);
        Assert.DoesNotContain("KindSeries", js);
        Assert.DoesNotContain("KindSeason", js);
    }

    [Fact]
    public void SeasonDetails_PrehydratesChannelChildrenWithoutJellyfinPatch()
    {
        var js = ReadScript();

        Assert.Contains("getPhantomSeasonItem", js);
        Assert.Contains("/^season_\\d+_s\\d+$/.test(item.ExternalId)", js);
        Assert.Contains("prehydratePhantomSeasonChildren", js);
        Assert.Contains("Channels/", js);
        Assert.Contains("FolderId: item.Id", js);
        Assert.Contains("refreshVisibleItemContainers", js);
    }

    [Fact]
    public void ActionSheet_ShowsRejectForMaterialisedAndMaterialiseForUnmaterialised()
    {
        var js = ReadScript();

        Assert.Contains("isMaterialisedState(state) && canRejectState(state)", js);
        Assert.Contains("REJECT_DATA_ID", js);
        Assert.Contains("canMaterialiseState(state)", js);
        Assert.Contains("MATERIALISE_DATA_ID", js);
        Assert.Contains("fireRejectCurrent(ctx.externalId)", js);
        Assert.Contains("fireMaterialise(itemId)", js);
    }

    [Fact]
    public void MobileDomEvidence_HarnessPasses()
    {
        // Executes the real shim against a phone-sized DOM and asserts the injected
        // controls, touch sizing, responsive layout, and the materialise/reject tap ->
        // API flow (movie + TV episode). Node is an optional toolchain here: when it is
        // absent we skip rather than fail a Node-less `dotnet test`; the same harness is
        // wrapped by tools/rig-scenarios/38-mobile-source-dom.sh for CI/rig runs.
        var node = FindOnPath("node") ?? FindOnPath("nodejs");
        if (node is null)
        {
            return;
        }

        var harness = FindRepoFile("tools/rig-scenarios/phantom-kebab-mobile-dom.mjs");
        var psi = new ProcessStartInfo(node, "\"" + harness + "\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var proc = Process.Start(psi)!;
        var stdout = proc.StandardOutput.ReadToEnd();
        var stderr = proc.StandardError.ReadToEnd();
        Assert.True(proc.WaitForExit(60000), "mobile DOM/API harness timed out");
        Assert.True(proc.ExitCode == 0, "mobile DOM/API harness failed:\n" + stdout + "\n" + stderr);
        Assert.Contains("PASSED", stdout);
    }

    private static string MobileMediaBlock(string css)
    {
        var at = css.IndexOf("@media (max-width: 600px)", StringComparison.Ordinal);
        Assert.True(at >= 0, "expected a max-width:600px mobile media query");
        var open = css.IndexOf('{', at);
        var depth = 0;
        for (var i = open; i < css.Length; i++)
        {
            if (css[i] == '{')
            {
                depth++;
            }
            else if (css[i] == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return css.Substring(open + 1, i - open - 1);
                }
            }
        }

        throw new Xunit.Sdk.XunitException("unterminated @media block in phantomKebab.js");
    }

    private static string? FindOnPath(string exe)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (File.Exists(candidate + ".exe"))
            {
                return candidate + ".exe";
            }
        }

        return null;
    }

    private static string ReadScript()
    {
        return File.ReadAllText(FindRepoFile("src/Jellyfin.Plugin.PhantomLibrary/Configuration/phantomKebab.js"));
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
