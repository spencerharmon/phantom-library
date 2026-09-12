using System.Globalization;

namespace Phantom.Tools.VersionBump;

/// <summary>
/// CLI entrypoint for the plugin version auto-bump tool.
///
///   phantom-version-bump --repo &lt;dir&gt; [--bump | --set A.B.C[.D]]
///        [--build-yaml &lt;path&gt;] [--csproj &lt;path&gt;] [--check] [--quiet]
///
///   --repo &lt;dir&gt;     repo root (default: cwd). build.yaml + csproj are resolved
///                    under it unless overridden.
///   --bump           auto-increment the revision (4th part) — the default op.
///   --set A.B.C[.D]  set an explicit target; must be >= current (monotonic).
///   --build-yaml     override the build.yaml path (default &lt;repo&gt;/build.yaml).
///   --csproj         override the plugin csproj path
///                    (default &lt;repo&gt;/src/Jellyfin.Plugin.PhantomLibrary/
///                     Jellyfin.Plugin.PhantomLibrary.csproj).
///   --check          do not write; exit 0 if versions are already in lockstep at
///                    the target the op would pick, non-zero otherwise.
///   --quiet          suppress the human line on stdout (the new version is still
///                    printed as the sole stdout token for CI capture).
///
/// Exit codes: 0 = success (files updated, or already correct); 2 = usage/IO/parse
/// error; 3 = --check found a mismatch.
///
/// meta.json is NOT written here: install.sh derives it from build.yaml's version,
/// so keeping build.yaml canonical keeps meta.json in lockstep by construction.
/// </summary>
public static class Program
{
    private static readonly string DefaultCsprojRel =
        Path.Combine("src", "Jellyfin.Plugin.PhantomLibrary", "Jellyfin.Plugin.PhantomLibrary.csproj");

    public static int Main(string[] args)
    {
        try
        {
            var opts = Options.Parse(args);
            if (opts.ShowHelp)
            {
                Console.Out.WriteLine(Options.Usage);
                return 0;
            }

            var buildYamlPath = opts.BuildYamlPath ?? Path.Combine(opts.Repo, "build.yaml");
            var csprojPath = opts.CsprojPath ?? Path.Combine(opts.Repo, DefaultCsprojRel);

            if (!File.Exists(buildYamlPath))
            {
                return Fail($"build.yaml not found at {buildYamlPath}");
            }

            if (!File.Exists(csprojPath))
            {
                return Fail($"csproj not found at {csprojPath}");
            }

            var buildYaml = File.ReadAllText(buildYamlPath);
            var csproj = File.ReadAllText(csprojPath);

            var current = VersionRewriter.ReadBuildYamlVersion(buildYaml);

            var plan = opts.SetTarget is { } set
                ? BumpPlanner.PlanSet(current, set)
                : BumpPlanner.PlanAutoBump(current);

            var newBuildYaml = VersionRewriter.SetBuildYamlVersion(buildYaml, plan.Target);
            var newCsproj = VersionRewriter.SetCsprojVersion(csproj, plan.Target);

            if (opts.Check)
            {
                var yamlOk = string.Equals(newBuildYaml, buildYaml, StringComparison.Ordinal);
                var csprojOk = string.Equals(newCsproj, csproj, StringComparison.Ordinal);
                if (yamlOk && csprojOk)
                {
                    Emit(opts, plan.Target, "in lockstep");
                    return 0;
                }

                Console.Error.WriteLine(
                    $"version mismatch: target {plan.Target.ToFourPart()} not written to " +
                    (yamlOk ? "" : "build.yaml ") + (csprojOk ? "" : "csproj"));
                return 3;
            }

            File.WriteAllText(buildYamlPath, newBuildYaml);
            File.WriteAllText(csprojPath, newCsproj);

            Emit(opts, plan.Target, plan.Changed ? $"bumped {plan.Current.ToFourPart()} -> {plan.Target.ToFourPart()}" : "already at target");
            return 0;
        }
        catch (Options.UsageException ux)
        {
            Console.Error.WriteLine(ux.Message);
            Console.Error.WriteLine(Options.Usage);
            return 2;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or IOException or ArgumentException)
        {
            return Fail(ex.Message);
        }
    }

    private static void Emit(Options opts, PluginVersion target, string note)
    {
        if (!opts.Quiet)
        {
            Console.Error.WriteLine($"phantom-version-bump: {note}");
        }

        // The new version is the sole stdout token, for CI capture.
        Console.Out.WriteLine(target.ToFourPart());
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine($"phantom-version-bump: {message}");
        return 2;
    }

    internal sealed class Options
    {
        public const string Usage =
            "usage: phantom-version-bump --repo <dir> [--bump | --set A.B.C[.D]]\n" +
            "                            [--build-yaml <path>] [--csproj <path>] [--check] [--quiet]";

        public string Repo { get; private set; } = Directory.GetCurrentDirectory();
        public string? BuildYamlPath { get; private set; }
        public string? CsprojPath { get; private set; }
        public PluginVersion? SetTarget { get; private set; }
        public bool Check { get; private set; }
        public bool Quiet { get; private set; }
        public bool ShowHelp { get; private set; }

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                switch (a)
                {
                    case "-h":
                    case "--help":
                        o.ShowHelp = true;
                        return o;
                    case "--repo":
                        o.Repo = Next(args, ref i, "--repo");
                        break;
                    case "--build-yaml":
                        o.BuildYamlPath = Next(args, ref i, "--build-yaml");
                        break;
                    case "--csproj":
                        o.CsprojPath = Next(args, ref i, "--csproj");
                        break;
                    case "--set":
                        o.SetTarget = PluginVersion.Parse(Next(args, ref i, "--set"));
                        break;
                    case "--bump":
                        // auto-increment revision; this is the default, flag is explicit-intent.
                        break;
                    case "--check":
                        o.Check = true;
                        break;
                    case "--quiet":
                        o.Quiet = true;
                        break;
                    default:
                        throw new UsageException($"unknown argument: {a}");
                }
            }

            return o;
        }

        private static string Next(string[] args, ref int i, string flag)
        {
            if (i + 1 >= args.Length)
            {
                throw new UsageException($"{flag} requires a value");
            }

            return args[++i];
        }

        public sealed class UsageException(string message) : Exception(message);
    }
}
