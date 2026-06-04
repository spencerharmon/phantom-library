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
    Error,
}

public sealed record MaterialisationOutcome
{
    public required MaterialisationStatus Status { get; init; }
    public string? FusePath { get; init; }
    public string? StubPath { get; init; }
    public string? Error { get; init; }
}

public interface IMaterialiser
{
    System.Threading.Tasks.Task<MaterialisationOutcome> MaterialiseAsync(
        System.Guid jellyfinItemId,
        MaterialiseTrigger trigger,
        System.Threading.CancellationToken ct);
}
