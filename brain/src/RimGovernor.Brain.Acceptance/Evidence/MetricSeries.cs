using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

/// <summary>
/// One line of the append-only series: which case, which run (<see cref="RunId"/> names the run's output
/// directory), what source it ran from, on which world seed (the report's world block), when, whether it passed and
/// its block.
/// </summary>
public sealed record SeriesRow
{
    public string Case { get; init; } = "";

    public string RunId { get; init; } = "";

    public string SourceRevision { get; init; } = "";

    public string Seed { get; init; } = "";

    public DateTimeOffset Timestamp { get; init; }

    public bool Passed { get; init; }

    public Metrics Metrics { get; init; } = new();

    /// <summary>The row as one JSON object, with the Go field names (empty source_revision and seed omitted).</summary>
    public JsonObject ToJson()
    {
        var row = new JsonObject
        {
            ["case"] = Case,
            ["run_id"] = RunId,
        };
        if (SourceRevision.Length > 0)
        {
            row["source_revision"] = SourceRevision;
        }

        if (Seed.Length > 0)
        {
            row["seed"] = Seed;
        }

        row["timestamp"] = Timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        row["passed"] = Passed;
        row["metrics"] = Metrics.ToJson();
        return row;
    }

    /// <summary>Reads a row from its JSON object; null when it names no case.</summary>
    public static SeriesRow? FromJson(JsonObject? row)
    {
        if (row is null || row["case"].AsString() is not { Length: > 0 } name)
        {
            return null;
        }

        return new SeriesRow
        {
            Case = name,
            RunId = row["run_id"].AsString(),
            SourceRevision = row["source_revision"].AsString(),
            Seed = row["seed"].AsString(),
            Timestamp = DateTimeOffset.TryParse(row["timestamp"].AsString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : default,
            Passed = row["passed"].AsBool() == true,
            Metrics = Metrics.Of(row["metrics"] as JsonObject) ?? new Metrics(),
        };
    }
}

/// <summary>
/// A case's recent pass record over the series: the share of its last <see cref="MetricSeries.FlakeWindow"/> runs
/// that failed, whatever the reason. A rate strictly between 0 and 1 is a case that passes and fails on the same
/// code; the suite prints it beside a failed or flagged row so a known flake is not read as a regression.
/// </summary>
public sealed record Flake(double Rate, int Failures, int Samples)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Failures} of {Samples} recent runs failed ({Rate * 100:0}%)");

    public JsonObject ToJson() => new() { ["rate"] = Rate, ["failures"] = Failures, ["samples"] = Samples };
}

/// <summary>Says when a metric's value has drifted from its trailing median: past Ratio times the median and Floor over it (Lower: below Ratio times the median and Floor under it, for a metric where less is worse). The floor keeps a tiny median from flagging noise.</summary>
public sealed record DriftRule(double Ratio, double Floor, bool Lower = false);

/// <summary>One metric of one case past its rule.</summary>
/// <param name="Samples">How many prior rows the median spans.</param>
public sealed record DriftFlag(string Case, string Metric, double Value, double Median, double Ratio, int Samples)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Case} {Metric} {Value:G6} vs median {Median:G6} ({Ratio:0.00}x over {Samples})");

    public JsonObject ToJson() => new()
    {
        ["case"] = Case,
        ["metric"] = Metric,
        ["value"] = Value,
        ["median"] = Median,
        ["ratio"] = Ratio,
        ["samples"] = Samples,
    };
}

/// <summary>The per-run metrics series, drift and flake bookkeeping (Go <c>metrics.go</c>, second half).</summary>
public static class MetricSeries
{
    /// <summary>How many prior rows of a case its flake rate spans.</summary>
    public const int FlakeWindow = 10;

    /// <summary>How many prior rows of a case the trailing median spans.</summary>
    public const int DriftWindow = 10;

    /// <summary>The per-metric rule set; wall_ms keeps the suite's regression ratio and floor. A metric without a rule never flags.</summary>
    public static readonly IReadOnlyDictionary<string, DriftRule> DriftRules = new Dictionary<string, DriftRule>(StringComparer.Ordinal)
    {
        ["wall_ms"] = new(1.25, 5000),
        ["boot_ms"] = new(1.5, 10000),
        ["ticks_advanced"] = new(0.8, 1000, true),
        ["wall_tps"] = new(0.8, 50, true),
        ["waits"] = new(1.5, 5),
        ["waits_stalled"] = new(1, 0.5),
        ["max_quiet_ms"] = new(1.5, 10000),
        ["native_calls"] = new(1.5, 200),
        ["native_errors"] = new(1, 0.5),
        ["native_queue_ms_mean"] = new(1.5, 20),
        ["native_exec_ms_mean"] = new(1.5, 20),
        ["paused_fraction"] = new(1.5, 0.1),
        ["service_launches"] = new(1, 0.5),
        ["evidence_bytes"] = new(1.5, 1 << 20),
    };

    private static readonly Lazy<string> SourceRevisionValue = new(ReadSourceRevision);

    /// <summary>The flake record of case <paramref name="name"/> over the last <see cref="FlakeWindow"/> rows of the series that are not the run's own (<paramref name="runId"/>); Samples is zero when the series has none.</summary>
    public static Flake FlakeOf(string name, string runId, IReadOnlyList<SeriesRow> series)
    {
        ArgumentNullException.ThrowIfNull(series);
        var prior = series.Where(row => row.Case == name && row.RunId != runId).TakeLast(FlakeWindow).ToList();
        var failures = prior.Count(row => !row.Passed);
        return new Flake(prior.Count > 0 ? (double)failures / prior.Count : 0, failures, prior.Count);
    }

    /// <summary>Reads a report's "flake" block; null when it has none.</summary>
    public static Flake? FlakeOfReport(Report report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Data["flake"] is not JsonObject block)
        {
            return null;
        }

        return new Flake(block["rate"].AsNumber(), (int)block["failures"].AsNumber(), (int)block["samples"].AsNumber());
    }

    /// <summary>Where a run rooted at <paramref name="output"/> keeps its series: metrics.jsonl in the output directory's parent, so successive runs (each a fresh output directory beside the last) share one.</summary>
    public static string SeriesPath(string output) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output)) ?? "", "metrics.jsonl");

    /// <summary>Appends one row to <paramref name="path"/> as a single line (append mode, so the suite's workers appending at once interleave whole lines).</summary>
    public static void AppendSeries(string path, SeriesRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".");
        var line = Encoding.UTF8.GetBytes(row.ToJson().ToJsonString() + "\n");
        using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        file.Write(line);
    }

    /// <summary>Reads every row of <paramref name="path"/> in file order; a missing file is an empty series and a corrupt line is skipped.</summary>
    public static IReadOnlyList<SeriesRow> ReadSeries(string path)
    {
        var rows = new List<SeriesRow>();
        if (!File.Exists(path))
        {
            return rows;
        }

        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(file, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            try
            {
                if (SeriesRow.FromJson(JsonNode.Parse(line) as JsonObject) is { } row)
                {
                    rows.Add(row);
                }
            }
            catch (JsonException)
            {
            }
        }

        return rows;
    }

    /// <summary>
    /// Compares one case's block with its trailing median over the prior passing rows of the series (the last
    /// <see cref="DriftWindow"/> passes of the case, in series order, excluding rows of <paramref name="runId"/>: the
    /// run's own appends; a failed or refused run's cost is not the case's) and lists every metric past its rule, in
    /// <see cref="Metrics.MetricNames"/> order. A metric whose median is zero or absent never flags: there is nothing
    /// to drift from.
    /// </summary>
    public static IReadOnlyList<DriftFlag> Drift(string name, string runId, Metrics m, IReadOnlyList<SeriesRow> series)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(series);
        var prior = series.Where(row => row.Case == name && row.RunId != runId && row.Passed).TakeLast(DriftWindow).ToList();
        var flags = new List<DriftFlag>();
        foreach (var metric in Metrics.MetricNames)
        {
            if (!DriftRules.TryGetValue(metric, out var rule))
            {
                continue;
            }

            var samples = prior.Where(row => row.Metrics.Values.ContainsKey(metric)).Select(row => row.Metrics.Values[metric]).ToList();
            var median = MedianOf(samples);
            if (median <= 0)
            {
                continue;
            }

            var value = m.Values.GetValueOrDefault(metric);
            var drifted = rule.Lower
                ? value < median * rule.Ratio && median - value > rule.Floor
                : value > median * rule.Ratio && value - median > rule.Floor;
            if (drifted)
            {
                flags.Add(new DriftFlag(name, metric, value, median, value / median, samples.Count));
            }
        }

        return flags;
    }

    /// <summary>
    /// Appends the finalized report's block for case <paramref name="name"/> to the series at <paramref name="path"/>
    /// under <paramref name="runId"/>, stamped with <see cref="SourceRevision"/> and the run's world seed, and returns
    /// the drift flags against the rows already there. The report gains "series" (the path), "flake" (when there are
    /// prior rows) and, when any, "drift". Errors are reported, not fatal: the run's verdict does not depend on its
    /// bookkeeping.
    /// </summary>
    public static IReadOnlyList<DriftFlag> RecordSeries(string path, string name, string runId, Report report, string seed)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (Metrics.Of(report) is not { } m)
        {
            return [];
        }

        report["series"] = path;
        IReadOnlyList<SeriesRow> prior = [];
        try
        {
            prior = ReadSeries(path);
        }
        catch (IOException e)
        {
            report["series_error"] = e.Message;
        }

        var flags = Drift(name, runId, m, prior);
        if (flags.Count > 0)
        {
            report["drift"] = new JsonArray([.. flags.Select(flag => (JsonNode)flag.ToJson())]);
        }

        if (FlakeOf(name, runId, prior) is { Samples: > 0 } flake)
        {
            report["flake"] = flake.ToJson();
        }

        var row = new SeriesRow
        {
            Case = name,
            RunId = runId,
            SourceRevision = SourceRevision(),
            Seed = seed,
            Timestamp = DateTimeOffset.UtcNow,
            Passed = report.Passed,
            Metrics = m,
        };
        try
        {
            AppendSeries(path, row);
        }
        catch (IOException e)
        {
            report["series_error"] = e.Message;
        }

        return flags;
    }

    /// <summary>The HEAD commit of the checkout enclosing the working directory, or "" without one; the series stamps every row with it so a drift can be read against the change that ran.</summary>
    public static string SourceRevision() => SourceRevisionValue.Value;

    private static string ReadSourceRevision()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, ".git")) && !File.Exists(Path.Combine(dir.FullName, ".git")))
        {
            dir = dir.Parent;
        }

        if (dir is null)
        {
            return "";
        }

        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-C");
        start.ArgumentList.Add(dir.FullName);
        start.ArgumentList.Add("rev-parse");
        start.ArgumentList.Add("HEAD");
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return "";
            }

            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : "";
        }
        catch (Win32Exception)
        {
            return "";
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    private static double MedianOf(List<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        var n = sorted.Count;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2;
    }
}
