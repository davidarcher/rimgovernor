using System.Text.RegularExpressions;

namespace RimGovernor.Tools.Workflow.Commands;

/// <summary>
/// The native project lists each <c>scripts/fixtures/*.cs</c> by hand as a conditioned Compile item (#3436): a source
/// with no item is never built, and an item whose source was deleted breaks every fixture build. The gate's fixture
/// build catches a source that does not compile; this catches a source nothing compiles and an item with no source.
/// </summary>
internal static partial class FixtureCompileItems
{
    private const string Project = "integrations/rimgovernor-native/src/Bridge/RimGovernor.Bridge.csproj";

    private const string Root = "scripts/fixtures";

    [GeneratedRegex(@"<Compile\s+Include=""[^""]*?scripts/fixtures/([^""/]+\.cs)""", RegexOptions.CultureInvariant)]
    private static partial Regex Item();

    /// <summary>The problems, empty when every fixture source has a Compile item and every item has its source.</summary>
    public static IReadOnlyList<string> Check(string repo)
    {
        var listed = Item().Matches(File.ReadAllText(Path.Combine(repo, Project))).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var present = Directory.EnumerateFiles(Path.Combine(repo, Root), "*.cs", SearchOption.TopDirectoryOnly)
            .Select(f => Path.GetFileName(f))
            .ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var file in present.Except(listed).Order(StringComparer.Ordinal))
        {
            problems.Add($"{Root}/{file} has no Compile item in {Project}; add one conditioned on its build flag");
        }
        foreach (var file in listed.Except(present).Order(StringComparer.Ordinal))
        {
            problems.Add($"{Project} compiles {Root}/{file}, which does not exist; delete the item and its flag");
        }
        return problems;
    }
}
