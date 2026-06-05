using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PhantomLibrary.Configuration;

/// <summary>
/// Server-wide Phantom Library configuration. Persisted by Jellyfin in
/// the plugin configuration XML file; the admin dashboard UI in
/// <c>configPage.html</c> reads and writes these fields via the standard
/// <c>/Plugins/&lt;id&gt;/Configuration</c> REST endpoints.
/// </summary>
/// <remarks>
/// Field defaults reflect the resolved decisions in PLAN.md. Per-user
/// toggles (protect-favourites, show-phantoms, allow-eager-pre-resolve)
/// live in a separate per-user table inside <c>PhantomDb</c>, not here.
/// </remarks>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class
    /// with PLAN-defaults applied.
    /// </summary>
    public PluginConfiguration()
    {
        TmdbApiKey = string.Empty;
        GostreamBaseUrl = "http://127.0.0.1:9080";
        GostreamDiagnosticsBaseUrl = "http://127.0.0.1:8090";
        ProwlarrBaseUrl = string.Empty;
        ProwlarrApiKey = string.Empty;
        TorrentioBaseUrl = "https://torrentio.strem.fun";

        QualityPreset = QualityPreset.GostreamDefault;
        MinSeeders = 5;
        MinSizeGb1080p = 4;
        MinSizeGb4K = 20;

        EvictionEnabled = true;
        EvictionIdleDays = 7;
        EvictionScheduleCron = "0 4 * * *";

        MaterialisationConcurrencyGlobal = 4;
        MaterialisationConcurrencyPerIndexer = 2;

        EagerResolveEnabled = true;
        EagerResolveMaxConcurrent = 2;

        PhantomRetentionDays = 7;

        SeriesAutopilotEnabled = true;
        SeriesAutopilotPrefetchEpisodes = 1;

        PhantomBadgeVisibility = PhantomBadgeVisibility.AlwaysShow;

        SplashLoopAssetPath = string.Empty;
        PhantomTargetLibraryId = string.Empty;
    }

    /// <summary>Gets or sets the TMDB v3 API key used by the plugin's TMDB client.</summary>
    public string TmdbApiKey { get; set; }

    /// <summary>
    /// Gets or sets the gostream library-control base URL. Targets the
    /// <c>POST /api/library/add</c> endpoint added by the primary patch
    /// (default port 9080).
    /// </summary>
    public string GostreamBaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the gostream diagnostics / streaming base URL
    /// (default port 8090). Used only for health probes; never written to.
    /// </summary>
    public string GostreamDiagnosticsBaseUrl { get; set; }

    /// <summary>Gets or sets the Prowlarr base URL. Empty disables Prowlarr.</summary>
    public string ProwlarrBaseUrl { get; set; }

    /// <summary>Gets or sets the Prowlarr API key.</summary>
    public string ProwlarrApiKey { get; set; }

    /// <summary>Gets or sets the Torrentio fallback base URL.</summary>
    public string TorrentioBaseUrl { get; set; }

    /// <summary>Gets or sets the quality scoring preset.</summary>
    public QualityPreset QualityPreset { get; set; }

    /// <summary>Gets or sets the minimum acceptable seeder count.</summary>
    public int MinSeeders { get; set; }

    /// <summary>Gets or sets the minimum 1080p file size in GB.</summary>
    public int MinSizeGb1080p { get; set; }

    /// <summary>Gets or sets the minimum 4K file size in GB.</summary>
    public int MinSizeGb4K { get; set; }

    /// <summary>Gets or sets a value indicating whether eviction sweeping is active.</summary>
    public bool EvictionEnabled { get; set; }

    /// <summary>Gets or sets days of inactivity before a Materialised item is demoted to Virtual.</summary>
    public int EvictionIdleDays { get; set; }

    /// <summary>Gets or sets the cron schedule for the eviction sweeper hosted service.</summary>
    public string EvictionScheduleCron { get; set; }

    /// <summary>Gets or sets the total concurrent materialisations across all lanes.</summary>
    public int MaterialisationConcurrencyGlobal { get; set; }

    /// <summary>Gets or sets the per-indexer concurrent queries cap.</summary>
    public int MaterialisationConcurrencyPerIndexer { get; set; }

    /// <summary>Gets or sets a value indicating whether the background eager indexer-resolver runs.</summary>
    public bool EagerResolveEnabled { get; set; }

    /// <summary>Gets or sets the maximum concurrent eager pre-resolves.</summary>
    public int EagerResolveMaxConcurrent { get; set; }

    /// <summary>Gets or sets days a Phantom item is retained before pruning if never promoted.</summary>
    public int PhantomRetentionDays { get; set; }

    /// <summary>Gets or sets a value indicating whether series autopilot pre-materialises upcoming episodes.</summary>
    public bool SeriesAutopilotEnabled { get; set; }

    /// <summary>Gets or sets the autopilot prefetch window in episodes ahead of the current cursor.</summary>
    public int SeriesAutopilotPrefetchEpisodes { get; set; }

    /// <summary>Gets or sets the visibility policy for the "phantom" badge in client UIs.</summary>
    public PhantomBadgeVisibility PhantomBadgeVisibility { get; set; }

    /// <summary>
    /// Gets or sets an absolute filesystem path to a custom splash MP4 loop served by
    /// <c>PhantomMediaSourceProvider</c>. Empty means use the bundled default loop.
    /// </summary>
    public string SplashLoopAssetPath { get; set; }

    /// <summary>
    /// Gets or sets the GUID (hex/string) of the Jellyfin library Phantom
    /// Library writes Virtual items into. Empty = first Movies / TV library
    /// auto-picked at runtime. Advanced; the auto-pick is correct for
    /// almost all single-library installs.
    /// </summary>
    public string PhantomTargetLibraryId { get; set; }
}

/// <summary>Quality-scoring preset chooser.</summary>
public enum QualityPreset
{
    /// <summary>Mirror gostream's scorer.go default weighting (4K DV &gt; 4K HDR10+ &gt; 4K HDR &gt; 4K &gt; 1080p REMUX &gt; 1080p).</summary>
    GostreamDefault = 0,

    /// <summary>Simple preset: biggest .mkv with most seeders.</summary>
    BiggestMostSeeded = 1,

    /// <summary>Operator supplies a custom scorer-weight blob in advanced config.</summary>
    Custom = 2,
}

/// <summary>Visibility policy for the "phantom" badge surfaced on Virtual items.</summary>
public enum PhantomBadgeVisibility
{
    /// <summary>Always render the badge for all users.</summary>
    AlwaysShow = 0,

    /// <summary>Render the badge only for admin users.</summary>
    HideForNonAdmins = 1,

    /// <summary>Never render the badge.</summary>
    Off = 2,
}
