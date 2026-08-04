using Phantom.Perf.LoadtimeCompare;
using Phantom.Perf.RatchetGuard;
using Xunit;

namespace Phantom.Perf.LoadtimeCompare.Tests;

public class LoadtimeComparerTests
{
    private static ScenarioMeasurement M(string flow, string backend, double ms, string quantile = "p90") =>
        new() { Flow = flow, Backend = backend, Quantile = quantile, ValueMs = ms };

    private static MeasurementSet Set(params ScenarioMeasurement[] ms) =>
        new() { Measurements = ms.ToList() };

    [Fact]
    public void FasterPostgres_IsImproved_WithMeasuredDelta()
    {
        var baseline = Set(M("list_view_load", "sqlite", 100));
        var after = Set(M("list_view_load", "postgres", 70));

        var report = LoadtimeComparer.Compare(baseline, after);

        var c = Assert.Single(report.Comparisons);
        Assert.Equal(ComparisonDirection.Improved, c.Direction);
        Assert.Equal(-30, c.DeltaMs);
        Assert.Equal(-30, c.DeltaPercent);
        Assert.Equal("sqlite", c.BaselineBackend);
        Assert.Equal("postgres", c.AfterBackend);
        Assert.False(report.HasRegression);
    }

    [Fact]
    public void SlowerPostgres_IsRegressed_NeverAssumedAGain()
    {
        var baseline = Set(M("season_listing", "sqlite", 80));
        var after = Set(M("season_listing", "postgres", 120));

        var report = LoadtimeComparer.Compare(baseline, after);

        var c = Assert.Single(report.Comparisons);
        Assert.Equal(ComparisonDirection.Regressed, c.Direction);
        Assert.Equal(40, c.DeltaMs);
        Assert.Equal(50, c.DeltaPercent);
        Assert.True(report.HasRegression);
        Assert.Single(report.Regressions);
    }

    [Fact]
    public void WithinNeutralBand_IsNeutral()
    {
        var baseline = Set(M("episode_listing", "sqlite", 100));
        var after = Set(M("episode_listing", "postgres", 103)); // 3% < 5% band

        var report = LoadtimeComparer.Compare(baseline, after);

        Assert.Equal(ComparisonDirection.Neutral, Assert.Single(report.Comparisons).Direction);
        Assert.False(report.HasRegression);
    }

    [Fact]
    public void NeutralBand_BoundaryIsInclusive()
    {
        var baseline = Set(M("f", "sqlite", 100));
        var after = Set(M("f", "postgres", 105)); // exactly +5% == band edge => neutral

        var report = LoadtimeComparer.Compare(baseline, after, neutralBandRatio: 0.05);

        Assert.Equal(ComparisonDirection.Neutral, Assert.Single(report.Comparisons).Direction);
    }

    [Fact]
    public void ScenarioOnlyInOneRun_IsReportedUnpaired_NotDropped()
    {
        var baseline = Set(M("list_view_load", "sqlite", 100), M("only_before", "sqlite", 50));
        var after = Set(M("list_view_load", "postgres", 90), M("only_after", "postgres", 40));

        var report = LoadtimeComparer.Compare(baseline, after);

        Assert.Single(report.Comparisons); // list_view_load only
        Assert.Equal(2, report.Unpaired.Count);
        Assert.Contains(report.Unpaired, u => u.Flow == "only_before" && u.Side == "baseline");
        Assert.Contains(report.Unpaired, u => u.Flow == "only_after" && u.Side == "after");
    }

    [Fact]
    public void MatchesAcrossDifferentBackends_ByFlowAndQuantile()
    {
        // The whole point: the backend differs between sides, so matching ignores it.
        var baseline = Set(M("f", "sqlite", 100, "p50"), M("f", "sqlite", 200, "p90"));
        var after = Set(M("f", "postgres", 80, "p50"), M("f", "postgres", 190, "p90"));

        var report = LoadtimeComparer.Compare(baseline, after);

        Assert.Equal(2, report.Comparisons.Count);
        Assert.Empty(report.Unpaired);
    }

    [Fact]
    public void InvalidMeasurement_Throws()
    {
        var baseline = Set(M("f", "sqlite", double.NaN));
        var after = Set(M("f", "postgres", 100));
        Assert.Throws<FormatException>(() => LoadtimeComparer.Compare(baseline, after));
    }

    [Fact]
    public void DuplicateFlowQuantile_Throws()
    {
        var baseline = Set(M("f", "sqlite", 100), M("f", "sqlite", 110));
        var after = Set(M("f", "postgres", 90));
        Assert.Throws<FormatException>(() => LoadtimeComparer.Compare(baseline, after));
    }

    [Fact]
    public void NegativeNeutralBand_Throws()
    {
        var baseline = Set(M("f", "sqlite", 100));
        var after = Set(M("f", "postgres", 90));
        Assert.Throws<FormatException>(() => LoadtimeComparer.Compare(baseline, after, neutralBandRatio: -0.1));
    }
}

public class ThresholdFeedTests
{
    private static RatchetThresholds SqliteOnly() => new()
    {
        Scenarios =
        {
            new ScenarioThreshold { Flow = "list_view_load", Backend = "sqlite", Quantile = "p90", ThresholdMs = 0 },
        },
    };

    private static MeasurementSet After(params (string flow, double ms)[] flows) => new()
    {
        Measurements = flows
            .Select(f => new ScenarioMeasurement { Flow = f.flow, Backend = "postgres", Quantile = "p90", ValueMs = f.ms })
            .ToList(),
    };

    [Fact]
    public void Unseeded_AddsPostgresScenariosAtZero_KeepsFabricatedNumbersOutOfGit()
    {
        var merged = ThresholdFeed.MergeAfter(SqliteOnly(), After(("list_view_load", 90)), seed: false);

        var pg = Assert.Single(merged.Scenarios, s => s.Backend == "postgres");
        Assert.Equal(0, pg.ThresholdMs);
        // original preserved
        Assert.Contains(merged.Scenarios, s => s.Backend == "sqlite" && s.ThresholdMs == 0);
    }

    [Fact]
    public void Seed_SeedsCeilingFromMeasuredValuePlusHeadroom()
    {
        var thresholds = SqliteOnly();
        thresholds.RatchetHeadroomRatio = 0.05;

        var merged = ThresholdFeed.MergeAfter(thresholds, After(("list_view_load", 100)), seed: true);

        var pg = Assert.Single(merged.Scenarios, s => s.Backend == "postgres");
        Assert.Equal(105, pg.ThresholdMs); // 100 * 1.05
    }

    [Fact]
    public void Merge_DoesNotMutateInput()
    {
        var input = SqliteOnly();
        var before = input.Scenarios.Count;
        ThresholdFeed.MergeAfter(input, After(("list_view_load", 90), ("season_listing", 50)), seed: true);
        Assert.Equal(before, input.Scenarios.Count);
    }

    [Fact]
    public void Merge_NeverOverwritesAnAlreadySeededPostgresCeiling()
    {
        var thresholds = new RatchetThresholds
        {
            Scenarios =
            {
                new ScenarioThreshold { Flow = "list_view_load", Backend = "postgres", Quantile = "p90", ThresholdMs = 42 },
            },
        };

        var merged = ThresholdFeed.MergeAfter(thresholds, After(("list_view_load", 10)), seed: true);

        Assert.Equal(42, Assert.Single(merged.Scenarios).ThresholdMs);
    }

    [Fact]
    public void FedThresholds_RoundTripThroughJson()
    {
        var merged = ThresholdFeed.MergeAfter(SqliteOnly(), After(("list_view_load", 90)), seed: false);
        var reparsed = RatchetThresholds.Parse(merged.Serialize());
        Assert.Equal(merged.Scenarios.Count, reparsed.Scenarios.Count);
        Assert.Contains(reparsed.Scenarios, s => s.Backend == "postgres");
    }

    [Fact]
    public void InvalidAfterMeasurement_Throws()
    {
        Assert.Throws<FormatException>(() =>
            ThresholdFeed.MergeAfter(SqliteOnly(), After(("list_view_load", double.PositiveInfinity)), seed: true));
    }
}
