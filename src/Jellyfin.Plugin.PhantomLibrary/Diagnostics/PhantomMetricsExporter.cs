using System;
using Jellyfin.Plugin.PhantomLibrary.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PhantomLibrary.Diagnostics;

/// <summary>
/// OTLP export protocol, mirroring OpenTelemetry's <c>OtlpExportProtocol</c> but
/// declared plugin-locally so the plugin assembly carries no OpenTelemetry
/// reference (see the class remarks for why the OpenTelemetry SDK is currently
/// not shipped). When OTLP export is re-enabled this maps 1:1 onto
/// <c>OpenTelemetry.Exporter.OtlpExportProtocol</c>.
/// </summary>
public enum PhantomOtlpProtocol
{
    /// <summary>OTLP over gRPC (default).</summary>
    Grpc,

    /// <summary>OTLP over HTTP with protobuf payloads.</summary>
    HttpProtobuf,
}

/// <summary>
/// Hosted service that would stand up an OpenTelemetry <c>MeterProvider</c>
/// subscribed to <see cref="PhantomFlowMetrics.MeterName"/> and export the P5
/// browse-flow histograms over OTLP to the operator-configured collector.
///
/// OTLP export is currently DISABLED and the OpenTelemetry SDK is not a
/// dependency of the plugin. Reason: Jellyfin 10.11 runs on .NET 9, whose shared
/// framework ships <c>System.Diagnostics.DiagnosticSource</c> 9.0. Every
/// OpenTelemetry release that is patched against advisory GHSA-4625-4j76-fww9
/// (i.e. &gt;= 1.15.3) targets <c>DiagnosticSource</c> 10.0, which cannot load in
/// Jellyfin's isolated plugin load context on a .NET 9 host (the plugin is
/// disabled at startup with a <c>TypeLoadException</c> on
/// <c>OpenTelemetryMetricsListener</c>); every release still on
/// <c>DiagnosticSource</c> 9.0 (&lt;= 1.13.x) is inside the vulnerable range of
/// that advisory. There is therefore no OpenTelemetry version that is both
/// non-vulnerable and loadable here, so the export path is deferred until the
/// host advances to a runtime whose <c>DiagnosticSource</c> matches a patched
/// OpenTelemetry (tracked alongside the "wire OTLP -&gt; Mimir" follow-up).
///
/// The metric INSTRUMENTS in <see cref="PhantomFlowMetrics"/> keep recording on a
/// standard <c>System.Diagnostics.Metrics.Meter</c> regardless — they are cheap
/// no-ops without a listener and can be scraped by any in-process
/// <c>MeterListener</c> — so no measurement code changes when export returns; only
/// this class and the two OpenTelemetry PackageReferences are reinstated.
/// </summary>
public sealed class PhantomMetricsExporter : IHostedService, IDisposable
{
    private readonly ILogger<PhantomMetricsExporter> _logger;
    private readonly Func<PluginConfiguration> _configProvider;

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
            var protocol = ResolveProtocol(config);
            _logger.LogWarning(
                "Phantom OTLP flow metrics are enabled (endpoint={Endpoint}, protocol={Protocol}) but OTLP "
                + "export is not available in this build: no OpenTelemetry release is both patched against "
                + "GHSA-4625-4j76-fww9 and loadable on the .NET 9 host (DiagnosticSource 9.0). The flow "
                + "instruments continue recording in-process; wire-up is deferred to the OTLP->Mimir follow-up.",
                endpoint ?? "(unresolved)",
                protocol);
        }
        catch (Exception ex)
        {
            // Never let a metrics-plumbing failure break plugin/host startup.
            _logger.LogError(ex, "Failed to evaluate Phantom OTLP flow metrics configuration; continuing without it.");
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc/>
    public void Dispose()
    {
        // No unmanaged/OTel resources while export is deferred.
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

    internal static PhantomOtlpProtocol ResolveProtocol(PluginConfiguration config)
    {
        var raw = config.MetricsOtlpProtocol;
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL");
        }

        return raw?.Trim().ToUpperInvariant() switch
        {
            "HTTP/PROTOBUF" or "HTTP" or "HTTPPROTOBUF" => PhantomOtlpProtocol.HttpProtobuf,
            _ => PhantomOtlpProtocol.Grpc,
        };
    }
}
