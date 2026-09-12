namespace Phantom.Tools.VersionBump;

/// <summary>
/// Pure decision layer: given the CURRENT build.yaml version and the desired
/// operation, compute the TARGET version and whether a write is needed. Kept
/// side-effect-free so the monotonicity / idempotency rules are unit-tested.
/// </summary>
public static class BumpPlanner
{
    /// <summary>
    /// Decide the target version for a <c>--bump</c> (auto-increment revision)
    /// operation. Idempotent per commit is the caller's concern; here we only
    /// guarantee strict monotonic increase from <paramref name="current"/>.
    /// </summary>
    public static BumpPlan PlanAutoBump(PluginVersion current)
    {
        var target = current.BumpRevision();
        return new BumpPlan(current, target, Changed: true);
    }

    /// <summary>
    /// Decide the target for an explicit <c>--set X.Y.Z[.W]</c>. Refuses a
    /// target that is not strictly greater than the current version (monotonic
    /// guard) UNLESS it is exactly equal — an equal target is a no-op (idempotent
    /// re-run), never an error.
    /// </summary>
    public static BumpPlan PlanSet(PluginVersion current, PluginVersion target)
    {
        if (target == current)
        {
            return new BumpPlan(current, target, Changed: false);
        }

        if (target < current)
        {
            throw new InvalidOperationException(
                $"refusing non-monotonic version: target {target.ToFourPart()} < current {current.ToFourPart()}");
        }

        return new BumpPlan(current, target, Changed: true);
    }
}

/// <summary>The result of planning a bump: what to write, and whether anything changed.</summary>
public readonly record struct BumpPlan(PluginVersion Current, PluginVersion Target, bool Changed);
