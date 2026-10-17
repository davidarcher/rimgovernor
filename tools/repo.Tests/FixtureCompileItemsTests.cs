using RimGovernor.Tools.Workflow.Commands;

namespace RimGovernor.Tools.Workflow.Tests;

/// <summary>The fixture sources and the native project's Compile items agree (#3436).</summary>
public sealed class FixtureCompileItemsTests : IDisposable
{
    private readonly string repo = Directory.CreateTempSubdirectory("fixture-items-").FullName;

    public void Dispose() => Directory.Delete(repo, recursive: true);

    [Fact]
    public void FlagsASourceWithNoItemAndAnItemWithNoSource()
    {
        Lay(["A.cs", "B.cs"], ["A.cs", "Gone.cs"]);

        Assert.Equal(
            [
                "scripts/fixtures/B.cs has no Compile item in integrations/rimgovernor-native/src/Bridge/RimGovernor.Bridge.csproj; add one conditioned on its build flag",
                "integrations/rimgovernor-native/src/Bridge/RimGovernor.Bridge.csproj compiles scripts/fixtures/Gone.cs, which does not exist; delete the item and its flag",
            ],
            FixtureCompileItems.Check(repo));
    }

    [Fact]
    public void PassesWhenTheyAgree()
    {
        Lay(["A.cs", "B.cs"], ["A.cs", "B.cs"]);

        Assert.Empty(FixtureCompileItems.Check(repo));
    }

    private void Lay(string[] sources, string[] items)
    {
        Directory.CreateDirectory(Path.Combine(repo, "scripts", "fixtures"));
        foreach (var s in sources)
        {
            File.WriteAllText(Path.Combine(repo, "scripts", "fixtures", s), "");
        }
        var project = Path.Combine(repo, "integrations", "rimgovernor-native", "src", "Bridge");
        Directory.CreateDirectory(project);
        File.WriteAllText(
            Path.Combine(project, "RimGovernor.Bridge.csproj"),
            string.Concat(items.Select(i => $"<Compile Include=\"../../../../scripts/fixtures/{i}\" Condition=\"'$(X)' == 'true'\" />\n")));
    }
}
