using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Channels;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.Sources;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Scheduled;

/// <summary>
/// Bounded background source-availability scheduler. It only mutates
/// Phantom's private DB and external source caches; it never touches
/// Jellyfin DB rows or media-tree paths.
/// </summary>
public sealed class AvailabilityProbeWorker : IHostedService, IDisposable
{
    private readonly PhantomDb _db;
    private readonly MagnetSelector _selector;
    private readonly TmdbExternalIdResolver _externalIds;
    private readonly ITmdbClient _tmdb;
    private readonly ChannelStateProvider _state;
    private readonly ILogger<AvailabilityProbeWorker> _logger;
    private readonly Func<PluginConfiguration> _configProvider;
    private readonly string _owner = $"availability-{Environment.MachineName}-{Guid.NewGuid():N}";
    private Timer? _timer;
    private CancellationTokenSource? _stopping;
    private Task? _currentTick;
    private int _running;

    public AvailabilityProbeWorker(
        PhantomDb db,
        MagnetSelector selector,
        TmdbExternalIdResolver externalIds,
        ITmdbClient tmdb,
        ChannelStateProvider state,
        ILogger<AvailabilityProbeWorker> logger)
        : this(db, selector, externalIds, tmdb, state, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal AvailabilityProbeWorker(
        PhantomDb db,
        MagnetSelector selector,
        TmdbExternalIdResolver externalIds,
        ITmdbClient tmdb,
        ChannelStateProvider state,
        ILogger<AvailabilityProbeWorker> logger,
        Func<PluginConfiguration> configProvider)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _externalIds = externalIds ?? throw new ArgumentNullException(nameof(externalIds));
        _tmdb = tmdb ?? throw new ArgumentNullException(nameof(tmdb));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cfg = _configProvider();
        if (!cfg.AvailabilityProbeEnabled)
        {
            _logger.LogInformation("Availability probe worker disabled by configuration");
            return Task.CompletedTask;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, cfg.AvailabilityProbeMinIntervalSeconds));
        _timer = new Timer(_ => _currentTick = TickAsync(_stopping.Token), null, interval, interval);
        _logger.LogInformation("Availability probe worker started interval={Interval}s owner={Owner}", interval.TotalSeconds, _owner);
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

    private async Task TickAsync(CancellationToken serviceStopping)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        try
        {
            var cfg = _configProvider();
            if (!cfg.AvailabilityProbeEnabled)
            {
                return;
            }

            var batch = Math.Max(1, cfg.AvailabilityMaxBatchSize);
            var anyWork = false;
            for (var i = 0; i < batch; i++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(serviceStopping);
                cts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, cfg.AvailabilityLeaseMinutes)));
                var didWork = await ProbeOneAvailabilityAsync(cfg, cts.Token).ConfigureAwait(false);
                if (!didWork)
                {
                    didWork = await ExpandOneSeriesAsync(cfg, cts.Token).ConfigureAwait(false);
                }

                anyWork |= didWork;
                if (!didWork)
                {
                    break;
                }
            }

            var nextDelay = TimeSpan.FromSeconds(Math.Max(1, anyWork ? cfg.AvailabilityProbeMinIntervalSeconds : cfg.AvailabilityProbeMaxIntervalSeconds));
            _timer?.Change(nextDelay, nextDelay);
        }
        catch (OperationCanceledException) when (serviceStopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Availability probe tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task<bool> ProbeOneAvailabilityAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var policyHash = ComputePolicyHash(cfg);
        var lease = await _db.ClaimDueAvailabilityAsync(
            _owner,
            TimeSpan.FromMinutes(Math.Max(1, cfg.AvailabilityLeaseMinutes)),
            now,
            policyHash,
            ct).ConfigureAwait(false);
        if (lease is null)
        {
            return false;
        }

        try
        {
            var metaType = lease.Type == "movie" ? "movie" : "series";
            var meta = await _db.GetTmdbMetadataAsync(lease.TmdbId, metaType, ct).ConfigureAwait(false);
            if (meta is null || string.IsNullOrWhiteSpace(meta.Title) || !meta.Year.HasValue)
            {
                await _db.RescheduleAvailabilityTransientAsync(
                    lease,
                    now.AddMinutes(Math.Max(1, cfg.AvailabilityTransientRetryMinutes)),
                    "missing_metadata",
                    $"Missing title/year for {metaType}/{lease.TmdbId}",
                    ct).ConfigureAwait(false);
                return true;
            }

            var imdbType = lease.Type == "movie" ? "movie" : "series";
            var imdb = await _externalIds.GetImdbIdAsync(lease.TmdbId, imdbType, ct).ConfigureAwait(false);
            var probe = await _selector.ProbeAsync(
                lease.TmdbId,
                imdb,
                lease.Type,
                lease.Type == "episode" ? lease.Season : null,
                lease.Type == "episode" ? lease.Episode : null,
                meta.Title,
                meta.Year,
                ct).ConfigureAwait(false);

            switch (probe.Outcome)
            {
                case MagnetProbeOutcome.Available:
                    {
                        var picked = probe.Candidates[0];
                        var entry = new MagnetCacheEntry
                        {
                            Magnet = picked.Magnet,
                            InfoHash = picked.InfoHash,
                            Size = picked.Size,
                            Seeders = picked.Seeders,
                            Indexer = picked.Indexer,
                            CachedAt = now,
                            Ttl = TimeSpan.FromHours(Math.Max(1, cfg.MagnetCacheTtlHours)),
                            Source = "availability",
                        };
                        var magnetKey = new MagnetCacheKey(lease.TmdbId, imdb, lease.Type,
                            lease.Type == "episode" ? lease.Season : null,
                            lease.Type == "episode" ? lease.Episode : null,
                            cfg.SourcePickerPreset);
                        await _db.PutCachedMagnetAsync(magnetKey, entry, ct).ConfigureAwait(false);
                        await _db.DeleteUnavailableAsync(
                            new UnavailableKey(lease.TmdbId, imdb, lease.Type,
                                lease.Type == "episode" ? lease.Season : null,
                                lease.Type == "episode" ? lease.Episode : null),
                            ct).ConfigureAwait(false);
                        await _db.CompleteAvailabilityProbeAsync(
                            lease,
                            "available",
                            now,
                            now.AddDays(Math.Max(1, cfg.AvailabilityAvailableTtlDays)),
                            policyHash,
                            entry,
                            null,
                            null,
                            ct).ConfigureAwait(false);
                        if (lease.Status != "available")
                        {
                            BumpFor(lease.Type);
                        }

                        _logger.LogInformation("Availability available {Type}/{Tmdb} s{Season}e{Episode} via {Indexer}", lease.Type, lease.TmdbId, lease.Season, lease.Episode, picked.Indexer);
                        return true;
                    }

                case MagnetProbeOutcome.DefinitiveUnavailable:
                    await _db.CompleteAvailabilityProbeAsync(
                        lease,
                        "unavailable",
                        now,
                        now.AddDays(Math.Max(1, cfg.AvailabilityUnavailableTtlDays)),
                        policyHash,
                        candidate: null,
                        errorKind: null,
                        errorMessage: null,
                        ct).ConfigureAwait(false);
                    if (lease.Status == "available")
                    {
                        BumpFor(lease.Type);
                    }

                    _logger.LogInformation("Availability unavailable {Type}/{Tmdb} s{Season}e{Episode}", lease.Type, lease.TmdbId, lease.Season, lease.Episode);
                    return true;

                case MagnetProbeOutcome.IndeterminateTransient:
                    await _db.RescheduleAvailabilityTransientAsync(
                        lease,
                        now.AddMinutes(Math.Max(1, cfg.AvailabilityTransientRetryMinutes)),
                        probe.ErrorKind ?? "transient",
                        probe.ErrorMessage,
                        ct).ConfigureAwait(false);
                    _logger.LogInformation("Availability transient {Type}/{Tmdb} s{Season}e{Episode}: {Kind}", lease.Type, lease.TmdbId, lease.Season, lease.Episode, probe.ErrorKind);
                    return true;

                default:
                    throw new InvalidOperationException($"Unknown probe outcome {probe.Outcome}");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _db.RescheduleAvailabilityTransientAsync(
                lease,
                DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, cfg.AvailabilityTransientRetryMinutes)),
                "probe_exception",
                ex.Message,
                CancellationToken.None).ConfigureAwait(false);
            _logger.LogWarning(ex, "Availability probe exception for {Type}/{Tmdb}", lease.Type, lease.TmdbId);
            return true;
        }
    }

    private async Task<bool> ExpandOneSeriesAsync(PluginConfiguration cfg, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var lease = await _db.ClaimDueSeriesExpansionAsync(
            _owner,
            TimeSpan.FromMinutes(Math.Max(1, cfg.AvailabilityLeaseMinutes)),
            now,
            ct).ConfigureAwait(false);
        if (lease is null)
        {
            return false;
        }

        try
        {
            var language = string.IsNullOrWhiteSpace(cfg.DiscoveryLanguage) ? null : cfg.DiscoveryLanguage;
            var details = await _tmdb.GetSeriesAsync(lease.SeriesTmdbId, language, ct).ConfigureAwait(false);
            if (details is null || details.NumberOfSeasons <= 0)
            {
                await _db.FailSeriesExpansionAsync(
                    lease,
                    now.AddDays(Math.Max(1, cfg.SeriesExpansionTtlDays)),
                    "series_not_found_or_empty",
                    null,
                    ct).ConfigureAwait(false);
                return true;
            }

            var rows = new List<TmdbEpisodeRow>();
            var ids = new Dictionary<(int Season, int Episode), (int EpisodeTmdbId, string? AirDate)>();
            for (var season = 1; season <= details.NumberOfSeasons; season++)
            {
                ct.ThrowIfCancellationRequested();
                var seasonDetails = await _tmdb.GetSeasonAsync(lease.SeriesTmdbId, season, language, ct).ConfigureAwait(false);
                if (seasonDetails is null)
                {
                    continue;
                }

                foreach (var e in seasonDetails.Episodes)
                {
                    if (e.SeasonNumber <= 0 || e.EpisodeNumber <= 0)
                    {
                        continue;
                    }

                    var title = string.IsNullOrWhiteSpace(e.Name) ? $"Episode {e.EpisodeNumber}" : e.Name!;
                    rows.Add(new TmdbEpisodeRow(
                        lease.SeriesTmdbId,
                        e.SeasonNumber,
                        e.EpisodeNumber,
                        title,
                        e.Overview,
                        BuildImageUrl(e.StillPath),
                        e.AirDate,
                        e.Runtime,
                        now));
                    ids[(e.SeasonNumber, e.EpisodeNumber)] = (e.Id, e.AirDate);
                }
            }

            await _db.CompleteSeriesExpansionAsync(
                lease,
                rows,
                ids,
                now,
                now.AddDays(Math.Max(1, cfg.SeriesExpansionTtlDays)),
                TimeSpan.FromHours(Math.Max(0, cfg.EpisodeReleaseDelayHours)),
                ct).ConfigureAwait(false);
            _logger.LogInformation("Series expansion complete tmdb={Tmdb} episodes={Episodes}", lease.SeriesTmdbId, rows.Count);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _db.FailSeriesExpansionAsync(
                lease,
                DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, cfg.SeriesExpansionTransientRetryMinutes)),
                "series_expansion_exception",
                ex.Message,
                CancellationToken.None).ConfigureAwait(false);
            _logger.LogWarning(ex, "Series expansion failed for tmdb={Tmdb}", lease.SeriesTmdbId);
            return true;
        }
    }

    private void BumpFor(string type)
    {
        if (type == "movie")
        {
            _state.BumpDataVersion(ChannelStateProvider.KindMovies);
        }
        else
        {
            _state.BumpDataVersion(ChannelStateProvider.KindShows);
        }
    }

    private static string? BuildImageUrl(string? path)
        => string.IsNullOrWhiteSpace(path) ? null : "https://image.tmdb.org/t/p/w500" + path;

    private static string ComputePolicyHash(PluginConfiguration cfg)
    {
        var raw = string.Join("|",
            cfg.SourcePickerPreset,
            cfg.QualityPreset.ToString(),
            cfg.PreferredResolution,
            cfg.ResolutionFallbackOrder,
            cfg.SeederWeight.ToString(CultureInfo.InvariantCulture),
            cfg.MinSeeders.ToString(CultureInfo.InvariantCulture),
            cfg.MinSizeGb1080p.ToString(CultureInfo.InvariantCulture),
            cfg.MinSizeGb4K.ToString(CultureInfo.InvariantCulture),
            cfg.ProwlarrBaseUrl,
            string.IsNullOrWhiteSpace(cfg.ProwlarrApiKey) ? "no-prowlarr-key" : "prowlarr-key-set",
            cfg.TorrentioBaseUrl);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }
}
