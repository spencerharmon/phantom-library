using System.Text.Json;
using System.Text.Json.Serialization;

namespace Phantom.Perf.RatchetGuard;

/// <summary>
/// One beehive performance-review task to file for a breached scenario, plus the
/// dependency edge that blocks the source guard task on it. The bash wrapper turns
/// each entry into `beehive task add` + `beehive task block` so the swarm re-examines
/// the regression instead of the guard silently swallowing it.
/// </summary>
public sealed class FilingEntry
{
    [JsonPropertyName("task_id")]
    public required string TaskId { get; init; }

    [JsonPropertyName("scenario")]
    public required string Scenario { get; init; }

    [JsonPropertyName("measured_ms")]
    public required double MeasuredMs { get; init; }

    [JsonPropertyName("threshold_ms")]
    public required double ThresholdMs { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("body")]
    public required string Body { get; init; }
}

public sealed class FilingPlan
{
    [JsonPropertyName("breach_count")]
    public required int BreachCount { get; init; }

    [JsonPropertyName("entries")]
    public required IReadOnlyList<FilingEntry> Entries { get; init; }

    public static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public static FilingPlan Build(GuardResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var entries = result.Breaches
            .OrderBy(b => b.Key, StringComparer.Ordinal)
            .Select(b => new FilingEntry
            {
                TaskId = TaskIdFor(b.Key),
                Scenario = b.Key,
                MeasuredMs = b.MeasuredMs,
                ThresholdMs = b.PreviousThresholdMs,
                Title = $"perf regression: {b.Key} exceeded ratchet ceiling",
                Body =
                    $"Scenario `{b.Key}` measured {b.MeasuredMs:0.###}ms against a ratchet "
                    + $"ceiling of {b.PreviousThresholdMs:0.###}ms ({b.Detail}). The ratcheting "
                    + "guard refuses to loosen the ceiling; investigate and either fix the "
                    + "regression or justify a new baseline before the guard can pass again.",
            })
            .ToList();

        return new FilingPlan { BreachCount = entries.Count, Entries = entries };
    }

    /// <summary>
    /// Deterministic, filesystem/CLI-safe task id for a breached scenario, so re-running
    /// the guard on the same standing regression targets the SAME task id (the wrapper's
    /// `task add` is idempotent-by-id) rather than filing a duplicate each run.
    /// </summary>
    public static string TaskIdFor(string scenarioKey)
    {
        ArgumentNullException.ThrowIfNull(scenarioKey);
        var chars = scenarioKey
            .Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-')
            .ToArray();
        var slug = new string(chars).Trim('-');
        while (slug.Contains("--", StringComparison.Ordinal))
        {
            slug = slug.Replace("--", "-", StringComparison.Ordinal);
        }

        return $"p5-perf-regression-{slug}";
    }
}
