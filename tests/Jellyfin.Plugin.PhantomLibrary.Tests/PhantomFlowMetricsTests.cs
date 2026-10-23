using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Jellyfin.Plugin.PhantomLibrary.Diagnostics;
using Jellyfin.Plugin.PhantomLibrary.State.Db;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
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

    [Theory]
    [InlineData(PhantomFlowMetrics.PlaybackFlowMaterialiseThenPlay, PhantomFlowMetrics.ItemTypeMovie)]
    [InlineData(PhantomFlowMetrics.PlaybackFlowMaterialiseThenPlay, PhantomFlowMetrics.ItemTypeEpisode)]
    [InlineData(PhantomFlowMetrics.PlaybackFlowPlayAlreadyMaterialised, PhantomFlowMetrics.ItemTypeMovie)]
    [InlineData(PhantomFlowMetrics.PlaybackFlowPlayAlreadyMaterialised, PhantomFlowMetrics.ItemTypeEpisode)]
    public async Task SeedPlaybackOutcomes_PluginThenExporterStartup_PublishesZerosAndPreservesAttempts(
        string targetFlow, string targetItemType)
    {
        var counter = Metrics.CreateCounter(
            "phantom_playback_outcome_total",
            "Definitive per-attempt phantom playback outcome, split by flow/item_type/cause.",
            new CounterConfiguration { LabelNames = new[] { "flow", "item_type", "cause" } });
        // The assembly disables parallel tests. Remove static children so prior
        // playback tests cannot turn an incorrect nonzero seed into a passing delta.
        foreach (var labels in counter.GetAllLabelValues().ToArray())
        {
            counter.RemoveLabelled(labels);
        }

        var previousPlugin = Plugin.Instance;
        try
        {
            var paths = new Mock<IApplicationPaths>();
            paths.SetupGet(p => p.PluginsPath).Returns(Path.GetTempPath());
            _ = new Plugin(paths.Object, Mock.Of<IXmlSerializer>());
            await AssertSamplesAsync(null, attempted: false);

            using var snapshot = new PlaybackSnapshotExporter();
            using var reader = new PeriodicExportingMetricReader(snapshot, exportIntervalMilliseconds: int.MaxValue)
            {
                TemporalityPreference = MetricReaderTemporalityPreference.Cumulative,
            };
            using var service = new PhantomMetricsExporter(
                NullLogger<PhantomMetricsExporter>.Instance,
                () => new PluginConfiguration
                {
                    MetricsOtlpEnabled = true,
                    MetricsOtlpEndpoint = "http://127.0.0.1:1/v1/metrics",
                    MetricsOtlpProtocol = "http/protobuf",
                },
                builder => builder.AddReader(reader));
            await service.StartAsync(CancellationToken.None);
            Assert.True(reader.Collect());
            await AssertSamplesAsync(snapshot, attempted: false);

            PhantomFlowMetrics.SeedPlaybackOutcomes();
            Assert.True(reader.Collect());
            await AssertSamplesAsync(snapshot, attempted: false);

            PhantomFlowMetrics.RecordPlaybackOutcome(targetFlow, targetItemType, PhantomFlowMetrics.CauseFirstByteTimeout);
            Assert.True(reader.Collect());
            await AssertSamplesAsync(snapshot, attempted: true);

            PhantomFlowMetrics.SeedPlaybackOutcomes();
            Assert.True(reader.Collect());
            await AssertSamplesAsync(snapshot, attempted: true);
        }
        finally
        {
            foreach (var labels in counter.GetAllLabelValues().ToArray())
            {
                counter.RemoveLabelled(labels);
            }

            typeof(Plugin).GetProperty(nameof(Plugin.Instance))!.SetValue(null, previousPlugin);
        }

        async Task AssertSamplesAsync(PlaybackSnapshotExporter? snapshot, bool attempted)
        {
            using var stream = new MemoryStream();
            await Metrics.DefaultRegistry.CollectAndExportAsTextAsync(stream);
            var samples = Encoding.UTF8.GetString(stream.ToArray()).Split('\n')
                .Where(line => line.StartsWith("phantom_playback_outcome_total{", StringComparison.Ordinal)).ToArray();
            Assert.Equal(32, samples.Length);
            if (snapshot != null)
            {
                Assert.Equal(32, snapshot.Values.Count);
            }

            foreach (var flow in PhantomFlowMetrics.PlaybackFlows)
            foreach (var itemType in PhantomFlowMetrics.ItemTypes)
            foreach (var cause in PhantomFlowMetrics.PlaybackCauses)
            {
                var expected = attempted && flow == targetFlow && itemType == targetItemType
                    && cause == PhantomFlowMetrics.CauseFirstByteTimeout ? 1 : 0;
                Assert.Contains($"phantom_playback_outcome_total{{flow=\"{flow}\",item_type=\"{itemType}\",cause=\"{cause}\"}} {expected}", samples);
                if (snapshot != null)
                {
                    Assert.Equal(expected, snapshot.Values[(flow, itemType, cause)]);
                }
            }
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("configured-instance", null)]
    [InlineData("configured-instance", "builder-instance")]
    public async Task StartupProvenance_UsesBuiltProviderResource_AndLoadedAssembly(
        string? environmentInstance, string? builderInstance)
    {
        var previousAttributes = Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES");
        var previousName = Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME");
        try
        {
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", environmentInstance == null ? null
                : $"service.instance.id={environmentInstance},service.name=attribute-name,service.namespace=test-namespace,service.version=configured-version");
            Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", environmentInstance == null ? null : "configured-name");
            var logger = new ProvenanceLogger();
            using var snapshot = new PlaybackSnapshotExporter();
            using var reader = new PeriodicExportingMetricReader(snapshot, exportIntervalMilliseconds: int.MaxValue);
            using var service = new PhantomMetricsExporter(logger, ExporterConfiguration, builder =>
            {
                builder.AddReader(reader);
                if (builderInstance != null)
                {
                    builder.ConfigureResource(r => r.AddService("builder-name", serviceInstanceId: builderInstance));
                }
            });

            await service.StartAsync(CancellationToken.None);
            Assert.True(reader.Collect());
            var record = Assert.Single(logger.Records);
            var resource = snapshot.ResourceAttributes;
            Assert.Equal(resource["service.instance.id"], record["ServiceInstanceId"]);
            Assert.Equal(resource["service.name"], record["ServiceName"]);
            Assert.False(string.IsNullOrWhiteSpace(Assert.IsType<string>(record["ServiceInstanceId"])));
            Assert.Equal(builderInstance ?? environmentInstance ?? resource["service.instance.id"], record["ServiceInstanceId"]);
            Assert.Equal(builderInstance != null ? "builder-name" : environmentInstance != null ? "configured-name" : "jellyfin-phantom-library", record["ServiceName"]);
            Assert.Equal(resource.GetValueOrDefault("service.namespace"), record["ServiceNamespace"]);
            Assert.Equal(resource.GetValueOrDefault("service.version"), record["ServiceVersion"]);
            if (environmentInstance != null)
            {
                Assert.Equal("test-namespace", record["ServiceNamespace"]);
                Assert.Equal("configured-version", record["ServiceVersion"]);
            }

            var assembly = typeof(PhantomMetricsExporter).Assembly;
            Assert.Equal(assembly.ManifestModule.ModuleVersionId, record["AssemblyMvid"]);
            Assert.Equal(assembly.GetName().Version?.ToString(), record["PluginVersion"]);
            var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            Assert.Equal(informationalVersion, record["InformationalVersion"]);
            Assert.Equal(informationalVersion?.Split('+', 2).ElementAtOrDefault(1), record["SourceRevision"]);
            Assert.Equal(Environment.ProcessId, record["ProcessId"]);
            using var process = Process.GetCurrentProcess();
            Assert.Equal(process.StartTime.ToUniversalTime(), record["ProcessStartTimeUtc"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", previousAttributes);
            Environment.SetEnvironmentVariable("OTEL_SERVICE_NAME", previousName);
        }
    }

    [Fact]
    public async Task StartupProvenance_ProviderLifecycle_KeepsSdkProcessIdentity()
    {
        var previous = Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES");
        try
        {
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", null);
            var logger = new ProvenanceLogger();
            var snapshots = new List<PlaybackSnapshotExporter>();
            using var service = new PhantomMetricsExporter(logger, ExporterConfiguration, builder =>
            {
                var snapshot = new PlaybackSnapshotExporter();
                snapshots.Add(snapshot);
                builder.AddReader(new PeriodicExportingMetricReader(snapshot, exportIntervalMilliseconds: int.MaxValue));
            });
            await service.StartAsync(CancellationToken.None);
            await service.StartAsync(CancellationToken.None);
            Assert.Single(logger.Records);
            Assert.Single(snapshots);
            await service.StopAsync(CancellationToken.None);
            Assert.True(snapshots[0].Disposed);
            await service.StartAsync(CancellationToken.None);
            await service.StopAsync(CancellationToken.None);
            Assert.Equal(2, logger.Records.Count);
            Assert.All(snapshots, snapshot => Assert.True(snapshot.Disposed));

            using var otherService = new PhantomMetricsExporter(logger, ExporterConfiguration);
            await otherService.StartAsync(CancellationToken.None);
            Assert.Equal(3, logger.Records.Count);
            Assert.All(logger.Records, record =>
            {
                Assert.Equal(logger.Records[0]["ServiceInstanceId"], record["ServiceInstanceId"]);
                Assert.Equal(logger.Records[0]["ProcessStartTimeUtc"], record["ProcessStartTimeUtc"]);
                Assert.Equal(Environment.ProcessId, record["ProcessId"]);
            });
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", previous);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.2.3")]
    public void StartupProvenance_AbsentOptionalMetadata_IsNull(string? informationalVersion)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("MetadataFixture"), AssemblyBuilderAccess.Run);
        assembly.DefineDynamicModule("MetadataFixture");
        if (informationalVersion != null)
        {
            assembly.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(AssemblyInformationalVersionAttribute).GetConstructor(new[] { typeof(string) })!,
                new object[] { informationalVersion }));
        }

        using var provider = Sdk.CreateMeterProviderBuilder().SetResourceBuilder(ResourceBuilder.CreateEmpty()).Build();
        var logger = new ProvenanceLogger();
        using var service = new PhantomMetricsExporter(logger, ExporterConfiguration);
        service.LogProvenance(provider, assembly);
        var record = Assert.Single(logger.Records);
        Assert.Null(record["ServiceInstanceId"]);
        Assert.Null(record["ServiceNamespace"]);
        Assert.Null(record["ServiceVersion"]);
        Assert.Equal(informationalVersion, record["InformationalVersion"]);
        Assert.Null(record["SourceRevision"]);
        Assert.Equal(assembly.ManifestModule.ModuleVersionId, record["AssemblyMvid"]);
    }

    private static void AssertLoadedRuntimeInfo(PlaybackSnapshotExporter snapshot)
    {
        var point = Assert.Single(snapshot.RuntimeInfoPoints);
        Assert.Equal(1, point.Value);
        var assembly = typeof(PhantomMetricsExporter).Assembly;
        Assert.Equal(assembly.ManifestModule.ModuleVersionId.ToString(), point.Tags["assembly.mvid"]);
        Assert.Equal(assembly.GetName().Version?.ToString(), point.Tags["plugin.version"]);
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        Assert.Equal(informational, point.Tags.GetValueOrDefault("assembly.informational_version"));
        Assert.Equal(informational?.Split('+', 2).ElementAtOrDefault(1), point.Tags.GetValueOrDefault("source.revision"));
        var allowed = new[] { "assembly.mvid", "plugin.version", "assembly.informational_version", "source.revision" };
        Assert.All(point.Tags.Keys, k => Assert.Contains(k, allowed));
        Assert.All(allowed, k => Assert.DoesNotContain(k, snapshot.OutcomeTagKeys));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("configured-instance", null)]
    [InlineData("configured-instance", "builder-instance")]
    public async Task RuntimeInfo_NoTraffic_EveryCollectionPublishesOneLoadedBuildPoint_WithProviderResource(
        string? environmentInstance, string? builderInstance)
    {
        var previousAttributes = Environment.GetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES");
        try
        {
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", environmentInstance == null ? null
                : $"service.instance.id={environmentInstance},service.version=configured-version");
            var logger = new ProvenanceLogger();
            using var snapshot = new PlaybackSnapshotExporter();
            using var reader = new PeriodicExportingMetricReader(snapshot, exportIntervalMilliseconds: int.MaxValue);
            using var service = new PhantomMetricsExporter(logger, ExporterConfiguration, builder =>
            {
                builder.AddReader(reader);
                if (builderInstance != null)
                {
                    builder.ConfigureResource(r => r.AddService("builder-name", serviceInstanceId: builderInstance));
                }
            });
            await service.StartAsync(CancellationToken.None);
            for (var i = 0; i < 2; i++)
            {
                Assert.True(reader.Collect());
                AssertLoadedRuntimeInfo(snapshot);
                var resource = snapshot.ResourceAttributes;
                Assert.Equal(builderInstance ?? environmentInstance ?? resource["service.instance.id"], resource["service.instance.id"]);
                Assert.Equal(resource["service.instance.id"], Assert.Single(logger.Records)["ServiceInstanceId"]);
                if (environmentInstance != null)
                {
                    Assert.Equal("configured-version", resource["service.version"]);
                }
            }

            Assert.Equal(2, snapshot.Exports);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_RESOURCE_ATTRIBUTES", previousAttributes);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.2.3")]
    [InlineData("1.2.3+abc123")]
    public void RuntimeInfo_MissingOptionalMetadata_IsOmitted(string? informationalVersion)
    {
        var assembly = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("RuntimeInfoFixture"), AssemblyBuilderAccess.Run);
        assembly.DefineDynamicModule("RuntimeInfoFixture");
        if (informationalVersion != null)
        {
            assembly.SetCustomAttribute(new CustomAttributeBuilder(
                typeof(AssemblyInformationalVersionAttribute).GetConstructor(new[] { typeof(string) })!,
                new object[] { informationalVersion }));
        }

        var tags = PhantomFlowMetrics.RuntimeInfoTags(assembly).ToDictionary(p => p.Key, p => p.Value);
        Assert.Equal(assembly.ManifestModule.ModuleVersionId.ToString(), tags["assembly.mvid"]);
        Assert.Equal(informationalVersion, tags.GetValueOrDefault("assembly.informational_version"));
        Assert.Equal(informationalVersion == "1.2.3+abc123" ? "abc123" : null, tags.GetValueOrDefault("source.revision"));
        Assert.Equal(informationalVersion == "1.2.3+abc123", tags.ContainsKey("source.revision"));
    }

    [Fact]
    public async Task RuntimeInfo_DisabledExporter_BuildsNoProvider()
    {
        var previous = Environment.GetEnvironmentVariable("PHANTOM_METRICS_OTLP_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("PHANTOM_METRICS_OTLP_ENABLED", null);
            var built = 0;
            using var service = new PhantomMetricsExporter(
                NullLogger<PhantomMetricsExporter>.Instance,
                () => new PluginConfiguration { MetricsOtlpEnabled = false, MetricsOtlpEndpoint = "http://127.0.0.1:1/v1/metrics" },
                _ => built++);
            await service.StartAsync(CancellationToken.None);
            Assert.Equal(0, built);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PHANTOM_METRICS_OTLP_ENABLED", previous);
        }
    }

    [Fact]
    public async Task RuntimeInfo_LifecycleAndConcurrentProviders_OnePointPerProviderWithOwnIdentity()
    {
        var snapshots = new List<(string Id, PlaybackSnapshotExporter Snapshot, PeriodicExportingMetricReader Reader)>();
        PhantomMetricsExporter Make(string id) => new(NullLogger<PhantomMetricsExporter>.Instance, ExporterConfiguration, builder =>
        {
            var snapshot = new PlaybackSnapshotExporter();
            var reader = new PeriodicExportingMetricReader(snapshot, exportIntervalMilliseconds: int.MaxValue);
            snapshots.Add((id, snapshot, reader));
            builder.AddReader(reader).ConfigureResource(r => r.AddService("phantom-" + id, serviceInstanceId: id));
        });

        using var a = Make("instance-a");
        await a.StartAsync(CancellationToken.None);
        await a.StartAsync(CancellationToken.None);
        Assert.Single(snapshots);
        using var b = Make("instance-b");
        await b.StartAsync(CancellationToken.None);
        Assert.Equal(2, snapshots.Count);
        for (var i = 0; i < 2; i++)
        {
            foreach (var (id, snapshot, reader) in snapshots)
            {
                Assert.True(reader.Collect());
                AssertLoadedRuntimeInfo(snapshot);
                Assert.Equal(id, snapshot.ResourceAttributes["service.instance.id"]);
            }
        }

        await a.StopAsync(CancellationToken.None);
        await a.StartAsync(CancellationToken.None);
        Assert.Equal(3, snapshots.Count);
        Assert.True(snapshots[0].Snapshot.Disposed);
        foreach (var (id, snapshot, reader) in snapshots.Skip(1))
        {
            Assert.True(reader.Collect());
            AssertLoadedRuntimeInfo(snapshot);
            Assert.Equal(id, snapshot.ResourceAttributes["service.instance.id"]);
        }

        await a.StopAsync(CancellationToken.None);
        await b.StopAsync(CancellationToken.None);
    }

    private static PluginConfiguration ExporterConfiguration() => new()
    {
        MetricsOtlpEnabled = true,
        MetricsOtlpEndpoint = "http://127.0.0.1:1/v1/metrics",
        MetricsOtlpProtocol = "http/protobuf",
    };

    private sealed class ProvenanceLogger : ILogger<PhantomMetricsExporter>
    {
        public List<Dictionary<string, object?>> Records { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.NotEqual(LogLevel.Error, logLevel);
            if (eventId.Name == "PhantomMetricsProvenance")
            {
                Assert.Equal(LogLevel.Information, logLevel);
                Records.Add(((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(p => p.Key, p => p.Value));
            }
        }
    }

    private sealed class PlaybackSnapshotExporter : BaseExporter<Metric>
    {
        public Dictionary<(string Flow, string ItemType, string Cause), long> Values { get; } = new();

        public Dictionary<string, object> ResourceAttributes { get; private set; } = new();

        public bool Disposed { get; private set; }

        public List<(long Value, Dictionary<string, object?> Tags)> RuntimeInfoPoints { get; } = new();

        public HashSet<string> OutcomeTagKeys { get; } = new();

        public int Exports { get; private set; }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }

        public override ExportResult Export(in Batch<Metric> batch)
        {
            ResourceAttributes = ParentProvider.GetResource().Attributes.ToDictionary(p => p.Key, p => p.Value);
            Values.Clear();
            RuntimeInfoPoints.Clear();
            OutcomeTagKeys.Clear();
            Exports++;
            foreach (var metric in batch)
            {
                if (metric.Name == PhantomFlowMetrics.RuntimeInfoName)
                {
                    foreach (ref readonly var point in metric.GetMetricPoints())
                    {
                        var info = new Dictionary<string, object?>();
                        foreach (var tag in point.Tags)
                        {
                            info.Add(tag.Key, tag.Value);
                        }

                        RuntimeInfoPoints.Add((point.GetGaugeLastValueLong(), info));
                    }

                    continue;
                }

                if (metric.Name != "phantom_playback_outcome_total")
                {
                    continue;
                }

                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    var tags = new Dictionary<string, object?>();
                    foreach (var tag in point.Tags)
                    {
                        tags.Add(tag.Key, tag.Value);
                    }

                    OutcomeTagKeys.UnionWith(tags.Keys);
                    Values.Add(((string)tags["flow"]!, (string)tags["item_type"]!, (string)tags["cause"]!), point.GetSumLong());
                }
            }

            return ExportResult.Success;
        }
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
