using System;
using System.IO;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
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

        // Phantom DB (singleton, lazily ensures schema on first use)
        var paths = applicationHost.Resolve<IApplicationPaths>();
        var dbPath = Path.Combine(paths.PluginConfigurationsPath, "PhantomLibrary", "phantom.db");
        serviceCollection.AddSingleton(_ => new PhantomDb(dbPath));

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
    }
}
