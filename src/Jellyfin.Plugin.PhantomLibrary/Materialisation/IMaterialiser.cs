using System;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.PhantomLibrary.Materialisation;

public enum MaterialiseTrigger
{
    Favourite,
    Play,
    Autopilot,
    PreResolve,
    Manual,
}

public enum MaterialisationStatus
{
    Success,
    Unavailable,
    Duplicate,
    AlreadyInProgress,
    Error,
}

public sealed record MaterialisationOutcome
{
    public required MaterialisationStatus Status { get; init; }
    public string? FusePath { get; init; }
    public string? StubPath { get; init; }
    public string? Error { get; init; }

    public static MaterialisationOutcome Success(string fusePath, string stubPath)
        => new()
        {
            Status = MaterialisationStatus.Success,
            FusePath = fusePath,
            StubPath = stubPath,
        };

    public static MaterialisationOutcome Duplicate { get; } = new()
    {
        Status = MaterialisationStatus.Duplicate,
    };

    public static MaterialisationOutcome AlreadyInProgress { get; } = new()
    {
        Status = MaterialisationStatus.AlreadyInProgress,
    };

    public static MaterialisationOutcome ErrorResult(string message)
        => new()
        {
            Status = MaterialisationStatus.Error,
            Error = message,
        };
}

public enum MaterialisationLifecyclePhase
{
    Queued,
    Started,
    Finished,
}

public sealed record MaterialisationLifecycleEvent(
    Guid ItemId,
    MaterialisationLifecyclePhase Phase,
    MaterialisationOutcome? Outcome);

/// <summary>
/// Materialise a phantom channel item into a real gostream-backed
/// MediaSource. Two entry points:
/// <list type="bullet">
///   <item>The tuple form (<see cref="MaterialiseAsync(int, string, int?, int?, MaterialiseTrigger, CancellationToken)"/>)
///   takes channel-arch primary keys directly; preferred for plugin-internal
///   callers that already know the tmdb id (autopilot, queue, listeners).</item>
///   <item>The Guid form
///   (<see cref="MaterialiseAsync(Guid, MaterialiseTrigger, CancellationToken)"/>)
///   resolves a Jellyfin BaseItem into channel-id form and dispatches to
///   the tuple path. Retained for the existing
///   <c>MaterialisationQueue</c>-driven user-trigger flow.</item>
/// </list>
/// </summary>
public interface IMaterialiser
{
    event EventHandler<MaterialisationLifecycleEvent>? LifecycleChanged;

    Task<MaterialisationOutcome> MaterialiseAsync(
        Guid jellyfinItemId,
        MaterialiseTrigger trigger,
        CancellationToken ct);

    Task<MaterialisationOutcome> MaterialiseAsync(
        int tmdbId,
        string type,
        int? season,
        int? episode,
        MaterialiseTrigger trigger,
        CancellationToken ct);
}
