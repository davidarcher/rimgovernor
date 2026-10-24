using System.Text;
using System.Text.Json;
using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Commands.ColonyReview;

/// <summary>Writes a run report directory and the site that indexes them.</summary>
internal static class ReviewReport
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Reads the case output <paramref name="input"/> and writes the run report to <paramref name="output"/>: the images,
    /// index.html and run.json (the meta plus the summary and the layout section, whose thresholds fail the run). Returns the
    /// summary. An empty <paramref name="journal"/> or <paramref name="logs"/> reads the run's own (<see cref="LayoutReviewLoad"/>).
    /// </summary>
    public static RunSummary Write(string input, string output, IReadOnlyDictionary<string, string> meta, string baselines, string journal = "", string logs = "")
    {
        List<Row> rows;
        CaseResult result;
        try
        {
            (rows, result) = ReviewTimeline.Load(input);
        }
        catch (FileNotFoundException)
        {
            // The case never wrote a result (bootstrap or launch failed): the page says so instead of the workflow
            // having nothing to publish.
            rows = [];
            var error = "the case left no result.json";
            if (meta.TryGetValue("outcome", out var outcome) && outcome.Length > 0)
            {
                error += " (play step: " + outcome + ")";
            }
            result = new CaseResult(error + "; see the run log", new Dictionary<string, string>());
        }
        var merged = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in result.Review)
        {
            merged[k] = v;
        }
        foreach (var (k, v) in meta)
        {
            merged[k] = v;
        }
        var summary = ReviewTimeline.Derive(rows, merged) with { Error = result.Error.Length > 0 ? result.Error : null };
        var inputs = LayoutReviewLoad.Read(input, journal, logs);
        var layout = LayoutReview.Compute(inputs);
        summary = summary with { Layout = layout, Score = summary.Score with { Thresholds = LayoutReview.Thresholds(layout, inputs) } };
        summary = ColonyScoring.CompareToStore(summary, baselines, Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(output))));
        Directory.CreateDirectory(Path.Combine(output, "review"));
        foreach (var r in rows)
        {
            foreach (var shot in new[] { r.ColonyShot, r.MapShot })
            {
                if (shot.Length > 0)
                {
                    File.Copy(Path.Combine(input, "review", shot), Path.Combine(output, "review", shot), overwrite: true);
                }
            }
        }
        File.WriteAllText(Path.Combine(output, "run.json"), JsonSerializer.Serialize(summary, WriteOptions), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(output, "index.html"), ReviewPages.RunPage(rows, summary), new UTF8Encoding(false));
        return summary;
    }

    /// <summary>
    /// Indexes every run dir under <paramref name="runs"/> that holds a run.json into <paramref name="output"/>, newest
    /// first by directory name, copying each beside the index.
    /// </summary>
    public static void WriteSite(string runs, string output)
    {
        if (!Directory.Exists(runs))
        {
            throw new ToolException($"{runs}: no such directory");
        }
        Directory.CreateDirectory(output);
        var copy = !string.Equals(Path.GetFullPath(runs), Path.GetFullPath(output), StringComparison.Ordinal);
        var list = new List<(string Dir, RunSummary S)>();
        foreach (var dir in Directory.EnumerateDirectories(runs).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            var path = Path.Combine(dir, "run.json");
            if (!File.Exists(path))
            {
                continue;
            }
            RunSummary? s;
            try
            {
                s = JsonSerializer.Deserialize<RunSummary>(File.ReadAllBytes(path), ReadOptions);
            }
            catch (JsonException)
            {
                continue;
            }
            if (s is null)
            {
                continue;
            }
            if (copy)
            {
                CopyDirectory(dir, Path.Combine(output, name));
            }
            list.Add((name, s));
        }
        list.Sort((a, b) => string.CompareOrdinal(b.Dir, a.Dir));
        File.WriteAllText(Path.Combine(output, "index.html"), ReviewPages.SitePage(list), new UTF8Encoding(false));
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(target, Path.GetFileName(dir)));
        }
    }
}
