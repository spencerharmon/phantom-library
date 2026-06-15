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
        TmdbApiBaseUrl = string.Empty;

        PhantomStubRoot = "/var/lib/jellyfin/phantom-library";
        PhantomMoviesLibraryName = "gostream-movies";
        PhantomShowsLibraryName = "gostream-shows";

        SuggestionsCatalogueMaxItems = 5000;

        DiscoveryRefreshIntervalHours = 6;
        DiscoveryCacheTtlDays = 30;
        DiscoveryLanguage = string.Empty;
        GostreamMoviesRoot = "/var/gostream/gostream-mkv-virtual/movies";
        GostreamShowsRoot = "/var/gostream/gostream-mkv-virtual/tv";

        SourcePickerPreset = "gostream-default";
        UnavailableRetryAfterHours = 24;
        MagnetCacheTtlHours = 24 * 7;
        MaterialiseInFlightStaleMinutes = 10;
        GostreamMinQuality = string.Empty;
        FusePathWaitTimeoutSeconds = 60;
        FusePathPollIntervalMilliseconds = 500;
    }

    /// <summary>Gets or sets the TMDB v3 API key used by the plugin's TMDB client.</summary>
    public string TmdbApiKey { get; set; }

    /// <summary>
    /// Optional TMDB v3 base URL override. Empty (default) means use the
    /// production endpoint at <c>https://api.themoviedb.org/3</c>. Test
    /// rigs point this at a local mock server. Operators normally never
    /// set this.
    /// </summary>
    public string TmdbApiBaseUrl { get; set; }

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

    /// <summary>
    /// Gets or sets the plugin-owned writable directory that holds per-phantom
    /// symlinks (one symlink per phantom, all pointing at the extracted splash).
    /// Operator must <c>mkdir -p</c> the <c>movies/</c> and <c>shows/</c>
    /// subdirs and <c>chown</c> them to the Jellyfin user before first use.
    /// </summary>
    public string PhantomStubRoot { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin CollectionFolder name into which phantom
    /// movie stubs are bound (default <c>gostream-movies</c>). Must match
    /// an existing library name in Jellyfin's library settings.
    /// </summary>
    public string PhantomMoviesLibraryName { get; set; }

    /// <summary>
    /// Gets or sets the Jellyfin CollectionFolder name into which phantom
    /// series stubs are bound (default <c>gostream-shows</c>).
    /// </summary>
    public string PhantomShowsLibraryName { get; set; }

    /// <summary>
    /// Gets or sets the total cap on Virtual items materialised by the
    /// Discover catalogue walk (split evenly between movies and series).
    /// Default 5000 → ~2500 of each kind across ~125 TMDB pages per kind.
    /// </summary>
    public int SuggestionsCatalogueMaxItems { get; set; }

    /// <summary>
    /// Interval (hours) between <c>DiscoveryRefreshTask</c> runs. Drives
    /// trending + similar-of-favourites + tmdb_metadata warm passes.
    /// </summary>
    public int DiscoveryRefreshIntervalHours { get; set; }

    /// <summary>
    /// TTL (days) for rows in the <c>discovery_cache</c> table. Rows
    /// not refreshed within this window are eligible for deletion at
    /// the end of each DiscoveryRefreshTask run (unless they have a
    /// matching materialised_state row, which acts as protection).
    /// </summary>
    public int DiscoveryCacheTtlDays { get; set; }

    /// <summary>
    /// TMDB language code passed to discovery calls. Empty means
    /// TMDB default (en-US). Maps to the <c>language</c> query
    /// parameter on /trending, /movie/{id}/similar, etc.
    /// </summary>
    public string DiscoveryLanguage { get; set; }

    /// <summary>
    /// Filesystem path to the gostream movies FUSE mount. The
    /// movies channel walks this directory to enumerate orphan files
    /// (gostream-served movies not surfaced by phantom discovery).
    /// </summary>
    public string GostreamMoviesRoot { get; set; }

    /// <summary>
    /// Filesystem path to the gostream shows FUSE mount. Walked by
    /// the shows channel for orphan enumeration (Stage 5.1).
    /// </summary>
    public string GostreamShowsRoot { get; set; }

    /// <summary>
    /// Preset label embedded in <c>magnet_cache</c> rows. Lets the
    /// operator invalidate a magnet cohort by changing the preset
    /// string; defaults to <c>"gostream-default"</c>.
    /// </summary>
    public string SourcePickerPreset { get; set; }

    /// <summary>
    /// How long an <c>unavailable_marker</c> row keeps short-circuiting
    /// repeated materialise attempts for the same (tmdb, type, season,
    /// episode) tuple before the indexers are re-consulted.
    /// </summary>
    public int UnavailableRetryAfterHours { get; set; }

    /// <summary>
    /// Per-magnet cache TTL written into <c>magnet_cache</c> rows.
    /// </summary>
    public int MagnetCacheTtlHours { get; set; }

    /// <summary>
    /// Age threshold (minutes) above which a row in
    /// <c>materialise_in_flight</c> is considered stale and swept on
    /// startup. Tuned for the worst-case materialise wallclock; rows
    /// younger than this are left alone so a long-running materialise
    /// is not interrupted by the startup sweep racing it.
    /// </summary>
    public int MaterialiseInFlightStaleMinutes { get; set; }

    /// <summary>
    /// Optional <c>min_quality</c> hint forwarded to gostream's
    /// <c>POST /api/library/add</c>. Empty (default) lets gostream pick.
    /// </summary>
    public string GostreamMinQuality { get; set; }

    /// <summary>
    /// Upper bound on how long the materialiser waits for the
    /// gostream FUSE path to appear after a successful <c>add</c>
    /// call. The wait is best-effort; a timeout does not roll the
    /// materialise back, but the badge will reflect 'Materialised'
    /// without a probe-driven MediaSource until the next browse.
    /// </summary>
    public int FusePathWaitTimeoutSeconds { get; set; }

    /// <summary>
    /// Polling interval for the FUSE-path wait loop.
    /// </summary>
    public int FusePathPollIntervalMilliseconds { get; set; }
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
