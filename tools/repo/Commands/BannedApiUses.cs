using System.Text.Json;

namespace RimGovernor.Tools.Workflow.Commands;

/// <summary>
/// The acceptance banned-API rule (#3397, #3410) holds in every file: RS0030 (<c>BannedSymbols.Acceptance.txt</c>) is an
/// error in every <c>RimGovernor.Brain.Acceptance*</c> project, harness and cases, so a use that reaches the compiler's log (<c>banned-api.sarif</c>)
/// without failing the build was kept from failing by an in-file suppression, and fails here.
/// </summary>
internal static class BannedApiUses
{
    private const string Rule = "RS0030";

    /// <summary>The problems, empty when the log shows no banned-API use: <paramref name="cases"/> is the acceptance project directory.</summary>
    public static IReadOnlyList<string> Check(string cases, string sarif)
    {
        var uses = Uses(cases, sarif).Order(StringComparer.Ordinal).ToList();
        return uses.Count == 0
            ? []
            : [$"{uses.Count} files use a banned API under a suppression; port them to typed reads and paced waits:\n  {string.Join("\n  ", uses)}"];
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
