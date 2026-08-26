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
using Jellyfin.Plugin.PhantomLibrary.Diagnostics;
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
    private readonly ProbeDelegate _probe;
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
        : this(db, selector, externalIds, tmdb, state, logger, configProvider, probe: null)
    {
    }

    /// <summary>
    /// Delegate matching <see cref="MagnetSelector.ProbeAsync"/>. Exists purely
    /// as a test seam so a test can inject a synthetic probe outcome (e.g.
    /// <see cref="MagnetProbeOutcome.NoCapableIndexer"/>) without reaching into
    /// the source-selection layer owned by a sibling.
    /// </summary>
    internal delegate Task<MagnetProbeResult> ProbeDelegate(
        int tmdbId,
        string? imdbId,
        string type,
        int? season,
        int? episode,
        string title,
        int? year,
        CancellationToken ct);

    internal AvailabilityProbeWorker(
        PhantomDb db,
        MagnetSelector selector,
        TmdbExternalIdResolver externalIds,
        ITmdbClient tmdb,
        ChannelStateProvider state,
        ILogger<AvailabilityProbeWorker> logger,
        Func<PluginConfiguration> configProvider,
        ProbeDelegate? probe)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _externalIds = externalIds ?? throw new ArgumentNullException(nameof(externalIds));
        _tmdb = tmdb ?? throw new ArgumentNullException(nameof(tmdb));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _probe = probe ?? _selector.ProbeAsync;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var cfg = _configProvider();
        var interval = TimeSpan.FromSeconds(Math.Max(1, cfg.AvailabilityProbeMinIntervalSeconds));
        _timer = new Timer(_ => _currentTick = TickAsync(_stopping.Token), null, interval, interval);
        _logger.LogInformation(
            "Availability probe worker started interval={Interval}s owner={Owner} enabled={Enabled}",
            interval.TotalSeconds,
            _owner,
            cfg.AvailabilityProbeEnabled);
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

            // Yield to user-initiated work: if a user-driven on-demand probe
            // touched the activity marker very recently, back off this tick so
            // the background sweep does not compete for the DB write lock /
            // indexer budget while the user is actively driving. Reschedule at
            // the slow (max) interval.
            var yieldWindow = Math.Max(0, cfg.AvailabilityYieldToUserSeconds);
            if (yieldWindow > 0)
            {
                var lastActivity = await _db.GetUserActivityAtAsync(serviceStopping).ConfigureAwait(false);
                if (lastActivity is { } activityAt
                    && DateTimeOffset.UtcNow - activityAt < TimeSpan.FromSeconds(yieldWindow))
                {
                    var backoff = TimeSpan.FromSeconds(Math.Max(1, cfg.AvailabilityProbeMaxIntervalSeconds));
                    _timer?.Change(backoff, backoff);
                    return;
                }
            }

            var batch = Math.Max(1, cfg.AvailabilityMaxBatchSize);
            var anyWork = false;
            for (var i = 0; i < batch; i++)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(serviceStopping);
                cts.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, cfg.AvailabilityLeaseMinutes)));

                // TV parity: series expansion creates episode availability work,
                // and episode probes must not sit behind a movie backlog. Each
                // slot gives TV expansion a chance, then probes availability
                // with alternating episode/movie preference and fallback.
                var expanded = i % 2 == 0 && await ExpandOneSeriesAsync(cfg, cts.Token).ConfigureAwait(false);
                var preferredType = i % 2 == 0 ? "episode" : "movie";
                var probed = await ProbeOneAvailabilityAsync(cfg, preferredType, cts.Token).ConfigureAwait(false);
                if (!probed)
                {
                    probed = await ProbeOneAvailabilityAsync(cfg, preferredType == "episode" ? "movie" : "episode", cts.Token).ConfigureAwait(false);
                }
                if (!probed)
                {
                    probed = await ProbeOneAvailabilityAsync(cfg, preferredType: null, cts.Token).ConfigureAwait(false);
                }
                if (!expanded && !probed && i % 2 != 0)
                {
                    expanded = await ExpandOneSeriesAsync(cfg, cts.Token).ConfigureAwait(false);
                }

                var didWork = expanded || probed;
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

    private Task<bool> ProbeOneAvailabilityAsync(PluginConfiguration cfg, CancellationToken ct)
        => ProbeOneAvailabilityAsync(cfg, preferredType: null, ct);

    private async Task<bool> ProbeOneAvailabilityAsync(PluginConfiguration cfg, string? preferredType, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var policyHash = ComputePolicyHash(cfg);
        var lease = await _db.ClaimDueAvailabilityAsync(
            _owner,
            TimeSpan.FromMinutes(Math.Max(1, cfg.AvailabilityLeaseMinutes)),
            now,
            policyHash,
            ct,
            preferredType).ConfigureAwait(false);
        if (lease is null)
        {
            return false;
        }

        using var timer = PhantomMetrics.TimeAvailabilityProbe();
        try
        {
            var metaType = lease.Type == "movie" ? "movie" : "series";
            var meta = await _db.GetTmdbMetadataAsync(lease.TmdbId, metaType, ct).ConfigureAwait(false);
            if (meta is null || string.IsNullOrWhiteSpace(meta.Title) || !meta.Year.HasValue)
            {
                await _db.RescheduleAvailabilityTransientAsync(
                    lease,
                    ComputeTransientRetryAt(cfg, lease, now),
                    "missing_metadata",
                    $"Missing title/year for {metaType}/{lease.TmdbId}",
                    ct).ConfigureAwait(false);
                PhantomMetrics.AvailabilityProbe(lease.Type, "transient");
                return true;
            }

            var imdbType = lease.Type == "movie" ? "movie" : "series";
            var imdb = await _externalIds.GetImdbIdAsync(lease.TmdbId, imdbType, ct).ConfigureAwait(false);

            // Pre-classify before spending a probe cycle: neither
            // "no capable indexer" nor "not yet released/aired" is a
            // 30-minute transient — churning the queue at that cadence on a
            // permanent (or long-lived) no-op wastes cycles that should go to
            // items where availability is plausible. Deep-defer both instead.
            if (!_selector.HasCapableIndexer(imdb))
            {
                var backoff = now.AddHours(Math.Max(1, cfg.AvailabilityNoIndexerRetryHours));
                await _db.RescheduleAvailabilityTransientAsync(
                    lease,
                    backoff,
                    "no_capable_indexer",
                    "Pre-filtered: no enabled indexer can serve this query without an imdb id",
                    ct).ConfigureAwait(false);
                PhantomMetrics.AvailabilityProbe(lease.Type, "no_capable_indexer");
                _logger.LogInformation(
                    "Availability pre-filtered no-capable-indexer {Type}/{Tmdb} s{Season}e{Episode}; long backoff {Hours}h",
                    lease.Type, lease.TmdbId, lease.Season, lease.Episode, cfg.AvailabilityNoIndexerRetryHours);
                return true;
            }

            if (lease.Type == "episode")
            {
                var airDate = await _db.GetEpisodeAirDateAsync(lease.TmdbId, lease.Season, lease.Episode, ct).ConfigureAwait(false);
                var releaseDelay = TimeSpan.FromHours(Math.Max(0, cfg.EpisodeReleaseDelayHours));
                var boundary = PhantomDb.ComputeEpisodeNextCheck(airDate, now, releaseDelay);
                if (boundary > now)
                {
                    await _db.RescheduleAvailabilityTransientAsync(
                        lease,
                        boundary,
                        "unreleased",
                        $"Episode air date {airDate} is in the future",
                        ct).ConfigureAwait(false);
                    PhantomMetrics.AvailabilityProbe(lease.Type, "unreleased");
                    _logger.LogInformation(
                        "Availability pre-filtered unreleased {Type}/{Tmdb} s{Season}e{Episode}; deferred to {Boundary}",
                        lease.Type, lease.TmdbId, lease.Season, lease.Episode, boundary);
                    return true;
                }
            }
            else if (meta.Year.Value > now.Year)
            {
                // Movies only carry a release YEAR (see tmdb_metadata), not a
                // precise date; mirror the same Jan-1 synthetic boundary
                // PhantomMoviesChannel already uses for display so scheduling
                // and UI agree on what "unreleased" means.
                var boundary = new DateTimeOffset(meta.Year.Value, 1, 1, 0, 0, 0, TimeSpan.Zero);
                await _db.RescheduleAvailabilityTransientAsync(
                    lease,
                    boundary,
                    "unreleased",
                    $"Movie release year {meta.Year.Value} is in the future",
                    ct).ConfigureAwait(false);
                PhantomMetrics.AvailabilityProbe(lease.Type, "unreleased");
                _logger.LogInformation(
                    "Availability pre-filtered unreleased movie {Tmdb}; deferred to {Boundary}",
                    lease.TmdbId, boundary);
                return true;
            }

            var probe = await _probe(
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
                        await _db.UpsertSourceCandidatesAsync(
                            lease.TmdbId,
                            lease.Type,
                            lease.Season,
                            lease.Episode,
                            cfg.SourcePickerPreset,
                            probe.Candidates,
                            "availability_probe",
                            TimeSpan.FromHours(Math.Max(1, cfg.MagnetCacheTtlHours)),
                            ct).ConfigureAwait(false);
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

                        PhantomMetrics.AvailabilityProbe(lease.Type, "available");
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

                    PhantomMetrics.AvailabilityProbe(lease.Type, "unavailable");
                    _logger.LogInformation("Availability unavailable {Type}/{Tmdb} s{Season}e{Episode}", lease.Type, lease.TmdbId, lease.Season, lease.Episode);
                    return true;

                case MagnetProbeOutcome.IndeterminateTransient:
                    await _db.RescheduleAvailabilityTransientAsync(
                        lease,
                        ComputeTransientRetryAt(cfg, lease, now),
                        probe.ErrorKind ?? "transient",
                        probe.ErrorMessage,
                        ct).ConfigureAwait(false);
                    PhantomMetrics.AvailabilityProbe(lease.Type, "transient");
                    _logger.LogInformation("Availability transient {Type}/{Tmdb} s{Season}e{Episode}: {Kind}", lease.Type, lease.TmdbId, lease.Season, lease.Episode, probe.ErrorKind);
                    return true;

                case MagnetProbeOutcome.NoCapableIndexer:
                    {
                        // No enabled indexer can serve this query as-is (e.g. no
                        // resolvable imdb id and Prowlarr disabled). This is NOT a
                        // 30-minute transient: retrying at that cadence just churns
                        // the queue. Apply a long backoff and leave status
                        // 'unknown' (visibility unchanged). RescheduleAvailability-
                        // Transient only moves next_check_at + records the error;
                        // it does not change status.
                        var backoff = now.AddHours(Math.Max(1, cfg.AvailabilityNoIndexerRetryHours));
                        await _db.RescheduleAvailabilityTransientAsync(
                            lease,
                            backoff,
                            probe.ErrorKind ?? "no_capable_indexer",
                            probe.ErrorMessage,
                            ct).ConfigureAwait(false);
                        PhantomMetrics.AvailabilityProbe(lease.Type, "no_capable_indexer");
                        _logger.LogInformation(
                            "Availability no-capable-indexer {Type}/{Tmdb} s{Season}e{Episode}; long backoff {Hours}h",
                            lease.Type, lease.TmdbId, lease.Season, lease.Episode, cfg.AvailabilityNoIndexerRetryHours);
                        return true;
                    }

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
                ComputeTransientRetryAt(cfg, lease, DateTimeOffset.UtcNow),
                "probe_exception",
                ex.Message,
                CancellationToken.None).ConfigureAwait(false);
            PhantomMetrics.AvailabilityProbe(lease.Type, "transient");
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
                PhantomMetrics.SeriesExpansion("empty");
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
                Math.Max(1, cfg.AvailabilityBackgroundEpisodesPerSeries),
                TimeSpan.FromDays(Math.Max(1, cfg.AvailabilityDeferredEpisodeDays)),
                ct).ConfigureAwait(false);
            PhantomMetrics.SeriesExpansion("success");
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
            PhantomMetrics.SeriesExpansion("transient");
            _logger.LogWarning(ex, "Series expansion failed for tmdb={Tmdb}", lease.SeriesTmdbId);
            return true;
        }
    }

    /// <summary>
    /// Convergence guarantee (ROI Priority 6 item 5): computes the next
    /// transient-retry boundary. Below the escalation threshold this is the
    /// ordinary short <see cref="PluginConfiguration.AvailabilityTransientRetryMinutes"/>
    /// cadence; once <see cref="AvailabilityItemRow.AttemptCount"/> (the
    /// count of consecutive non-definitive outcomes for this row, bumped by
    /// <c>ClaimDueAvailabilityAsync</c> on every claim and reset to 0 on any
    /// definitive completion) exceeds
    /// <see cref="PluginConfiguration.AvailabilityTransientMaxAttempts"/>, the
    /// short interval is replaced with the bounded
    /// <see cref="PluginConfiguration.AvailabilityTransientEscalatedRetryHours"/>
    /// backoff — the same shape already used for the
    /// <c>no_capable_indexer</c>/unreleased pre-filters — so a permanently
    /// transient item (a flaky indexer, a persistent probe exception, missing
    /// metadata that never resolves) cannot churn the short interval forever.
    /// </summary>
    internal static DateTimeOffset ComputeTransientRetryAt(PluginConfiguration cfg, AvailabilityItemRow lease, DateTimeOffset now)
    {
        var maxAttempts = Math.Max(1, cfg.AvailabilityTransientMaxAttempts);
        if (lease.AttemptCount > maxAttempts)
        {
            return now.AddHours(Math.Max(1, cfg.AvailabilityTransientEscalatedRetryHours));
        }

        return now.AddMinutes(Math.Max(1, cfg.AvailabilityTransientRetryMinutes));
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
            KeyFingerprint(cfg.ProwlarrApiKey),
            cfg.TorrentioBaseUrl);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }

    private static string KeyFingerprint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "no-prowlarr-key";
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())));
    }
}
