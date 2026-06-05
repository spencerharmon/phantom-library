using System;
using System.IO;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Library;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.Playback;
using Jellyfin.Plugin.PhantomLibrary.Scheduled;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.PhantomLibrary;

/// <summary>
/// Wires Phantom Library services into Jellyfin's DI container. Search
/// providers and image providers are auto-discovered by Jellyfin from
/// the plugin assembly and do not need explicit registration here.
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
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin.Plugin.PhantomLibrary/0.1.0");
            c.Timeout = TimeSpan.FromSeconds(15);
        });

        // Phantom DB (singleton, lazily ensures schema on first use).
        // We resolve IApplicationPaths from the DI container at first
        // request rather than from applicationHost during registration,
        // because IServerApplicationHost.ServiceProvider is not wired up
        // yet at this point and Resolve<T>() would throw / return null.
        serviceCollection.AddSingleton(sp =>
        {
            var paths = sp.GetRequiredService<IApplicationPaths>();
            var dbPath = Path.Combine(paths.PluginConfigurationsPath, "PhantomLibrary", "phantom.db");
            return new PhantomDb(dbPath);
        });

        // Gostream
        serviceCollection.AddHttpClient<IGostreamClient, GostreamClient>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(60);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin.Plugin.PhantomLibrary/0.1.0");
        });

        // Indexers — registered as IIndexerClient in the order Prowlarr → Torrentio.
        serviceCollection.AddHttpClient<ProwlarrClient>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        serviceCollection.AddHttpClient<TorrentioClient>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin.Plugin.PhantomLibrary/0.1.0");
        });
        serviceCollection.AddTransient<IIndexerClient>(sp => sp.GetRequiredService<ProwlarrClient>());
        serviceCollection.AddTransient<IIndexerClient>(sp => sp.GetRequiredService<TorrentioClient>());

        // Materialisation pipeline
        serviceCollection.AddSingleton<QualityScorer>();
        serviceCollection.AddSingleton<IMaterialiser, Materialiser>();
        serviceCollection.AddSingleton<MaterialisationQueue>();
        serviceCollection.AddSingleton<IMaterialisationQueue>(sp => sp.GetRequiredService<MaterialisationQueue>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<MaterialisationQueue>());
        serviceCollection.AddHostedService<EagerResolver>();
        serviceCollection.AddHostedService<UserDataSavedListener>();
        serviceCollection.AddHostedService<EvictionSweeper>();

        // M8: TV series ingestion + autopilot
        serviceCollection.AddSingleton<ISeriesIngestor, SeriesIngestor>();
        serviceCollection.AddSingleton<SeriesAutopilot>();
        serviceCollection.AddSingleton<ISeriesAutopilot>(sp => sp.GetRequiredService<SeriesAutopilot>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<SeriesAutopilot>());

        // Playback / splash
        serviceCollection.AddSingleton<PhantomMediaSourceProvider>();
        serviceCollection.AddSingleton<IMediaSourceProvider>(
            sp => sp.GetRequiredService<PhantomMediaSourceProvider>());
        serviceCollection.AddSingleton<PhantomStatusDecorator>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<PhantomStatusDecorator>());

        // Suggestions (M6)
        serviceCollection.AddSingleton<VirtualLibraryRoot>();
        serviceCollection.AddSingleton<CachedTmdbReader>();
        serviceCollection.AddSingleton<IEagerHintSink, EagerHintSink>();
        serviceCollection.AddSingleton<ISuggestionsContributor, SuggestionsContributor>();
        serviceCollection.AddSingleton<IScheduledTask, SuggestionsRefreshTask>();

        // Phantom stubs + CollectionFolder binder (M10).
        serviceCollection.AddSingleton<IPhantomStubManager, PhantomStubManager>();
        serviceCollection.AddSingleton<IPhantomCollectionFolderBinder, PhantomCollectionFolderBinder>();
        serviceCollection.AddHostedService<PhantomBootstrapService>();
    }
}
