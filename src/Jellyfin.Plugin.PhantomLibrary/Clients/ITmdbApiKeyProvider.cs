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

    /// <summary>
    /// Gets the TMDB v3 base URL (no trailing slash). Production default
    /// is <c>https://api.themoviedb.org/3</c>. Test rigs override to point
    /// at a local mock; operators normally never set this.
    /// </summary>
    string GetBaseUrl();
}

/// <summary>Default key provider sourcing from the live plugin configuration.</summary>
public sealed class PluginConfigTmdbApiKeyProvider : ITmdbApiKeyProvider
{
    /// <inheritdoc/>
    public string GetApiKey() => Plugin.Instance?.Configuration.TmdbApiKey ?? string.Empty;

    /// <inheritdoc/>
    public string GetBaseUrl()
    {
        var configured = Plugin.Instance?.Configuration.TmdbApiBaseUrl;
        return string.IsNullOrWhiteSpace(configured)
            ? "https://api.themoviedb.org/3"
            : configured!.TrimEnd('/');
    }
}
