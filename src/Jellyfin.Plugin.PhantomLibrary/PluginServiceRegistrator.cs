using System;
using System.IO;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Library;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.Playback;
using Jellyfin.Plugin.PhantomLibrary.Scheduled;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Channels;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.PhantomLibrary;

/// <summary>
/// Wires Phantom Library services into Jellyfin's DI container.
///
/// Stage 2.1: the file-on-disk phantom architecture has been deleted.
/// Channel skeletons + DI wiring for the new architecture arrive in
/// Stage 2.4 per <c>docs/plans/channel-handoff.md</c>.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc/>
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);
        ArgumentNullException.ThrowIfNull(applicationHost);

        // TMDB
        serviceCollection.AddSingleton<ITmdbApiKeyProvider, PluginConfigTmdbApiKeyProvider>();
        serviceCollection.AddHttpClient<ITmdbClient, TmdbClient>(c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin.Plugin.PhantomLibrary/0.2.0");
            c.Timeout = TimeSpan.FromSeconds(15);
        });

        // Phantom DB (singleton, lazily ensures schema on first use).
        serviceCollection.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            var dbPath = Path.Combine(paths.PluginConfigurationsPath, "PhantomLibrary", "phantom.db");
            return new PhantomDb(dbPath);
        });

        // Gostream
        serviceCollection.AddSingleton<GostreamHeavyLimiter>();
        serviceCollection.AddHttpClient<IGostreamClient, GostreamClient>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(60);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin.Plugin.PhantomLibrary/0.2.0");
        });

        // Indexers — registered as IIndexerClient in the order Prowlarr → Torrentio.
        serviceCollection.AddHttpClient<ProwlarrClient>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
        })
        .ConfigurePrimaryHttpMessageHandler(() => new System.Net.Http.HttpClientHandler
        {
            AllowAutoRedirect = false,
        });
        serviceCollection.AddHttpClient<TorrentioClient>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin.Plugin.PhantomLibrary/0.2.0");
        });
        serviceCollection.AddTransient<IIndexerClient>(sp => sp.GetRequiredService<ProwlarrClient>());
        serviceCollection.AddTransient<IIndexerClient>(sp => sp.GetRequiredService<TorrentioClient>());

        // Materialisation pipeline (Stage 4.2 rewrite — full tuple
        // signature, MagnetSelector + TmdbExternalIdResolver,
        // IChannelItemRefreshManager hand-off, in-flight sweeper).
        serviceCollection.AddSingleton<QualityScorer>();
        serviceCollection.AddSingleton<MagnetSelector>();
        serviceCollection.AddSingleton<TmdbExternalIdResolver>();
        serviceCollection.AddSingleton<PhantomSourceManager>();
        serviceCollection.AddSingleton<IItemActionProvider, PhantomItemActionProvider>();
        serviceCollection.AddSingleton<IMaterialiser, Materialiser>();
        serviceCollection.AddHostedService<MaterialiseInFlightSweeper>();
        serviceCollection.AddSingleton<MaterialisationQueue>();
        serviceCollection.AddSingleton<IMaterialisationQueue>(sp => sp.GetRequiredService<MaterialisationQueue>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<MaterialisationQueue>());

        // Channel-arch listeners. Heavy autopilot logic lands in
        // Stage 5.2; the listeners wired here forward to ISeriesAutopilot
        // and fire-and-forget materialise via IMaterialiser.
        serviceCollection.AddHostedService<UserDataSavedListener>();
        serviceCollection.AddHostedService<PlaybackTriggerListener>();
        serviceCollection.AddHostedService<EvictionSweeper>();
        serviceCollection.AddHostedService<AvailabilityProbeWorker>();

        // SeriesAutopilot (stage-2.1 stub; rewritten in Stage 5.2).
        serviceCollection.AddSingleton<SeriesAutopilot>();
        serviceCollection.AddSingleton<ISeriesAutopilot>(sp => sp.GetRequiredService<SeriesAutopilot>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<SeriesAutopilot>());

        // TMDB read-through cache (survives the rewrite; used by upcoming
        // channel + suggestions code).
        serviceCollection.AddSingleton<CachedTmdbReader>();

        // SplashSourceProvider — lazily extracts the embedded splash.mp4
        // on first CreateMediaSource() call (synchronous, idempotent),
        // so it's on disk before any channel emits a MediaSourceInfo
        // pointing at it.
        serviceCollection.AddSingleton<SplashSourceProvider>();

        // ChannelStateProvider — backs IChannel.DataVersion for each
        // phantom channel and lets background tasks bump invalidation
        // markers (persisted via plugin_meta so they survive restart).
        serviceCollection.AddSingleton<ChannelStateProvider>();

        // GostreamFilesystemEnumerator — walks the gostream FUSE mount
        // for orphan files the movies/shows channel surfaces alongside
        // discovery + materialised items.
        serviceCollection.AddSingleton<GostreamFilesystemEnumerator>();

        // Native-client phantom playback opener. Emits/opens RequiresOpening
        // sources so TV/mobile clients show native loading while materialise
        // completes, then start the real gostream file.
        serviceCollection.AddSingleton<PhantomMaterialisingMediaSourceProvider>();
        serviceCollection.AddSingleton<IMediaSourceProvider>(sp => sp.GetRequiredService<PhantomMaterialisingMediaSourceProvider>());

        // Channels.
        serviceCollection.AddSingleton<IChannel, PhantomMoviesChannel>();
        serviceCollection.AddSingleton<IChannel, PhantomShowsChannel>();

        // Scheduled tasks.
        serviceCollection.AddSingleton<IScheduledTask, DiscoveryRefreshTask>();
    }
}
