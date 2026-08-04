using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Phantom.Perf.RatchetGuard;

namespace Phantom.Perf.LoadtimeCompare;

/// <summary>
/// CLI entrypoint for the P5 Postgres load-time before/after comparison.
///
///   phantom-loadtime-compare --baseline &lt;sqlite.json&gt; --after &lt;postgres.json&gt;
///        [--neutral-band &lt;ratio&gt;] [--json &lt;file&gt;] [--fail-on-regression]
///        [--thresholds &lt;file&gt; [--seed] [--apply]]
///
/// Reads the SQLite baseline and the Postgres-backed "after" MeasurementSet runs, computes the
/// measured per-flow delta (never assuming a gain), and — when --thresholds is given — feeds the
/// Postgres "after" scenarios into the ratchet guard's threshold table.
///
/// Exit codes:
///   0 = ok (comparison rendered; thresholds fed if asked)
///   3 = at least one flow REGRESSED and --fail-on-regression was given
///   2 = usage / IO / parse error
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

            var baseline = MeasurementSet.Load(opts.BaselinePath);
            var after = MeasurementSet.Load(opts.AfterPath);
            var report = LoadtimeComparer.Compare(baseline, after, opts.NeutralBandRatio);

            Console.Out.Write(ReportRenderer.RenderText(report));

            if (opts.JsonPath is { } jsonPath)
            {
                File.WriteAllText(jsonPath, ReportRenderer.RenderJson(report) + "\n");
                Console.Error.WriteLine($"loadtime-compare: comparison JSON written to {jsonPath}");
            }

            if (opts.ThresholdsPath is { } thresholdsPath)
            {
                var thresholds = RatchetThresholds.Load(thresholdsPath);
                var merged = ThresholdFeed.MergeAfter(thresholds, after, opts.Seed);
                if (opts.Apply)
                {
                    merged.Save(thresholdsPath);
                    Console.Out.WriteLine(
                        $"loadtime-compare: fed Postgres 'after' scenarios into {thresholdsPath} "
                        + (opts.Seed ? "(ceilings seeded from measured values)" : "(added unseeded)"));
                }
                else
                {
                    Console.Out.WriteLine("loadtime-compare: merged thresholds (dry run; pass --apply to persist):");
                    Console.Out.WriteLine(merged.Serialize());
                }
            }

            if (opts.FailOnRegression && report.HasRegression)
            {
                Console.Error.WriteLine(
                    $"loadtime-compare: {report.Regressions.Count()} flow(s) regressed against the SQLite baseline");
                return 3;
            }

            return 0;
        }
        catch (CliUsageException ex)
        {
            Console.Error.WriteLine($"loadtime-compare: {ex.Message}");
            Console.Error.WriteLine(CliOptions.Usage);
            return 2;
        }
        catch (Exception ex) when (ex is IOException or FormatException or JsonException)
        {
            Console.Error.WriteLine($"loadtime-compare: {ex.Message}");
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
    public string BaselinePath { get; private set; } = string.Empty;
    public string AfterPath { get; private set; } = string.Empty;
    public string? JsonPath { get; private set; }
    public string? ThresholdsPath { get; private set; }
    public double NeutralBandRatio { get; private set; } = 0.05;
    public bool Seed { get; private set; }
    public bool Apply { get; private set; }
    public bool FailOnRegression { get; private set; }
    public bool ShowHelp { get; private set; }

    public const string Usage =
        "usage: phantom-loadtime-compare --baseline <sqlite.json> --after <postgres.json>\n"
        + "         [--neutral-band <ratio>] [--json <file>] [--fail-on-regression]\n"
        + "         [--thresholds <ratchet-thresholds.json> [--seed] [--apply]]\n"
        + "  --baseline           SQLite baseline MeasurementSet JSON (the 'before')\n"
        + "  --after              Postgres-backed MeasurementSet JSON (the 'after')\n"
        + "  --neutral-band       fraction within which a change is noise, not a gain/loss (default 0.05)\n"
        + "  --json               write the full comparison as JSON to this path\n"
        + "  --fail-on-regression exit 3 if any flow is slower on Postgres than the SQLite baseline\n"
        + "  --thresholds         feed the Postgres 'after' scenarios into this ratchet thresholds file\n"
        + "  --seed               seed the fed ceilings from the measured values (else add unseeded, =0)\n"
        + "  --apply              persist the fed thresholds (else dry-run print)\n"
        + "exit 0 = ok, 3 = regression (with --fail-on-regression), 2 = error";

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
                case "--baseline":
                    o.BaselinePath = RequireValue(args, ref i);
                    break;
                case "--after":
                    o.AfterPath = RequireValue(args, ref i);
                    break;
                case "--json":
                    o.JsonPath = RequireValue(args, ref i);
                    break;
                case "--thresholds":
                    o.ThresholdsPath = RequireValue(args, ref i);
                    break;
                case "--neutral-band":
                    o.NeutralBandRatio = ParseRatio(RequireValue(args, ref i));
                    break;
                case "--seed":
                    o.Seed = true;
                    break;
                case "--apply":
                    o.Apply = true;
                    break;
                case "--fail-on-regression":
                    o.FailOnRegression = true;
                    break;
                default:
                    throw new CliUsageException($"unknown argument '{args[i]}'");
            }
        }

        if (string.IsNullOrEmpty(o.BaselinePath))
        {
            throw new CliUsageException("--baseline is required");
        }

        if (string.IsNullOrEmpty(o.AfterPath))
        {
            throw new CliUsageException("--after is required");
        }

        return o;
    }

    private static double ParseRatio(string raw) =>
        double.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var v)
            ? v
            : throw new CliUsageException($"--neutral-band expects a number, got '{raw}'");

    private static string RequireValue(string[] args, ref int i)
    {
        if (i + 1 >= args.Length)
        {
            throw new CliUsageException($"'{args[i]}' requires a value");
        }

        return args[++i];
    }
}

public static class ReportRenderer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string RenderText(ComparisonReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("== phantom load-time before/after (SQLite -> Postgres) ==");
        foreach (var c in report.Comparisons)
        {
            var tag = c.Direction switch
            {
                ComparisonDirection.Improved => "IMPROVED ",
                ComparisonDirection.Regressed => "REGRESSED",
                _ => "neutral  ",
            };
            var sign = c.DeltaMs >= 0 ? "+" : string.Empty;
            sb.Append(tag).Append("  ").Append(c.Flow).Append(':').Append(c.Quantile)
                .Append("  ").Append(c.BaselineMs.ToString("0.###")).Append("ms -> ")
                .Append(c.AfterMs.ToString("0.###")).Append("ms  (")
                .Append(sign).Append(c.DeltaMs.ToString("0.###")).Append("ms, ")
                .Append(sign).Append(c.DeltaPercent.ToString("0.#")).AppendLine("%)");
        }

        foreach (var u in report.Unpaired)
        {
            sb.Append("UNPAIRED   ").Append(u.Flow).Append(':').Append(u.Quantile)
                .Append("  only in ").Append(u.Side).Append(" (").Append(u.Backend).Append(", ")
                .Append(u.ValueMs.ToString("0.###")).AppendLine("ms) — no delta computed");
        }

        sb.AppendLine(
            $"summary: {report.Improvements.Count()} improved, {report.Regressions.Count()} regressed, "
            + $"{report.Comparisons.Count(c => c.Direction == ComparisonDirection.Neutral)} neutral, "
            + $"{report.Unpaired.Count} unpaired");
        return sb.ToString();
    }

    public static string RenderJson(ComparisonReport report) => JsonSerializer.Serialize(
        new
        {
            comparisons = report.Comparisons.Select(c => new
            {
                flow = c.Flow,
                quantile = c.Quantile,
                baseline_backend = c.BaselineBackend,
                after_backend = c.AfterBackend,
                baseline_ms = c.BaselineMs,
                after_ms = c.AfterMs,
                delta_ms = c.DeltaMs,
                delta_percent = c.DeltaPercent,
                direction = c.Direction.ToString(),
            }),
            unpaired = report.Unpaired.Select(u => new
            {
                flow = u.Flow,
                quantile = u.Quantile,
                backend = u.Backend,
                value_ms = u.ValueMs,
                side = u.Side,
            }),
            summary = new
            {
                improved = report.Improvements.Count(),
                regressed = report.Regressions.Count(),
                neutral = report.Comparisons.Count(c => c.Direction == ComparisonDirection.Neutral),
                unpaired = report.Unpaired.Count,
            },
        },
        JsonOptions);
}
