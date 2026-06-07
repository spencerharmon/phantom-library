using System;
using System.Collections.Generic;
using System.Globalization;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.PhantomLibrary;

/// <summary>
/// Phantom Library plugin entry point. Makes the entire TMDB catalogue
/// appear inside a Jellyfin library; titles materialise on demand via
/// gostream's FUSE-backed virtual MKV files.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// Plugin GUID. Stable across versions; do not change after first
    /// release or existing installations will lose their configuration.
    /// </summary>
    public static readonly Guid PluginId = new("9e7a1f4c-2b5d-4e8f-9a3b-7c1d2e5f6a8b");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">Application paths from Jellyfin host.</param>
    /// <param name="xmlSerializer">XML serializer from Jellyfin host.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the single plugin instance. Set by the constructor when
    /// Jellyfin loads the plugin.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc/>
    public override string Name => "Phantom Library";

    /// <inheritdoc/>
    public override Guid Id => PluginId;

    /// <inheritdoc/>
    public override string Description =>
        "Makes the entire TMDB catalogue appear inside Jellyfin. Titles materialise " +
        "on demand via gostream's FUSE-backed virtual MKV files. Mascot: Stygiomedusa gigantea.";

    /// <inheritdoc/>
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.configPage.html",
                    GetType().Namespace),
            },
            new PluginPageInfo
            {
                Name = Name + " \u2014 User Prefs",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.userPrefsPage.html",
                    GetType().Namespace),
            },
            // PhantomKebab: browser-side JS shim that injects a
            // 'Materialise' entry into the item kebab/action-sheet
            // menu on detail pages. Served via this PluginPageInfo so
            // the operator can wire it via Dashboard → General →
            // Custom HTML/Branding with a single <script> tag:
            //   <script src="/web/ConfigurationPage?name=PhantomKebab" defer></script>
            // Name='PhantomKebab' (no spaces) so the URL is clean.
            new PluginPageInfo
            {
                Name = "PhantomKebab",
                EmbeddedResourcePath = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0}.Configuration.phantomKebab.js",
                    GetType().Namespace),
            },
        };
    }
}
