using System.Text.Json;

namespace RimGovernor.Tools.Workflow.Commands;

/// <summary>
/// The shrink-only baseline of the acceptance cases' banned-API rule (#3397). RS0030 (<c>BannedSymbols.txt</c>) is an
/// error in every file of <c>RimGovernor.Brain.Acceptance.Cases</c>; the project's <c>.editorconfig</c> holds each legacy
/// file that still uses a banned API in a <c>[/Area/File.cs]</c> section at suggestion, so the compiler records its uses
/// in <c>banned-api.sarif</c> without failing. A baselined file the log shows no use in is stale and fails (delete its
/// section), and so does a use in a file without a section that an in-file setting kept from failing the build.
/// </summary>
internal static class BannedApiBaseline
{
    private const string Rule = "RS0030";

    /// <summary>The problems, empty when the baseline matches the log: <paramref name="cases"/> is the project directory.</summary>
    public static IReadOnlyList<string> Check(string cases, string editorConfig, string sarif, out int baselined)
    {
        var baseline = File.ReadAllLines(editorConfig)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("[/", StringComparison.Ordinal) && l.EndsWith(']'))
            .Select(l => l[2..^1])
            .ToHashSet(StringComparer.Ordinal);
        baselined = baseline.Count;
        var uses = Uses(cases, sarif);
        var problems = new List<string>();
        var stale = baseline.Except(uses, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (stale.Count > 0)
        {
            problems.Add($"{stale.Count} baselined files no longer use a banned API; delete their sections from {editorConfig}:\n  {string.Join("\n  ", stale)}");
        }
        var added = uses.Except(baseline, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (added.Count > 0)
        {
            problems.Add($"{added.Count} files use a banned API without a baseline section; port them to the typed case API (never add a section):\n  {string.Join("\n  ", added)}");
        }
        return problems;
    }

    /// <summary>The files, relative to <paramref name="cases"/> with forward slashes, that the compiler's log shows an RS0030 in.</summary>
    private static HashSet<string> Uses(string cases, string sarif)
    {
        using var log = JsonDocument.Parse(File.ReadAllBytes(sarif));
        var files = new HashSet<string>(StringComparer.Ordinal);
        foreach (var run in log.RootElement.GetProperty("runs").EnumerateArray())
        {
            if (!run.TryGetProperty("results", out var results))
            {
                continue;
            }
            foreach (var result in results.EnumerateArray())
            {
                if (result.GetProperty("ruleId").GetString() != Rule || !result.TryGetProperty("locations", out var locations))
                {
                    continue;
                }
                foreach (var location in locations.EnumerateArray())
                {
                    var uri = location.GetProperty("physicalLocation").GetProperty("artifactLocation").GetProperty("uri").GetString()!;
                    files.Add(Path.GetRelativePath(cases, new Uri(uri).LocalPath).Replace('\\', '/'));
                }
            }
        }
        return files;
    }
}
