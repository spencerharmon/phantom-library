using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PhantomLibrary.Clients;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Diagnostics;
using Jellyfin.Plugin.PhantomLibrary.Materialisation;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Sources;

/// <summary>
/// Chosen magnet candidate returned by <see cref="MagnetSelector"/>.
/// Identical shape to the cached row in <c>magnet_cache</c> minus the
/// cache bookkeeping, so callers can flow this straight into a
/// <c>GostreamAddRequest</c> or into the cache.
/// </summary>
public sealed record MagnetCandidate(
    string Magnet,
    string InfoHash,
    long Size,
    int Seeders,
    string Indexer)
{
    public string? Title { get; init; }
}

public enum MagnetProbeOutcome
{
    Available,
    DefinitiveUnavailable,
    IndeterminateTransient,
    NoCapableIndexer,

    /// <summary>
    /// The item's title was degenerate (empty / whitespace / punctuation-only),
    /// could not be repaired from the TMDB record, and no enabled indexer can
    /// serve it IMDB-id-only — so NO indexer request was issued
    /// (p13-degenerate-query-guard). Not a definitive "unavailable": nothing
    /// was searched.
    /// </summary>
    DegenerateQuerySkipped,
}

public sealed record MagnetProbeResult(
    MagnetProbeOutcome Outcome,
    IReadOnlyList<MagnetCandidate> Candidates,
    string? ErrorKind,
    string? ErrorMessage)
{
    public static MagnetProbeResult Available(IReadOnlyList<MagnetCandidate> candidates)
        => new(MagnetProbeOutcome.Available, candidates, null, null);

    public static MagnetProbeResult DefinitiveUnavailable()
        => new(MagnetProbeOutcome.DefinitiveUnavailable, Array.Empty<MagnetCandidate>(), null, null);

    public static MagnetProbeResult NoCapableIndexer(string? message)
        => new(MagnetProbeOutcome.NoCapableIndexer, Array.Empty<MagnetCandidate>(), "no_capable_indexer", message);

    public static MagnetProbeResult Transient(string kind, string? message)
        => new(MagnetProbeOutcome.IndeterminateTransient, Array.Empty<MagnetCandidate>(), kind, message);

    public static MagnetProbeResult DegenerateQuerySkipped(string? message)
        => new(MagnetProbeOutcome.DegenerateQuerySkipped, Array.Empty<MagnetCandidate>(), MagnetSelector.DegenerateQuerySkippedKind, message);
}

/// <summary>
/// Raised by <see cref="MagnetSelector.SelectRankedAsync"/> (whose list-shaped
/// return cannot carry an outcome) when the probe was
/// <see cref="MagnetProbeOutcome.DegenerateQuerySkipped"/>, so callers such as
/// the magnet-cache builder record the distinct
/// <c>degenerate_query_skipped</c> outcome instead of a silent empty build.
/// </summary>
public sealed class DegenerateQuerySkippedException : Exception
{
    public DegenerateQuerySkippedException()
    {
    }

    public DegenerateQuerySkippedException(string message)
        : base(message)
    {
    }

    public DegenerateQuerySkippedException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// Aggregates results from every registered <see cref="IIndexerClient"/>
/// and picks the best release per the configured <see cref="QualityScorer"/>
/// + seeder/size floors. Replaces the pre-channel-arch in-line magnet
/// selection inside <c>Materialiser.cs</c>; tuple-shaped so the
/// channel-arch materialiser can call it without a BaseItem.
///
/// Plan §4.2.0.
/// </summary>
public sealed class MagnetSelector : IDisposable
{
    /// <summary>Error-kind / counted outcome for a skipped degenerate query.</summary>
    public const string DegenerateQuerySkippedKind = "degenerate_query_skipped";

    private readonly IEnumerable<IIndexerClient> _indexers;
    private readonly IQueryTitleRepairSource? _titleRepair;
    private static readonly Regex EpisodeToken = new(@"\bS(?<s>\d{1,2})E(?<e>\d{1,3})\b|\b(?<s2>\d{1,2})x(?<e2>\d{1,3})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly QualityScorer _scorer;
    private readonly Func<PluginConfiguration> _configProvider;
    private readonly ILogger<MagnetSelector> _logger;
    private readonly ILateIndexerResultSink? _lateSink;

    // p13-parallel-indexer-search: past-budget indexer requests outlive the
    // probe that started them. They are bounded by the per-request timeout and
    // IndexerLateCollectorMaxConcurrent, and ALL of them are cancelled when the
    // host disposes this singleton (shutdown).
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly CancellationToken _lifetime;
    private readonly ConcurrentDictionary<long, Task> _lateWork = new();
    private long _lateWorkSeq;
    private int _lateCollectorsActive;

    public MagnetSelector(
        IEnumerable<IIndexerClient> indexers,
        QualityScorer scorer,
        IQueryTitleRepairSource titleRepair,
        ILogger<MagnetSelector> logger,
        ILateIndexerResultSink lateSink)
        : this(indexers, scorer, logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration(), titleRepair, lateSink)
    {
    }

    // Test-friendly ctor: lets tests inject a synthetic configuration
    // without spinning up Plugin.Instance. A null titleRepair means a
    // degenerate title is never repaired (IMDB-only or skip only).
    internal MagnetSelector(
        IEnumerable<IIndexerClient> indexers,
        QualityScorer scorer,
        ILogger<MagnetSelector> logger,
        Func<PluginConfiguration> configProvider,
        IQueryTitleRepairSource? titleRepair = null,
        ILateIndexerResultSink? lateSink = null)
    {
        _indexers = indexers ?? throw new ArgumentNullException(nameof(indexers));
        _scorer = scorer ?? throw new ArgumentNullException(nameof(scorer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _configProvider = configProvider ?? throw new ArgumentNullException(nameof(configProvider));
        _titleRepair = titleRepair;
        _lateSink = lateSink;
        _lifetime = _lifetimeCts.Token;
    }

    /// <summary>Number of probes whose past-budget requests are still running in the background.</summary>
    internal int LateCollectorsActive => Volatile.Read(ref _lateCollectorsActive);

    /// <summary>Completes when every background (past-budget) collection has finished. Test/diagnostic hook.</summary>
    internal Task WhenLateWorkIdleAsync() => Task.WhenAll(_lateWork.Values.ToArray());

    /// <summary>Cancels every still-running background indexer request (host shutdown).</summary>
    public void Dispose()
    {
        if (!_lifetimeCts.IsCancellationRequested)
        {
            _lifetimeCts.Cancel();
        }

        _lifetimeCts.Dispose();
    }

    /// <summary>
    /// Picks the best magnet for the given identifiers, or returns
    /// <c>null</c> if no indexer returned an acceptable candidate.
    /// Errors from individual indexers are logged and skipped; the
    /// remaining indexers' results are still scored.
    /// </summary>
    public async Task<MagnetCandidate?> SelectAsync(
        int tmdbId,
        string? imdbId,
        string type,
        int? season,
        int? episode,
        string? title,
        int? year,
        CancellationToken ct)
    {
        var ranked = await SelectRankedAsync(tmdbId, imdbId, type, season, episode, title, year, ct)
            .ConfigureAwait(false);
        return ranked.Count > 0 ? ranked[0] : null;
    }

    /// <summary>
    /// Returns all acceptable magnets in preference order. Errors from
    /// individual indexers are logged and skipped; the remaining indexers'
    /// results are still scored.
    /// </summary>
    public Task<IReadOnlyList<MagnetCandidate>> SelectRankedAsync(
        int tmdbId, string? imdbId, string type, int? season, int? episode, string? title, int? year, CancellationToken ct)
        => SelectRankedCoreAsync(tmdbId, imdbId, type, season, episode, title, year, IndexerPriority.Interactive, ct);

    public Task<IReadOnlyList<MagnetCandidate>> SelectRankedBackgroundAsync(
        int tmdbId, string? imdbId, string type, int? season, int? episode, string? title, int? year, CancellationToken ct)
        => SelectRankedCoreAsync(tmdbId, imdbId, type, season, episode, title, year, IndexerPriority.Background, ct);

    private async Task<IReadOnlyList<MagnetCandidate>> SelectRankedCoreAsync(
        int tmdbId, string? imdbId, string type, int? season, int? episode, string? title, int? year,
        IndexerPriority priority, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        var probe = await ProbeCoreAsync(tmdbId, imdbId, type, season, episode, title, year, false, priority, ct).ConfigureAwait(false);
        if (probe.Outcome == MagnetProbeOutcome.DegenerateQuerySkipped)
        {
            throw new DegenerateQuerySkippedException(probe.ErrorMessage ?? DegenerateQuerySkippedKind);
        }

        if (priority == IndexerPriority.Background && probe.Outcome == MagnetProbeOutcome.IndeterminateTransient)
        {
            // A skipped/backed-off build must not complete its cache job as an
            // authoritative empty. Preserve the builder's failed/retry path.
            throw new IndexerTransientException(probe.ErrorMessage ?? "Background indexer search indeterminate");
        }

        return probe.Outcome == MagnetProbeOutcome.Available ? probe.Candidates : Array.Empty<MagnetCandidate>();
    }

    /// <summary>
    /// Pre-classifies "no capable indexer" for the given imdb id WITHOUT
    /// invoking <see cref="ProbeAsync"/> — lets the availability-sweep
    /// claim/enqueue path deep-defer a title before spending a probe cycle
    /// on it. Mirrors <see cref="ProbeAsync"/>'s abstention logic exactly:
    /// an item is capable if at least one enabled indexer either does not
    /// require an IMDB id (e.g. a configured Prowlarr) or an IMDB id is
    /// present. If no indexer is enabled at all, this returns true
    /// (capable) deliberately — that is a configuration gap, not a
    /// permanent per-title fact, and stays on the ordinary transient
    /// retry cadence so it recovers promptly once an indexer is enabled.
    /// </summary>
    public bool HasCapableIndexer(string? imdbId)
        => HasCapableIndexer(imdbId, availabilityOracleOnly: false);

    /// <summary>
    /// Availability-sweep variant of <see cref="HasCapableIndexer"/>: pre-classifies
    /// "no capable indexer" scoped to ONLY the indexers eligible for the
    /// high-frequency availability-oracle hot loop (Torrentio) — see
    /// <see cref="ProbeAvailabilityAsync"/>. A Prowlarr-only configuration (which
    /// can serve without an IMDB id) must NOT be counted here: Prowlarr is not
    /// invoked by the availability sweep, so its presence must not mask a
    /// no-IMDB item as "capable" when only Torrentio actually runs.
    /// </summary>
    public bool HasCapableAvailabilityIndexer(string? imdbId)
        => HasCapableIndexer(imdbId, availabilityOracleOnly: true);

    private bool HasCapableIndexer(string? imdbId, bool availabilityOracleOnly)
    {
        var enabled = EnabledIndexers(availabilityOracleOnly);
        if (enabled.Count == 0)
        {
            return true;
        }

        return enabled.Any(i => !i.RequiresImdb || !string.IsNullOrWhiteSpace(imdbId));
    }

    public Task<MagnetProbeResult> ProbeAsync(
        int tmdbId,
        string? imdbId,
        string type,
        int? season,
        int? episode,
        string? title,
        int? year,
        CancellationToken ct)
        => ProbeCoreAsync(tmdbId, imdbId, type, season, episode, title, year, false, IndexerPriority.Interactive, ct);

    /// <summary>
    /// Availability-oracle probe (ROI Priority 6, revised architecture item 1 —
    /// 2026-08-26): used by the high-frequency, full-catalogue availability
    /// sweep. Restricted to indexers with <see cref="Clients.IIndexerClient.IsAvailabilityOracle"/>
    /// set (Torrentio only) so Prowlarr's heavy multi-indexer fan-out never
    /// runs in this hot per-item loop. A no-IMDB item still gets a cheap
    /// signal: Torrentio abstains via <see cref="IndexerNotApplicableException"/>,
    /// which this method (like <see cref="ProbeAsync"/>) surfaces as
    /// <see cref="MagnetProbeOutcome.NoCapableIndexer"/> so the caller applies
    /// the existing no-capable-indexer deep-defer rather than an inline heavy
    /// Prowlarr search.
    /// </summary>
    public Task<MagnetProbeResult> ProbeAvailabilityAsync(
        int tmdbId,
        string? imdbId,
        string type,
        int? season,
        int? episode,
        string? title,
        int? year,
        CancellationToken ct)
        => ProbeCoreAsync(tmdbId, imdbId, type, season, episode, title, year, true, IndexerPriority.Background, ct);

    private List<IIndexerClient> EnabledIndexers(bool availabilityOracleOnly)
    {
        var candidates = availabilityOracleOnly ? _indexers.Where(i => i.IsAvailabilityOracle) : _indexers;
        return candidates.Where(i => i.IsEnabled).ToList();
    }

    private async Task<MagnetProbeResult> ProbeCoreAsync(
        int tmdbId,
        string? imdbId,
        string type,
        int? season,
        int? episode,
        string? title,
        int? year,
        bool availabilityOracleOnly,
        IndexerPriority priority,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        var enabled = EnabledIndexers(availabilityOracleOnly);
        if (enabled.Count == 0)
        {
            return MagnetProbeResult.Transient("no_enabled_indexers", "No enabled indexers are configured");
        }

        // Degenerate-query guard (ROI P13, p13-degenerate-query-guard): the ONE
        // choke point every indexer request flows through (materialise,
        // details/source picker, availability sweep + reconcile, magnet-cache
        // builder — interactive and background, movie and episode). Resolve a
        // non-degenerate title term BEFORE any SearchAsync, repairing from the
        // TMDB record, else fall back to IMDB-id-only indexers, else skip.
        var term = await ResolveQueryTermAsync(tmdbId, imdbId, type, season, episode, title, ct).ConfigureAwait(false);
        if (term.Skip)
        {
            return MagnetProbeResult.DegenerateQuerySkipped(term.Message);
        }

        if (term.Title is null)
        {
            // IMDB-id-only: query only the indexers that can serve it.
            enabled = enabled.Where(i => i.SupportsImdbOnlyQuery(type)).ToList();
            if (enabled.Count == 0)
            {
                PhantomMetrics.DegenerateQuery(type, DegenerateQuerySkippedKind);
                _logger.LogWarning(
                    "Degenerate query skipped for {Type}/{Tmdb} s{Season}e{Episode}: title {Title} unrepairable and no enabled indexer serves an IMDB-only query",
                    type, tmdbId, season, episode, title);
                return MagnetProbeResult.DegenerateQuerySkipped(
                    $"degenerate title '{title}' unrepairable; no enabled indexer serves an IMDB-only {type} query");
            }

            PhantomMetrics.DegenerateQuery(type, "imdb_only");
        }

        var query = new IndexerQuery
        {
            Priority = priority,
            Type = type,
            Tmdb = tmdbId,
            Imdb = type == "movie" ? imdbId : null,
            SeriesImdb = type == "episode" ? imdbId : null,
            Title = term.Title,
            Year = year,
            Season = season,
            Episode = episode,
        };

        // p13-parallel-indexer-search (ROI Priority 13): per-indexer fan-out under a
        // search BUDGET. Every plain indexer (Torrentio) is one request; every
        // fan-out gateway (Prowlarr) first discovers its enabled upstream indexers
        // (runtime, TTL-cached) and then issues one request PER upstream indexer —
        // all concurrently. A budgeted collector gathers results as they arrive and
        // stops at the first of: (a) every request finished, (b) the budget
        // (IndexerSearchBudgetMs, default 10 s) elapsed, (c) early return — at least
        // MinEarlyReturnCandidates candidates with seeders >= MinSeeders are in and
        // EarlyReturnMinElapsedMs has passed (FastIndexerEarlyReturnEnabled).
        // Requests still running at that point are NOT discarded: they continue in
        // the background (each still capped by IndexerProbeTimeoutSeconds, all
        // cancelled on shutdown) and their results are re-ranked with the in-budget
        // ones and written to the magnet cache via ILateIndexerResultSink.
        ct.ThrowIfCancellationRequested();
        var cfg = _configProvider();
        var requestCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        query = query with { SearchCancellation = requestCts.Token };
        var run = new ProbeRun(query, tmdbId, type, season, episode, TimeSpan.FromSeconds(cfg.IndexerProbeTimeoutSeconds), requestCts.Token);

        // While the caller is waiting, its cancellation cancels every request
        // (interactive preemption / request abort). Once this probe has returned,
        // the link is removed so background requests outlive the caller.
        var callerLink = ct.Register(static s => ((CancellationTokenSource)s!).Cancel(), requestCts);

        var top = new List<FanOutRequest>(enabled.Count);
        for (var i = 0; i < enabled.Count; i++)
        {
            var indexer = enabled[i];
            top.Add(indexer is IFanOutIndexerClient gateway
                ? StartDiscovery(run, gateway, i)
                : StartRequest(run, i, -1, indexer.Name, indexer.Name, isFanOutChild: false, t => indexer.SearchAsync(query, t)));
        }

        var pending = new List<FanOutRequest>(top);
        var collected = new List<FanOutRequest>();
        var budgetExpired = false;
        using (var budgetCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            var budgetTask = Task.Delay(TimeSpan.FromMilliseconds(cfg.IndexerSearchBudgetMs), budgetCts.Token);
            Task? earlyWake = cfg.FastIndexerEarlyReturnEnabled
                ? Task.Delay(TimeSpan.FromMilliseconds(cfg.EarlyReturnMinElapsedMs), budgetCts.Token)
                : null;
            var earlyMinimumReached = false;
            try
            {
                while (pending.Count > 0)
                {
                    if (earlyMinimumReached
                        && CountPassing(collected, cfg.MinSeeders) >= cfg.MinEarlyReturnCandidates)
                    {
                        break;
                    }

                    var waitSet = new Task[pending.Count + (earlyWake is null ? 1 : 2)];
                    for (var i = 0; i < pending.Count; i++)
                    {
                        waitSet[i] = pending[i].Task;
                    }

                    waitSet[pending.Count] = budgetTask;
                    if (earlyWake is not null)
                    {
                        waitSet[^1] = earlyWake;
                    }
                    var done = await Task.WhenAny(waitSet).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (done == budgetTask)
                    {
                        budgetExpired = true;
                        break;
                    }

                    if (done == earlyWake)
                    {
                        // Wake even if N hits arrived before the minimum and
                        // every remaining request is hung. Remove the timer once
                        // consumed so insufficient hits cannot cause a busy loop.
                        earlyMinimumReached = true;
                        earlyWake = null;
                        continue;
                    }

                    Collect(pending, collected, pending.First(p => p.Task == done));
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller cancelled: callerLink has already cancelled every request;
                // drain them in the background (metrics + disposal) without caching.
                run.BudgetClosed = true;
                callerLink.Dispose();
                HandOffRemaining(run, pending, collected, requestCts, write: false);
                throw;
            }
            finally
            {
                budgetCts.Cancel();
            }
        }

        run.BudgetClosed = true;
        callerLink.Dispose();

        // Anything that finished in the same instant the budget fired is in-budget.
        FanOutRequest? finished;
        while ((finished = pending.FirstOrDefault(p => p.Task.IsCompleted)) is not null)
        {
            Collect(pending, collected, finished);
        }

        if (budgetExpired && pending.Count > 0)
        {
            IndexerSearchMetrics.RecordBudgetExpired(type, CountPassing(collected, cfg.MinSeeders));
            _logger.LogInformation(
                "Indexer search budget {Budget}ms expired for {Type}/{Tmdb} with {Pending} request(s) still running; ranking {Collected} received candidate(s), late results go to the magnet cache",
                cfg.IndexerSearchBudgetMs, type, tmdbId, pending.Count, collected.Sum(c => c.Hits?.Count ?? 0));
        }

        var fallbackLabel = enabled[0].Name;
        HandOffRemaining(run, pending, collected, requestCts, write: true, fallbackLabel);

        var aggregated = Aggregate(collected);

        // Classify per TOP-LEVEL client so one failing upstream Prowlarr indexer
        // does not turn the whole gateway into a failure when its siblings ran.
        var failures = new List<string>();
        var abstentions = new List<string>();
        var stillPending = 0;
        foreach (var t in top)
        {
            if (!collected.Contains(t))
            {
                stillPending++;
                continue;
            }

            if (t.Failure is not null)
            {
                failures.Add(t.Failure);
                continue;
            }

            if (t.Abstention is not null)
            {
                abstentions.Add(t.Abstention);
                continue;
            }

            if (!t.IsDiscovery || t.Children is not { Count: > 0 } kids)
            {
                continue;
            }

            // Empty/rejected successes do not settle the gateway while a
            // sibling is pending. Use the in-budget snapshot, not state that
            // the late collector could mutate after handoff.
            if (kids.Any(k => !collected.Contains(k)))
            {
                stillPending++;
                continue;
            }

            if (kids.Any(k => k.Succeeded))
            {
                continue;
            }

            failures.Add(string.Join(";", kids.Select(k => k.Failure ?? k.Label)));
        }

        if (aggregated.Count == 0)
        {
            if (failures.Count > 0)
            {
                return MagnetProbeResult.Transient("indexer_partial_or_total_failure", string.Join(";", failures));
            }

            if (stillPending > 0)
            {
                return MagnetProbeResult.Transient(
                    "indexer_budget_expired",
                    $"no candidate within {cfg.IndexerSearchBudgetMs}ms; {pending.Count} indexer request(s) still running");
            }

            if (abstentions.Count == enabled.Count)
            {
                // No enabled indexer could even serve this query (e.g. only Torrentio
                // enabled and no IMDB id). Not a definitive "unavailable" — no indexer ran.
                _logger.LogInformation(
                    "No capable indexer for {Type}/{Tmdb} s{Season} e{Episode}; all {N} enabled indexer(s) abstained",
                    type, tmdbId, season, episode, enabled.Count);
                return MagnetProbeResult.NoCapableIndexer(string.Join(";", abstentions));
            }

            _logger.LogInformation("No indexer returned candidates for {Type}/{Tmdb} s{Season} e{Episode}", type, tmdbId, season, episode);
            return MagnetProbeResult.DefinitiveUnavailable();
        }

        var candidates = Rank(aggregated, type, season, episode, _configProvider(), fallbackLabel);
        if (candidates.Count == 0)
        {
            if (failures.Count > 0)
            {
                return MagnetProbeResult.Transient("indexer_partial_failure_all_candidates_rejected", string.Join(";", failures));
            }

            if (stillPending > 0)
            {
                return MagnetProbeResult.Transient(
                    "indexer_budget_expired_all_candidates_rejected",
                    $"{aggregated.Count} candidate(s) rejected; {pending.Count} indexer request(s) still running");
            }

            _logger.LogInformation("Scorer rejected all {N} candidates for {Type}/{Tmdb}", aggregated.Count, type, tmdbId);
            return MagnetProbeResult.DefinitiveUnavailable();
        }

        return MagnetProbeResult.Available(candidates);
    }

    private static void Collect(List<FanOutRequest> pending, List<FanOutRequest> collected, FanOutRequest req)
    {
        pending.Remove(req);
        collected.Add(req);
        if (req.Children is { Count: > 0 } kids)
        {
            pending.AddRange(kids);
        }
    }

    private static int CountPassing(IEnumerable<FanOutRequest> collected, int minSeeders)
        => collected.Sum(r => r.Hits?.Count(h => h.Seeders >= minSeeders) ?? 0);

    /// <summary>
    /// Union of the collected requests' hits in deterministic (enabled-list,
    /// discovered-list) order, independent of arrival order. The same release
    /// reported by several upstream indexers of ONE gateway is kept once.
    /// </summary>
    private static List<IndexerCandidate> Aggregate(IEnumerable<FanOutRequest> collected)
    {
        var list = new List<IndexerCandidate>();
        var seen = new HashSet<(int, string)>();
        foreach (var r in collected.Where(r => r.Hits is { Count: > 0 }).OrderBy(r => r.Major).ThenBy(r => r.Minor))
        {
            foreach (var h in r.Hits!)
            {
                if (r.IsFanOutChild && !seen.Add((r.Major, h.InfoHash.ToUpperInvariant())))
                {
                    continue;
                }

                list.Add(h);
            }
        }

        return list;
    }

    private List<MagnetCandidate> Rank(
        List<IndexerCandidate> aggregated,
        string type,
        int? season,
        int? episode,
        PluginConfiguration cfg,
        string fallbackLabel)
    {
        var ranked = _scorer.RankCandidates(
            aggregated,
            cfg.QualityPreset,
            cfg.MinSeeders,
            cfg.MinSizeGb1080p,
            cfg.MinSizeGb4K,
            cfg.ResolutionFallbackOrder,
            cfg.SeederWeight,
            cfg.PreferredResolution);

        var episodeRanked = string.Equals(type, "episode", StringComparison.OrdinalIgnoreCase)
            && season.HasValue
            && episode.HasValue
                ? ranked
                    .Select((candidate, index) => new { candidate, index })
                    .OrderByDescending(x => EpisodeSpecificityScore(x.candidate.Title, season.Value, episode.Value))
                    .ThenBy(x => x.index)
                    .Select(x => x.candidate)
                    .ToList()
                : ranked;

        return episodeRanked.Select(picked =>
        {
            var indexerLabel = !string.IsNullOrWhiteSpace(picked.IndexerName) ? picked.IndexerName! : fallbackLabel;
            return new MagnetCandidate(picked.Magnet, picked.InfoHash, picked.Size, picked.Seeders, indexerLabel)
            {
                Title = picked.Title,
            };
        }).ToList();
    }

    /// <summary>Starts one indexer request with the per-request timeout; never throws (outcome is classified on the request).</summary>
    private FanOutRequest StartRequest(
        ProbeRun run,
        int major,
        int minor,
        string client,
        string label,
        bool isFanOutChild,
        Func<CancellationToken, Task<IReadOnlyList<IndexerCandidate>>> search)
    {
        var req = new FanOutRequest(major, minor, client, label, IsDiscovery: false, isFanOutChild);
        req.Task = Task.Run(async () =>
        {
            var sw = Stopwatch.StartNew();
            string status;
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(run.RequestToken);
            timeoutCts.CancelAfter(run.PerRequestTimeout);
            try
            {
                var hits = await search(timeoutCts.Token).ConfigureAwait(false);
                if (hits is { Count: > 0 })
                {
                    req.Hits = new List<IndexerCandidate>(hits);
                }

                req.Succeeded = true;
                status = IndexerSearchMetrics.StatusOk;
            }
            catch (OperationCanceledException) when (run.RequestToken.IsCancellationRequested)
            {
                // Caller cancelled while waiting, late-collector bound hit, or shutdown.
                req.Failure = $"{label}:cancelled";
                status = IndexerSearchMetrics.StatusCancelled;
            }
            catch (OperationCanceledException ex)
            {
                // Only a local timeout may salvage partial results. The caller/
                // shutdown cancellation branch above deliberately never caches.
                if (ex is IndexerPartialCancellationException partial && partial.Candidates.Count > 0)
                {
                    req.Hits = new List<IndexerCandidate>(partial.Candidates);
                }

                req.Failure = $"{label}:timeout";
                status = IndexerSearchMetrics.StatusTimeout;
                _logger.LogWarning(ex, "Indexer {Indexer} timed out after {Timeout}s for {Type}/{Tmdb}", label, run.PerRequestTimeout.TotalSeconds, run.Type, run.TmdbId);
            }
            catch (IndexerNotApplicableException ex)
            {
                // Abstention: the indexer cannot serve this query as-is (e.g. Torrentio
                // with no IMDB id). Not a failure and not transient — do not count it.
                req.Abstention = label;
                status = IndexerSearchMetrics.StatusAbstain;
                _logger.LogDebug(ex, "Indexer {Indexer} abstained (not applicable) for {Type}/{Tmdb}", label, run.Type, run.TmdbId);
            }
            catch (IndexerAuthException ex)
            {
                req.Failure = $"{label}:auth";
                status = IndexerSearchMetrics.StatusAuth;
                _logger.LogWarning(ex, "Indexer {Indexer} returned auth failure for {Type}/{Tmdb}", label, run.Type, run.TmdbId);
            }
            catch (Exception ex)
            {
                req.Failure = $"{label}:transient";
                status = ex is IndexerTransientException transient
                    ? transient.HealthStatus ?? (transient.HttpStatus == 429 ? "429" : IndexerSearchMetrics.StatusError)
                    : IndexerSearchMetrics.StatusError;
                if (status.StartsWith("skipped_", StringComparison.Ordinal))
                {
                    _logger.LogDebug("Indexer {Indexer} {Status}", label, status);
                }
                else
                {
                    _logger.LogWarning(ex, "Indexer {Indexer} failed for {Type}/{Tmdb}", label, run.Type, run.TmdbId);
                }
            }

            IndexerSearchMetrics.RecordRequest(
                client,
                label,
                status,
                run.BudgetClosed ? IndexerSearchMetrics.PhaseLate : IndexerSearchMetrics.PhaseInBudget,
                sw.Elapsed.TotalMilliseconds,
                req.Hits?.Count ?? 0);
        });
        return req;
    }

    /// <summary>Discovers a gateway's enabled upstream indexers, then starts one request per indexer.</summary>
    private FanOutRequest StartDiscovery(ProbeRun run, IFanOutIndexerClient gateway, int major)
    {
        var req = new FanOutRequest(major, -1, gateway.Name, gateway.Name, IsDiscovery: true, IsFanOutChild: false);
        req.Task = Task.Run(async () =>
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(run.RequestToken);
            timeoutCts.CancelAfter(run.PerRequestTimeout);
            try
            {
                var discovered = await gateway.DiscoverIndexersAsync(run.Query, timeoutCts.Token).ConfigureAwait(false);
                var kids = new List<FanOutRequest>(discovered.Count);
                for (var j = 0; j < discovered.Count; j++)
                {
                    var ix = discovered[j];
                    kids.Add(StartRequest(run, major, j, gateway.Name, ix.Name, isFanOutChild: true, t => gateway.SearchIndexerAsync(ix, run.Query, t)));
                }

                req.Children = kids;
                req.Succeeded = true;
            }
            catch (OperationCanceledException) when (run.RequestToken.IsCancellationRequested)
            {
                req.Failure = $"{gateway.Name}:cancelled";
            }
            catch (OperationCanceledException ex)
            {
                req.Failure = $"{gateway.Name}:discovery_timeout";
                _logger.LogWarning(ex, "{Gateway} indexer discovery timed out", gateway.Name);
            }
            catch (IndexerAuthException ex)
            {
                req.Failure = $"{gateway.Name}:auth";
                _logger.LogWarning(ex, "{Gateway} indexer discovery auth failure", gateway.Name);
            }
            catch (Exception ex)
            {
                req.Failure = $"{gateway.Name}:discovery_failed";
                _logger.LogWarning(ex, "{Gateway} indexer discovery failed", gateway.Name);
            }
        });
        return req;
    }

    private readonly record struct QueryTerm(string? Title, bool Skip, string? Message);

    /// <summary>
    /// Resolve the title term for an indexer query. Normal titles are only
    /// normalised (trim / whitespace collapse — the scorer never sees the query
    /// term, so ranking is unchanged). A degenerate title is repaired from the
    /// TMDB record (original title → alternative/translated titles); if none is
    /// usable and an IMDB id exists, returns a null title (IMDB-id-only query);
    /// otherwise <c>Skip</c> with the counted <c>degenerate_query_skipped</c>
    /// outcome. A title is NEVER fabricated.
    /// </summary>
    private async Task<QueryTerm> ResolveQueryTermAsync(
        int tmdbId,
        string? imdbId,
        string type,
        int? season,
        int? episode,
        string? title,
        CancellationToken ct)
    {
        var normalised = QueryTitleGuard.Normalize(title);
        if (normalised is not null)
        {
            return new QueryTerm(normalised, false, null);
        }

        if (_titleRepair is not null)
        {
            QueryTitleRepair? repair;
            try
            {
                repair = await _titleRepair.RepairAsync(tmdbId, type, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A failed TMDB repair lookup is not proof the item is
                // unrepairable; continue to the IMDB-only / skip decision
                // (which still never sends a degenerate term).
                _logger.LogWarning(ex, "Degenerate-title repair lookup failed for {Type}/{Tmdb}", type, tmdbId);
                repair = null;
            }

            var repaired = QueryTitleGuard.Normalize(repair?.Title);
            if (repaired is not null)
            {
                var outcome = repair!.Source == QueryTitleSource.OriginalTitle
                    ? "repaired_original_title"
                    : "repaired_alternate_title";
                PhantomMetrics.DegenerateQuery(type, outcome);
                _logger.LogInformation(
                    "Degenerate title {Title} for {Type}/{Tmdb} s{Season}e{Episode} repaired from TMDB {Source}: {Repaired}",
                    title, type, tmdbId, season, episode, repair.Source, repaired);
                return new QueryTerm(repaired, false, null);
            }
        }

        if (!string.IsNullOrWhiteSpace(imdbId))
        {
            _logger.LogInformation(
                "Degenerate title {Title} for {Type}/{Tmdb} s{Season}e{Episode} unrepairable; falling back to IMDB-only query ({Imdb})",
                title, type, tmdbId, season, episode, imdbId);
            return new QueryTerm(null, false, null);
        }

        PhantomMetrics.DegenerateQuery(type, DegenerateQuerySkippedKind);
        _logger.LogWarning(
            "Degenerate query skipped for {Type}/{Tmdb} s{Season}e{Episode}: title {Title} unrepairable from TMDB and no IMDB id",
            type, tmdbId, season, episode, title);
        return new QueryTerm(null, true, $"degenerate title '{title}' unrepairable from TMDB and no IMDB id");
    }

    /// <summary>
    /// Hands still-running requests to a background collector (bounded by
    /// IndexerLateCollectorMaxConcurrent). With <paramref name="write"/>, each late
    /// arrival is re-ranked together with everything received so far and written
    /// to the magnet cache; nothing that arrives late is discarded.
    /// </summary>
    private void HandOffRemaining(
        ProbeRun run,
        List<FanOutRequest> pending,
        List<FanOutRequest> collected,
        CancellationTokenSource requestCts,
        bool write,
        string fallbackLabel = "unknown")
    {
        if (pending.Count == 0)
        {
            requestCts.Dispose();
            return;
        }

        var counted = false;
        if (write)
        {
            var max = _configProvider().IndexerLateCollectorMaxConcurrent;
            if (Interlocked.Increment(ref _lateCollectorsActive) > max)
            {
                Interlocked.Decrement(ref _lateCollectorsActive);
                _logger.LogWarning(
                    "Late indexer collectors at bound ({Max}); cancelling {Pending} past-budget request(s) for {Type}/{Tmdb}",
                    max, pending.Count, run.Type, run.TmdbId);
                requestCts.Cancel();
                write = false;
            }
            else
            {
                counted = true;
            }
        }

        var remaining = new List<FanOutRequest>(pending);
        var soFar = new List<FanOutRequest>(collected);
        var id = Interlocked.Increment(ref _lateWorkSeq);
        var work = Task.Run(() => CollectLateAsync(run, remaining, soFar, requestCts, write, counted, fallbackLabel));
        _lateWork[id] = work;
        _ = work.ContinueWith(
            _ => _lateWork.TryRemove(id, out Task? _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task CollectLateAsync(
        ProbeRun run,
        List<FanOutRequest> pending,
        List<FanOutRequest> all,
        CancellationTokenSource requestCts,
        bool write,
        bool counted,
        string fallbackLabel)
    {
        try
        {
            while (pending.Count > 0)
            {
                var done = await Task.WhenAny(pending.Select(p => p.Task)).ConfigureAwait(false);
                var req = pending.First(p => p.Task == done);
                Collect(pending, all, req);
                if (!write || _lateSink is null || req.Hits is not { Count: > 0 })
                {
                    continue;
                }

                var ranked = Rank(Aggregate(all), run.Type, run.Season, run.Episode, _configProvider(), fallbackLabel);
                if (ranked.Count == 0)
                {
                    continue;
                }

                try
                {
                    await _lateSink.WriteAsync(
                        new LateIndexerResult(run.TmdbId, run.Type, run.Season, run.Episode, ranked),
                        _lifetime).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
                {
                    write = false;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Writing late indexer results for {Type}/{Tmdb} to the magnet cache failed", run.Type, run.TmdbId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Late indexer collector for {Type}/{Tmdb} failed", run.Type, run.TmdbId);
        }
        finally
        {
            requestCts.Dispose();
            if (counted)
            {
                Interlocked.Decrement(ref _lateCollectorsActive);
            }
        }
    }

    private sealed class ProbeRun(
        IndexerQuery query,
        int tmdbId,
        string type,
        int? season,
        int? episode,
        TimeSpan perRequestTimeout,
        CancellationToken requestToken)
    {
        private volatile bool _budgetClosed;

        public IndexerQuery Query { get; } = query;

        public int TmdbId { get; } = tmdbId;

        public string Type { get; } = type;

        public int? Season { get; } = season;

        public int? Episode { get; } = episode;

        public CancellationToken RequestToken { get; } = requestToken;

        public TimeSpan PerRequestTimeout { get; } = perRequestTimeout;

        /// <summary>Set once the caller has been answered; later completions are "late".</summary>
        public bool BudgetClosed
        {
            get => _budgetClosed;
            set => _budgetClosed = value;
        }
    }

    private sealed record FanOutRequest(int Major, int Minor, string Client, string Label, bool IsDiscovery, bool IsFanOutChild)
    {
        public Task Task { get; set; } = Task.CompletedTask;

        public List<IndexerCandidate>? Hits { get; set; }

        public string? Failure { get; set; }

        public string? Abstention { get; set; }

        public bool Succeeded { get; set; }

        public List<FanOutRequest>? Children { get; set; }
    }

    internal static int EpisodeSpecificityScore(string? title, int season, int episode)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return 0;
        }

        var score = 0;
        foreach (Match match in EpisodeToken.Matches(title))
        {
            var seasonText = match.Groups["s"].Success ? match.Groups["s"].Value : match.Groups["s2"].Value;
            var episodeText = match.Groups["e"].Success ? match.Groups["e"].Value : match.Groups["e2"].Value;
            if (!int.TryParse(seasonText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s)
                || !int.TryParse(episodeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var e))
            {
                continue;
            }

            if (s == season && e == episode)
            {
                score += 1000;
            }
            else if (s == season)
            {
                score -= 100;
            }
            else
            {
                score -= 200;
            }
        }

        if (title.Contains("complete", StringComparison.OrdinalIgnoreCase)
            || title.Contains("season 1-", StringComparison.OrdinalIgnoreCase)
            || title.Contains("s01-s", StringComparison.OrdinalIgnoreCase)
            || title.Contains("s01 to s", StringComparison.OrdinalIgnoreCase))
        {
            score -= 50;
        }

        return score;
    }
}
