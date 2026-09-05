using System.Text;
using System.Text.Json;

namespace Phantom.Perf.RatchetGuard;

/// <summary>
/// CLI entrypoint for the ratcheting regression guard.
///
///   phantom-ratchet-guard --thresholds &lt;file&gt; --measurements &lt;file&gt;
///        [--apply] [--file-plan &lt;file&gt;]
///
/// Reads the recorded per-scenario ceilings and a perf run's measurements,
/// ratchets, and reports. Exit codes:
///   0 = no breach (thresholds possibly tightened when --apply given)
///   3 = at least one scenario BREACHED (a regression); a filing plan is emitted
///   2 = usage / IO / parse error
///
/// On breach it writes a filing plan (JSON, to --file-plan or stdout) describing the
/// beehive performance-review task(s) to file. The bash wrapper (ratchet-guard.sh)
/// consumes that plan and runs `beehive task add` + `beehive task block`, so a breach
/// is never silently accepted.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var opts = CliOptions.Parse(args);
            if (opts.ShowHelp)
            {
                Console.Out.WriteLine(CliOptions.Usage);
                return 0;
            }

            var thresholds = RatchetThresholds.Load(opts.ThresholdsPath);
            var measurements = MeasurementSet.Load(opts.MeasurementsPath);
            var result = RatchetEngine.Evaluate(thresholds, measurements);

            Console.Out.Write(Report.Render(result));

            // Persist tightened/seeded thresholds when asked. A breach never loosens,
            // so applying on a breach still only ever writes tighter (or unchanged) ceilings.
            if (opts.Apply && result.ThresholdsChanged)
            {
                result.UpdatedThresholds.Save(opts.ThresholdsPath);
                Console.Out.WriteLine($"ratchet: wrote updated thresholds to {opts.ThresholdsPath}");
            }

            if (result.HasBreach)
            {
                var plan = FilingPlan.Build(result, opts.TaskIdPrefix);
                var planJson = JsonSerializer.Serialize(plan, FilingPlan.SerializerOptions);
                if (opts.FilePlanPath is { } planPath)
                {
                    File.WriteAllText(planPath, planJson + "\n");
                    Console.Error.WriteLine($"ratchet: breach filing plan written to {planPath}");
                }
                else
                {
                    Console.Out.WriteLine(planJson);
                }

                return 3;
            }

            return 0;
        }
        catch (CliUsageException ex)
        {
            Console.Error.WriteLine($"ratchet-guard: {ex.Message}");
            Console.Error.WriteLine(CliOptions.Usage);
            return 2;
        }
        catch (Exception ex) when (ex is IOException or FormatException or JsonException)
        {
            Console.Error.WriteLine($"ratchet-guard: {ex.Message}");
            return 2;
        }
    }
}

public sealed class CliUsageException : Exception
{
    public CliUsageException(string message) : base(message) { }
}

public sealed class CliOptions
{
    public string ThresholdsPath { get; private set; } = string.Empty;
    public string MeasurementsPath { get; private set; } = string.Empty;
    public string? FilePlanPath { get; private set; }
    public bool Apply { get; private set; }
    public bool ShowHelp { get; private set; }
    public string TaskIdPrefix { get; private set; } = "p5-perf-regression";

    public const string Usage =
        "usage: phantom-ratchet-guard --thresholds <file> --measurements <file> "
        + "[--apply] [--file-plan <file>] [--task-prefix <prefix>]\n"
        + "  --thresholds    ratchet threshold JSON (read; rewritten only with --apply)\n"
        + "  --measurements  perf-run measurement JSON to guard against the thresholds\n"
        + "  --apply         persist tightened/seeded thresholds back to the thresholds file\n"
        + "  --file-plan     write the breach filing plan JSON to this path instead of stdout\n"
        + "  --task-prefix   task-id prefix for filed breach tasks (default p5-perf-regression)\n"
        + "exit 0 = ok, 3 = breach (regression), 2 = error";

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-h" or "--help":
                    o.ShowHelp = true;
                    return o;
                case "--thresholds":
                    o.ThresholdsPath = RequireValue(args, ref i);
                    break;
                case "--measurements":
                    o.MeasurementsPath = RequireValue(args, ref i);
                    break;
                case "--file-plan":
                    o.FilePlanPath = RequireValue(args, ref i);
                    break;
                case "--task-prefix":
                    o.TaskIdPrefix = RequireValue(args, ref i);
                    break;
                case "--apply":
                    o.Apply = true;
                    break;
                default:
                    throw new CliUsageException($"unknown argument '{args[i]}'");
            }
        }

        if (string.IsNullOrEmpty(o.ThresholdsPath))
        {
            throw new CliUsageException("--thresholds is required");
        }

        if (string.IsNullOrEmpty(o.MeasurementsPath))
        {
            throw new CliUsageException("--measurements is required");
        }

        return o;
    }

    private static string RequireValue(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
        {
            throw new CliUsageException($"'{args[i]}' requires a value");
        }

        return args[++i];
    }
}

public static class Report
{
    public static string Render(GuardResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== phantom ratcheting regression guard ==");
        foreach (var r in result.Results.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            var tag = r.Outcome switch
            {
                ScenarioOutcome.Breached => "BREACH ",
                ScenarioOutcome.Ratcheted => "RATCHET",
                ScenarioOutcome.Seeded => "SEED   ",
                _ => "hold   ",
            };
            sb.Append(tag).Append("  ").Append(r.Key).Append("  ").AppendLine(r.Detail);
        }

        sb.AppendLine(
            $"summary: {result.Breaches.Count()} breach, {result.Ratchets.Count()} ratchet, "
            + $"{result.Seeded.Count()} seed, "
            + $"{result.Results.Count(x => x.Outcome == ScenarioOutcome.Held)} held");
        return sb.ToString();
    }
}
