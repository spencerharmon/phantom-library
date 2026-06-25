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
        Assert.Contains("fetchSources(ctx.externalId)", js);
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
    public void ActionSheet_UsesServerAdvertisedItemActions()
    {
        var js = ReadScript();

        Assert.Contains("fetchItemActions(itemId)", js);
        Assert.Contains("currentUserQuery", js);
        Assert.Contains("scanActionSheets", js);
        Assert.Contains("setInterval(scanActionSheets", js);
        Assert.Contains("Items/", js);
        Assert.Contains("/Actions", js);
        Assert.Contains("fireItemAction(itemId, actionId)", js);
        Assert.Contains("refreshClientAfterAction", js);
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
