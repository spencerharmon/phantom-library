using Phantom.Perf.RatchetGuard;
using Xunit;

namespace PhantomRatchetGuard.Tests;

public class RatchetEngineTests
{
    private static RatchetThresholds Thresholds(params (string flow, double ms)[] scenarios)
    {
        return new RatchetThresholds
        {
            ImprovementMarginRatio = 0.10,
            RatchetHeadroomRatio = 0.05,
            Scenarios = scenarios
                .Select(s => new ScenarioThreshold
                {
                    Flow = s.flow,
                    Backend = "sqlite",
                    Quantile = "p90",
                    ThresholdMs = s.ms,
                })
                .ToList(),
        };
    }

    private static MeasurementSet Measure(params (string flow, double ms)[] measurements)
    {
        return new MeasurementSet
        {
            Measurements = measurements
                .Select(m => new ScenarioMeasurement
                {
                    Flow = m.flow,
                    Backend = "sqlite",
                    Quantile = "p90",
                    ValueMs = m.ms,
                })
                .ToList(),
        };
    }

    [Fact]
    public void MeasurementAboveCeiling_Breaches_AndDoesNotLoosen()
    {
        var t = Thresholds(("list-view", 1000));
        var m = Measure(("list-view", 1500));

        var r = RatchetEngine.Evaluate(t, m);

        var res = Assert.Single(r.Results);
        Assert.Equal(ScenarioOutcome.Breached, res.Outcome);
        Assert.True(r.HasBreach);
        // Never loosen: the ceiling stays exactly where it was on a breach.
        Assert.Equal(1000, res.NewThresholdMs);
        Assert.Equal(1000, r.UpdatedThresholds.Scenarios.Single().ThresholdMs);
        Assert.False(r.ThresholdsChanged);
    }

    [Fact]
    public void Improvement_TightensCeilingDownward_NeverUp()
    {
        var t = Thresholds(("season-listing", 1000));
        // 800ms is a 20% improvement, beyond the 10% margin -> ratchet.
        var m = Measure(("season-listing", 800));

        var r = RatchetEngine.Evaluate(t, m);

        var res = Assert.Single(r.Ratchets);
        Assert.Equal(ScenarioOutcome.Ratcheted, res.Outcome);
        // new ceiling = 800 * 1.05 = 840, strictly below the old 1000.
        Assert.Equal(840, res.NewThresholdMs);
        Assert.True(res.NewThresholdMs < res.PreviousThresholdMs);
        Assert.True(r.ThresholdsChanged);
        Assert.False(r.HasBreach);
    }

    [Fact]
    public void Ratchet_NeverProducesCeilingAbovePrevious()
    {
        // A measurement just under the ceiling but the headroom would push the
        // candidate back above it: must HOLD, not loosen.
        var t = Thresholds(("episode-listing", 1000));
        // improvement bar = 900; 850 <= 900 so it qualifies, candidate = 850*1.05 = 892.5 < 1000 -> ratchet.
        var m = Measure(("episode-listing", 850));
        var r = RatchetEngine.Evaluate(t, m);
        var res = Assert.Single(r.Results);
        Assert.Equal(ScenarioOutcome.Ratcheted, res.Outcome);
        Assert.True(res.NewThresholdMs < 1000);
    }

    [Fact]
    public void WithinBand_Holds_NoChange()
    {
        var t = Thresholds(("materialised-listing", 1000));
        // 950 is faster than ceiling but only 5% -> under the 10% margin -> hold.
        var m = Measure(("materialised-listing", 950));

        var r = RatchetEngine.Evaluate(t, m);

        var res = Assert.Single(r.Results);
        Assert.Equal(ScenarioOutcome.Held, res.Outcome);
        Assert.Equal(1000, res.NewThresholdMs);
        Assert.False(r.ThresholdsChanged);
    }

    [Fact]
    public void MeasurementEqualToCeiling_Holds_NotBreach()
    {
        var t = Thresholds(("sort-filter", 500));
        var m = Measure(("sort-filter", 500));
        var r = RatchetEngine.Evaluate(t, m);
        Assert.Equal(ScenarioOutcome.Held, Assert.Single(r.Results).Outcome);
        Assert.False(r.HasBreach);
    }

    [Fact]
    public void UnknownScenario_IsSeeded_NotBreached()
    {
        var t = Thresholds();
        var m = Measure(("brand-new-flow", 400));

        var r = RatchetEngine.Evaluate(t, m);

        var res = Assert.Single(r.Results);
        Assert.Equal(ScenarioOutcome.Seeded, res.Outcome);
        Assert.False(r.HasBreach);
        Assert.Equal(420, res.NewThresholdMs); // 400 * 1.05
        Assert.Single(r.UpdatedThresholds.Scenarios);
        Assert.True(r.ThresholdsChanged);
    }

    [Fact]
    public void RegisteredScenarioWithZeroCeiling_IsSeeded_NotBreached()
    {
        // The shipped thresholds file lists the five flows with threshold_ms=0
        // (unseeded). The first measured baseline must seed them, not breach.
        var t = Thresholds(("list-view", 0));
        var m = Measure(("list-view", 1000));

        var r = RatchetEngine.Evaluate(t, m);

        var res = Assert.Single(r.Results);
        Assert.Equal(ScenarioOutcome.Seeded, res.Outcome);
        Assert.False(r.HasBreach);
        Assert.Equal(1050, res.NewThresholdMs); // 1000 * 1.05
        Assert.True(r.ThresholdsChanged);
    }

    [Fact]
    public void InputThresholds_AreNotMutated()
    {
        var t = Thresholds(("list-view", 1000));
        var m = Measure(("list-view", 700));

        RatchetEngine.Evaluate(t, m);

        // The passed-in object is untouched; only the returned UpdatedThresholds carries the ratchet.
        Assert.Equal(1000, t.Scenarios.Single().ThresholdMs);
    }

    [Fact]
    public void MixedRun_ClassifiesEachScenarioIndependently()
    {
        var t = Thresholds(("a", 1000), ("b", 1000), ("c", 1000));
        var m = Measure(("a", 1200), ("b", 700), ("c", 990));

        var r = RatchetEngine.Evaluate(t, m);

        Assert.Equal(ScenarioOutcome.Breached, r.Results.Single(x => x.Key == "a:sqlite:p90").Outcome);
        Assert.Equal(ScenarioOutcome.Ratcheted, r.Results.Single(x => x.Key == "b:sqlite:p90").Outcome);
        Assert.Equal(ScenarioOutcome.Held, r.Results.Single(x => x.Key == "c:sqlite:p90").Outcome);
        Assert.True(r.HasBreach);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void InvalidMeasurement_Throws(double bad)
    {
        var t = Thresholds(("a", 1000));
        var m = Measure(("a", bad));
        Assert.Throws<FormatException>(() => RatchetEngine.Evaluate(t, m));
    }

    [Fact]
    public void BreachedCeiling_IsPreservedAcrossSerialization()
    {
        var t = Thresholds(("a", 1000));
        var m = Measure(("a", 5000));
        var r = RatchetEngine.Evaluate(t, m);

        var roundTripped = RatchetThresholds.Parse(r.UpdatedThresholds.Serialize());
        Assert.Equal(1000, roundTripped.Scenarios.Single().ThresholdMs);
    }
}

public class FilingPlanTests
{
    [Fact]
    public void Breach_ProducesFilingEntry_WithDeterministicId()
    {
        var t = new RatchetThresholds
        {
            Scenarios = { new ScenarioThreshold { Flow = "list-view", Backend = "sqlite", Quantile = "p90", ThresholdMs = 1000 } },
        };
        var m = new MeasurementSet
        {
            Measurements = { new ScenarioMeasurement { Flow = "list-view", Backend = "sqlite", Quantile = "p90", ValueMs = 1500 } },
        };
        var r = RatchetEngine.Evaluate(t, m);

        var plan = FilingPlan.Build(r);

        Assert.Equal(1, plan.BreachCount);
        var entry = Assert.Single(plan.Entries);
        Assert.Equal("p5-perf-regression-list-view-sqlite-p90", entry.TaskId);
        Assert.Equal(1500, entry.MeasuredMs);
        Assert.Equal(1000, entry.ThresholdMs);
        Assert.Contains("list-view", entry.Title, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskIdFor_IsStableAndCliSafe()
    {
        var id = FilingPlan.TaskIdFor("season-listing:sqlite:p99");
        Assert.Equal("p5-perf-regression-season-listing-sqlite-p99", id);
        Assert.DoesNotContain(':', id);
        Assert.Equal(id, FilingPlan.TaskIdFor("season-listing:sqlite:p99"));
    }

    [Fact]
    public void NoBreach_ProducesEmptyPlan()
    {
        var t = new RatchetThresholds
        {
            Scenarios = { new ScenarioThreshold { Flow = "a", ThresholdMs = 1000 } },
        };
        var m = new MeasurementSet
        {
            Measurements = { new ScenarioMeasurement { Flow = "a", Backend = "sqlite", Quantile = "p90", ValueMs = 500 } },
        };
        var r = RatchetEngine.Evaluate(t, m);
        var plan = FilingPlan.Build(r);
        Assert.Equal(0, plan.BreachCount);
        Assert.Empty(plan.Entries);
    }
}

public class ModelTests
{
    [Fact]
    public void Thresholds_RoundTripThroughJson()
    {
        var t = new RatchetThresholds
        {
            ImprovementMarginRatio = 0.15,
            RatchetHeadroomRatio = 0.04,
            Scenarios = { new ScenarioThreshold { Flow = "list-view", Backend = "postgres", Quantile = "p50", ThresholdMs = 42.5 } },
        };

        var back = RatchetThresholds.Parse(t.Serialize());

        Assert.Equal(0.15, back.ImprovementMarginRatio);
        Assert.Equal(0.04, back.RatchetHeadroomRatio);
        var s = Assert.Single(back.Scenarios);
        Assert.Equal("list-view:postgres:p50", s.Key);
        Assert.Equal(42.5, s.ThresholdMs);
    }

    [Fact]
    public void Measurements_ParseFromJson()
    {
        const string json = """
        { "measurements": [ { "flow": "list-view", "backend": "sqlite", "quantile": "p90", "value_ms": 812.4 } ] }
        """;
        var set = MeasurementSet.Parse(json);
        var m = Assert.Single(set.Measurements);
        Assert.Equal("list-view:sqlite:p90", m.Key);
        Assert.Equal(812.4, m.ValueMs);
    }
}
