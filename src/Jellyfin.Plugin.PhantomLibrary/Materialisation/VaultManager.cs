using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

/// <summary>
/// Vault Mode coordinator (plan §"Vault Mode", REQ-M14-VAULT). Bridges the
/// phantom favourite / eviction lifecycle to gostream's Vault Mode
/// prestage / unprestage endpoints so a favourited materialised item is kept
/// resident on the gostream box, and a de-favourited or evicted item releases
/// that footprint.
///
/// Everything here is best-effort. Vault Mode is optional on the gostream
/// side, so every call is gated on
/// <see cref="IGostreamClient.IsVaultModePresentAsync"/> and transport / HTTP
/// failures are swallowed (logged at Warning). Two asymmetric gates:
/// <list type="bullet">
///   <item><b>Prestage</b> additionally requires
///   <see cref="PluginConfiguration.VaultModeEnabled"/> — an operator who
///   turns Vault Mode off stops <em>adding</em> footprint.</item>
///   <item><b>Unprestage</b> is <em>not</em> config-gated — turning Vault Mode
///   off (or de-favouriting / evicting) still <em>releases</em> footprint, so a
///   disabled Vault Mode drains rather than stranding prestaged bytes.</item>
/// </list>
///
/// Movie/TV parity: the tuple methods take the same (tmdbId, type, season,
/// episode) shape the materialiser and eviction sweeper use; movies pass
/// <c>(null, null)</c> season/episode and are stored under the
/// <see cref="ChannelItemId.Sentinel"/> form via
/// <see cref="ChannelItemId.ToSentinels(int?, int?)"/>, episodes pass their
/// real numbers. Neither path is privileged.
/// </summary>
public interface IVaultManager
{
    /// <summary>
    /// If Vault Mode is enabled and present, resolve the materialised stub for
    /// (tmdb, type, season, episode) and ask gostream to prestage it at the
    /// configured priority. No-op when Vault Mode is disabled, the gostream
    /// server lacks Vault Mode, or the tuple has no <c>materialised_state</c>
    /// row (nothing to keep resident yet).
    /// </summary>
    Task PrestageAsync(int tmdbId, string type, int? season, int? episode, CancellationToken ct);

    /// <summary>
    /// If Vault Mode is present, resolve the materialised stub for the tuple and
    /// ask gostream to unprestage it. NOT gated on
    /// <see cref="PluginConfiguration.VaultModeEnabled"/> so footprint is
    /// released even after Vault Mode is switched off. No-op when the gostream
    /// server lacks Vault Mode or the tuple has no <c>materialised_state</c> row.
    /// </summary>
    Task UnprestageAsync(int tmdbId, string type, int? season, int? episode, CancellationToken ct);

    /// <summary>
    /// If Vault Mode is present, ask gostream to unprestage the given stub path
    /// directly. Used by the eviction sweeper, which already holds the row's
    /// stub path (and is about to delete the row) so a DB round-trip would be
    /// wasteful. NOT gated on <see cref="PluginConfiguration.VaultModeEnabled"/>.
    /// </summary>
    Task UnprestageStubAsync(string stubPath, CancellationToken ct);
}

/// <inheritdoc />
public sealed class VaultManager : IVaultManager
{
    private readonly PhantomDb _db;
    private readonly IGostreamClient _gostream;
    private readonly ILogger<VaultManager> _logger;
    private readonly Func<PluginConfiguration> _configProvider;

    public VaultManager(PhantomDb db, IGostreamClient gostream, ILogger<VaultManager> logger)
        : this(db, gostream, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    // Test-friendly ctor: internal so ActivatorUtilities ignores it during DI resolution.
    internal VaultManager(
        PhantomDb db,
        IGostreamClient gostream,
        ILogger<VaultManager> logger,
        Func<PluginConfiguration> configProvider)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _gostream = gostream ?? throw new ArgumentNullException(nameof(gostream));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    /// <inheritdoc />
    public async Task PrestageAsync(int tmdbId, string type, int? season, int? episode, CancellationToken ct)
    {
        var cfg = _configProvider();
        if (!cfg.VaultModeEnabled)
        {
            return;
        }

        try
        {
            if (!await _gostream.IsVaultModePresentAsync(ct).ConfigureAwait(false))
            {
                return;
            }

            var stubPath = await ResolveStubPathAsync(tmdbId, type, season, episode, ct).ConfigureAwait(false);
            if (stubPath is null)
            {
                _logger.LogDebug(
                    "[Vault] prestage skipped: no materialised_state row for tmdb={Tmdb} type={Type} s={Season} e={Episode}",
                    tmdbId, type, season, episode);
                return;
            }

            await _gostream.PrestageAsync(stubPath, Math.Max(0, cfg.VaultPrestagePriority), ct).ConfigureAwait(false);
            _logger.LogInformation(
                "[Vault] prestaged stub_path={Stub} (tmdb={Tmdb} type={Type} priority={Priority})",
                stubPath, tmdbId, type, Math.Max(0, cfg.VaultPrestagePriority));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[Vault] prestage failed for tmdb={Tmdb} type={Type} s={Season} e={Episode}; best-effort, ignoring",
                tmdbId, type, season, episode);
        }
    }

    /// <inheritdoc />
    public async Task UnprestageAsync(int tmdbId, string type, int? season, int? episode, CancellationToken ct)
    {
        try
        {
            if (!await _gostream.IsVaultModePresentAsync(ct).ConfigureAwait(false))
            {
                return;
            }

            var stubPath = await ResolveStubPathAsync(tmdbId, type, season, episode, ct).ConfigureAwait(false);
            if (stubPath is null)
            {
                _logger.LogDebug(
                    "[Vault] unprestage skipped: no materialised_state row for tmdb={Tmdb} type={Type} s={Season} e={Episode}",
                    tmdbId, type, season, episode);
                return;
            }

            await _gostream.UnprestageAsync(stubPath, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "[Vault] unprestaged stub_path={Stub} (tmdb={Tmdb} type={Type})",
                stubPath, tmdbId, type);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[Vault] unprestage failed for tmdb={Tmdb} type={Type} s={Season} e={Episode}; best-effort, ignoring",
                tmdbId, type, season, episode);
        }
    }

    /// <inheritdoc />
    public async Task UnprestageStubAsync(string stubPath, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(stubPath))
        {
            return;
        }

        try
        {
            if (!await _gostream.IsVaultModePresentAsync(ct).ConfigureAwait(false))
            {
                return;
            }

            await _gostream.UnprestageAsync(stubPath, ct).ConfigureAwait(false);
            _logger.LogInformation("[Vault] unprestaged stub_path={Stub}", stubPath);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "[Vault] unprestage failed for stub_path={Stub}; best-effort, ignoring",
                stubPath);
        }
    }

    private async Task<string?> ResolveStubPathAsync(int tmdbId, string type, int? season, int? episode, CancellationToken ct)
    {
        var (seasonSentinel, episodeSentinel) = ChannelItemId.ToSentinels(season, episode);
        var row = await _db.GetMaterialisedStateAsync(tmdbId, type, seasonSentinel, episodeSentinel, ct)
            .ConfigureAwait(false);
        if (row is null || string.IsNullOrWhiteSpace(row.StubPath))
        {
            return null;
        }

        return row.StubPath;
    }
}
