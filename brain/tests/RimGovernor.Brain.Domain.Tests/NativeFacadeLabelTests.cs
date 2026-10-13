using System.Text.RegularExpressions;
using Xunit;

namespace RimGovernor.Brain.Domain.Tests;

/// <summary>
/// Every operation label of the in-process native facade (#3116) names a registered native tool. The label is the name of
/// the facade's game-thread hop in the watchdog and observation account (<c>facade/&lt;label&gt;</c>), so a label that
/// matches no tool name cannot be joined to that tool's own accounting. The mod assembly needs the game to load, so the
/// check reads the two sources: the label of each <c>Read(...)</c> / <c>Write(...)</c> call in InProcessNativeFacade.cs
/// against the <c>rimgovernor/&lt;name&gt;</c> tool names the Bridge declares in [Tool] attributes and tool-name constants.
/// </summary>
public sealed partial class NativeFacadeLabelTests
{
    private const string BridgeSources = "integrations/rimgovernor-native/src/Bridge";
    private const string FacadeSource = BridgeSources + "/Protocol/InProcessNativeFacade.cs";

    [GeneratedRegex("""\b(?:Read|Write)\("([a-z0-9_]+)",""")]
    private static partial Regex FacadeLabel();

    [GeneratedRegex("""(?:\[Tool\(\s*|\bconst\s+string\s+\w+\s*=\s*)"rimgovernor/([a-z0-9_]+)""")]
    private static partial Regex ToolName();

    [Fact]
    public void EveryFacadeOperationLabelIsARegisteredToolName()
    {
        var root = RepositoryRoot();
        var labels = FacadeLabel().Matches(File.ReadAllText(Path.Combine(root, FacadeSource))).Select(m => m.Groups[1].Value).ToList();
        Assert.True(labels.Count >= 60, $"found only {labels.Count} facade operation labels in {FacadeSource}; the pattern no longer matches its Read/Write calls");

        var tools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, BridgeSources), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in ToolName().Matches(File.ReadAllText(file)))
            {
                tools.Add(match.Groups[1].Value);
            }
        }

        Assert.True(tools.Count >= 60, $"found only {tools.Count} registered tool names under {BridgeSources}; the pattern no longer matches its declarations");

        var unregistered = labels.Where(label => !tools.Contains(label)).Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.True(unregistered.Count == 0, $"facade operation labels that match no registered tool name: {string.Join(", ", unregistered)}");

        var repeated = labels.GroupBy(label => label, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key).Order(StringComparer.Ordinal).ToList();
        Assert.True(repeated.Count == 0, $"facade operation labels used by more than one method: {string.Join(", ", repeated)}");
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "AGENTS.md")) && File.Exists(Path.Combine(directory.FullName, FacadeSource)))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"no repository root (AGENTS.md beside {FacadeSource}) above {AppContext.BaseDirectory}");
    }
}
