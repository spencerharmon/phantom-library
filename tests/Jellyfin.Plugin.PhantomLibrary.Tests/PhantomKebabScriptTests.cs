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
        Assert.Contains("fetchSources(ctx.externalId, refreshCandidates === true)", js);
        Assert.Contains("resolveExternalId(currentItemId())", js);
        Assert.Contains("Plugins/PhantomLibrary/Items/ResolveExternalId/", js);
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
        Assert.DoesNotContain("phantom-item-actions-section", js);
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
    public void SourceControls_ExposeResetRejectAndCandidateMaterialiseControls()
    {
        var js = ReadScript();

        Assert.Contains("canRejectState(state)", js);
        Assert.Contains("canResetState(state)", js);
        Assert.Contains("canMaterialiseState(state)", js);
        Assert.Contains("fireRejectCurrent(ctx.externalId)", js);
        Assert.Contains("fireReset(ctx.externalId)", js);
        Assert.Contains("fireMaterialiseCandidate(ctx.externalId, selected)", js);
        Assert.Contains("Refresh sources", js);
        Assert.Contains("refreshSourceSection(true)", js);
        Assert.Contains("?refresh=true", js);
    }

    [Fact]
    public void SourceControls_PollDetailStateAfterMaterialiseResetAndReject()
    {
        var js = ReadScript();

        Assert.Contains("detailPoll", js);
        Assert.Contains("startDetailPolling(ctx, 'materialise-candidate')", js);
        Assert.Contains("startDetailPolling(ctx, 'reset')", js);
        Assert.Contains("startDetailPolling(ctx, 'reject')", js);
        Assert.Contains("startDetailPollingForCurrent(actionId)", js);
        Assert.Contains("actionId === 'phantom.rejectCurrent'", js);
        Assert.Contains("startDetailPollingForCurrent('item-action')", js);
        Assert.Contains("setInterval(pollDetailState, 2000)", js);
        Assert.Contains("fetchSources(externalId, false)", js);
        Assert.Contains("refreshVisibleItemContainers()", js);
        Assert.Contains("scanActionSheets()", js);
        Assert.Contains("window.location.reload()", js);
    }

    [Fact]
    public void ActionSheet_UsesServerAdvertisedItemActions()
    {
        var js = ReadScript();

        Assert.Contains("fetchItemActions(itemId)", js);
        Assert.Contains("currentUserQuery", js);
        Assert.Contains("scanActionSheets", js);
        Assert.Contains("setInterval(scanActionSheets", js);
        Assert.Contains("patchApiClientForChannelItems", js);
        Assert.Contains("cachedChannelItem(itemId)", js);
        Assert.Contains("querySelector('.actionSheetScroller') || sheet.querySelector('.actionSheetContent')", js);
        var injectStart = js.IndexOf("function injectIntoSheet", StringComparison.Ordinal);
        var injectEnd = js.IndexOf("function scanActionSheets", StringComparison.Ordinal);
        Assert.True(injectStart >= 0 && injectEnd > injectStart, "injectIntoSheet block not found");
        var injectBlock = js[injectStart..injectEnd];
        Assert.Contains("fetchItemActions(itemId)", injectBlock);
        Assert.DoesNotContain("getPlayablePhantomItem", injectBlock);
        Assert.Contains("Items/", js);
        Assert.Contains("/Actions", js);
        Assert.Contains("fireItemAction(itemId, actionId)", js);
        Assert.Contains("refreshClientAfterAction", js);
        Assert.Contains("isKebabAction", js);
        Assert.Contains("phantom.reset", js);
        Assert.Contains("phantom.rejectCurrent", js);
        Assert.DoesNotContain("interceptDetailMoreButtonClick", js);
        Assert.DoesNotContain("showPhantomActionMenu", js);
        Assert.Contains("window.location.reload()", js);
        Assert.Contains("ConfirmationText", js);
        Assert.Contains("phantom-action-", js);
    }

    [Fact]
    public void ShowHide_MapsEveryPhantomNodeToItsHideTarget_MovieAndTv()
    {
        var js = ReadScript();

        // Movie hides itself; every TV node (series/season/episode) maps to the
        // series tmdb — the first numeric group — so hiding any of them hides the
        // whole series for the calling user.
        Assert.Contains("parsePhantomHideTarget", js);
        Assert.Contains("/^movie_(\\d+)$/", js);
        Assert.Contains("/^series_(\\d+)$/", js);
        Assert.Contains("/^season_(\\d+)_s\\d+$/", js);
        Assert.Contains("/^episode_(\\d+)_s\\d+e\\d+$/", js);
        Assert.Contains("type: 'movie'", js);
        Assert.Contains("type: 'series'", js);
        // getHideablePhantomItem accepts all four node types (not just the
        // materialisable movie/episode leaves the source section gates to).
        Assert.Contains("getHideablePhantomItem", js);
        Assert.Contains("item.Type !== 'Series' && item.Type !== 'Season'", js);
    }

    [Fact]
    public void ShowHide_UsesAuthorizedUserHiddenEndpoints_WithMethodPerAction()
    {
        var js = ReadScript();

        Assert.Contains("Plugins/PhantomLibrary/User/Hidden/", js);
        Assert.Contains("encodeURIComponent(target.type)", js);
        Assert.Contains("encodeURIComponent(target.tmdbId)", js);
        // GET reports state; POST hides; DELETE unhides.
        Assert.Contains("fetchHiddenState", js);
        Assert.Contains("fireHide", js);
        Assert.Contains("fireUnhide", js);
        Assert.Matches(new Regex(@"function fireHide\(target\)\s*\{[\s\S]*?type:\s*'POST'"), js);
        Assert.Matches(new Regex(@"function fireUnhide\(target\)\s*\{[\s\S]*?type:\s*'DELETE'"), js);
    }

    [Fact]
    public void ShowHide_RendersDetailSectionAndActionSheetEntry_TouchSized()
    {
        var js = ReadScript();

        // A standalone visibility section, distinct from the source section, so
        // it renders for series/season too. It reuses the touch-sized
        // .phantom-source-* classes for mobile parity.
        Assert.Contains("phantom-visibility-section", js);
        Assert.Contains("Phantom Visibility", js);
        Assert.Contains("phantom-visibility-button", js);
        Assert.Contains("phantom-source-button", js); // reused touch-sized class
        Assert.Contains("Hide from my library", js);
        Assert.Contains("Unhide from my library", js);
        // Kebab action-sheet entry (primary mobile affordance).
        Assert.Contains("injectVisibilityIntoSheet", js);
        Assert.Contains("HIDE_DATA_ID", js);
        Assert.Contains("UNHIDE_DATA_ID", js);
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
