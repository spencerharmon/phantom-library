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
    private int _sourceValidationParallelism;
    private int _sourceValidationWindowSize;
    private int _sourceValidationTimeoutSeconds;
    private int _sourceValidationDetailsBudgetSeconds;
    private int _sourceValidationTtlHours;
    private int _sourceValidationTransientRetryMinutes;
    private int _sourceValidationLeaseMinutes;
    private int _deadSwarmBrowsePruneThreshold;
    private int _bulkMaterialiseRunningStaleMinutes;
    private int _bulkMaterialiseWorkerCount;
    private int _bulkMaterialiseMaxAttempts;
    private int _gostreamHeavyConcurrency;
    private int _indexerProbeTimeoutSeconds;
    private int _minEarlyReturnCandidates;
    private int _earlyReturnMinElapsedMs;
    private string _sourceValidationPolicyVersion = "sv14-parser-audio-v1";
    private string _allowedVideoContainers = "mkv";

    /// <summary>
    /// Initializes a new instance of the <see cref="PluginConfiguration"/> class
    /// with PLAN-defaults applied.
    /// </summary>
    public PluginConfiguration()
    {
        TmdbApiKey = string.Empty;
        GostreamBaseUrl = "http://127.0.0.1:9080";
        GostreamApiToken = string.Empty;
        GostreamDiagnosticsBaseUrl = "http://127.0.0.1:8090";
        ProwlarrBaseUrl = string.Empty;
        ProwlarrApiKey = string.Empty;
        TorrentioBaseUrl = "https://torrentio.strem.fun";

        QualityPreset = QualityPreset.ResolutionSeeders;
        PreferredResolution = "1080p";
        ResolutionFallbackOrder = "1080p,720p,480p,2160p,4k,unknown";
        SeederWeight = 3;
        MinSeeders = 5;
        MinSizeGb1080p = 4;
        MinSizeGb4K = 20;
        AllowedVideoContainers = "mkv";

        EvictionEnabled = true;
        EvictionIdleDays = 7;
        EvictionScheduleCron = "0 4 * * *";
        ProtectFavourites = true;

        MaterialisationConcurrencyGlobal = 4;
        MaterialisationConcurrencyPerIndexer = 2;

        EagerResolveEnabled = true;
        EagerResolveMaxConcurrent = 2;

        PhantomRetentionDays = 7;

        SeriesAutopilotEnabled = true;
        SeriesAutopilotPrefetchEpisodes = 1;
        SeriesMinAvailableEpisodes = 1;

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
        TrendingCacheTtlHours = 6;
        DiscoverCacheTtlHours = 24;
        DiscoverPagesPerRun = 50;
        DiscoverPageDelayMilliseconds = 100;
        DiscoveryLanguage = string.Empty;
        FavouriteRecommendationsEnabled = true;
        FavouriteRecommendationsMaxPerFavourite = 40;

        AvailabilityProbeEnabled = true;
        AvailabilityProbeMinIntervalSeconds = 4;
        AvailabilityProbeMaxIntervalSeconds = 28;
        AvailabilityAvailableTtlDays = 7;
        AvailabilityUnavailableTtlDays = 7;
        AvailabilityUnavailableMaxTtlDays = 56;
        AvailabilityTransientRetryMinutes = 30;
        AvailabilityMaxBatchSize = 1;
        AvailabilityLeaseMinutes = 15;
        AvailabilityBackgroundEpisodesPerSeries = 1;
        AvailabilityDeferredEpisodeDays = 30;
        AvailabilityNoIndexerRetryHours = 24;
        AvailabilityYieldToUserSeconds = 20;
        AvailabilityTransientMaxAttempts = 8;
        AvailabilityTransientEscalatedRetryHours = 24;
        SeriesExpansionTtlDays = 7;
        SeriesExpansionTransientRetryMinutes = 60;
        EpisodeReleaseDelayHours = 12;
        GostreamMoviesRoot = "/var/gostream/gostream-mkv-virtual/movies";
        GostreamShowsRoot = "/var/gostream/gostream-mkv-virtual/tv";

        SourcePickerPreset = "gostream-default";
        UnavailableRetryAfterHours = 24;
        MagnetCacheTtlHours = 24 * 7;
        MagnetCacheBuildLeaseMinutes = 10;
        MagnetCacheSweepEnabled = true;
        MagnetCacheSweepMinIntervalSeconds = 15;
        MagnetCacheSweepMaxIntervalSeconds = 120;
        MagnetCacheSweepBatchSize = 5;
        MagnetCacheDrainEnabled = true;
        MagnetCacheDrainMinIntervalSeconds = 5;
        MagnetCacheDrainMaxIntervalSeconds = 60;
        MagnetCacheDrainMaxJobsPerTick = 3;
        MaterialiseInFlightStaleMinutes = 10;
        MaterialiseInFlightForeignOwnerHardTtlMinutes = 60;
        GostreamMinQuality = string.Empty;
        FusePathWaitTimeoutSeconds = 60;
        FusePathPollIntervalMilliseconds = 500;

        SourceValidationParallelism = 2;
        SourceValidationWindowSize = 4;
        SourceValidationTimeoutSeconds = 180;
        SourceValidationDetailsBudgetSeconds = 8;
        SourceValidationTtlHours = 168;
        SourceValidationTransientRetryMinutes = 30;
        DeadSwarmBrowsePruneThreshold = 2;
        SourceValidationLeaseMinutes = 10;
        BulkMaterialiseRunningStaleMinutes = 30;
        BulkMaterialiseWorkerCount = 2;
        BulkMaterialiseMaxAttempts = 5;
        SourceValidationPolicyVersion = "sv14-parser-audio-v1";
        GostreamHeavyConcurrency = 2;
        IndexerProbeTimeoutSeconds = 20;
        FastIndexerEarlyReturnEnabled = false;
        MinEarlyReturnCandidates = 1;
        EarlyReturnMinElapsedMs = 250;
        GostreamToken = string.Empty;

        MetricsOtlpEnabled = false;
        MetricsOtlpEndpoint = string.Empty;
        MetricsOtlpProtocol = "grpc";

        // p10-netflix-style-rows: curated browse rows.
        CuratedRowsEnabled = true;
        CuratedRowSize = 40;
        CuratedGenreRowMinItems = 3;
        CuratedHomeMaxRails = 14;
        ChannelWarmupEnabled = true;
        ChannelWarmupIntervalMinutes = 10;
        ChannelWarmupStartupDelaySeconds = 30;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the movie/TV channels present
    /// their top level as multiple curated "Netflix-style" rows (Available
    /// now, Popular on Phantom, New releases, Trending this week, Because you
    /// played…, genre rows, Leaving soon) instead of a single flat list.
    /// (p10-netflix-style-rows.) When <c>false</c> the channels fall back to
    /// the legacy single flat, relevance-sorted list. On by default.
    /// </summary>
    public bool CuratedRowsEnabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of titles rendered inside a single
    /// curated row (p10-netflix-style-rows). Each row query/derivation is
    /// bounded by this value so a Home-screen load stays O(row-size), never an
    /// O(catalogue) enumeration. Clamped to at least 1.
    /// </summary>
    public int CuratedRowSize { get; set; }

    /// <summary>
    /// Gets or sets the minimum number of titles a genre must contribute
    /// before it earns its own curated genre row (p10-netflix-style-rows). A
    /// genre with fewer visible playable titles is folded away rather than
    /// surfacing a near-empty row. Clamped to at least 1.
    /// </summary>
    public int CuratedGenreRowMinItems { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of Home-screen shelves (rails) returned
    /// by <c>/Plugins/PhantomLibrary/Shelves</c> for a single user
    /// (home-shelves-per-user-curation). Each category now yields a separate
    /// Movie and TV rail, so the full candidate set is large; this bounds how
    /// many the per-user curator keeps, ranked by the user's watch history
    /// (genre affinity × movie/TV share). Clamped to at least 1.
    /// </summary>
    public int CuratedHomeMaxRails { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the channel-item BaseItem warmup
    /// worker runs (home-shelves-basitem-warmup). It periodically wraps the root
    /// of both phantom channels so the Home shelves' cards always resolve to a
    /// navigable <c>BaseItem</c> — without it, Jellyfin's lazily-populated
    /// channel BaseItem cache decays to near-empty (nothing browses the folderless
    /// channels any more) and rails collapse. On by default.
    /// </summary>
    public bool ChannelWarmupEnabled { get; set; }

    /// <summary>
    /// Gets or sets the interval, in minutes, between channel BaseItem warmup
    /// passes. Must comfortably beat the channel <c>DataVersion</c>-driven cache
    /// invalidation cadence so the shelves stay populated between passes.
    /// Clamped to at least 1.
    /// </summary>
    public int ChannelWarmupIntervalMinutes { get; set; }

    /// <summary>
    /// Gets or sets the delay, in seconds, before the first channel BaseItem
    /// warmup pass after startup — short so a freshly-deployed pod repopulates
    /// the shelves quickly, but non-zero so it does not contend with the rest of
    /// startup. Clamped to at least 0.
    /// </summary>
    public int ChannelWarmupStartupDelaySeconds { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the plugin pushes its
    /// user-facing browse-flow latency histograms (P5 baseline instrumentation)
    /// to an OTLP collector. Off by default; enabling it without a resolvable
    /// endpoint is a no-op. The pull-based prometheus-net metrics on Jellyfin's
    /// <c>/metrics</c> endpoint are unaffected by this toggle.
    /// </summary>
    public bool MetricsOtlpEnabled { get; set; }

    /// <summary>
    /// Gets or sets the OTLP collector endpoint the flow metrics exporter ships
    /// to (for example <c>http://otel-collector.observability.svc:4317</c>).
    /// Empty (default) falls back to the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>
    /// environment variable. Never bake a deployment hostname into code — the
    /// observability stack target is operator-configured and has changed
    /// (OpenObserve retired in favour of the grafana-mimir-prometheus stack).
    /// </summary>
    public string MetricsOtlpEndpoint { get; set; }

    /// <summary>
    /// Gets or sets the OTLP transport protocol: <c>grpc</c> (default, port
    /// 4317) or <c>http/protobuf</c> (port 4318). Empty falls back to the
    /// standard <c>OTEL_EXPORTER_OTLP_PROTOCOL</c> environment variable, then
    /// <c>grpc</c>.
    /// </summary>
    public string MetricsOtlpProtocol { get; set; }

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
    /// Gets or sets the optional shared secret sent as <c>X-Gostream-Token</c>
    /// to protected gostream library-control endpoints.
    /// </summary>
    public string GostreamApiToken { get; set; }

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

    /// <summary>
    /// Gets or sets the preferred materialise resolution token for the
    /// ResolutionSeeders quality preset (for example <c>1080p</c>).
    /// </summary>
    public string PreferredResolution { get; set; }

    /// <summary>
    /// Gets or sets comma-separated resolution preference order for the
    /// ResolutionSeeders quality preset. Unknown/untagged releases are
    /// matched by the <c>unknown</c> token.
    /// </summary>
    public string ResolutionFallbackOrder { get; set; }

    /// <summary>
    /// Gets or sets score weight per seeder for the ResolutionSeeders
    /// quality preset. Higher values make seed count dominate more.
    /// </summary>
    public int SeederWeight { get; set; }

    /// <summary>Gets or sets the minimum acceptable seeder count.</summary>
    public int MinSeeders { get; set; }

    /// <summary>Gets or sets the minimum 1080p file size in GB.</summary>
    public int MinSizeGb1080p { get; set; }

    /// <summary>Gets or sets the minimum 4K file size in GB.</summary>
    public int MinSizeGb4K { get; set; }

    /// <summary>
    /// Gets or sets comma-separated torrent video containers allowed during
    /// gostream file selection. Default <c>mkv</c> favours the most compatible
    /// Jellyfin client path. Empty means allow every gostream-recognised video
    /// container.
    /// </summary>
    public string AllowedVideoContainers
    {
        get => _allowedVideoContainers;
        set => _allowedVideoContainers = NormalizeAllowedVideoContainers(value);
    }

    /// <summary>Gets or sets a value indicating whether eviction sweeping is active.</summary>
    public bool EvictionEnabled { get; set; }

    /// <summary>Gets or sets days of inactivity before a Materialised item is demoted to Virtual.</summary>
    public int EvictionIdleDays { get; set; }

    /// <summary>Gets or sets the cron schedule for the eviction sweeper hosted service.</summary>
    public string EvictionScheduleCron { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether materialised items with
    /// at least one favouriting user are protected from idle eviction.
    /// Defaults to true (Stage 6.1).
    /// </summary>
    public bool ProtectFavourites { get; set; }

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

    /// <summary>
    /// Gets or sets how many distinct available/materialised episodes are
    /// required before a TV series appears. Once threshold is met, all known
    /// episodes in the series display; unknown episodes behave as phantoms and
    /// unavailable episodes receive the unavailable badge.
    /// </summary>
    public int SeriesMinAvailableEpisodes { get; set; }

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
    /// trending + Discover + tmdb_metadata warm passes.
    /// </summary>
    public int DiscoveryRefreshIntervalHours { get; set; }

    /// <summary>
    /// TTL (days) for rows in the <c>discovery_cache</c> table. Rows
    /// not refreshed within this window are eligible for deletion at
    /// the end of each DiscoveryRefreshTask run (unless they have a
    /// matching materialised_state row, which acts as protection).
    /// </summary>
    public int DiscoveryCacheTtlDays { get; set; }

    /// <summary>TTL for raw TMDB trending response cache rows.</summary>
    public int TrendingCacheTtlHours { get; set; }

    /// <summary>TTL for raw TMDB Discover page response cache rows.</summary>
    public int DiscoverCacheTtlHours { get; set; }

    /// <summary>
    /// Maximum TMDB Discover pages walked per kind per discovery run. The
    /// task stores a cursor and resumes next run; zero means walk to TMDB's
    /// page-500 limit in one run.
    /// </summary>
    public int DiscoverPagesPerRun { get; set; }

    /// <summary>Delay between Discover page fetch/write batches.</summary>
    public int DiscoverPageDelayMilliseconds { get; set; }

    /// <summary>Enables bounded background source availability probing.</summary>
    public bool AvailabilityProbeEnabled { get; set; }

    /// <summary>Fastest availability scheduler cadence during backlog catch-up.</summary>
    public int AvailabilityProbeMinIntervalSeconds { get; set; }

    /// <summary>Slowest availability scheduler cadence during steady state.</summary>
    public int AvailabilityProbeMaxIntervalSeconds { get; set; }

    /// <summary>TTL for available phantom source probes.</summary>
    public int AvailabilityAvailableTtlDays { get; set; }

    /// <summary>TTL for unavailable phantom source probes.</summary>
    public int AvailabilityUnavailableTtlDays { get; set; }

    /// <summary>
    /// Bounded ceiling (days) for the exponential negative-result backoff
    /// (availability-probe-reconcile-001 item 3): the effective re-probe
    /// interval for a repeatedly-confirmed-negative item is
    /// <c>AvailabilityUnavailableTtlDays * 2^negative_streak</c>, capped at
    /// this value so a genuinely unavailable item is still eventually
    /// re-checked rather than backed off forever.
    /// </summary>
    public int AvailabilityUnavailableMaxTtlDays { get; set; }

    /// <summary>Retry delay after transient probe failures that must not change visibility.</summary>
    public int AvailabilityTransientRetryMinutes { get; set; }

    /// <summary>Maximum items probed per scheduler tick.</summary>
    public int AvailabilityMaxBatchSize { get; set; }

    /// <summary>Lease window for in-progress availability probes.</summary>
    public int AvailabilityLeaseMinutes { get; set; }

    /// <summary>
    /// Number of representative episodes per series the BACKGROUND sweep
    /// enqueues as immediately due on series expansion. The remaining
    /// episodes are deferred (see <see cref="AvailabilityDeferredEpisodeDays"/>)
    /// so a series does not flood the probe queue with one row per episode.
    /// On-demand/user probes bypass this queue and can check any episode
    /// immediately. Default 1 (breadth-first: one rep decides series visibility).
    /// </summary>
    public int AvailabilityBackgroundEpisodesPerSeries { get; set; }

    /// <summary>
    /// How far into the future non-representative episodes are deferred at
    /// series-expansion time so the background sweep stays breadth-first
    /// instead of probing every episode. Default 30 days.
    /// </summary>
    public int AvailabilityDeferredEpisodeDays { get; set; }

    /// <summary>
    /// Long backoff applied when a probe reports that no enabled indexer can
    /// serve the query as-is (<c>no_capable_indexer</c>): e.g. no resolvable
    /// imdb id and Prowlarr disabled. Status stays <c>unknown</c>; this is not
    /// treated as a 30-minute transient retry. Default 24 hours.
    /// </summary>
    public int AvailabilityNoIndexerRetryHours { get; set; }

    /// <summary>
    /// If a user-initiated probe touched the activity marker within this many
    /// seconds, the background sweep yields (skips its tick and reschedules at
    /// the max interval) so on-demand work is not slowed by the sweep.
    /// Default 20 seconds.
    /// </summary>
    public int AvailabilityYieldToUserSeconds { get; set; }

    /// <summary>
    /// Convergence guarantee (ROI Priority 6 item 5): once an item's
    /// consecutive-transient <c>attempt_count</c> exceeds this threshold, the
    /// short <see cref="AvailabilityTransientRetryMinutes"/> retry cadence is
    /// replaced by <see cref="AvailabilityTransientEscalatedRetryHours"/> so a
    /// permanently-transient item (a flaky indexer, a persistent probe
    /// exception, missing metadata that never resolves) stops churning the
    /// short interval forever. The counter is reset to 0 whenever the item
    /// reaches a definitive state (<c>available</c>/<c>unavailable</c>), so
    /// only *consecutive* transient failures escalate. Default 8 attempts
    /// (~4 hours of churn at the default 30-minute retry before escalating).
    /// </summary>
    public int AvailabilityTransientMaxAttempts { get; set; }

    /// <summary>
    /// Bounded long backoff applied once <see cref="AvailabilityTransientMaxAttempts"/>
    /// is exceeded. Default 24 hours — the same bound already used for the
    /// <c>no_capable_indexer</c> / unreleased pre-filters, so every non-
    /// definitive outcome converges to the same long-backoff cadence rather
    /// than looping on the short interval forever.
    /// </summary>
    public int AvailabilityTransientEscalatedRetryHours { get; set; }

    /// <summary>TTL for TV series expansion passes.</summary>
    public int SeriesExpansionTtlDays { get; set; }

    /// <summary>Retry delay after transient series expansion failures.</summary>
    public int SeriesExpansionTransientRetryMinutes { get; set; }

    /// <summary>Delay after an episode air date before probing sources.</summary>
    public int EpisodeReleaseDelayHours { get; set; }

    /// <summary>
    /// TMDB language code passed to discovery calls. Empty means
    /// TMDB default (en-US). Maps to the <c>language</c> query
    /// parameter on /trending, /movie/{id}/similar, etc.
    /// </summary>
    public string DiscoveryLanguage { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether favouriting a movie or
    /// series triggers TMDB similar/recommendations ingestion into the
    /// append-only discovery catalogue. Default true. When off, the
    /// favourite still materialises normally; only the taste-based
    /// catalogue expansion is skipped.
    /// </summary>
    public bool FavouriteRecommendationsEnabled { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of catalogue rows ingested per
    /// favourite event. TMDB "similar" and "recommendations" hits are
    /// merged, de-duplicated, the seed title dropped, then capped to this
    /// many. Default 40; values &lt;= 0 fall back to the default.
    /// </summary>
    public int FavouriteRecommendationsMaxPerFavourite { get; set; }

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
    /// Lease (minutes) a magnet-cache builder holds a claimed
    /// <c>magnet_cache_jobs</c> row for while it runs the Prowlarr fan-out.
    /// After it expires an unfinished job becomes claimable again so a crashed
    /// builder never strands a job (p6-magnet-cache-store).
    /// </summary>
    public int MagnetCacheBuildLeaseMinutes { get; set; }

    /// <summary>
    /// Master switch for <c>MagnetCacheBackgroundSweepWorker</c> (p6-magnet-cache-background-sweep):
    /// the lowest-priority magnet-cache lane that walks available items
    /// lacking a fresh cache entry and enqueues LOW-priority
    /// <c>magnet_cache_jobs</c> rows to backfill them.
    /// </summary>
    public bool MagnetCacheSweepEnabled { get; set; }

    /// <summary>
    /// Fast tick interval (seconds) for the background magnet-cache sweep
    /// while it is finding work to enqueue.
    /// </summary>
    public int MagnetCacheSweepMinIntervalSeconds { get; set; }

    /// <summary>
    /// Slow tick interval (seconds) for the background magnet-cache sweep
    /// once it finds no more items to enqueue, or when it yields to a
    /// recent user-activity marker.
    /// </summary>
    public int MagnetCacheSweepMaxIntervalSeconds { get; set; }

    /// <summary>
    /// How many available items the background magnet-cache sweep
    /// considers per tick.
    /// </summary>
    public int MagnetCacheSweepBatchSize { get; set; }

    /// <summary>
    /// Master switch for <c>MagnetCacheDrainWorker</c>
    /// (ttfb-magnet-cache-drain-worker): the timer-driven CONSUMER that
    /// drains the <c>magnet_cache_jobs</c> queue in priority order by
    /// looping <see cref="Sources.MagnetCacheBuilder.ProcessNextAsync"/>.
    /// Without it, opportunistic and background enqueues never build a
    /// cache entry and materialise always pays the full synchronous probe.
    /// </summary>
    public bool MagnetCacheDrainEnabled { get; set; }

    /// <summary>
    /// Fast tick interval (seconds) for the magnet-cache drain worker while
    /// it is finding claimable jobs to process.
    /// </summary>
    public int MagnetCacheDrainMinIntervalSeconds { get; set; }

    /// <summary>
    /// Slow tick interval (seconds) for the magnet-cache drain worker once
    /// the queue is empty, or when it yields to recent user activity.
    /// </summary>
    public int MagnetCacheDrainMaxIntervalSeconds { get; set; }

    /// <summary>
    /// Maximum number of <c>magnet_cache_jobs</c> rows the drain worker
    /// claims and processes per tick (bounded so one tick never monopolises
    /// the Prowlarr fan-out or the DB write lock).
    /// </summary>
    public int MagnetCacheDrainMaxJobsPerTick { get; set; }

    /// <summary>
    /// Age threshold (minutes) above which a row in
    /// <c>materialise_in_flight</c> is considered stale and swept on
    /// startup. Tuned for the worst-case materialise wallclock; rows
    /// younger than this are left alone so a long-running materialise
    /// is not interrupted by the startup sweep racing it.
    /// </summary>
    public int MaterialiseInFlightStaleMinutes { get; set; }

    /// <summary>
    /// Hard crash-recovery age threshold (minutes) above which a
    /// <c>materialise_in_flight</c> row owned by a DIFFERENT replica host
    /// (or a legacy row with no recorded owner) is presumed leaked and
    /// swept on startup. Deliberately much longer than
    /// <see cref="MaterialiseInFlightStaleMinutes"/>: that shorter window
    /// only ever applies to rows THIS host itself wrote, so it is safe to
    /// treat as "presumably crashed materialise, on this very process."
    /// A row belonging to a sibling replica sharing the same
    /// <c>phantom.db</c> may still be a genuinely in-flight materialise
    /// there, so it is only reclaimed once it is old enough that the
    /// owning replica must itself have crashed
    /// (p4-phantomdb-multiwriter-safety-fixes,
    /// docs/tasks/p4-phantomdb-multiwriter-audit.md).
    /// </summary>
    public int MaterialiseInFlightForeignOwnerHardTtlMinutes { get; set; }

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

    /// <summary>Maximum parallel candidate validation tasks launched per materialise pass.</summary>
    public int SourceValidationParallelism
    {
        get => _sourceValidationParallelism;
        set => _sourceValidationParallelism = Math.Clamp(value, 1, 6);
    }

    /// <summary>Rank-window size for candidate validation.</summary>
    public int SourceValidationWindowSize
    {
        get => _sourceValidationWindowSize;
        set => _sourceValidationWindowSize = Math.Clamp(value, 1, 12);
    }

    /// <summary>Absolute source-validation budget for one materialise pass.</summary>
    public int SourceValidationTimeoutSeconds
    {
        get => _sourceValidationTimeoutSeconds;
        set => _sourceValidationTimeoutSeconds = Math.Clamp(value, 5, 300);
    }

    /// <summary>Foreground details-page validation budget.</summary>
    public int SourceValidationDetailsBudgetSeconds
    {
        get => _sourceValidationDetailsBudgetSeconds;
        set => _sourceValidationDetailsBudgetSeconds = Math.Clamp(value, 1, 30);
    }

    /// <summary>TTL for valid/invalid source-validation results.</summary>
    public int SourceValidationTtlHours
    {
        get => _sourceValidationTtlHours;
        set => _sourceValidationTtlHours = Math.Clamp(value, 1, 720);
    }

    /// <summary>Retry delay for transient source-validation results.</summary>
    public int SourceValidationTransientRetryMinutes
    {
        get => _sourceValidationTransientRetryMinutes;
        set => _sourceValidationTransientRetryMinutes = Math.Clamp(value, 1, 1440);
    }

    /// <summary>gostream validation lease lifetime.</summary>
    public int SourceValidationLeaseMinutes
    {
        get => _sourceValidationLeaseMinutes;
        set => _sourceValidationLeaseMinutes = Math.Clamp(value, 1, 60);
    }

    /// <summary>
    /// Number of times an available item's sole remaining candidate must be
    /// re-confirmed as a dead/stale swarm (a <c>magnet_dead_stale</c>-family
    /// transient validation failure) before that candidate stops counting as
    /// "viable" for default-browse visibility (browse-prune-dead-swarm-001,
    /// ROI P12 dial #2). A single transient blip below this threshold keeps the
    /// item visible so a recoverable swarm is not evicted prematurely; the item
    /// reappears automatically the moment a fresh non-dead candidate is cached.
    /// </summary>
    public int DeadSwarmBrowsePruneThreshold
    {
        get => _deadSwarmBrowsePruneThreshold;
        set => _deadSwarmBrowsePruneThreshold = Math.Clamp(value, 1, 100);
    }

    /// <summary>Age after which running bulk items are reset to retry on startup.</summary>
    public int BulkMaterialiseRunningStaleMinutes
    {
        get => _bulkMaterialiseRunningStaleMinutes;
        set => _bulkMaterialiseRunningStaleMinutes = Math.Clamp(value, 1, 1440);
    }

    /// <summary>Maximum active bulk materialise workers.</summary>
    public int BulkMaterialiseWorkerCount
    {
        get => _bulkMaterialiseWorkerCount;
        set => _bulkMaterialiseWorkerCount = Math.Clamp(value, 1, 8);
    }

    /// <summary>Maximum attempts before a bulk materialise item fails permanently.</summary>
    public int BulkMaterialiseMaxAttempts
    {
        get => _bulkMaterialiseMaxAttempts;
        set => _bulkMaterialiseMaxAttempts = Math.Clamp(value, 1, 20);
    }

    /// <summary>Validation policy version persisted with candidate/failure state.</summary>
    public string SourceValidationPolicyVersion
    {
        get => _sourceValidationPolicyVersion + "|containers:" + SourceValidationContainerPolicyToken;
        set
        {
            var raw = string.IsNullOrWhiteSpace(value) ? "sv14-parser-audio-v1" : value.Trim();
            var suffix = raw.IndexOf("|containers:", StringComparison.Ordinal);
            _sourceValidationPolicyVersion = suffix >= 0 ? raw[..suffix] : raw;
        }
    }

    /// <summary>Normalized container policy token appended to source-validation cache keys.</summary>
    public string SourceValidationContainerPolicyToken => string.IsNullOrWhiteSpace(_allowedVideoContainers) ? "any" : _allowedVideoContainers;

    /// <summary>Global cap for heavy gostream validate/add HTTP calls.</summary>
    public int GostreamHeavyConcurrency
    {
        get => _gostreamHeavyConcurrency;
                set => _gostreamHeavyConcurrency = Math.Clamp(value, 1, 4);
    }

    /// <summary>
    /// Bounded per-indexer timeout (seconds) for a single indexer's
    /// <c>SearchAsync</c> during the concurrent probe fan-out in
    /// <c>MagnetSelector.ProbeCoreAsync</c>. A single slow indexer cannot
    /// inflate the whole probe past this ceiling: its own task is cancelled
    /// via a linked <see cref="System.Threading.CancellationTokenSource"/>
    /// when the timeout elapses, surfaced as a per-indexer transient failure
    /// exactly like any other indexer exception, without blocking or dropping
    /// the other indexers' results. Clamped to [5, 120].
    /// </summary>
    public int IndexerProbeTimeoutSeconds
    {
        get => _indexerProbeTimeoutSeconds;
        set => _indexerProbeTimeoutSeconds = Math.Clamp(value, 5, 120);
    }

    /// <summary>
    /// ROI Priority 9 (ttfb-fast-indexer-early-return): when enabled, the probe
    /// fan-out in <c>MagnetSelector.ProbeCoreAsync</c> returns as soon as the
    /// candidates aggregated so far from ANY completed indexer (never
    /// hardcoded to a specific indexer) satisfy <see cref="MinEarlyReturnCandidates"/>
    /// scorer-passing candidates AND <see cref="EarlyReturnMinElapsedMs"/> has
    /// elapsed since the fan-out started, instead of waiting for every enabled
    /// indexer via <c>Task.WhenAll</c>. Still-running slower indexers are NOT
    /// cancelled — they keep running in the background so their result still
    /// lands in the shared magnet cache for future hits; only the caller stops
    /// waiting on them. Default <c>false</c> (conservative rollout): today's
    /// full-wait behavior is unchanged until an operator explicitly enables
    /// this.
    /// </summary>
    public bool FastIndexerEarlyReturnEnabled { get; set; }

    /// <summary>
    /// Minimum number of scorer-passing (<see cref="MinSeeders"/> floor, etc.)
    /// candidates required from already-completed indexers before
    /// <see cref="FastIndexerEarlyReturnEnabled"/> may return early. A
    /// conservative default of 1 favours returning as soon as a single usable
    /// candidate exists. Clamped to [1, 50].
    /// </summary>
    public int MinEarlyReturnCandidates
    {
        get => _minEarlyReturnCandidates;
        set => _minEarlyReturnCandidates = Math.Clamp(value, 1, 50);
    }

    /// <summary>
    /// Minimum wall-clock time (milliseconds) that must elapse since the probe
    /// fan-out started before <see cref="FastIndexerEarlyReturnEnabled"/> may
    /// trigger an early return, even if <see cref="MinEarlyReturnCandidates"/>
    /// is already satisfied. This floor exists so a near-instant
    /// abstention/failure from a fast-but-unhelpful indexer combined with a
    /// coincidentally-already-cached fast hit can never short-circuit the
    /// probe before OTHER indexers have had a fair chance to start and
    /// contribute. Clamped to [0, 10000].
    /// </summary>
    public int EarlyReturnMinElapsedMs
    {
        get => _earlyReturnMinElapsedMs;
        set => _earlyReturnMinElapsedMs = Math.Clamp(value, 0, 10000);
    }

    /// <summary>Optional shared secret sent to gostream mutation/validation endpoints.</summary>
    public string GostreamToken { get; set; }    /// <summary>Normalizes a comma-separated video-container allow-list.</summary>
    public static string NormalizeAllowedVideoContainers(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = new List<string>();
        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.Trim().TrimStart('.').ToUpperInvariant();
            if (token.Length == 0 || !seen.Add(token))
            {
                continue;
            }

            tokens.Add(token);
        }

        return string.Join(',', tokens);
    }
}

/// <summary>Quality-scoring preset chooser.</summary>
public enum QualityPreset
{
    /// <summary>Mirror gostream's scorer.go default weighting (4K DV &gt; 4K HDR10+ &gt; 4K HDR &gt; 4K &gt; 1080p REMUX &gt; 1080p).</summary>
    GostreamDefault = 0,

    /// <summary>Simple preset: biggest .mkv with most seeders.</summary>
    BiggestMostSeeded = 1,

    /// <summary>Prefer configured resolution order, then seeders. Default order prefers 1080p, allows lower resolutions, and de-prioritises 4K.</summary>
    ResolutionSeeders = 2,

    /// <summary>Operator supplies a custom scorer-weight blob in advanced config.</summary>
    Custom = 3,
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
