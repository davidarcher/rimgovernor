using RimGovernor.Tools.Common;
using static RimGovernor.Tools.Workflow.Tests.Sandbox;

namespace RimGovernor.Tools.Workflow.Tests;

/// <summary>The lane rebuilds the merged tree only when main brought in files the gate builds; the full gate is -test's.</summary>
public sealed class LandGateTests
{
    [Fact]
    public async Task MergeThatBroughtGateInputsBuildsTheMergedTree()
    {
        using var s = new Sandbox();
        Write(s.Worktree, "a.txt", "a\n");
        Commit(s.Worktree, "Add a");
        s.PushFromElsewhere("brain/Thing.cs", "class Thing {}\n", "Main changes the brain");
        var built = new List<bool>();

        await s.LandAsync(s.Options() with { Build = tree => { built.Add(File.Exists(Path.Combine(tree, "brain", "Thing.cs"))); return Task.CompletedTask; } });

        Assert.Equal([true], built);
        Assert.Equal("Add a", Git(s.Origin, "log", "-1", "--format=%s", "main"));
    }

    [Fact]
    public async Task DocOnlyMergeLandsWithoutABuild()
    {
        using var s = new Sandbox();
        Write(s.Worktree, "a.txt", "a\n");
        Commit(s.Worktree, "Add a");
        s.PushFromElsewhere("docs/note.md", "note\n", "Main edits docs");
        var built = 0;

        await s.LandAsync(s.Options() with { Build = _ => { built++; return Task.CompletedTask; } });

        Assert.Equal(0, built);
        Assert.Equal("Add a", Git(s.Origin, "log", "-1", "--format=%s", "main"));
    }

    [Fact]
    public async Task FailedBuildLandsNothingAndUndoesTheMergeSoTheNextLandRebuilds()
    {
        using var s = new Sandbox();
        Write(s.Worktree, "a.txt", "a\n");
        Commit(s.Worktree, "Add a");
        var before = Git(s.Worktree, "rev-parse", "HEAD");
        var competing = s.PushFromElsewhere("brain/Thing.cs", "class Thing {}\n", "Main changes the brain");
        var runs = 0;
        var failing = s.Options() with { Build = _ => { runs++; throw new ToolException("boom"); } };

        var refused = await SandboxAssert.RefusedAsync(() => s.LandAsync(failing));
        Assert.Contains("the merged tree does not build", refused.Message, StringComparison.Ordinal);
        Assert.Equal(competing, s.OriginMain());
        Assert.Equal(before, Git(s.Worktree, "rev-parse", "HEAD"));

        await SandboxAssert.RefusedAsync(() => s.LandAsync(failing));
        Assert.Equal(2, runs);
    }
}
