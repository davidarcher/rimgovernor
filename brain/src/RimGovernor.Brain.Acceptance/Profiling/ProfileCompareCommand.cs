using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RimGovernor.Brain.Acceptance;

// acceptance profile-compare: the nightly snapshot-capture perf check. snapshot-perf.yml times profile-capture on the
// fixture factory's newest bundle; this command records the stats with their commit as the run's artifact, compares
// each family's p90 to the median p90 of the last -window successful runs' records, and opens or comments on one
// regression issue naming the families and the commit range.

/// <summary>One nightly run's artifact (Go <c>perfRecord</c>).</summary>
public sealed class PerfRecord
{
    public string Commit { get; set; } = "";

    public long RunId { get; set; }

    public DateTimeOffset At { get; set; }

    public ProfileSummary Profile { get; set; } = new();

    /// <summary>The record as its JSON object (tags commit, run_id (omitted when 0), at, profile).</summary>
    public JsonObject ToJson()
    {
        var row = new JsonObject { ["commit"] = Commit };
        if (RunId != 0)
        {
            row["run_id"] = RunId;
        }

        row["at"] = At.ToString("O", CultureInfo.InvariantCulture);
        row["profile"] = Profile.ToJson();
        return row;
    }

    public static PerfRecord FromJson(JsonObject node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var at = node["at"].AsString();
        return new PerfRecord
        {
            Commit = node["commit"].AsString(),
            RunId = (long)Math.Max(node["run_id"].AsNumber(), 0),
            At = at.Length > 0 && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed : default,
            Profile = ProfileSummary.FromJson(node["profile"]),
        };
    }
}

/// <summary>One span whose p90 exceeds its baseline (Go <c>perfRegression</c>).</summary>
public sealed record PerfRegression(string Name, double P90, double Baseline, int Samples)
{
    public double Ratio => (P90 / Baseline) - 1;
}

/// <summary>The <c>acceptance profile-compare</c> subcommand (Go <c>profileCompare</c>).</summary>
public static class ProfileCompareCommand
{
    public const string Usage = """

          acceptance profile-compare -current <profile.json> -commit <sha> -out <record.json> [-history <dir> -fetch -window <n> -threshold <f> -min-ms <ms> -summary <file> -issue]
            records profile-capture -json stats with their commit and compares each family's p90 to the median of the last -window records
            (-fetch downloads them from successful snapshot-perf runs); -issue opens or comments on the regression issue
        """;

    public const string FactoryRepo = "davidarcher/rimgovernor";
    public const string PerfWorkflow = "snapshot-perf.yml";
    public const string PerfArtifact = "snapshot-perf-";
    public const string PerfIssueTitle = "Snapshot capture regression (nightly snapshot-perf)";
    public const int PerfMinSamples = 3;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>
    /// Flags the spans (total and families) of <paramref name="current"/> whose p90 exceeds the median p90 of
    /// <paramref name="history"/> by more than <paramref name="threshold"/> and <paramref name="minMs"/>. A span with
    /// fewer than <see cref="PerfMinSamples"/> history samples has no baseline.
    /// </summary>
    public static IReadOnlyList<PerfRegression> ComparePerf(ProfileSummary current, IReadOnlyList<PerfRecord> history, double threshold, double minMs)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(history);
        var samples = new Dictionary<string, List<double>>(StringComparer.Ordinal);

        void Add(string name, double value)
        {
            if (!samples.TryGetValue(name, out var list))
            {
                list = [];
                samples[name] = list;
            }

            list.Add(value);
        }

        foreach (var h in history)
        {
            Add("total", h.Profile.Total.P90);
            foreach (var f in h.Profile.Families)
            {
                Add(f.Name, f.P90);
            }
        }

        var output = new List<PerfRegression>();
        foreach (var st in new[] { current.Total }.Concat(current.Families))
        {
            if (!samples.TryGetValue(st.Name, out var values) || values.Count < PerfMinSamples)
            {
                continue;
            }

            var baseline = Median(values);
            if (st.P90 > baseline * (1 + threshold) && st.P90 - baseline > minMs)
            {
                output.Add(new PerfRegression(st.Name, st.P90, baseline, values.Count));
            }
        }

        return output;
    }

    public static double Median(IReadOnlyList<double> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var sorted = values.Order().ToArray();
        var n = sorted.Length;
        return n % 2 == 1 ? sorted[n / 2] : (sorted[(n / 2) - 1] + sorted[n / 2]) / 2;
    }

    /// <summary>Reads every record under <paramref name="dir"/>, newest first, at most <paramref name="window"/> of them.</summary>
    public static IReadOnlyList<PerfRecord> ReadPerfHistory(string dir, int window)
    {
        var records = new List<PerfRecord>();
        if (Directory.Exists(dir))
        {
            foreach (var path in Directory.EnumerateFiles(dir, "record.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                try
                {
                    records.Add(PerfRecord.FromJson(Dyn.ParseObject(File.ReadAllText(path))));
                }
                catch (Exception e) when (e is JsonException or AcceptanceException)
                {
                    throw new AcceptanceException($"{path}: {e.Message}", e);
                }
            }
        }

        var sorted = records.OrderByDescending(r => r.At).Take(window).ToList();
        return sorted;
    }

    /// <summary>Downloads the last <paramref name="window"/> successful snapshot-perf runs' records into dir/&lt;run id&gt;.</summary>
    public static async Task FetchPerfHistoryAsync(string dir, int window, CancellationToken cancellationToken)
    {
        string output;
        try
        {
            output = await CiFixture.Gh(
                ["run", "list", "-R", FactoryRepo, "--workflow", PerfWorkflow, "--branch", "main", "--status", "success", "--limit", window.ToString(CultureInfo.InvariantCulture), "--json", "databaseId"],
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException($"gh run list {PerfWorkflow}: {e.Message}", e);
        }

        JsonNode? runs;
        try
        {
            runs = JsonNode.Parse(output);
        }
        catch (JsonException e)
        {
            throw new AcceptanceException(e.Message, e);
        }

        foreach (var run in runs.AsSlice())
        {
            var id = ((long)run.Member("databaseId").AsNumber()).ToString(CultureInfo.InvariantCulture);
            try
            {
                _ = await CiFixture.Gh(["run", "download", id, "-R", FactoryRepo, "--pattern", PerfArtifact + "*", "--dir", Path.Combine(dir, id)], cancellationToken).ConfigureAwait(false);
            }
            catch (AcceptanceException e)
            {
                throw new AcceptanceException($"gh run download {id}: {e.Message}", e);
            }
        }
    }

    /// <summary>The markdown body for the summary and the issue.</summary>
    public static string PerfReport(PerfRecord current, IReadOnlyList<PerfRecord> history, IReadOnlyList<PerfRegression> regressions, double threshold)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(regressions);
        var culture = CultureInfo.InvariantCulture;
        var b = new StringBuilder();
        b.Append(culture, $"Snapshot capture at {Short(current.Commit)}");
        if (current.RunId != 0)
        {
            b.Append(culture, $" ([run](https://github.com/{FactoryRepo}/actions/runs/{current.RunId}))");
        }

        b.Append(culture, $": {current.Profile.Count} captures, {current.Profile.Pawns} pawns, {current.Profile.Things} things; baseline is the median p90 of {history.Count} run(s), threshold +{threshold * 100:F0}%.\n\n");
        if (regressions.Count == 0)
        {
            b.Append("No family regressed.\n");
            return b.ToString();
        }

        if (history.Count > 0)
        {
            var from = history[0].Commit;
            b.Append(culture, $"Commit range: [{Short(from)}..{Short(current.Commit)}](https://github.com/{FactoryRepo}/compare/{from}...{current.Commit})\n\n");
        }

        b.Append("| span | p90 ms | baseline ms | change |\n|---|---:|---:|---:|\n");
        foreach (var r in regressions)
        {
            b.Append(culture, $"| {r.Name} | {r.P90:F2} | {r.Baseline:F2} | +{r.Ratio * 100:F0}% |\n");
        }

        return b.ToString();
    }

    private static string Short(string sha) => sha.Length > 10 ? sha[..10] : sha;

    /// <summary>Comments on the open regression issue, or opens one.</summary>
    public static async Task FilePerfIssueAsync(string body, CancellationToken cancellationToken)
    {
        string output;
        try
        {
            output = await CiFixture.Gh(
                ["issue", "list", "-R", FactoryRepo, "--state", "open", "--search", "in:title \"" + PerfIssueTitle + "\"", "--json", "number,title"],
                cancellationToken).ConfigureAwait(false);
        }
        catch (AcceptanceException e)
        {
            throw new AcceptanceException("gh issue list: " + e.Message, e);
        }

        JsonNode? issues;
        try
        {
            issues = JsonNode.Parse(output);
        }
        catch (JsonException e)
        {
            throw new AcceptanceException(e.Message, e);
        }

        foreach (var issue in issues.AsSlice())
        {
            if (issue.Member("title").AsString() == PerfIssueTitle)
            {
                var number = ((long)issue.Member("number").AsNumber()).ToString(CultureInfo.InvariantCulture);
                _ = await CiFixture.Gh(["issue", "comment", number, "-R", FactoryRepo, "--body", body], cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        _ = await CiFixture.Gh(["issue", "create", "-R", FactoryRepo, "--title", PerfIssueTitle, "--label", "priority:P1", "--body", body], cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);
        var fs = new FlagSet("profile-compare");
        var currentPath = fs.String("current", "");
        var commit = fs.String("commit", "");
        var runText = fs.String("run", "0");
        var outPath = fs.String("out", "");
        var historyDir = fs.String("history", "");
        var fetch = fs.Bool("fetch", false);
        var windowText = fs.String("window", "7");
        var thresholdText = fs.String("threshold", "0.3");
        var minMsText = fs.String("min-ms", "0.5");
        var summary = fs.String("summary", "");
        var issue = fs.Bool("issue", false);
        try
        {
            fs.Parse(args);
        }
        catch (UsageException e)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 2;
        }

        var culture = CultureInfo.InvariantCulture;
        if (!long.TryParse(runText.Value, NumberStyles.AllowLeadingSign, culture, out var runId)
            || !int.TryParse(windowText.Value, NumberStyles.AllowLeadingSign, culture, out var window)
            || !double.TryParse(thresholdText.Value, NumberStyles.Float, culture, out var threshold)
            || !double.TryParse(minMsText.Value, NumberStyles.Float, culture, out var minMs))
        {
            await stderr.WriteLineAsync("-run and -window must be integers; -threshold and -min-ms numbers").ConfigureAwait(false);
            return 2;
        }

        if (currentPath.Value.Length == 0 || commit.Value.Length == 0 || outPath.Value.Length == 0 || window < 1
            || (fetch.Value && historyDir.Value.Length == 0) || fs.Args.Count > 0)
        {
            await stderr.WriteLineAsync("-current, -commit and -out are required; -fetch needs -history; -window >= 1").ConfigureAwait(false);
            return 2;
        }

        try
        {
            var current = new PerfRecord { Commit = commit.Value, RunId = runId, At = DateTimeOffset.UtcNow };
            try
            {
                current.Profile = ProfileSummary.FromJson(Dyn.ParseObject(await File.ReadAllTextAsync(currentPath.Value, cancellationToken).ConfigureAwait(false)));
            }
            catch (Exception e) when (e is JsonException or AcceptanceException)
            {
                throw new AcceptanceException($"{currentPath.Value}: {e.Message}", e);
            }

            if (current.Profile.Count == 0)
            {
                throw new AcceptanceException($"{currentPath.Value} holds no captures");
            }

            var record = current.ToJson().ToJsonString(Indented);
            var outDir = Path.GetDirectoryName(Path.GetFullPath(outPath.Value));
            if (!string.IsNullOrEmpty(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            await File.WriteAllTextAsync(outPath.Value, record + "\n", cancellationToken).ConfigureAwait(false);
            IReadOnlyList<PerfRecord> history = [];
            if (historyDir.Value.Length > 0)
            {
                if (fetch.Value)
                {
                    await FetchPerfHistoryAsync(historyDir.Value, window, cancellationToken).ConfigureAwait(false);
                }

                history = ReadPerfHistory(historyDir.Value, window);
            }

            var regressions = ComparePerf(current.Profile, history, threshold, minMs);
            var report = PerfReport(current, history, regressions, threshold);
            await stdout.WriteAsync(report).ConfigureAwait(false);
            if (summary.Value.Length > 0)
            {
                try
                {
                    await File.AppendAllTextAsync(summary.Value, report, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // The step summary is a courtesy; the report already went to stdout.
                }
            }

            if (regressions.Count > 0 && issue.Value)
            {
                await FilePerfIssueAsync(report, cancellationToken).ConfigureAwait(false);
            }

            return 0;
        }
        catch (Exception e) when (e is AcceptanceException or IOException or UnauthorizedAccessException)
        {
            await stderr.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 1;
        }
    }
}
