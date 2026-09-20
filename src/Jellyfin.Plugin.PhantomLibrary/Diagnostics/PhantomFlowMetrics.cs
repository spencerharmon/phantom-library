using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Jellyfin.Plugin.PhantomLibrary.State.Db;
using Prometheus;

namespace Jellyfin.Plugin.PhantomLibrary.Diagnostics;

/// <summary>
/// OTLP-native latency instrumentation for the five browse flows P5 targets
/// as the pre-Postgres performance baseline:
///
/// <list type="number">
///   <item><see cref="FlowListView"/> — top-level channel list-view load;</item>
///   <item><see cref="FlowSortFilter"/> — sort/filter change (badge/state re-resolve);</item>
///   <item><see cref="FlowSeasonListing"/> — series → season listing;</item>
///   <item><see cref="FlowEpisodeListing"/> — season → episode listing;</item>
///   <item><see cref="FlowMaterialisedListing"/> — materialised-only enumeration.</item>
/// </list>
///
/// Measurements are recorded on a <see cref="Meter"/> named
/// <see cref="MeterName"/> using the standard <c>System.Diagnostics.Metrics</c>
/// instruments. That meter is OTLP-native: <see cref="PhantomMetricsExporter"/>
/// subscribes to it and ships the aggregated histogram/count to the OTLP
/// collector whose endpoint comes from configuration (never a baked-in host).
/// A build with no exporter configured still records the instruments as
/// no-ops, so instrumentation is always safe to leave in the hot path.
///
/// This is intentionally separate from the pull-based prometheus-net
/// <see cref="PhantomMetrics"/> counters (discovery/availability internals);
/// P5 measures the user-facing browse latency the Postgres migration must not
/// regress, exported push-style over OTLP.
/// </summary>
internal static class PhantomFlowMetrics
{
    /// <summary>Meter name that <see cref="PhantomMetricsExporter"/> subscribes to.</summary>
    public const string MeterName = "Phantom.Flows";

    /// <summary>Flow tag: top-level channel list-view load.</summary>
    public const string FlowListView = "list_view_load";

    /// <summary>Flow tag: sort/filter change (badge/state batch re-resolve).</summary>
    public const string FlowSortFilter = "sort_filter_change";

    /// <summary>Flow tag: series → season listing.</summary>
    public const string FlowSeasonListing = "season_listing";

    /// <summary>Flow tag: season → episode listing.</summary>
    public const string FlowEpisodeListing = "episode_listing";

    /// <summary>Flow tag: materialised-only enumeration.</summary>
    public const string FlowMaterialisedListing = "materialised_listing";

    // -----------------------------------------------------------------------
    // Playback-outcome instrumentation (playback-outcome-instrumentation-001).
    //
    // Every playback ATTEMPT records exactly one definitive outcome — success or
    // a single failure cause — split by flow × item_type × cause, on the same
    // OTLP-native Phantom.Flows meter (PhantomMetricsExporter already subscribes
    // to it, so no wiring change). The playback-error-rate series ranks causes
    // from these numbers; this is MEASUREMENT only (no behaviour change).
    // -----------------------------------------------------------------------

    /// <summary>Playback flow: a fresh materialise then play in the same attempt.</summary>
    public const string PlaybackFlowMaterialiseThenPlay = "materialise_then_play";

    /// <summary>Playback flow: play of an already-materialised item.</summary>
    public const string PlaybackFlowPlayAlreadyMaterialised = "play_already_materialised";

    /// <summary>Item type tag: a movie.</summary>
    public const string ItemTypeMovie = "movie";

    /// <summary>Item type tag: an episode.</summary>
    public const string ItemTypeEpisode = "episode";

    /// <summary>Definitive outcome cause: the attempt produced a playable stream.</summary>
    public const string CauseSuccess = "success";

    /// <summary>Cause: the availability oracle abstained / gave no definitive available verdict.</summary>
    public const string CauseAvailabilityAbstain = "availability_abstain";

    /// <summary>Cause: no surviving source candidate (none, or all invalid).</summary>
    public const string CauseNoCandidate = "no_candidate";

    /// <summary>Cause: a candidate resolved but nothing was fetchable (dead/stale magnet).</summary>
    public const string CauseMagnetDeadStale = "magnet_dead_stale";

    /// <summary>Cause: gostream register handoff failed.</summary>
    public const string CauseGostreamRegisterFail = "gostream_register_fail";

    /// <summary>Cause: gostream registered but could not fetch the media.</summary>
    public const string CauseGostreamCannotFetch = "gostream_cannot_fetch";

    /// <summary>Cause: first-byte handoff timed out.</summary>
    public const string CauseFirstByteTimeout = "first_byte_timeout";

    /// <summary>Cause: catch-all for any other plugin-host-path exception.</summary>
    public const string CausePluginHostError = "plugin_host_error";

    private static readonly Meter Meter = new(MeterName, "1.0.0");

    private static readonly Counter<long> PlaybackOutcomes = Meter.CreateCounter<long>(
        "phantom_playback_outcome_total",
        unit: "{attempt}",
        description: "Definitive per-attempt phantom playback outcome, split by flow/item_type/cause.");

    // Pull-based prometheus-net mirror of the OTLP counter above (playback-
    // outcome-real-cause-dual-emit-001). Same metric name and {flow,item_type,
    // cause} labels, dual-emitted from the SAME RecordPlaybackOutcome call
    // site, so the in-cluster scrape path that already picks up
    // phantom_availability_probes_total (see PhantomMetrics) picks up this
    // series too, with no OTLP/exporter/observe.spencerharmon.com change.
    private static readonly Counter PlaybackOutcomesPrometheus = Metrics.CreateCounter(
        "phantom_playback_outcome_total",
        "Definitive per-attempt phantom playback outcome, split by flow/item_type/cause.",
        new CounterConfiguration { LabelNames = new[] { "flow", "item_type", "cause" } });

    private static readonly Histogram<double> FlowDurationMs = Meter.CreateHistogram<double>(
        "phantom_flow_duration_ms",
        unit: "ms",
        description: "Wall-clock latency of a Phantom user-facing browse flow.");

    private static readonly Counter<long> FlowItems = Meter.CreateCounter<long>(
        "phantom_flow_items",
        unit: "{item}",
        description: "Items returned by a Phantom user-facing browse flow.");

    /// <summary>
    /// Records one completed flow observation: its wall-clock latency (ms) and,
    /// when known, the number of items it produced. The <paramref name="flow"/>
    /// tag is one of the <c>Flow*</c> constants; <paramref name="backend"/>
    /// tags the storage backend serving the flow (e.g. <c>sqlite</c> /
    /// <c>postgres</c>) so the same series can be compared across the migration.
    /// </summary>
    public static void Record(string flow, double milliseconds, int? itemCount = null, string backend = "sqlite")
    {
        var flowTag = new KeyValuePair<string, object?>("flow", flow);
        var backendTag = new KeyValuePair<string, object?>("backend", backend);
        FlowDurationMs.Record(milliseconds, flowTag, backendTag);
        if (itemCount is { } count)
        {
            FlowItems.Add(count, flowTag, backendTag);
        }
    }

    /// <summary>
    /// Records ONE definitive playback-attempt outcome on the <c>phantom_playback_outcome_total</c>
    /// counter. Every playback attempt calls this exactly once with either
    /// <see cref="CauseSuccess"/> or a single failure cause, tagged by
    /// <paramref name="flow"/> (materialise_then_play vs play_already_materialised) and
    /// <paramref name="itemType"/> (movie vs episode — parity is mandatory: every call site
    /// tags the real item type). The playback error rate the series ratchets is
    /// <c>sum(cause!="success") / sum(all)</c>, sliceable by flow/item_type/cause.
    /// The <c>Phantom.Flows</c> meter is OTLP-native (see <see cref="PhantomMetricsExporter"/>),
    /// so this record is shipped over OTLP with no extra wiring, and is mirrored into Mimir by
    /// the rig/emitter path (<c>47-loadtime-flows.sh</c> + <c>phantom-loadtime-push.sh</c>).
    /// </summary>
    /// <param name="flow">One of the <c>PlaybackFlow*</c> constants.</param>
    /// <param name="itemType">One of <see cref="ItemTypeMovie"/> / <see cref="ItemTypeEpisode"/>.</param>
    /// <param name="cause">One of the <c>Cause*</c> constants.</param>
    public static void RecordPlaybackOutcome(string flow, string itemType, string cause)
    {
        PlaybackOutcomes.Add(
            1,
            new KeyValuePair<string, object?>("flow", flow),
            new KeyValuePair<string, object?>("item_type", itemType),
            new KeyValuePair<string, object?>("cause", cause));
        PlaybackOutcomesPrometheus.WithLabels(flow, itemType, cause).Inc();
    }

    /// <summary>
    /// Starts timing a flow. Dispose the returned scope (ideally via
    /// <c>using</c>) to record the elapsed latency. Set
    /// <see cref="FlowScope.ItemCount"/> before disposal to also record the
    /// item count.
    /// </summary>
    public static FlowScope Time(string flow, string backend = "sqlite") => new(flow, backend);

    /// <summary>
    /// Maps the runtime <see cref="PhantomDbBackend"/> to the string value of
    /// the <c>backend</c> metric tag (<c>postgres</c> / <c>sqlite</c>).
    /// </summary>
    public static string BackendTag(PhantomDbBackend backend) => backend switch
    {
        PhantomDbBackend.Postgres => "postgres",
        _ => "sqlite",
    };

    /// <summary>
    /// Starts timing a flow, tagging it with the concrete storage backend the
    /// flow is served from (derived from <see cref="State.PhantomDb.Backend"/>).
    /// Use this overload from a call site that has a <see cref="State.PhantomDb"/>
    /// so the emitted series carries the real <c>backend</c> label instead of the
    /// compiled-in default.
    /// </summary>
    public static FlowScope Time(string flow, PhantomDbBackend backend) => new(flow, BackendTag(backend));

    /// <summary>Disposable timing scope; records latency (and optional item count) on dispose.</summary>
    public sealed class FlowScope : IDisposable
    {
        private readonly string _flow;
        private readonly string _backend;
        private readonly long _startTimestamp;
        private bool _disposed;

        internal FlowScope(string flow, string backend)
        {
            _flow = flow;
            _backend = backend;
            _startTimestamp = Stopwatch.GetTimestamp();
        }

        /// <summary>Gets or sets an optional item count recorded alongside the latency on dispose.</summary>
        public int? ItemCount { get; set; }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            var elapsedMs = Stopwatch.GetElapsedTime(_startTimestamp).TotalMilliseconds;
            Record(_flow, elapsedMs, ItemCount, _backend);
        }
    }
}
