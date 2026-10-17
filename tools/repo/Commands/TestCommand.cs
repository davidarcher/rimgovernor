using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Workflow.Commands;

/// <summary>
/// Runs the checks a change owes: the native contract probes build when its inputs changed, then task build and
/// task test when any C# project changed.
/// <code>RimGovernor.cmd test [-base main] [-full]</code>
/// Run from anywhere inside the worktree. It diffs the working tree (committed, staged, unstaged and untracked)
/// against -base, so it is the edit/test loop's check as well as the pre-land one; the landing lane does not test.
/// -full runs the gate even with no changed C# project.
/// </summary>
internal static class TestCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        var flags = Flags.Parse(args, new HashSet<string> { "full" }, new HashSet<string> { "base" });
        if (flags.HelpRequested)
        {
            Log.Info("usage: test [-base main] [-full]");
            Log.Info("  -base   revision to diff the working tree against (default main)");
            Log.Info("  -full   run task build and task test even without changes; for the end of an epic");
            return 0;
        }
        var repo = Repo.Require(Directory.GetCurrentDirectory());
        var changed = await Affected.ChangedFilesAsync(repo, flags.String("base", "main"), ct).ConfigureAwait(false);
        await Affected.TestAsync(repo, changed, flags.Bool("full"), ct).ConfigureAwait(false);
        return 0;
    }
}
