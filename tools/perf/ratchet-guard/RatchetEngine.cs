namespace Phantom.Perf.RatchetGuard;

/// <summary>What happened to one scenario when a measurement was ratcheted against its threshold.</summary>
public enum ScenarioOutcome
{
    /// <summary>Measurement within the acceptable band: at or below the ceiling, not fast enough to tighten.</summary>
    Held,

    /// <summary>Measurement beat the ceiling by the improvement margin: threshold tightened downward.</summary>
    Ratcheted,

    /// <summary>Measurement exceeded the ceiling: a regression. Fails the guard and must file a task.</summary>
    Breached,

    /// <summary>Measurement present for a scenario that has no recorded threshold yet: seeds it (informational).</summary>
    Seeded,
}

public sealed class ScenarioResult
{
    public required string Key { get; init; }
    public required ScenarioOutcome Outcome { get; init; }
    public double MeasuredMs { get; init; }
    public double PreviousThresholdMs { get; init; }
    public double NewThresholdMs { get; init; }
    public required string Detail { get; init; }
}

public sealed class GuardResult
{
    public required IReadOnlyList<ScenarioResult> Results { get; init; }

    /// <summary>The thresholds object after ratcheting/seeding, ready to persist. Never loosened.</summary>
    public required RatchetThresholds UpdatedThresholds { get; init; }

    public IEnumerable<ScenarioResult> Breaches => Results.Where(r => r.Outcome == ScenarioOutcome.Breached);
    public IEnumerable<ScenarioResult> Ratchets => Results.Where(r => r.Outcome == ScenarioOutcome.Ratcheted);
    public IEnumerable<ScenarioResult> Seeded => Results.Where(r => r.Outcome == ScenarioOutcome.Seeded);

    public bool HasBreach => Breaches.Any();

    /// <summary>True if any threshold changed (ratchet or seed) and the file should be rewritten.</summary>
    public bool ThresholdsChanged =>
        Results.Any(r => r.Outcome is ScenarioOutcome.Ratcheted or ScenarioOutcome.Seeded);
}

/// <summary>
/// The pure ratcheting decision engine.
///
/// For every measured scenario, compared against its recorded ceiling:
///   * measured &gt; ceiling                       => BREACH (guard fails; file a regression task).
///   * measured &lt;= ceiling*(1-improvementMargin) => RATCHET the ceiling DOWN toward the
///     measurement (with a small headroom), never up. A ratchet is only ever a tightening.
///   * otherwise                                   => HOLD the ceiling unchanged.
/// A scenario measured with no recorded threshold is SEEDED at measurement+headroom
/// (informational, not a breach) so the ratchet has a starting ceiling.
///
/// The engine NEVER loosens a threshold: a breach leaves the ceiling exactly where it
/// was (so the regression stays failing until fixed) rather than accepting the slower value.
/// </summary>
public static class RatchetEngine
{
    public static GuardResult Evaluate(RatchetThresholds thresholds, MeasurementSet measurements)
    {
        ArgumentNullException.ThrowIfNull(thresholds);
        ArgumentNullException.ThrowIfNull(measurements);

        var globalMargin = Math.Clamp(thresholds.ImprovementMarginRatio, 0.0, 0.99);
        var globalHeadroom = Math.Max(0.0, thresholds.RatchetHeadroomRatio);

        // Work on a copy so callers never see a mutated input; the returned object is what to persist.
        var updated = new RatchetThresholds
        {
            ImprovementMarginRatio = thresholds.ImprovementMarginRatio,
            RatchetHeadroomRatio = thresholds.RatchetHeadroomRatio,
            Scenarios = thresholds.Scenarios
                .Select(s => new ScenarioThreshold
                {
                    Flow = s.Flow,
                    Backend = s.Backend,
                    Quantile = s.Quantile,
                    ThresholdMs = s.ThresholdMs,
                    ImprovementMarginRatio = s.ImprovementMarginRatio,
                    RatchetHeadroomRatio = s.RatchetHeadroomRatio,
                    TargetMs = s.TargetMs,
                })
                .ToList(),
        };

        var byKey = updated.Scenarios.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var results = new List<ScenarioResult>();

        foreach (var m in measurements.Measurements)
        {
            if (double.IsNaN(m.ValueMs) || double.IsInfinity(m.ValueMs) || m.ValueMs < 0)
            {
                throw new FormatException($"scenario '{m.Key}' has an invalid measurement: {m.ValueMs}");
            }

            if (!byKey.TryGetValue(m.Key, out var scenario))
            {
                var seeded = new ScenarioThreshold
                {
                    Flow = m.Flow,
                    Backend = m.Backend,
                    Quantile = m.Quantile,
                    ThresholdMs = Round(m.ValueMs * (1.0 + globalHeadroom)),
                };
                updated.Scenarios.Add(seeded);
                byKey[m.Key] = seeded;
                results.Add(new ScenarioResult
                {
                    Key = m.Key,
                    Outcome = ScenarioOutcome.Seeded,
                    MeasuredMs = m.ValueMs,
                    PreviousThresholdMs = 0,
                    NewThresholdMs = seeded.ThresholdMs,
                    Detail = $"no prior threshold; seeded ceiling at {seeded.ThresholdMs:0.###}ms",
                });
                continue;
            }

            var margin = Math.Clamp(scenario.ImprovementMarginRatio ?? globalMargin, 0.0, 0.99);
            var headroom = Math.Max(0.0, scenario.RatchetHeadroomRatio ?? globalHeadroom);
            var ceiling = scenario.ThresholdMs;

            // A registered scenario with a non-positive ceiling has never been
            // seeded (the shipped thresholds file lists the five flows with 0 so a
            // reviewer can see them, but the real ceiling is captured from the first
            // measured baseline). Seed it rather than breaching against a bogus 0.
            if (ceiling <= 0)
            {
                var seededMs = Round(m.ValueMs * (1.0 + headroom));
                scenario.ThresholdMs = seededMs;
                results.Add(new ScenarioResult
                {
                    Key = m.Key,
                    Outcome = ScenarioOutcome.Seeded,
                    MeasuredMs = m.ValueMs,
                    PreviousThresholdMs = 0,
                    NewThresholdMs = seededMs,
                    Detail = $"unseeded ceiling; seeded at {seededMs:0.###}ms from first baseline",
                });
                continue;
            }

            if (m.ValueMs > ceiling)
            {
                // Regression. Do NOT move the ceiling — a breach never loosens the guard.
                results.Add(new ScenarioResult
                {
                    Key = m.Key,
                    Outcome = ScenarioOutcome.Breached,
                    MeasuredMs = m.ValueMs,
                    PreviousThresholdMs = ceiling,
                    NewThresholdMs = ceiling,
                    Detail = $"{m.ValueMs:0.###}ms exceeds ceiling {ceiling:0.###}ms "
                        + $"(+{Percent(m.ValueMs, ceiling):0.#}%)",
                });
                continue;
            }

            var improvementBar = ceiling * (1.0 - margin);
            if (m.ValueMs <= improvementBar)
            {
                var candidate = Round(m.ValueMs * (1.0 + headroom));
                // Only ever tighten: the candidate must be strictly below the current ceiling.
                if (candidate < ceiling)
                {
                    scenario.ThresholdMs = candidate;
                    results.Add(new ScenarioResult
                    {
                        Key = m.Key,
                        Outcome = ScenarioOutcome.Ratcheted,
                        MeasuredMs = m.ValueMs,
                        PreviousThresholdMs = ceiling,
                        NewThresholdMs = candidate,
                        Detail = $"improved to {m.ValueMs:0.###}ms; ceiling tightened "
                            + $"{ceiling:0.###}ms -> {candidate:0.###}ms",
                    });
                    continue;
                }
            }

            results.Add(new ScenarioResult
            {
                Key = m.Key,
                Outcome = ScenarioOutcome.Held,
                MeasuredMs = m.ValueMs,
                PreviousThresholdMs = ceiling,
                NewThresholdMs = ceiling,
                Detail = $"{m.ValueMs:0.###}ms within band (ceiling {ceiling:0.###}ms)",
            });
        }

        return new GuardResult { Results = results, UpdatedThresholds = updated };
    }

    private static double Round(double v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);

    private static double Percent(double value, double baseline) =>
        baseline <= 0 ? 0 : (value - baseline) / baseline * 100.0;
}
