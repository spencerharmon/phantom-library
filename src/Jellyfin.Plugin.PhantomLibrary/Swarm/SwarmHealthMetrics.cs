using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Threading;
using Jellyfin.Plugin.PhantomLibrary.Diagnostics;

namespace Jellyfin.Plugin.PhantomLibrary.Swarm;

/// <summary>
/// Swarm-health metrics (ROI Priority 0.5), on the same OTel meter as the flow
/// metrics so they ship through the existing exporter to Mimir, mirrored to
/// prometheus-net for the in-cluster scrape path (like phantom_playback_outcome_total).
/// </summary>
internal static class SwarmHealthMetrics
{
    private static readonly Meter Meter = new(PhantomFlowMetrics.MeterName, "1.0.0");

    private static readonly Counter<long> Measured = Meter.CreateCounter<long>(
        "phantom_swarm_health_measured_total",
        unit: "{infohash}",
        description: "Candidate infohashes measured on the DHT, by item type and result (live|dead).");

    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "phantom_swarm_health_requests_total",
        unit: "{request}",
        description: "Swarm-health service requests by outcome (ok|error).");

    private static readonly Counter<long> BaseItemsUpdated = Meter.CreateCounter<long>(
        "phantom_swarm_health_baseitems_updated_total",
        unit: "{item}",
        description: "Phantom BaseItems whose Peers (CriticRating) value was written.");

    private static readonly Counter<long> SyncRowsRead = Meter.CreateCounter<long>(
        "phantom_swarm_health_sync_rows_read_total",
        unit: "{item}",
        description: "Persisted phantom channel BaseItems read by the bounded Peers score sweep.");

    private static readonly Counter<long> SyncSweeps = Meter.CreateCounter<long>(
        "phantom_swarm_health_sync_sweeps_total",
        unit: "{sweep}",
        description: "Completed full-catalogue Peers score sweeps.");

    private static readonly Prometheus.Counter MeasuredProm = Prometheus.Metrics.CreateCounter(
        "phantom_swarm_health_measured_total",
        "Candidate infohashes measured on the DHT, by item type and result (live|dead).",
        new Prometheus.CounterConfiguration { LabelNames = new[] { "type", "result" } });

    private static readonly Prometheus.Gauge TitlesProm = Prometheus.Metrics.CreateGauge(
        "phantom_swarm_health_titles",
        "Titles with a fresh DHT swarm measurement, by type (movie|series) and health (live|dead).",
        new Prometheus.GaugeConfiguration { LabelNames = new[] { "type", "health" } });

    private static long _movieLive;
    private static long _movieDead;
    private static long _seriesLive;
    private static long _seriesDead;

    static SwarmHealthMetrics()
    {
        Meter.CreateObservableGauge(
            "phantom_swarm_health_titles",
            () => new[]
            {
                Point(Interlocked.Read(ref _movieLive), "movie", "live"),
                Point(Interlocked.Read(ref _movieDead), "movie", "dead"),
                Point(Interlocked.Read(ref _seriesLive), "series", "live"),
                Point(Interlocked.Read(ref _seriesDead), "series", "dead"),
            },
            unit: "{title}",
            description: "Titles with a fresh DHT swarm measurement, by type (movie|series) and health (live|dead).");
    }

    public static void RecordMeasured(string type, int live, int dead)
    {
        if (live > 0)
        {
            Measured.Add(live, new KeyValuePair<string, object?>("type", type), new KeyValuePair<string, object?>("result", "live"));
            MeasuredProm.WithLabels(type, "live").Inc(live);
        }

        if (dead > 0)
        {
            Measured.Add(dead, new KeyValuePair<string, object?>("type", type), new KeyValuePair<string, object?>("result", "dead"));
            MeasuredProm.WithLabels(type, "dead").Inc(dead);
        }
    }

    public static void RecordRequest(bool ok)
        => Requests.Add(1, new KeyValuePair<string, object?>("outcome", ok ? "ok" : "error"));

    public static void RecordBaseItemsUpdated(int count)
    {
        if (count > 0)
        {
            BaseItemsUpdated.Add(count);
        }
    }

    public static void RecordSyncRead(int count)
    {
        if (count > 0)
        {
            SyncRowsRead.Add(count);
        }
    }

    public static void RecordSyncSweepCompleted() => SyncSweeps.Add(1);

    public static void SetTitles(string type, long live, long dead)
    {
        if (type == "movie")
        {
            Interlocked.Exchange(ref _movieLive, live);
            Interlocked.Exchange(ref _movieDead, dead);
        }
        else
        {
            Interlocked.Exchange(ref _seriesLive, live);
            Interlocked.Exchange(ref _seriesDead, dead);
        }

        TitlesProm.WithLabels(type, "live").Set(live);
        TitlesProm.WithLabels(type, "dead").Set(dead);
    }

    /// <summary>Current title-gauge values (what the OTel observable gauge reports).</summary>
    internal static (long MovieLive, long MovieDead, long SeriesLive, long SeriesDead) TitlesSnapshot()
        => (Interlocked.Read(ref _movieLive), Interlocked.Read(ref _movieDead), Interlocked.Read(ref _seriesLive), Interlocked.Read(ref _seriesDead));

    private static Measurement<long> Point(long value, string type, string health)
        => new(value, new KeyValuePair<string, object?>("type", type), new KeyValuePair<string, object?>("health", health));
}
