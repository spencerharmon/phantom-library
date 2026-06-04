namespace Jellyfin.Plugin.PhantomLibrary.Clients;

/// <summary>
/// Supplies the TMDB v3 API key. Default production implementation reads
/// from <c>Plugin.Instance.Configuration.TmdbApiKey</c> per-call so that
/// operators can rotate the key from the admin dashboard without
/// restarting Jellyfin. Tests substitute a constant provider.
/// </summary>
public interface ITmdbApiKeyProvider
{
    /// <summary>Gets the current TMDB API key, or empty/whitespace when unconfigured.</summary>
    string GetApiKey();
}

/// <summary>Default key provider sourcing from the live plugin configuration.</summary>
public sealed class PluginConfigTmdbApiKeyProvider : ITmdbApiKeyProvider
{
    /// <inheritdoc/>
    public string GetApiKey() => Plugin.Instance?.Configuration.TmdbApiKey ?? string.Empty;
}
