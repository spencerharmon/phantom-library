using Phantom.Perf.RatchetGuard;

namespace Phantom.Perf.LoadtimeCompare;

/// <summary>How one flow's Postgres "after" latency moved relative to its SQLite "before".</summary>
public enum ComparisonDirection
{
    /// <summary>After is faster than before by more than the neutral band: a genuine improvement.</summary>
    Improved,

    /// <summary>After is slower than before by more than the neutral band: a REGRESSION (never assumed away).</summary>
    Regressed,

    /// <summary>After and before are within the neutral band of each other: no meaningful change.</summary>
    Neutral,
}

/// <summary>
/// The before/after result for one (flow, quantile) pair. Delta is measured, never assumed:
/// <c>DeltaMs = AfterMs - BaselineMs</c> (negative = faster after), and the direction is
/// classified against a neutral band so measurement noise is not reported as a gain or a loss.
/// </summary>
public sealed class FlowComparison
{
    public required string Flow { get; init; }
    public required string Quantile { get; init; }
    public required string BaselineBackend { get; init; }
    public required string AfterBackend { get; init; }
    public double BaselineMs { get; init; }
    public double AfterMs { get; init; }

    /// <summary>After minus before, in ms. Negative = the Postgres backend is faster for this flow.</summary>
    public double DeltaMs => Math.Round(AfterMs - BaselineMs, 3, MidpointRounding.AwayFromZero);

    /// <summary>Change as a percentage of the baseline. Positive = slower after.</summary>
    public double DeltaPercent =>
        BaselineMs <= 0 ? 0 : Math.Round((AfterMs - BaselineMs) / BaselineMs * 100.0, 3, MidpointRounding.AwayFromZero);

    public required ComparisonDirection Direction { get; init; }
}

/// <summary>
/// A scenario present in exactly one of the two runs, so no before/after delta can be computed.
/// Reported explicitly rather than silently dropped (an unpaired flow is a capture gap, not a gain).
/// </summary>
public sealed class UnpairedScenario
{
    public required string Flow { get; init; }
    public required string Quantile { get; init; }
    public required string Backend { get; init; }
    public double ValueMs { get; init; }

    /// <summary>Which side supplied the lone measurement: "baseline" or "after".</summary>
    public required string Side { get; init; }
}

public sealed class ComparisonReport
{
    public required IReadOnlyList<FlowComparison> Comparisons { get; init; }
    public required IReadOnlyList<UnpairedScenario> Unpaired { get; init; }

    public IEnumerable<FlowComparison> Regressions =>
        Comparisons.Where(c => c.Direction == ComparisonDirection.Regressed);

    public IEnumerable<FlowComparison> Improvements =>
        Comparisons.Where(c => c.Direction == ComparisonDirection.Improved);

    public bool HasRegression => Regressions.Any();
}

/// <summary>
/// Pure before/after comparison of two <see cref="MeasurementSet"/> runs — the SQLite baseline
/// and the Postgres-backed run. Matches scenarios by (flow, quantile) IGNORING backend (the whole
/// point is that the backend differs between the two sides), computes the measured delta, and
/// classifies each flow against a neutral band. Never assumes the Postgres backend is faster.
/// </summary>
public static class LoadtimeComparer
{
    /// <summary>
    /// Compare a baseline run against an "after" run.
    /// </summary>
    /// <param name="baseline">The SQLite baseline measurements.</param>
    /// <param name="after">The Postgres-backed "after" measurements.</param>
    /// <param name="neutralBandRatio">
    /// Fraction of the baseline within which a change is treated as neutral (noise). Default 0.05 (5%).
    /// </param>
    public static ComparisonReport Compare(MeasurementSet baseline, MeasurementSet after, double neutralBandRatio = 0.05)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(after);
        if (double.IsNaN(neutralBandRatio) || neutralBandRatio < 0)
        {
            throw new FormatException($"neutral band ratio must be finite and non-negative, got {neutralBandRatio}");
        }

        var baselineByPair = Index(baseline, "baseline");
        var afterByPair = Index(after, "after");

        var comparisons = new List<FlowComparison>();
        var unpaired = new List<UnpairedScenario>();

        foreach (var (pair, b) in baselineByPair.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!afterByPair.TryGetValue(pair, out var a))
            {
                unpaired.Add(ToUnpaired(b, "baseline"));
                continue;
            }

            var band = b.ValueMs * neutralBandRatio;
            var delta = a.ValueMs - b.ValueMs;
            var direction = delta < -band
                ? ComparisonDirection.Improved
                : delta > band
                    ? ComparisonDirection.Regressed
                    : ComparisonDirection.Neutral;

            comparisons.Add(new FlowComparison
            {
                Flow = b.Flow,
                Quantile = b.Quantile,
                BaselineBackend = b.Backend,
                AfterBackend = a.Backend,
                BaselineMs = b.ValueMs,
                AfterMs = a.ValueMs,
                Direction = direction,
            });
        }

        foreach (var (pair, a) in afterByPair.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!baselineByPair.ContainsKey(pair))
            {
                unpaired.Add(ToUnpaired(a, "after"));
            }
        }

        return new ComparisonReport { Comparisons = comparisons, Unpaired = unpaired };
    }

    private static Dictionary<string, ScenarioMeasurement> Index(MeasurementSet set, string side)
    {
        var byPair = new Dictionary<string, ScenarioMeasurement>(StringComparer.Ordinal);
        foreach (var m in set.Measurements)
        {
            if (double.IsNaN(m.ValueMs) || double.IsInfinity(m.ValueMs) || m.ValueMs < 0)
            {
                throw new FormatException($"{side} scenario '{m.Key}' has an invalid measurement: {m.ValueMs}");
            }

            var pair = $"{m.Flow}:{m.Quantile}";
            if (!byPair.TryAdd(pair, m))
            {
                throw new FormatException($"{side} run has duplicate measurement for flow/quantile '{pair}'");
            }
        }

        return byPair;
    }

    private static UnpairedScenario ToUnpaired(ScenarioMeasurement m, string side) => new()
    {
        Flow = m.Flow,
        Quantile = m.Quantile,
        Backend = m.Backend,
        ValueMs = m.ValueMs,
        Side = side,
    };
}
