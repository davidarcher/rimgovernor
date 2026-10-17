using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Commands.ColonyReview;

/// <summary>
/// Turns a colony review recording (acceptance colony-review's result.json and JPEGs) into a static HTML report a player can skim, and
/// indexes reports into a site.
/// <code>
/// RimGovernor.cmd colonyreview report -in dir -out dir [-baselines dir] [-meta key=value ...]
/// RimGovernor.cmd colonyreview site -runs dir -out dir
/// </code>
/// <c>report</c> copies the images, writes index.html and run.json (meta plus the summary); <c>site</c> writes index.html at
/// -out listing every run dir under -runs that holds a run.json, newest first, and copies them beside it.
/// </summary>
internal static class ColonyReviewCommand
{
    private const string Usage =
        "usage: colonyreview report -in <dir> -out <dir> [-baselines <dir>] [-meta k=v ...] | site -runs <dir> -out <dir>";

    public static Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (args.Length == 0)
        {
            throw new UsageException(Usage);
        }
        var rest = args[1..];
        switch (args[0])
        {
            case "-h" or "-help" or "--help":
                Log.Info(Usage);
                Log.Info("  report  -in recorder directory (result.json and review/), -out run report directory to write,");
                Log.Info("          -baselines directory of earlier run reports (the newest with the same seed is the score baseline),");
                Log.Info("          -meta key=value shown in the report header and saved in run.json (repeatable)");
                Log.Info("  site    -runs directory holding run report directories, -out site directory to write");
                return Task.FromResult(0);
            case "report":
            {
                var meta = new SortedDictionary<string, string>(StringComparer.Ordinal);
                var flags = Flags.Parse(ExtractMeta(rest, meta), new HashSet<string>(), new HashSet<string> { "in", "out", "baselines" });
                if (flags.HelpRequested)
                {
                    Log.Info(Usage);
                    return Task.FromResult(0);
                }
                var input = flags.String("in");
                var output = flags.String("out");
                if (input.Length == 0 || output.Length == 0 || flags.Positional.Count > 0)
                {
                    throw new UsageException(Usage);
                }
                ReviewReport.Write(input, output, meta, flags.String("baselines"));
                return Task.FromResult(0);
            }
            case "site":
            {
                var flags = Flags.Parse(rest, new HashSet<string>(), new HashSet<string> { "runs", "out" });
                if (flags.HelpRequested)
                {
                    Log.Info(Usage);
                    return Task.FromResult(0);
                }
                var runs = flags.String("runs");
                var output = flags.String("out");
                if (runs.Length == 0 || output.Length == 0 || flags.Positional.Count > 0)
                {
                    throw new UsageException(Usage);
                }
                ReviewReport.WriteSite(runs, output);
                return Task.FromResult(0);
            }
            default:
                throw new UsageException(Usage);
        }
    }

    // -meta is repeatable, which Flags does not model: take every occurrence out and return the other arguments.
    private static string[] ExtractMeta(string[] args, SortedDictionary<string, string> meta)
    {
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string? value = null;
            if (arg is "-meta" or "--meta")
            {
                i++;
                value = i < args.Length ? args[i] : throw new UsageException("flag needs an argument: -meta");
            }
            else if (arg.StartsWith("-meta=", StringComparison.Ordinal))
            {
                value = arg["-meta=".Length..];
            }
            else if (arg.StartsWith("--meta=", StringComparison.Ordinal))
            {
                value = arg["--meta=".Length..];
            }
            if (value is null)
            {
                rest.Add(arg);
                continue;
            }
            var eq = value.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                throw new UsageException($"invalid value \"{value}\" for flag -meta: want key=value");
            }
            meta[value[..eq]] = value[(eq + 1)..];
        }
        return [.. rest];
    }
}
