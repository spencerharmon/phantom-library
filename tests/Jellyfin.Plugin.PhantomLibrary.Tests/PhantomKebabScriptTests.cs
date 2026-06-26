using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

public class PhantomKebabScriptTests
{
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
