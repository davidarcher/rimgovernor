using RimGovernor.Tools.Common;

namespace RimGovernor.Tools.Workflow.Commands;

/// <summary>
/// Runs the gate (<see cref="Gate"/>) when the change touched anything it builds.
/// <code>RimGovernor.cmd test [-base main] [-full]</code>
/// Run from anywhere inside the worktree. It diffs the working tree (committed, staged, unstaged and untracked)
/// against -base, so it is the edit/test loop's check as well as the pre-land one; the landing lane retests only what main brought in.
/// -full runs the gate regardless.
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
            Log.Info("  -full   run the gate even without changes; for the end of an epic");
            return 0;
        }
        var repo = Repo.Require(Directory.GetCurrentDirectory());
        var changed = await Affected.ChangedFilesAsync(repo, flags.String("base", "main"), ct).ConfigureAwait(false);
        await Affected.TestAsync(repo, changed, flags.Bool("full"), ct).ConfigureAwait(false);
        return 0;
    }
}
