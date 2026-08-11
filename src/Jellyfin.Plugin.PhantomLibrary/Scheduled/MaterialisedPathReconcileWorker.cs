using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Scheduled;

/// <summary>
/// Heals <c>materialised_state</c> rows whose recorded gostream FUSE path no
/// longer exists on disk.
///
/// gostream's virtual MKV tree is rebuilt from its own torrent state whenever it
/// restarts, and that rebuild is NOT guaranteed to reproduce the exact path a
/// previous run produced: the same episode can move from an IMDB-id directory
/// (<c>tv/tt9018736/Season.01/..._f8dbbe15.mkv</c>) to a series-name directory
/// (<c>tv/Avatar_The_Last_Airbender (2024)/Season.01/..._994f84bb.mkv</c>) with a
/// different per-episode hash. The channel decides availability with
/// <c>File.Exists(ResolveEpisodePath(state.FusePath))</c>, so a drifted path makes
/// a previously-materialised item show as <b>unavailable</b> even though the
/// content is still present at a new path.
///
/// This worker re-resolves each drifted row's current path using the plugin's own
/// authoritative, TMDB-scoped mapping (<c>gostream_path_tmdb</c>, populated as the
/// channels are browsed) and rewrites <c>fuse_path</c> in place. Keying on TMDB (not
/// a filename) is essential: two same-named series (Avatar 2005 tmdb=246 vs Avatar
/// 2024 tmdb=82452) must never be collapsed onto one directory. A row whose series
/// has no current mapping, or whose specific episode is genuinely absent from the
/// current tree, is left untouched (correctly stays unavailable).
///
/// Best-effort and idempotent: healthy rows are a cheap <c>File.Exists</c> check,
/// any failure is logged and swallowed, and it never touches gostream state.
/// </summary>
public sealed class MaterialisedPathReconcileWorker : IHostedService, IDisposable
{
    // gostream_path_tmdb is filled as the channels are browsed; start after the
    // prewarm worker (initial +15s) has driven at least one listing, then re-run
    // periodically so newly-browsed series get healed and post-restart drift is
    // absorbed off the interactive path.
    private static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private readonly PhantomDb _db;
    private readonly ILogger<MaterialisedPathReconcileWorker> _logger;
    private readonly Func<PluginConfiguration> _configProvider;
    private Timer? _timer;
    private CancellationTokenSource? _stopping;
    private Task? _currentTick;
    private int _running;

    public MaterialisedPathReconcileWorker(
        PhantomDb db,
        ILogger<MaterialisedPathReconcileWorker> logger)
        : this(db, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal MaterialisedPathReconcileWorker(
        PhantomDb db,
        ILogger<MaterialisedPathReconcileWorker> logger,
        Func<PluginConfiguration> configProvider)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _timer = new Timer(_ => _currentTick = TickAsync(_stopping.Token), null, InitialDelay, Interval);
        _logger.LogInformation(
            "Materialised-path reconcile worker started initialDelay={Initial}s interval={Interval}m",
            InitialDelay.TotalSeconds,
            Interval.TotalMinutes);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        _stopping?.Cancel();
        return _currentTick ?? Task.CompletedTask;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _stopping?.Dispose();
    }

    private async Task TickAsync(CancellationToken ct)
    {
        // Skip if a previous reconcile is still running.
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_configProvider().MaterialisedPathReconcileEnabled || ct.IsCancellationRequested)
            {
                return;
            }

            var healed = 0;
            var unresolved = 0;

            foreach (var type in new[] { "movie", "episode" })
            {
                IReadOnlyList<MaterialisedStateRow> rows;
                try
                {
                    rows = await _db.ListMaterialisedStateAsync(type, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Reconcile: listing materialised '{Type}' rows failed", type);
                    continue;
                }

                foreach (var row in rows)
                {
                    if (ct.IsCancellationRequested)
                    {
                        return;
                    }

                    // Healthy rows resolve through the same path the channel uses.
                    var resolved = string.Equals(row.Type, "movie", StringComparison.Ordinal)
                        ? GostreamPathResolver.ResolveMoviePath(row.FusePath)
                        : GostreamPathResolver.ResolveEpisodePath(row.FusePath);
                    if (SafeFileExists(resolved))
                    {
                        continue;
                    }

                    string? current;
                    try
                    {
                        current = await ResolveCurrentPathAsync(row, ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Reconcile: resolving current path for tmdb={Tmdb} {Type} s{S}e{E} failed", row.TmdbId, row.Type, row.Season, row.Episode);
                        current = null;
                    }

                    if (current is null)
                    {
                        unresolved++;
                        continue;
                    }

                    if (!string.Equals(current, row.FusePath, StringComparison.Ordinal))
                    {
                        try
                        {
                            var n = await _db.UpdateMaterialisedFusePathAsync(row.TmdbId, row.Type, row.Season, row.Episode, current, ct).ConfigureAwait(false);
                            if (n > 0)
                            {
                                healed++;
                                _logger.LogInformation(
                                    "Reconcile: re-homed materialised tmdb={Tmdb} {Type} s{S}e{E} -> {Path}",
                                    row.TmdbId, row.Type, row.Season, row.Episode, current);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "Reconcile: updating fuse_path for tmdb={Tmdb} {Type} s{S}e{E} failed", row.TmdbId, row.Type, row.Season, row.Episode);
                        }
                    }
                }
            }

            if (healed > 0 || unresolved > 0)
            {
                _logger.LogInformation(
                    "Materialised-path reconcile: healed={Healed} still-unresolved={Unresolved} (drifted rows whose content is not in the current gostream tree)",
                    healed,
                    unresolved);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Materialised-path reconcile tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>
    /// Resolve the drifted row's CURRENT gostream path using the TMDB-scoped
    /// <c>gostream_path_tmdb</c> mapping. Movie: the mapped file directly. Episode:
    /// the mapped series directory, then the file inside it whose SxxExx token
    /// matches (season, episode). Returns null when there is no current mapping or
    /// the specific episode is genuinely absent from the tree.
    /// </summary>
    private async Task<string?> ResolveCurrentPathAsync(MaterialisedStateRow row, CancellationToken ct)
    {
        if (string.Equals(row.Type, "movie", StringComparison.Ordinal))
        {
            var moviePath = await _db.GetGostreamPathByTmdbAsync(row.TmdbId, "movie", ct).ConfigureAwait(false);
            return moviePath is not null && SafeFileExists(moviePath) ? moviePath : null;
        }

        var seriesDir = await _db.GetGostreamPathByTmdbAsync(row.TmdbId, "series", ct).ConfigureAwait(false);
        if (seriesDir is null || !SafeDirExists(seriesDir))
        {
            return null;
        }

        return MaterialisedPathReconciler.FindEpisodeFile(seriesDir, row.Season, row.Episode);
    }

    private static bool SafeFileExists(string path)
    {
        try
        {
            return File.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SafeDirExists(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

/// <summary>
/// Pure, testable helpers for locating an episode file inside a series directory
/// by its SxxExx token (season/episode), tolerant of the per-episode hash suffix
/// and season-directory naming that gostream may change across restarts.
/// </summary>
public static class MaterialisedPathReconciler
{
    private static readonly string[] VideoExtensions = { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".webm" };

    // Matches SxxEyy / sNNeNN anywhere in a filename (1-3 digits each).
    private static readonly Regex SeasonEpisode = new(
        @"[Ss](\d{1,3})[Ee](\d{1,3})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Parse a season/episode pair from a file name (e.g.
    /// <c>Avatar_The_Last_Airbender_S01E01_994f84bb.mkv</c> -> (1, 1)). Returns
    /// false when no SxxExx token is present.
    /// </summary>
    public static bool TryParseSeasonEpisode(string fileName, out int season, out int episode)
    {
        season = -1;
        episode = -1;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var m = SeasonEpisode.Match(fileName);
        if (!m.Success)
        {
            return false;
        }

        return int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out season)
            && int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out episode);
    }

    /// <summary>
    /// Find the video file under <paramref name="seriesDir"/> (recursively) whose
    /// SxxExx token matches (<paramref name="season"/>, <paramref name="episode"/>),
    /// or null if none. Deterministic (ordinal-sorted) when several files match.
    /// </summary>
    public static string? FindEpisodeFile(string seriesDir, int season, int episode)
    {
        if (string.IsNullOrWhiteSpace(seriesDir))
        {
            return null;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(seriesDir, "*", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            return null;
        }

        string? best = null;
        foreach (var path in files)
        {
            if (!IsVideoFile(path))
            {
                continue;
            }

            if (TryParseSeasonEpisode(Path.GetFileName(path), out var s, out var e)
                && s == season && e == episode
                && (best is null || string.CompareOrdinal(path, best) < 0))
            {
                best = path;
            }
        }

        return best;
    }

    private static bool IsVideoFile(string path)
    {
        var ext = Path.GetExtension(path);
        foreach (var v in VideoExtensions)
        {
            if (string.Equals(ext, v, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
