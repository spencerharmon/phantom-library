using System.Collections.Generic;
using System.Diagnostics.Metrics;

namespace Jellyfin.Plugin.PhantomLibrary.Diagnostics;

/// <summary>
/// p13-parallel-indexer-search (ROI Priority 13): OTLP-native instruments for the
/// per-indexer source-search fan-out in <c>MagnetSelector</c>. Recorded on the
/// <see cref="MeterName"/> meter, which <see cref="PhantomMetricsExporter"/>
/// subscribes to alongside <see cref="PhantomFlowMetrics.MeterName"/> (OTel 1.10.x).
///
/// Cardinality is bounded: the <c>indexer</c> label is the indexer's own name
/// (Torrentio, or a Prowlarr indexer name DISCOVERED at runtime from
/// <c>/api/v1/indexer</c> — never a configured/hard-coded identifier), <c>status</c>
/// is one of the fixed <c>Status*</c> constants, and <c>phase</c> is
/// <see cref="PhaseInBudget"/> / <see cref="PhaseLate"/>. No title, tmdb id, or
/// query text is ever a label.
/// </summary>
internal static class IndexerSearchMetrics
{
    /// <summary>Meter name that <see cref="PhantomMetricsExporter"/> subscribes to.</summary>
    public const string MeterName = "Phantom.Indexers";

    /// <summary>Instrument: per-indexer request count, by indexer/status/phase.</summary>
    public const string RequestsName = "phantom_indexer_requests_total";

    /// <summary>Instrument: per-indexer request latency histogram (ms).</summary>
    public const string LatencyName = "phantom_indexer_request_duration_ms";

    /// <summary>Instrument: candidates returned per indexer.</summary>
    public const string CandidatesName = "phantom_indexer_candidates_total";

    /// <summary>Instrument: searches whose budget expired, valued by the candidate count held at expiry.</summary>
    public const string BudgetExpiredName = "phantom_indexer_search_budget_expired_candidates";

    /// <summary>Status: the request returned (possibly zero) results.</summary>
    public const string StatusOk = "ok";

    /// <summary>Status: the indexer returned a non-success HTTP status / malformed body / transport error.</summary>
    public const string StatusError = "error";

    /// <summary>Status: 401/403.</summary>
    public const string StatusAuth = "auth";

    /// <summary>Status: the per-request timeout (IndexerProbeTimeoutSeconds) cancelled it.</summary>
    public const string StatusTimeout = "timeout";

    /// <summary>Status: the indexer abstained (cannot serve this query, e.g. Torrentio without IMDB id).</summary>
    public const string StatusAbstain = "abstain";

    /// <summary>Status: cancelled by caller / shutdown / late-collector bound.</summary>
    public const string StatusCancelled = "cancelled";

    /// <summary>Phase: completed inside the search budget (fed the returned ranking).</summary>
    public const string PhaseInBudget = "in_budget";

    /// <summary>Phase: completed after the budget (fed the magnet cache in the background).</summary>
    public const string PhaseLate = "late";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        RequestsName,
        unit: "{request}",
        description: "Per-indexer source-search requests, by indexer, status and phase (in_budget|late).");

    private static readonly Histogram<double> Latency = Meter.CreateHistogram<double>(
        LatencyName,
        unit: "ms",
        description: "Per-indexer source-search request latency.");

    private static readonly Counter<long> Candidates = Meter.CreateCounter<long>(
        CandidatesName,
        unit: "{candidate}",
        description: "Candidates returned by an indexer source-search request.");

    private static readonly Histogram<long> BudgetExpired = Meter.CreateHistogram<long>(
        BudgetExpiredName,
        unit: "{candidate}",
        description: "Searches whose budget expired with indexers still pending; value = candidates held at expiry.");

    /// <summary>
    /// Record one finished indexer request. <paramref name="client"/> is the
    /// top-level client (Torrentio / Prowlarr); <paramref name="indexer"/> is the
    /// queried indexer (the client itself, or a Prowlarr indexer discovered at runtime).
    /// </summary>
    public static void RecordRequest(string client, string indexer, string status, string phase, double elapsedMs, int candidates)
    {
        var tags = new[]
        {
            new KeyValuePair<string, object?>("client", client),
            new KeyValuePair<string, object?>("indexer", indexer),
            new KeyValuePair<string, object?>("status", status),
            new KeyValuePair<string, object?>("phase", phase),
        };
        Requests.Add(1, tags);
        Latency.Record(elapsedMs, tags);
        if (candidates > 0)
        {
            Candidates.Add(
                candidates,
                new KeyValuePair<string, object?>("client", client),
                new KeyValuePair<string, object?>("indexer", indexer),
                new KeyValuePair<string, object?>("phase", phase));
        }
    }

    /// <summary>Record that a search's budget expired holding <paramref name="candidates"/> candidates.</summary>
    public static void RecordBudgetExpired(string itemType, int candidates)
        => BudgetExpired.Record(candidates, new KeyValuePair<string, object?>("item_type", itemType));
}
