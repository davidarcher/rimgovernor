using RimGovernor.Tools.Workflow.Commands;
using static RimGovernor.Tools.Workflow.Tests.Sandbox;

namespace RimGovernor.Tools.Workflow.Tests;

/// <summary>The lane resolves conflicts in the shrink-only gate baselines itself.</summary>
public sealed class LandBaselineTests
{
    private const string Rule9 = "brain/analyzers/baseline/rule9.txt";

    [Fact]
    public void CountedBaselineKeepsTheSmallerCountAndDropsWhatEitherSideDeleted()
    {
        var start = "a.cs|Fact|5\nb.cs|Fact|3\nc.cs|Unknown|2\n";
        var ours = "a.cs|Fact|4\nb.cs|Fact|3\nc.cs|Unknown|2\nd.cs|Fact|1\n";
        var theirs = "a.cs|Fact|2\nc.cs|Unknown|1\n";

        Assert.Equal("a.cs|Fact|2\nc.cs|Unknown|1\nd.cs|Fact|1\n", BaselineMerge.Merge(start, ours, theirs, counted: true));
    }

    [Fact]
    public void UncountedBaselineKeepsTheHeaderAndTreatsTrailingNumbersAsPartOfTheEntry()
    {
        var start = "# Rule 6\nx.cs|M|2500\ny.cs|N|\"Wood\"\n";
        var ours = "# Rule 6\nx.cs|M|2500\n";
        var theirs = "# Rule 6\nx.cs|M|2500\ny.cs|N|\"Wood\"\nz.cs|O|7\n";

        Assert.Equal("# Rule 6\nx.cs|M|2500\nz.cs|O|7\n", BaselineMerge.Merge(start, ours, theirs, counted: false));
    }

    [Fact]
    public async Task BaselineConflictResolvesAndLands()
    {
        using var s = new Sandbox();
        Write(s.Worktree, Rule9, "a.cs|Fact|5\nb.cs|Fact|3\n");
        Commit(s.Worktree, "Add the baseline");
        await s.LandAsync();
        Write(s.Worktree, Rule9, "a.cs|Fact|4\nb.cs|Fact|3\n");
        Write(s.Worktree, "a.cs", "a\n");
        Commit(s.Worktree, "Shrink a");
        s.PushFromElsewhere(Rule9, "a.cs|Fact|5\nb.cs|Fact|1\n", "Shrink b");

        await s.LandAsync();

        Assert.Equal("Shrink a", Git(s.Origin, "log", "-1", "--format=%s", "main"));
        Assert.Equal("a.cs|Fact|4\nb.cs|Fact|1", Git(s.Origin, "show", "main:" + Rule9));
    }
}
