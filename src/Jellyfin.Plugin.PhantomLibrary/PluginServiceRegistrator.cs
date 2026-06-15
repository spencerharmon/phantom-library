using System;
using System.IO;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Library;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.State;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
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
        serviceCollection.AddHttpClient<IGostreamClient, GostreamClient>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(60);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin.Plugin.PhantomLibrary/0.2.0");
        });

        // Indexers — registered as IIndexerClient in the order Prowlarr → Torrentio.
        serviceCollection.AddHttpClient<ProwlarrClient>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
        });
        serviceCollection.AddHttpClient<TorrentioClient>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin.Plugin.PhantomLibrary/0.2.0");
        });
        serviceCollection.AddTransient<IIndexerClient>(sp => sp.GetRequiredService<ProwlarrClient>());
        serviceCollection.AddTransient<IIndexerClient>(sp => sp.GetRequiredService<TorrentioClient>());

        // Materialisation pipeline (Materialiser is a stage-2.1 stub;
        // rewritten in Stage 4.2).
        serviceCollection.AddSingleton<QualityScorer>();
        serviceCollection.AddSingleton<IMaterialiser, Materialiser>();
        serviceCollection.AddSingleton<MaterialisationQueue>();
        serviceCollection.AddSingleton<IMaterialisationQueue>(sp => sp.GetRequiredService<MaterialisationQueue>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<MaterialisationQueue>());

        // Listeners + sweeper (all stage-2.1 stubs; rewritten in Stages 4 / 6.1).
        serviceCollection.AddHostedService<UserDataSavedListener>();
        serviceCollection.AddHostedService<PlaybackTriggerListener>();
        serviceCollection.AddHostedService<EvictionSweeper>();

        // SeriesAutopilot (stage-2.1 stub; rewritten in Stage 5.2).
        serviceCollection.AddSingleton<SeriesAutopilot>();
        serviceCollection.AddSingleton<ISeriesAutopilot>(sp => sp.GetRequiredService<SeriesAutopilot>());
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<SeriesAutopilot>());

        // TMDB read-through cache (survives the rewrite; used by upcoming
        // channel + suggestions code).
        serviceCollection.AddSingleton<CachedTmdbReader>();
    }
}
