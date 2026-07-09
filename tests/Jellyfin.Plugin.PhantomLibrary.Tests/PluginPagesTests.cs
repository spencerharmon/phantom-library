using System;
using System.Linq;
using System.Runtime.CompilerServices;
using MediaBrowser.Model.Plugins;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// Guards the plugin's advertised web pages. The m14 per-user show/hide surface
/// re-introduced the admin <c>userPrefsPage.html</c> sub-page (reverse of an
/// earlier removal), which requires BOTH a <see cref="Plugin.GetPages"/> entry
/// and the file shipping as an embedded resource. Either half missing means the
/// operator's Dashboard tab 404s, so both are asserted here.
/// </summary>
public class PluginPagesTests
{
    private const string UserPrefsResource =
        "Jellyfin.Plugin.PhantomLibrary.Configuration.userPrefsPage.html";

    private static IHasWebPages NewPluginWithoutHostConstruction()
        // GetPages() reads only the overridden Name literal and the type
        // namespace — no base/config state — so an uninitialised instance lets
        // us exercise the real method without a Jellyfin host (IApplicationPaths
        // / IXmlSerializer) or touching plugin configuration on disk.
        => (IHasWebPages)RuntimeHelpers.GetUninitializedObject(typeof(Plugin));

    [Fact]
    public void GetPages_WiresUserPrefsAdminPage()
    {
        var pages = NewPluginWithoutHostConstruction().GetPages().ToList();

        Assert.Contains(
            pages,
            p => p.EmbeddedResourcePath is not null
                && p.EmbeddedResourcePath.EndsWith(".Configuration.userPrefsPage.html", StringComparison.Ordinal));
    }

    [Fact]
    public void GetPages_StillWiresCoreConfigAndKebabPages()
    {
        // Guard against a regression that drops the pre-existing pages while
        // re-adding the prefs one.
        var paths = NewPluginWithoutHostConstruction()
            .GetPages()
            .Select(p => p.EmbeddedResourcePath ?? string.Empty)
            .ToList();

        Assert.Contains(paths, p => p.EndsWith(".Configuration.configPage.html", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith(".Configuration.phantomKebab.js", StringComparison.Ordinal));
        Assert.Contains(paths, p => p.EndsWith(".Configuration.phantomBadges.js", StringComparison.Ordinal));
    }

    [Fact]
    public void UserPrefsPage_ShipsAsEmbeddedResource()
    {
        var names = typeof(Plugin).Assembly.GetManifestResourceNames();
        Assert.Contains(UserPrefsResource, names);
    }
}
