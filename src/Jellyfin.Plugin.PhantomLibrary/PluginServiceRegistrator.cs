using System;
using Jellyfin.Plugin.PhantomLibrary.Clients;
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

        serviceCollection.AddSingleton<ITmdbApiKeyProvider, PluginConfigTmdbApiKeyProvider>();
        serviceCollection.AddHttpClient<ITmdbClient, TmdbClient>(c =>
        {
            c.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin.Plugin.PhantomLibrary/0.1.0");
            c.Timeout = TimeSpan.FromSeconds(15);
        });
    }
}
