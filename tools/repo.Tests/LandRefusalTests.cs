using System.Diagnostics;
using RimGovernor.Tools.Common;
using RimGovernor.Tools.Workflow.Commands;
using static RimGovernor.Tools.Workflow.Tests.Sandbox;

namespace RimGovernor.Tools.Workflow.Tests;

/// <summary>What the lane refuses before anything reaches origin, and the landing lock.</summary>
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

    [Fact]
    public async Task LiveLockHolderMakesTheLaneWaitThenGiveUp()
    {
        using var s = new Sandbox();
        var seed = s.OriginMain();
        Write(s.Worktree, "a.txt", "a\n");
        Commit(s.Worktree, "Add a");
        using var held = FileLock.TryAcquire(Path.Combine(s.CommonDir, "rimgovernor-land.lock"), "branch=someone-else");
        Assert.NotNull(held);

        var refused = await SandboxAssert.RefusedAsync(() => s.LandAsync(s.Options() with { LockTimeout = TimeSpan.FromMilliseconds(300) }));

        Assert.Contains("branch=someone-else", refused.Message, StringComparison.Ordinal);
        Assert.Equal(seed, s.OriginMain());
    }

    [Fact]
    public async Task KilledLockHolderLeavesNoLockBehind()
    {
        Assert.SkipUnless(OnPath("pwsh"), "needs pwsh to hold the lock from another process");
        using var s = new Sandbox();
        Write(s.Worktree, "a.txt", "a\n");
        Commit(s.Worktree, "Add a");
        var path = Path.Combine(s.CommonDir, "rimgovernor-land.lock");
        File.WriteAllText(path + ".holder", "pid=0 branch=killed");
        var info = new ProcessStartInfo("pwsh")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "-NoProfile", "-Command", $"$f = [IO.File]::Open('{path}', 'OpenOrCreate', 'ReadWrite', 'None'); 'held'; Start-Sleep 120" })
        {
            info.ArgumentList.Add(arg);
        }
        using (var holder = Process.Start(info)!)
        {
            Assert.Equal("held", await holder.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken));
            Assert.Null(FileLock.TryAcquire(path, "probe"));
            holder.Kill(entireProcessTree: true);
            await holder.WaitForExitAsync(TestContext.Current.CancellationToken);
        }

        await s.LandAsync(s.Options() with { LockTimeout = TimeSpan.FromSeconds(5) });

        Assert.Equal("Add a", Git(s.Origin, "log", "-1", "--format=%s", "main"));
    }

    [Theory]
    [InlineData("Merge branch 'main' into task", true)]
    [InlineData("wip", true)]
    [InlineData("WIP: half done", true)]
    [InlineData("fixup! Add a", true)]
    [InlineData("Add the hunting estimate (#3130)", false)]
    public void PlaceholderSubjectsCannotTitleASquash(string subject, bool untitled) =>
        Assert.Equal(untitled, LandCommand.Untitled(subject));

    private static bool OnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator)
            .Any(dir => File.Exists(Path.Combine(dir, exe)) || File.Exists(Path.Combine(dir, exe + ".exe")));
}
