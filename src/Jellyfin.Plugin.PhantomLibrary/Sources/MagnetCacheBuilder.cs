using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Jellyfin.Plugin.PhantomLibrary.State;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Sources;

/// <summary>
/// The magnet-cache STORE builder (p6-magnet-cache-store). Given a phantom
/// item (a claimed <c>magnet_cache_jobs</c> row), it runs the FULL Prowlarr
/// fan-out for that item (via <see cref="MagnetSelector"/>, which aggregates
/// every configured/enabled indexer) and writes the resulting high-confidence
/// candidate set into the <c>source_candidates</c> store keyed by the same
/// item tuple. This is the ONLY place in the plugin that still drives the
/// heavy Prowlarr fan-out for cache population; the availability hot loop no
/// longer does (p6-decouple-oracle-magnetcache), and materialise reads the
/// pre-built cache instead (p6-materialise-ttfb-fix, not this task).
///
/// Opportunistic (p6-magnet-cache-opportunistic-prefetch) and background
/// (p6-magnet-cache-background-sweep) callers do not call this directly — they
/// ENQUEUE jobs (<see cref="PhantomDb.EnqueueMagnetCacheJobAsync"/>) at their
/// chosen priority, and a driver claims + runs them in priority order via
/// <see cref="ProcessNextAsync"/>.
///
/// Movie AND episode parity: the builder is item-tuple driven (movie =>
/// season=-1/episode=-1 sentinels, episode => real season/episode) exactly
/// like <c>source_candidates</c> / <c>availability_items</c>, so both flows go
/// through the identical claim → fan-out → write path.
/// </summary>
public sealed class MagnetCacheBuilder
{
    /// <summary>
    /// Resolves an item's search metadata (imdb id, title, year) needed to
    /// drive the Prowlarr fan-out. Injectable so tests can seed metadata
    /// without a live TMDB.
    /// </summary>
    public delegate Task<MagnetCacheItemMeta?> MetaResolver(
        int tmdbId,
        string type,
        int season,
        int episode,
        CancellationToken ct);

    /// <summary>
    /// Runs the full Prowlarr fan-out for an item and returns the ranked
    /// candidate set. Injectable so tests can seed a mocked Prowlarr result;
    /// the default binds to <see cref="MagnetSelector.SelectRankedAsync"/>.
    /// </summary>
    public delegate Task<IReadOnlyList<MagnetCandidate>> FanOut(
        int tmdbId,
        string? imdbId,
        string type,
        int? season,
        int? episode,
        string title,
        int? year,
        CancellationToken ct);

    private readonly PhantomDb _db;
    private readonly MetaResolver _resolveMeta;
    private readonly FanOut _fanOut;
    private readonly ILogger<MagnetCacheBuilder> _logger;
    private readonly Func<PluginConfiguration> _configProvider;
    private readonly string _owner = $"magnet-cache-builder-{Environment.MachineName}-{Guid.NewGuid():N}";

    public MagnetCacheBuilder(
        PhantomDb db,
        MagnetSelector selector,
        TmdbExternalIdResolver externalIds,
        ILogger<MagnetCacheBuilder> logger)
        : this(
            db,
            BuildTmdbMetaResolver(db, externalIds),
            selector.SelectRankedAsync,
            logger,
            () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal MagnetCacheBuilder(
        PhantomDb db,
        MetaResolver resolveMeta,
        FanOut fanOut,
        ILogger<MagnetCacheBuilder> logger,
        Func<PluginConfiguration> configProvider)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _resolveMeta = resolveMeta ?? throw new ArgumentNullException(nameof(resolveMeta));
        _fanOut = fanOut ?? throw new ArgumentNullException(nameof(fanOut));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
    }

    private static MetaResolver BuildTmdbMetaResolver(PhantomDb db, TmdbExternalIdResolver externalIds)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(externalIds);
        return async (tmdbId, type, season, episode, ct) =>
        {
            var metaType = type == "movie" ? "movie" : "series";
            var meta = await db.GetTmdbMetadataAsync(tmdbId, metaType, ct).ConfigureAwait(false);
            if (meta is null || string.IsNullOrWhiteSpace(meta.Title))
            {
                return null;
            }

            var imdbType = type == "movie" ? "movie" : "series";
            var imdb = await externalIds.GetImdbIdAsync(tmdbId, imdbType, ct).ConfigureAwait(false);
            return new MagnetCacheItemMeta(imdb, meta.Title, meta.Year);
        };
    }

    /// <summary>
    /// Claim the single highest-priority pending magnet-cache job and build its
    /// candidate set. Returns the outcome, or <c>null</c> when no job was
    /// claimable. Callers loop this to drain the queue in priority order.
    /// </summary>
    public async Task<MagnetCacheBuildResult?> ProcessNextAsync(CancellationToken ct)
    {
        var cfg = _configProvider();
        var lease = TimeSpan.FromMinutes(Math.Max(1, cfg.MagnetCacheBuildLeaseMinutes));
        var job = await _db.ClaimNextMagnetCacheJobAsync(_owner, lease, ct).ConfigureAwait(false);
        if (job is null)
        {
            return null;
        }

        return await BuildClaimedAsync(job, cfg, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Build the candidate set for an already-claimed job. Exposed for tests
    /// and callers that already hold a claim.
    /// </summary>
    public async Task<MagnetCacheBuildResult> BuildClaimedAsync(
        MagnetCacheJobRow job,
        PluginConfiguration cfg,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(cfg);
        try
        {
            var meta = await _resolveMeta(job.TmdbId, job.Type, job.Season, job.Episode, ct).ConfigureAwait(false);
            if (meta is null || string.IsNullOrWhiteSpace(meta.Title))
            {
                await _db.FailMagnetCacheJobAsync(
                    job.TmdbId, job.Type, job.Season, job.Episode, job.Preset,
                    "no_metadata", ct).ConfigureAwait(false);
                _logger.LogWarning(
                    "MagnetCacheBuilder: no metadata for {Type}/{Tmdb} s{Season}e{Episode}; job failed",
                    job.Type, job.TmdbId, job.Season, job.Episode);
                return new MagnetCacheBuildResult(job, 0, "no_metadata");
            }

            var candidates = await _fanOut(
                job.TmdbId,
                meta.ImdbId,
                job.Type,
                job.Type == "episode" ? job.Season : null,
                job.Type == "episode" ? job.Episode : null,
                meta.Title,
                meta.Year,
                ct).ConfigureAwait(false);

            var ttl = TimeSpan.FromHours(Math.Max(1, cfg.MagnetCacheTtlHours));
            if (candidates.Count > 0)
            {
                await _db.UpsertSourceCandidatesAsync(
                    job.TmdbId,
                    job.Type,
                    job.Season,
                    job.Episode,
                    job.Preset,
                    candidates,
                    "magnet_cache_builder",
                    ttl,
                    ct).ConfigureAwait(false);
            }

            await _db.CompleteMagnetCacheJobAsync(
                job.TmdbId, job.Type, job.Season, job.Episode, job.Preset,
                candidates.Count, ct).ConfigureAwait(false);
            _logger.LogInformation(
                "MagnetCacheBuilder: built {Count} candidates for {Type}/{Tmdb} s{Season}e{Episode}",
                candidates.Count, job.Type, job.TmdbId, job.Season, job.Episode);
            return new MagnetCacheBuildResult(job, candidates.Count, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _db.FailMagnetCacheJobAsync(
                job.TmdbId, job.Type, job.Season, job.Episode, job.Preset,
                ex.GetType().Name, ct).ConfigureAwait(false);
            _logger.LogError(
                ex,
                "MagnetCacheBuilder: fan-out failed for {Type}/{Tmdb} s{Season}e{Episode}",
                job.Type, job.TmdbId, job.Season, job.Episode);
            return new MagnetCacheBuildResult(job, 0, ex.GetType().Name);
        }
    }
}

/// <summary>Search metadata for an item, resolved for the Prowlarr fan-out.</summary>
public sealed record MagnetCacheItemMeta(string? ImdbId, string Title, int? Year);

/// <summary>Outcome of building one magnet-cache job.</summary>
public sealed record MagnetCacheBuildResult(MagnetCacheJobRow Job, int CandidateCount, string? Error);
