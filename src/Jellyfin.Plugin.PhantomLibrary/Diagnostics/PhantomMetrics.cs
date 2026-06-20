using System;
using Prometheus;

namespace Jellyfin.Plugin.PhantomLibrary.Diagnostics;

internal static class PhantomMetrics
{
    private static readonly Counter DiscoveryRunsTotal = Metrics.CreateCounter(
        "phantom_discovery_runs_total",
        "Number of Phantom discovery task runs.");

    private static readonly Counter DiscoveryPagesTotal = Metrics.CreateCounter(
        "phantom_discovery_pages_total",
        "TMDB Discover pages processed by Phantom discovery.",
        new CounterConfiguration { LabelNames = new[] { "kind", "cache" } });

    private static readonly Counter DiscoveryRowsTotal = Metrics.CreateCounter(
        "phantom_discovery_rows_total",
        "TMDB catalogue rows seen/inserted by Phantom discovery.",
        new CounterConfiguration { LabelNames = new[] { "kind", "result" } });

    private static readonly Gauge DiscoveryCursorPage = Metrics.CreateGauge(
        "phantom_discovery_cursor_page",
        "Next TMDB Discover page cursor for a kind.",
        new GaugeConfiguration { LabelNames = new[] { "kind" } });

    private static readonly Histogram DiscoveryRunSeconds = Metrics.CreateHistogram(
        "phantom_discovery_run_seconds",
        "Elapsed seconds for Phantom discovery runs.",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(1, 2, 12) });

    private static readonly Counter AvailabilityProbesTotal = Metrics.CreateCounter(
        "phantom_availability_probes_total",
        "Availability probe outcomes.",
        new CounterConfiguration { LabelNames = new[] { "type", "outcome" } });

    private static readonly Counter SeriesExpansionsTotal = Metrics.CreateCounter(
        "phantom_series_expansions_total",
        "Series expansion outcomes.",
        new CounterConfiguration { LabelNames = new[] { "outcome" } });

    private static readonly Histogram AvailabilityProbeSeconds = Metrics.CreateHistogram(
        "phantom_availability_probe_seconds",
        "Elapsed seconds for one availability probe.",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(0.25, 2, 12) });

    public static IDisposable TimeDiscoveryRun()
    {
        DiscoveryRunsTotal.Inc();
        return DiscoveryRunSeconds.NewTimer();
    }

    public static IDisposable TimeAvailabilityProbe()
        => AvailabilityProbeSeconds.NewTimer();

    public static void DiscoveryPage(string kind, bool fromCache)
        => DiscoveryPagesTotal.WithLabels(kind, fromCache ? "hit" : "miss").Inc();

    public static void DiscoveryRows(string kind, int seen, int inserted, int availabilityInserted, int seriesExpansionInserted)
    {
        DiscoveryRowsTotal.WithLabels(kind, "seen").Inc(seen);
        DiscoveryRowsTotal.WithLabels(kind, "inserted").Inc(inserted);
        DiscoveryRowsTotal.WithLabels(kind, "availability_inserted").Inc(availabilityInserted);
        DiscoveryRowsTotal.WithLabels(kind, "series_expansion_inserted").Inc(seriesExpansionInserted);
    }

    public static void DiscoveryCursor(string kind, int nextPage)
        => DiscoveryCursorPage.WithLabels(kind).Set(nextPage);

    public static void AvailabilityProbe(string type, string outcome)
        => AvailabilityProbesTotal.WithLabels(type, outcome).Inc();

    public static void SeriesExpansion(string outcome)
        => SeriesExpansionsTotal.WithLabels(outcome).Inc();
}
