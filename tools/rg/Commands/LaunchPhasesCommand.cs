using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LaunchWaterfall = RimGovernor.Brain.Acceptance.LaunchWaterfall;
using Report = RimGovernor.Brain.Acceptance.Report;
using RimGovernor.Brain.Telemetry.Otlp;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Commands;

/// <summary>
/// <c>RimGovernor.cmd launch-phases [-json] [&lt;run&gt; [&lt;run&gt;]]</c> (#3288): prints one run's launch waterfall
/// (<see cref="LaunchWaterfall"/>) from its game's <c>traces.jsonl</c>, or with two runs the per-phase delta of the
/// second against the first. A run is an OTLP directory (or its <c>traces.jsonl</c>), whose latest run id is shown, or
/// an acceptance case output (its <c>result.json</c>, or the newest one under an output directory), whose trees rooted
/// between <c>started_at</c> and <c>finished_at</c> are read from <c>launch_dir</c>. With no run it takes the
/// newer of the launcher's latest game (<c>.rimgovernor/bridge/config/&lt;game&gt;/</c>) and the latest acceptance
/// output (<c>.rimgovernor/ev*</c>) in this checkout.
/// </summary>
internal static class LaunchPhasesCommand
{
    private const string Usage = """
        usage: launch-phases [-json] [<run> [<run>]]
          <run>  an OTLP directory or traces.jsonl (its latest run id), or an acceptance case output, its result.json
                 or an output directory (the newest result.json under it); default: the newer of the launcher's latest
                 game under .rimgovernor/bridge/config/ and the latest .rimgovernor/ev* acceptance output
          a second <run> prints the per-phase delta of the second against the first
          -json  print JSON instead of text
        """;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private sealed record Run(string Label, string TracesPath, DateTimeOffset? Since, DateTimeOffset? Until);

    public static Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var json = args.Any(a => a is "-json" or "--json");
        var flags = Flags.Parse([.. args.Where(a => a is not ("-json" or "--json"))], new HashSet<string>(), new HashSet<string>());
        if (flags.HelpRequested)
        {
            Console.Out.WriteLine(Usage);
            return Task.FromResult(0);
        }

        if (flags.Positional.Count > 2)
        {
            throw new UsageException(Usage);
        }

        ct.ThrowIfCancellationRequested();
        var runs = flags.Positional.Count == 0 ? [Latest()] : flags.Positional.Select(Resolve).ToList();
        var falls = runs.Select(Load).ToList();
        if (falls.Count == 1)
        {
            if (json)
            {
                var o = falls[0].ToJson();
                o["run"] = runs[0].Label;
                o["traces"] = runs[0].TracesPath;
                Console.Out.WriteLine(o.ToJsonString(Indented));
            }
            else
            {
                Console.Out.WriteLine(Header(runs[0], falls[0]));
                foreach (var line in falls[0].Lines())
                {
                    Console.Out.WriteLine(line);
                }
            }

            return Task.FromResult(0);
        }

        if (json)
        {
            var o = LaunchWaterfall.DeltaJson(falls[0], falls[1]);
            o["before"]!["run"] = runs[0].Label;
            o["after"]!["run"] = runs[1].Label;
            Console.Out.WriteLine(o.ToJsonString(Indented));
        }
        else
        {
            Console.Out.WriteLine("before: " + Header(runs[0], falls[0]));
            Console.Out.WriteLine("after:  " + Header(runs[1], falls[1]));
            foreach (var line in LaunchWaterfall.DeltaLines(falls[0], falls[1]))
            {
                Console.Out.WriteLine(line);
            }
        }

        return Task.FromResult(0);
    }

    private static string Header(Run run, LaunchWaterfall fall) =>
        string.Create(CultureInfo.InvariantCulture, $"{run.Label} (run {(fall.RunId.Length > 0 ? fall.RunId : "-")}, {run.TracesPath})");

    private static LaunchWaterfall Load(Run run)
    {
        if (!File.Exists(run.TracesPath))
        {
            throw new ToolException($"no traces ring at {run.TracesPath}");
        }

        var spans = LaunchWaterfall.ReadSpans(run.TracesPath);
        return run.Since is { } since ? LaunchWaterfall.Since(spans, since, run.Until) : LaunchWaterfall.LatestRun(spans);
    }

    private static Run Resolve(string arg)
    {
        var path = Path.GetFullPath(arg);
        if (File.Exists(path))
        {
            return Path.GetFileName(path).StartsWith(OtlpFiles.Traces, StringComparison.Ordinal)
                ? Ring(Path.GetDirectoryName(path)!)
                : FromReport(path);
        }

        if (!Directory.Exists(path))
        {
            throw new ToolException($"no such run: {arg}");
        }

        if (File.Exists(Path.Combine(path, OtlpFiles.Traces)))
        {
            return Ring(path);
        }

        return NewestReport(path) is { } report ? FromReport(report.FullName) : throw new ToolException($"{arg} holds neither {OtlpFiles.Traces} nor an acceptance result.json");
    }

    private static Run Latest()
    {
        var root = Repo.Require(Environment.CurrentDirectory);
        var state = Path.Combine(root, ".rimgovernor");
        var launcher = Directory.Exists(Path.Combine(state, "bridge", "config"))
            ? new DirectoryInfo(Path.Combine(state, "bridge", "config")).EnumerateDirectories()
                .Select(d => new FileInfo(Path.Combine(d.FullName, OtlpFiles.Traces)))
                .Where(f => f.Exists)
                .MaxBy(f => f.LastWriteTimeUtc)
            : null;
        var acceptance = Directory.Exists(state)
            ? new DirectoryInfo(state).EnumerateDirectories("ev*").Select(d => NewestReport(d.FullName)).OfType<FileInfo>().MaxBy(f => f.LastWriteTimeUtc)
            : null;
        if (launcher is null && acceptance is null)
        {
            throw new ToolException($"no launcher game under {Path.Combine(state, "bridge", "config")} and no acceptance output under {Path.Combine(state, "ev*")}; name a run");
        }

        return acceptance is null || (launcher is not null && launcher.LastWriteTimeUtc > acceptance.LastWriteTimeUtc)
            ? Ring(launcher!.DirectoryName!)
            : FromReport(acceptance.FullName);
    }

    // An output directory holds <area>/<case>/result.json (repeat/<n>/ and dev/<n>/ one level deeper).
    private static FileInfo? NewestReport(string dir) =>
        new DirectoryInfo(dir).EnumerateFiles("result.json", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 4, IgnoreInaccessible = true })
            .MaxBy(f => f.LastWriteTimeUtc);

    private static Run Ring(string dir) => new(dir, Path.Combine(dir, OtlpFiles.Traces), null, null);

    private static Run FromReport(string resultPath)
    {
        JsonNode? report;
        try
        {
            report = JsonNode.Parse(File.ReadAllText(resultPath));
        }
        catch (JsonException e)
        {
            throw new ToolException($"{resultPath}: {e.Message}");
        }

        var since = Time(report?[Report.StartedAtKey]) ?? throw new ToolException($"{resultPath} has no started_at");
        var launchDir = report?[Report.LaunchDirKey] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : "";
        if (launchDir.Length == 0)
        {
            throw new ToolException($"{resultPath} has no {Report.LaunchDirKey} (an older run, or no game was opened); pass the game's OTLP directory instead");
        }

        return new Run(Path.GetDirectoryName(resultPath)!, Path.Combine(launchDir, OtlpFiles.Traces), since, Time(report?[Report.FinishedAtKey]));
    }

    private static DateTimeOffset? Time(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.String
        && DateTimeOffset.TryParse(node.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at
            : null;
}
