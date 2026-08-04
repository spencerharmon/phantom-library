using Phantom.Perf.RatchetGuard;

namespace Phantom.Perf.LoadtimeCompare;

/// <summary>
/// Feeds the measured Postgres "after" numbers into the ratchet guard's threshold table
/// (<c>tools/perf/ratchet-thresholds.json</c>) so <c>p5-ratcheting-regression-guard</c> guards the
/// Postgres backend, exactly as it already guards SQLite. Producing the feed is deliberately
/// separate from the honest committed thresholds file: the committed file lists the Postgres
/// scenarios UNSEEDED (threshold_ms=0), and this feed seeds their real ceilings only from a real
/// operator perf capture — no fabricated numbers are ever committed.
/// </summary>
public static class ThresholdFeed
{
    /// <summary>
    /// Merge the Postgres scenarios implied by an "after" run into an existing thresholds object.
    /// </summary>
    /// <param name="existing">The current ratchet thresholds (never mutated).</param>
    /// <param name="after">The Postgres-backed "after" measurements to feed in.</param>
    /// <param name="seed">
    /// When true, seed each newly added scenario's ceiling from its measured value plus headroom
    /// (i.e. actually fold the after numbers in). When false, add the scenario UNSEEDED
    /// (threshold_ms=0) so the ratchet guard seeds it on its own first <c>--apply</c> run — this is
    /// what the committed thresholds file uses, keeping fabricated numbers out of git.
    /// </param>
    /// <returns>A new thresholds object with the Postgres scenarios present, ready to persist.</returns>
    public static RatchetThresholds MergeAfter(RatchetThresholds existing, MeasurementSet after, bool seed)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(after);

        var headroom = Math.Max(0.0, existing.RatchetHeadroomRatio);

        var merged = new RatchetThresholds
        {
            ImprovementMarginRatio = existing.ImprovementMarginRatio,
            RatchetHeadroomRatio = existing.RatchetHeadroomRatio,
            Scenarios = existing.Scenarios
                .Select(s => new ScenarioThreshold
                {
                    Flow = s.Flow,
                    Backend = s.Backend,
                    Quantile = s.Quantile,
                    ThresholdMs = s.ThresholdMs,
                })
                .ToList(),
        };

        var byKey = merged.Scenarios.ToDictionary(s => s.Key, StringComparer.Ordinal);

        foreach (var m in after.Measurements)
        {
            if (double.IsNaN(m.ValueMs) || double.IsInfinity(m.ValueMs) || m.ValueMs < 0)
            {
                throw new FormatException($"after scenario '{m.Key}' has an invalid measurement: {m.ValueMs}");
            }

            // Never overwrite an already-seeded ceiling; the ratchet guard owns tightening it.
            if (byKey.TryGetValue(m.Key, out var existingScenario))
            {
                if (seed && existingScenario.ThresholdMs <= 0)
                {
                    existingScenario.ThresholdMs = Round(m.ValueMs * (1.0 + headroom));
                }

                continue;
            }

            var added = new ScenarioThreshold
            {
                Flow = m.Flow,
                Backend = m.Backend,
                Quantile = m.Quantile,
                ThresholdMs = seed ? Round(m.ValueMs * (1.0 + headroom)) : 0,
            };
            merged.Scenarios.Add(added);
            byKey[added.Key] = added;
        }

        return merged;
    }

    private static double Round(double v) => Math.Round(v, 3, MidpointRounding.AwayFromZero);
}
