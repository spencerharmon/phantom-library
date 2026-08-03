using System;
using System.Diagnostics.Metrics;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Jellyfin.Plugin.PhantomLibrary.Diagnostics;

/// <summary>
/// Hosted service that stands up an OpenTelemetry <see cref="MeterProvider"/>
/// subscribed to <see cref="PhantomFlowMetrics.MeterName"/> and exports the
/// P5 browse-flow histograms over OTLP to the operator-configured collector.
///
/// The exporter target is resolved entirely from configuration / environment —
/// never a baked-in hostname (the deployment's observability stack is in flux:
/// OpenObserve at <c>observe.spencerharmon.com</c> was retired in favour of a
/// grafana-mimir-prometheus stack, so any hard-coded host would silently break).
/// Resolution order for the endpoint:
/// <list type="number">
///   <item><c>PluginConfiguration.MetricsOtlpEndpoint</c> (admin dashboard);</item>
///   <item><c>OTEL_EXPORTER_OTLP_ENDPOINT</c> environment variable.</item>
/// </list>
///
/// The service is fail-safe: if metrics are disabled, no endpoint resolves, or
/// exporter construction throws, it logs and no-ops rather than breaking
/// Jellyfin startup. The <see cref="PhantomFlowMetrics"/> instruments keep
/// recording regardless; without a provider they are cheap no-ops.
/// </summary>
public sealed class PhantomMetricsExporter : IHostedService, IDisposable
{
    private readonly ILogger<PhantomMetricsExporter> _logger;
    private readonly Func<PluginConfiguration> _configProvider;
    private MeterProvider? _meterProvider;

    /// <summary>Initializes a new instance of the <see cref="PhantomMetricsExporter"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public PhantomMetricsExporter(ILogger<PhantomMetricsExporter> logger)
        : this(logger, () => Plugin.Instance?.Configuration ?? new PluginConfiguration())
    {
    }

    internal PhantomMetricsExporter(ILogger<PhantomMetricsExporter> logger, Func<PluginConfiguration> configProvider)
    {
        _logger = logger;
        _configProvider = configProvider;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = _configProvider();
            if (!config.MetricsOtlpEnabled)
            {
                _logger.LogDebug("Phantom OTLP flow metrics disabled; not starting exporter.");
                return Task.CompletedTask;
            }

            var endpoint = ResolveEndpoint(config);
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                _logger.LogWarning(
                    "Phantom OTLP flow metrics enabled but no endpoint resolved "
                    + "(set MetricsOtlpEndpoint or OTEL_EXPORTER_OTLP_ENDPOINT); exporter not started.");
                return Task.CompletedTask;
            }

            var protocol = ResolveProtocol(config);

            _meterProvider = Sdk.CreateMeterProviderBuilder()
                .ConfigureResource(r => r.AddService("jellyfin-phantom-library"))
                .AddMeter(PhantomFlowMetrics.MeterName)
                .AddOtlpExporter((exporterOptions, readerOptions) =>
                {
                    exporterOptions.Endpoint = new Uri(endpoint);
                    exporterOptions.Protocol = protocol;
                })
                .Build();

            _logger.LogInformation(
                "Phantom OTLP flow metrics exporter started (endpoint={Endpoint}, protocol={Protocol}).",
                endpoint,
                protocol);
        }
        catch (Exception ex)
        {
            // Never let a metrics-plumbing failure break plugin/host startup.
            _logger.LogError(ex, "Failed to start Phantom OTLP flow metrics exporter; continuing without it.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _meterProvider?.Dispose();
        _meterProvider = null;
    }

    internal static string? ResolveEndpoint(PluginConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.MetricsOtlpEndpoint))
        {
            return config.MetricsOtlpEndpoint.Trim();
        }

        var env = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    internal static OtlpExportProtocol ResolveProtocol(PluginConfiguration config)
    {
        var raw = config.MetricsOtlpProtocol;
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL");
        }

        return raw?.Trim().ToUpperInvariant() switch
        {
            "HTTP/PROTOBUF" or "HTTP" or "HTTPPROTOBUF" => OtlpExportProtocol.HttpProtobuf,
            _ => OtlpExportProtocol.Grpc,
        };
    }
}
