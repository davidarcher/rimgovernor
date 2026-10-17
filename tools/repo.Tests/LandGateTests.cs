using RimGovernor.Tools.Common;
using static RimGovernor.Tools.Workflow.Tests.Sandbox;

namespace RimGovernor.Tools.Workflow.Tests;

/// <summary>The lane retests the merged tree only when main brought in files the gate builds.</summary>
public sealed class LandGateTests
{
    [Fact]
    public async Task MergeThatBroughtGateInputsRunsTheGateOnTheMergedTree()
    {
        using var s = new Sandbox();
        Write(s.Worktree, "a.txt", "a\n");
        Commit(s.Worktree, "Add a");
        s.PushFromElsewhere("brain/Thing.cs", "class Thing {}\n", "Main changes the brain");
        var gated = new List<bool>();

        await s.LandAsync(s.Options() with { Gate = tree => { gated.Add(File.Exists(Path.Combine(tree, "brain", "Thing.cs"))); return Task.CompletedTask; } });

        Assert.Equal([true], gated);
        Assert.Equal("Add a", Git(s.Origin, "log", "-1", "--format=%s", "main"));
    }

    [Fact]
    public async Task DocOnlyMergeLandsWithoutTheGate()
    {
        using var s = new Sandbox();
        Write(s.Worktree, "a.txt", "a\n");
        Commit(s.Worktree, "Add a");
        s.PushFromElsewhere("docs/note.md", "note\n", "Main edits docs");
        var gated = 0;

        await s.LandAsync(s.Options() with { Gate = _ => { gated++; return Task.CompletedTask; } });

        Assert.Equal(0, gated);
        Assert.Equal("Add a", Git(s.Origin, "log", "-1", "--format=%s", "main"));
    }

    [Fact]
    public async Task FailedGateLandsNothingAndUndoesTheMergeSoTheNextLandRetests()
    {
        using var s = new Sandbox();
        Write(s.Worktree, "a.txt", "a\n");
        Commit(s.Worktree, "Add a");
        var before = Git(s.Worktree, "rev-parse", "HEAD");
        var competing = s.PushFromElsewhere("brain/Thing.cs", "class Thing {}\n", "Main changes the brain");
        var runs = 0;
        var failing = s.Options() with { Gate = _ => { runs++; throw new ToolException("boom"); } };

        var refused = await SandboxAssert.RefusedAsync(() => s.LandAsync(failing));
        Assert.Contains("the gate failed on the merged tree", refused.Message, StringComparison.Ordinal);
        Assert.Equal(competing, s.OriginMain());
        Assert.Equal(before, Git(s.Worktree, "rev-parse", "HEAD"));

        await SandboxAssert.RefusedAsync(() => s.LandAsync(failing));
        Assert.Equal(2, runs);
    }
}
