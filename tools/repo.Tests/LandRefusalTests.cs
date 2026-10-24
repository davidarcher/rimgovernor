using static RimGovernor.Tools.Workflow.Tests.Sandbox;

namespace RimGovernor.Tools.Workflow.Tests;

/// <summary>What the lane refuses before anything reaches origin.</summary>
public sealed class LandRefusalTests
{
    [Fact]
    public async Task ConflictWithOriginIsLeftToTheBranchAndLandsNothing()
    {
        using var s = new Sandbox();
        var competing = s.PushFromElsewhere("shared.txt", "one\nmain's two\nthree\n", "Main edits line two");
        Write(s.Worktree, "shared.txt", "one\nthe branch's two\nthree\n");
        Commit(s.Worktree, "Branch edits line two");

        var refused = await SandboxAssert.RefusedAsync(() => s.LandAsync());

        Assert.Contains("resolve the conflict", refused.Message, StringComparison.Ordinal);
        Assert.Equal(competing, s.OriginMain());
        Assert.Equal("", Git(s.Worktree, "status", "--porcelain"));
        Assert.False(File.Exists(Path.Combine(Git(s.Worktree, "rev-parse", "--absolute-git-dir"), "MERGE_HEAD")));
    }

    [Fact]
    public async Task StaleTreeOverANewerMainIsRefused()
    {
        using var s = new Sandbox();
        var newer = s.PushFromElsewhere("shared.txt", "one\nTWO\nthree\n", "Main rewrites line two");
        // The branch's files still hold the seed; re-parenting them onto the new main commits the seed's shared.txt
        // over main's change while forking at main itself.
        Git(s.Worktree, "fetch", "-q", "origin");
        Git(s.Worktree, "reset", "-q", "--soft", "origin/main");
        Write(s.Worktree, "c.txt", "c\n");
        Commit(s.Worktree, "Add c on a stale tree");

        var refused = await SandboxAssert.RefusedAsync(() => s.LandAsync());

        Assert.Contains("puts back content main replaced", refused.Message, StringComparison.Ordinal);
        Assert.Contains("shared.txt", refused.Message, StringComparison.Ordinal);
        Assert.Equal(newer, s.OriginMain());
    }
}
