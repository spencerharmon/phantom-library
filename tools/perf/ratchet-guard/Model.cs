using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Perf.RatchetGuard;

/// <summary>
/// The ratcheting threshold recorded for one instrumented browse-flow scenario.
/// A scenario is identified by (flow, backend, quantile) of the
/// <c>phantom_flow_duration_ms</c> histogram emitted by PhantomFlowMetrics.
/// </summary>
public sealed class ScenarioThreshold
{
    /// <summary>The flow name (one of PhantomFlowMetrics' five flow constants).</summary>
    [JsonPropertyName("flow")]
    public string Flow { get; set; } = string.Empty;

    /// <summary>The backend tag the threshold applies to (sqlite/postgres).</summary>
    [JsonPropertyName("backend")]
    public string Backend { get; set; } = "sqlite";

    /// <summary>The latency quantile guarded (e.g. "p50", "p90", "p99").</summary>
    [JsonPropertyName("quantile")]
    public string Quantile { get; set; } = "p90";

    /// <summary>The current ratcheting ceiling in milliseconds. A measurement above this is a breach.</summary>
    [JsonPropertyName("threshold_ms")]
    public double ThresholdMs { get; set; }

    /// <summary>The scenario key (stable identity used for measurements and filed task ids).</summary>
    [JsonIgnore]
    public string Key => $"{Flow}:{Backend}:{Quantile}";
}

/// <summary>The persisted ratchet state: the guarded scenarios plus the ratchet policy knobs.</summary>
public sealed class RatchetThresholds
{
    /// <summary>
    /// A measurement must beat the current threshold by at least this fraction
    /// before it counts as a real improvement worth tightening to. Guards against
    /// flapping the file on measurement noise. Default 0.10 (10% faster).
    /// </summary>
    [JsonPropertyName("improvement_margin_ratio")]
    public double ImprovementMarginRatio { get; set; } = 0.10;

    /// <summary>
    /// When ratcheting down to a new (faster) measurement, keep this much headroom
    /// above the measured value so the very next run does not immediately breach on
    /// ordinary variance. Default 0.05 (5% headroom). The new threshold is still
    /// strictly below the old one — a ratchet only ever tightens.
    /// </summary>
    [JsonPropertyName("ratchet_headroom_ratio")]
    public double RatchetHeadroomRatio { get; set; } = 0.05;

    [JsonPropertyName("scenarios")]
    public List<ScenarioThreshold> Scenarios { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static RatchetThresholds Parse(string json)
    {
        var parsed = JsonSerializer.Deserialize<RatchetThresholds>(json, Options)
            ?? throw new FormatException("ratchet thresholds JSON parsed to null");
        parsed.Scenarios ??= new List<ScenarioThreshold>();
        return parsed;
    }

    public string Serialize() => JsonSerializer.Serialize(this, Options);

    public static RatchetThresholds Load(string path) => Parse(File.ReadAllText(path));

    public void Save(string path) => File.WriteAllText(path, Serialize() + "\n");
}

/// <summary>A single measured latency for one scenario, produced by a perf run.</summary>
public sealed class ScenarioMeasurement
{
    [JsonPropertyName("flow")]
    public string Flow { get; set; } = string.Empty;

    [JsonPropertyName("backend")]
    public string Backend { get; set; } = "sqlite";

    [JsonPropertyName("quantile")]
    public string Quantile { get; set; } = "p90";

    [JsonPropertyName("value_ms")]
    public double ValueMs { get; set; }

    [JsonIgnore]
    public string Key => $"{Flow}:{Backend}:{Quantile}";
}

public sealed class MeasurementSet
{
    [JsonPropertyName("measurements")]
    public List<ScenarioMeasurement> Measurements { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static MeasurementSet Parse(string json)
    {
        var parsed = JsonSerializer.Deserialize<MeasurementSet>(json, Options)
            ?? throw new FormatException("measurement JSON parsed to null");
        parsed.Measurements ??= new List<ScenarioMeasurement>();
        return parsed;
    }

    public static MeasurementSet Load(string path) => Parse(File.ReadAllText(path));
}
