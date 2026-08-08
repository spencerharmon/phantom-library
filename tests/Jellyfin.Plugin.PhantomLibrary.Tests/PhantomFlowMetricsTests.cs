using System;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Diagnostics;
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
    [InlineData("grpc", PhantomOtlpProtocol.Grpc)]
    [InlineData("http/protobuf", PhantomOtlpProtocol.HttpProtobuf)]
    [InlineData("", PhantomOtlpProtocol.Grpc)]
    [InlineData("nonsense", PhantomOtlpProtocol.Grpc)]
    public void ResolveProtocol_MapsTokens(string token, PhantomOtlpProtocol expected)
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
}
