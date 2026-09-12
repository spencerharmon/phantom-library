using Phantom.Tools.VersionBump;
using Xunit;

namespace PhantomVersionBump.Tests;

public class BumpPlannerTests
{
    [Fact]
    public void PlanAutoBump_IncrementsRevision_AndAlwaysChanges()
    {
        var current = new PluginVersion(0, 5, 5, 0);
        var plan = BumpPlanner.PlanAutoBump(current);

        Assert.Equal(current, plan.Current);
        Assert.Equal(new PluginVersion(0, 5, 5, 1), plan.Target);
        Assert.True(plan.Changed);
    }

    [Fact]
    public void PlanAutoBump_TargetIsStrictlyGreaterThanCurrent()
    {
        var current = new PluginVersion(1, 2, 3, 9);
        var plan = BumpPlanner.PlanAutoBump(current);
        Assert.True(plan.Target > plan.Current);
    }

    [Fact]
    public void PlanSet_GreaterTarget_Changes()
    {
        var current = new PluginVersion(1, 0, 0, 0);
        var target = new PluginVersion(1, 1, 0, 0);
        var plan = BumpPlanner.PlanSet(current, target);

        Assert.Equal(target, plan.Target);
        Assert.True(plan.Changed);
    }

    [Fact]
    public void PlanSet_EqualTarget_IsNoOp()
    {
        var current = new PluginVersion(1, 0, 0, 0);
        var plan = BumpPlanner.PlanSet(current, current);

        Assert.Equal(current, plan.Target);
        Assert.False(plan.Changed);
    }

    [Fact]
    public void PlanSet_LowerTarget_ThrowsMonotonicGuard()
    {
        var current = new PluginVersion(1, 0, 0, 0);
        var target = new PluginVersion(0, 9, 0, 0);

        Assert.Throws<InvalidOperationException>(() => BumpPlanner.PlanSet(current, target));
    }
}
