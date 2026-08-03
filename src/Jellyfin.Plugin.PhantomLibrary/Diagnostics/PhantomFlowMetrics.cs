using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;

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

    private static readonly Meter Meter = new(MeterName, "1.0.0");

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
    /// Starts timing a flow. Dispose the returned scope (ideally via
    /// <c>using</c>) to record the elapsed latency. Set
    /// <see cref="FlowScope.ItemCount"/> before disposal to also record the
    /// item count.
    /// </summary>
    public static FlowScope Time(string flow, string backend = "sqlite") => new(flow, backend);

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
