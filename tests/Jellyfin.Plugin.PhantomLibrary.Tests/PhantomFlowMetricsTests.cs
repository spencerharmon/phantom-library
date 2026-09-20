using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Diagnostics;
using Jellyfin.Plugin.PhantomLibrary.State.Db;
using OpenTelemetry.Exporter;
using Prometheus;
using Xunit;

namespace Jellyfin.Plugin.PhantomLibrary.Tests;

/// <summary>
/// Unit coverage for the P5 OTLP browse-flow instrumentation. Uses a
/// <see cref="MeterListener"/> to observe the real instruments without needing
/// a running OTLP collector, proving the five flow measurements are emitted
/// with the expected instrument names and tags.
/// </summary>
public sealed class PhantomFlowMetricsTests
{
    private static (List<(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags)> Measurements, MeterListener Listener) StartListener()
    {
        var measurements = new List<(string, double, IReadOnlyDictionary<string, object?>)>();
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == PhantomFlowMetrics.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };

        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            measurements.Add((instrument.Name, value, TagsToDict(tags)));
        });
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            measurements.Add((instrument.Name, value, TagsToDict(tags)));
        });

        listener.Start();
        return (measurements, listener);
    }

    private static Dictionary<string, object?> TagsToDict(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var t in tags)
        {
            d[t.Key] = t.Value;
        }

        return d;
    }

    [Fact]
    public void Record_EmitsDurationHistogram_WithFlowAndBackendTags()
    {
        var (measurements, listener) = StartListener();
        using (listener)
        {
            PhantomFlowMetrics.Record(PhantomFlowMetrics.FlowListView, 42.5, itemCount: 7, backend: "sqlite");
            listener.RecordObservableInstruments();
        }

        var duration = Assert.Single(measurements, m => m.Instrument == "phantom_flow_duration_ms");
        Assert.Equal(42.5, duration.Value);
        Assert.Equal(PhantomFlowMetrics.FlowListView, duration.Tags["flow"]);
        Assert.Equal("sqlite", duration.Tags["backend"]);

        var items = Assert.Single(measurements, m => m.Instrument == "phantom_flow_items");
        Assert.Equal(7d, items.Value);
        Assert.Equal(PhantomFlowMetrics.FlowListView, items.Tags["flow"]);
    }

    [Fact]
    public void Record_WithoutItemCount_OmitsItemsCounter()
    {
        var (measurements, listener) = StartListener();
        using (listener)
        {
            PhantomFlowMetrics.Record(PhantomFlowMetrics.FlowSeasonListing, 10);
        }

        Assert.Contains(measurements, m => m.Instrument == "phantom_flow_duration_ms");
        Assert.DoesNotContain(measurements, m => m.Instrument == "phantom_flow_items");
    }

    [Fact]
    public void TimeScope_RecordsOnDispose_WithItemCount()
    {
        var (measurements, listener) = StartListener();
        using (listener)
        {
            using (var scope = PhantomFlowMetrics.Time(PhantomFlowMetrics.FlowEpisodeListing, backend: "postgres"))
            {
                scope.ItemCount = 3;
            }
        }

        var duration = Assert.Single(measurements, m => m.Instrument == "phantom_flow_duration_ms");
        Assert.Equal(PhantomFlowMetrics.FlowEpisodeListing, duration.Tags["flow"]);
        Assert.Equal("postgres", duration.Tags["backend"]);
        Assert.True(duration.Value >= 0);

        var items = Assert.Single(measurements, m => m.Instrument == "phantom_flow_items");
        Assert.Equal(3d, items.Value);
    }

    [Fact]
    public void FiveFlowConstants_AreDistinct()
    {
        var flows = new[]
        {
            PhantomFlowMetrics.FlowListView,
            PhantomFlowMetrics.FlowSortFilter,
            PhantomFlowMetrics.FlowSeasonListing,
            PhantomFlowMetrics.FlowEpisodeListing,
            PhantomFlowMetrics.FlowMaterialisedListing,
        };

        Assert.Equal(5, flows.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void RecordPlaybackOutcome_EmitsCounter_WithFlowItemTypeAndCauseTags()
    {
        var (measurements, listener) = StartListener();
        using (listener)
        {
            PhantomFlowMetrics.RecordPlaybackOutcome(
                PhantomFlowMetrics.PlaybackFlowMaterialiseThenPlay,
                PhantomFlowMetrics.ItemTypeEpisode,
                PhantomFlowMetrics.CauseGostreamRegisterFail);
        }

        var outcome = Assert.Single(measurements, m => m.Instrument == "phantom_playback_outcome_total");
        Assert.Equal(1d, outcome.Value);
        Assert.Equal(PhantomFlowMetrics.PlaybackFlowMaterialiseThenPlay, outcome.Tags["flow"]);
        Assert.Equal(PhantomFlowMetrics.ItemTypeEpisode, outcome.Tags["item_type"]);
        Assert.Equal(PhantomFlowMetrics.CauseGostreamRegisterFail, outcome.Tags["cause"]);
    }

    [Fact]
    public void RecordPlaybackOutcome_MovieSuccess_TagsCauseSuccess()
    {
        var (measurements, listener) = StartListener();
        using (listener)
        {
            PhantomFlowMetrics.RecordPlaybackOutcome(
                PhantomFlowMetrics.PlaybackFlowPlayAlreadyMaterialised,
                PhantomFlowMetrics.ItemTypeMovie,
                PhantomFlowMetrics.CauseSuccess);
        }

        var outcome = Assert.Single(measurements, m => m.Instrument == "phantom_playback_outcome_total");
        Assert.Equal(PhantomFlowMetrics.ItemTypeMovie, outcome.Tags["item_type"]);
        Assert.Equal(PhantomFlowMetrics.CauseSuccess, outcome.Tags["cause"]);
    }

    /// <summary>
    /// playback-outcome-real-cause-dual-emit-001: RecordPlaybackOutcome must
    /// ALSO increment a prometheus-net registry counter of the identical name
    /// (<c>phantom_playback_outcome_total</c>) and {flow,item_type,cause}
    /// labels, alongside the existing OTLP Meter counter — a pure addition
    /// that lets the same in-cluster Prometheus scrape path that already
    /// picks up phantom_availability_probes_total (job/pod scraping, not
    /// per-metric) pick up the real per-attempt series too. Uses a
    /// before/after delta because the prometheus-net counter is a static
    /// process-wide singleton shared across test cases in this class.
    /// </summary>
    [Theory]
    [InlineData(
        PhantomFlowMetrics.PlaybackFlowMaterialiseThenPlay,
        PhantomFlowMetrics.ItemTypeEpisode,
        PhantomFlowMetrics.CauseGostreamRegisterFail)]
    [InlineData(
        PhantomFlowMetrics.PlaybackFlowPlayAlreadyMaterialised,
        PhantomFlowMetrics.ItemTypeMovie,
        PhantomFlowMetrics.CauseSuccess)]
    public void RecordPlaybackOutcome_AlsoIncrementsPrometheusNetCounter_ForMovieAndEpisode(
        string flow,
        string itemType,
        string cause)
    {
        var prometheusCounter = Metrics.CreateCounter(
            "phantom_playback_outcome_total",
            "Definitive per-attempt phantom playback outcome, split by flow/item_type/cause.",
            new CounterConfiguration { LabelNames = new[] { "flow", "item_type", "cause" } });
        var before = prometheusCounter.WithLabels(flow, itemType, cause).Value;

        PhantomFlowMetrics.RecordPlaybackOutcome(flow, itemType, cause);

        var after = prometheusCounter.WithLabels(flow, itemType, cause).Value;
        Assert.Equal(1d, after - before);
    }

    [Fact]
    public void PlaybackOutcomeCauseConstants_AreEightDistinct()
    {
        var causes = new[]
        {
            PhantomFlowMetrics.CauseSuccess,
            PhantomFlowMetrics.CauseAvailabilityAbstain,
            PhantomFlowMetrics.CauseNoCandidate,
            PhantomFlowMetrics.CauseMagnetDeadStale,
            PhantomFlowMetrics.CauseGostreamRegisterFail,
            PhantomFlowMetrics.CauseGostreamCannotFetch,
            PhantomFlowMetrics.CauseFirstByteTimeout,
            PhantomFlowMetrics.CausePluginHostError,
        };

        Assert.Equal(8, causes.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void PlaybackFlowConstants_AreTwoDistinct()
    {
        var flows = new[]
        {
            PhantomFlowMetrics.PlaybackFlowMaterialiseThenPlay,
            PhantomFlowMetrics.PlaybackFlowPlayAlreadyMaterialised,
        };

        Assert.Equal(2, flows.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ResolveEndpoint_PrefersConfigOverEnvironment()
    {
        var config = new PluginConfiguration { MetricsOtlpEndpoint = "http://collector.example:4317" };
        Assert.Equal("http://collector.example:4317", PhantomMetricsExporter.ResolveEndpoint(config));
    }

    [Fact]
    public void ResolveEndpoint_EmptyConfig_FallsBackToEnvironment()
    {
        var config = new PluginConfiguration { MetricsOtlpEndpoint = string.Empty };
        var key = "OTEL_EXPORTER_OTLP_ENDPOINT";
        var previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, "http://env-collector.example:4318");
            Assert.Equal("http://env-collector.example:4318", PhantomMetricsExporter.ResolveEndpoint(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }

    [Fact]
    public void ResolveEndpoint_NoneConfigured_ReturnsNull()
    {
        var config = new PluginConfiguration { MetricsOtlpEndpoint = string.Empty };
        var key = "OTEL_EXPORTER_OTLP_ENDPOINT";
        var previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, null);
            Assert.Null(PhantomMetricsExporter.ResolveEndpoint(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }

    [Theory]
    [InlineData("grpc", OtlpExportProtocol.Grpc)]
    [InlineData("http/protobuf", OtlpExportProtocol.HttpProtobuf)]
    [InlineData("", OtlpExportProtocol.Grpc)]
    [InlineData("nonsense", OtlpExportProtocol.Grpc)]
    public void ResolveProtocol_MapsTokens(string token, OtlpExportProtocol expected)
    {
        var config = new PluginConfiguration { MetricsOtlpProtocol = token };
        var key = "OTEL_EXPORTER_OTLP_PROTOCOL";
        var previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, null);
            Assert.Equal(expected, PhantomMetricsExporter.ResolveProtocol(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }

    [Theory]
    [InlineData(PhantomDbBackend.Postgres, "postgres")]
    [InlineData(PhantomDbBackend.Sqlite, "sqlite")]
    public void BackendTag_MapsRuntimeBackendToLabel(PhantomDbBackend backend, string expected)
    {
        Assert.Equal(expected, PhantomFlowMetrics.BackendTag(backend));
    }

    [Fact]
    public void TimeScope_TypedBackend_TagsWithRuntimeBackend()
    {
        var (measurements, listener) = StartListener();
        using (listener)
        {
            using (PhantomFlowMetrics.Time(PhantomFlowMetrics.FlowListView, PhantomDbBackend.Postgres))
            {
            }
        }

        var duration = Assert.Single(measurements, m => m.Instrument == "phantom_flow_duration_ms");
        Assert.Equal("postgres", duration.Tags["backend"]);
    }

    [Fact]
    public void ResolveEnabled_ConfigTrue_ShortCircuitsTrue()
    {
        var config = new PluginConfiguration { MetricsOtlpEnabled = true };
        var key = "PHANTOM_METRICS_OTLP_ENABLED";
        var previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, null);
            Assert.True(PhantomMetricsExporter.ResolveEnabled(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData("TRUE", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("0", false)]
    [InlineData("false", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ResolveEnabled_ConfigFalse_HonoursEnvFallback(string? envValue, bool expected)
    {
        var config = new PluginConfiguration { MetricsOtlpEnabled = false };
        var key = "PHANTOM_METRICS_OTLP_ENABLED";
        var previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, envValue);
            Assert.Equal(expected, PhantomMetricsExporter.ResolveEnabled(config));
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }
}
