using static RimGovernor.Tools.Workflow.Tests.Sandbox;

namespace RimGovernor.Tools.Workflow.Tests;

/// <summary>The lane's landing and push: what reaches origin, local main, the main checkout and the branch.</summary>
public sealed class LandTests
{
    [Fact]
    public async Task LandsOneSquashOnOriginAndBringsMainAndTheBranchAlong()
    {
        using var s = new Sandbox();
        var seed = s.OriginMain();
        Write(s.Worktree, "a.txt", "a\n");
        Commit(s.Worktree, "Add a");
        Write(s.Worktree, "b.txt", "b\n");
        Commit(s.Worktree, "Add b, the milestone");

        await s.LandAsync();

        var landed = s.OriginMain();
        Assert.Equal(seed, Git(s.Origin, "rev-parse", "main^"));
        Assert.Equal("Add b, the milestone", Git(s.Origin, "log", "-1", "--format=%s", "main"));
        Assert.Equal(landed, Git(s.Main, "rev-parse", "main"));
        Assert.True(File.Exists(Path.Combine(s.Main, "a.txt")));
        Assert.Equal("", Git(s.Main, "status", "--porcelain"));
        Assert.Equal(landed, Git(s.Worktree, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task LandsAgainOnTopWhenOriginMovesBeforeThePush()
    {
        using var s = new Sandbox();
        Write(s.Worktree, "a.txt", "a\n");
        Commit(s.Worktree, "Add a");
        var competing = "";
        var options = s.Options() with
        {
            BeforePush = attempt =>
            {
                if (attempt == 1)
                {
                    competing = s.PushFromElsewhere("other.txt", "x\n", "Someone else's landing");
                }
                return Task.CompletedTask;
            },
        };

        await s.LandAsync(options);

        Assert.Equal(competing, Git(s.Origin, "rev-parse", "main^"));
        Assert.Equal("Add a", Git(s.Origin, "log", "-1", "--format=%s", "main"));
        Assert.Equal(s.OriginMain(), Git(s.Main, "rev-parse", "main"));
        Assert.True(File.Exists(Path.Combine(s.Main, "other.txt")));
        Assert.True(File.Exists(Path.Combine(s.Main, "a.txt")));
    }

    [Fact]
    public async Task GivesUpWhenOriginMovesBeforeEveryPush()
    {
        using var s = new Sandbox();
        Write(s.Worktree, "a.txt", "a\n");
        Commit(s.Worktree, "Add a");
        var options = s.Options() with
        {
            BeforePush = attempt =>
            {
                s.PushFromElsewhere($"other{attempt}.txt", "x\n", $"Landing {attempt} elsewhere");
                return Task.CompletedTask;
            },
        };

        var refused = await SandboxAssert.RefusedAsync(() => s.LandAsync(options));

        Assert.Contains("nothing landed", refused.Message, StringComparison.Ordinal);
        Assert.Equal($"Landing {LandCommandAttempts} elsewhere", Git(s.Origin, "log", "-1", "--format=%s", "main"));
        Assert.DoesNotContain("Add a", Git(s.Origin, "log", "--format=%s", "main"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnreachableOriginLandsNothing()
    {
        using var s = new Sandbox();
        var seed = s.OriginMain();
        Write(s.Worktree, "a.txt", "a\n");
        Commit(s.Worktree, "Add a");
        var head = Git(s.Worktree, "rev-parse", "HEAD");
        Git(s.Worktree, "remote", "set-url", "origin", Path.Combine(s.Root, "missing.git"));

        var refused = await SandboxAssert.RefusedAsync(() => s.LandAsync());

        Assert.Contains("fetching origin's main failed", refused.Message, StringComparison.Ordinal);
        Assert.Equal(seed, Git(s.Main, "rev-parse", "main"));
        Assert.Equal(head, Git(s.Worktree, "rev-parse", "HEAD"));
    }

    [Fact]
    public async Task OriginRefusingThePushLandsNothing()
    {
        using var s = new Sandbox();
        var seed = s.OriginMain();
        Write(s.Origin, "hooks/pre-receive", "#!/bin/sh\necho main is protected >&2\nexit 1\n");
        Write(s.Worktree, "a.txt", "a\n");
        Commit(s.Worktree, "Add a");

        var refused = await SandboxAssert.RefusedAsync(() => s.LandAsync());

        Assert.Contains("pushing to origin's main failed", refused.Message, StringComparison.Ordinal);
        Assert.Equal(seed, s.OriginMain());
        Assert.Equal(seed, Git(s.Main, "rev-parse", "main"));
    }

    [Fact]
    public async Task BranchMatchingMainHasNothingToLand()
    {
        using var s = new Sandbox();
        var seed = s.OriginMain();

        await s.LandAsync();

        Assert.Equal(seed, s.OriginMain());
    }

    private static int LandCommandAttempts => Commands.LandCommand.Attempts;
}
